from __future__ import annotations

import argparse
import base64
import builtins
import contextvars
import importlib.util
import io
import json
import os
import site
import struct
import sys
import threading
import traceback
from typing import Any

PROTOCOL_VERSION = 3
MAX_REQUEST_FRAME_BYTES = 2 * 1024 * 1024
MAX_RESPONSE_FRAME_BYTES = 40 * 1024 * 1024
MAX_BRIDGE_CALL_FRAME_BYTES = 2 * 1024 * 1024
MAX_BRIDGE_RESULT_FRAME_BYTES = 8 * 1024 * 1024
MAX_CODE_UTF8_BYTES = 256 * 1024
MAX_OUTPUT_COUNT = 4
MAX_IMAGE_BYTES = 6 * 1024 * 1024
MAX_TOTAL_OUTPUT_BYTES = 6 * 1024 * 1024
MAX_EXCEPTION_MESSAGE_CHARS = 16 * 1024
MAX_TRACEBACK_CHARS = 64 * 1024
MAX_REQUEST_ID_CHARS = 128
MAX_OUTPUT_CHARS = 1_048_576

_EXIT_OK = 0
_EXIT_PROTOCOL_ERROR = 2

_original_stdout = sys.stdout
_original_stderr = sys.stderr
_capture_state: contextvars.ContextVar["_CaptureState | None"] = contextvars.ContextVar(
    "loom_capture_state",
    default=None,
)


class ProtocolError(RuntimeError):
    pass


class _BoundedTextBuffer:
    def __init__(self, limit: int) -> None:
        self._limit = limit
        self._parts: list[str] = []
        self._count = 0
        self._truncated = False
        self._closed = False
        self._lock = threading.Lock()

    def write(self, text: str) -> bool:
        with self._lock:
            if self._closed:
                return False

            remaining = self._limit - self._count
            if remaining <= 0:
                if text:
                    self._truncated = True
                return True

            if len(text) <= remaining:
                self._parts.append(text)
                self._count += len(text)
                return True

            self._parts.append(text[:remaining])
            self._count += remaining
            self._truncated = True
            return True

    def close(self) -> None:
        with self._lock:
            self._closed = True

    def snapshot(self) -> tuple[str, bool]:
        with self._lock:
            return "".join(self._parts), self._truncated


class _CaptureState:
    def __init__(self, max_chars: int) -> None:
        self.stdout = _BoundedTextBuffer(max_chars)
        self.stderr = _BoundedTextBuffer(max_chars)

    def write_stdout(self, text: str) -> bool:
        return self.stdout.write(text)

    def write_stderr(self, text: str) -> bool:
        return self.stderr.write(text)

    def close(self) -> None:
        self.stdout.close()
        self.stderr.close()


class _StreamProxy:
    def __init__(self, fallback: Any, stderr: bool) -> None:
        self._fallback = fallback
        self._stderr = stderr

    @property
    def encoding(self) -> str:
        return getattr(self._fallback, "encoding", None) or "utf-8"

    @property
    def errors(self) -> str:
        return getattr(self._fallback, "errors", None) or "strict"

    def writable(self) -> bool:
        return True

    def isatty(self) -> bool:
        try:
            return bool(self._fallback.isatty())
        except BaseException:
            return False

    def fileno(self) -> int:
        return self._fallback.fileno()

    def flush(self) -> None:
        state = _capture_state.get()
        if state is None:
            self._fallback.flush()

    def write(self, text: str) -> int:
        if not isinstance(text, str):
            raise TypeError(f"write() argument must be str, not {type(text).__name__}")

        state = _capture_state.get()
        if state is not None:
            accepted = (
                state.write_stderr(text)
                if self._stderr
                else state.write_stdout(text)
            )
            if accepted:
                return len(text)

        self._fallback.write(text)
        return len(text)


_stdout_proxy = _StreamProxy(_original_stdout, stderr=False)
_stderr_proxy = _StreamProxy(_original_stderr, stderr=True)

_namespace: dict[str, Any] = {
    "__name__": "__main__",
    "__builtins__": builtins,
}


def _install_worker_streams() -> None:
    sys.stdout = _stdout_proxy
    sys.stderr = _stderr_proxy
    sys.stdin = io.StringIO("")


def _read_exact(stream: Any, count: int, allow_clean_eof: bool = False) -> bytes | None:
    chunks: list[bytes] = []
    remaining = count

    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            if allow_clean_eof and remaining == count:
                return None
            raise ProtocolError("Unexpected EOF while reading protocol frame.")
        chunks.append(chunk)
        remaining -= len(chunk)

    return b"".join(chunks)


def _read_frame(
    stream: Any,
    max_frame_bytes: int,
) -> dict[str, Any] | None:
    header = _read_exact(stream, 4, allow_clean_eof=True)
    if header is None:
        return None

    (length,) = struct.unpack("<I", header)
    if length < 2:
        raise ProtocolError("Protocol frame payload is too small.")
    if length > max_frame_bytes:
        raise ProtocolError(
            f"Protocol frame exceeds {max_frame_bytes} bytes."
        )

    payload = _read_exact(stream, length)
    assert payload is not None

    try:
        text = payload.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise ProtocolError("Protocol frame is not valid UTF-8.") from exc

    def reject_constant(value: str) -> None:
        raise ValueError(f"Non-finite JSON constant is not allowed: {value}")

    try:
        value = json.loads(text, parse_constant=reject_constant)
    except (json.JSONDecodeError, ValueError) as exc:
        raise ProtocolError("Protocol frame is not valid strict JSON.") from exc

    if not isinstance(value, dict):
        raise ProtocolError("Protocol frame root must be a JSON object.")

    return value


def _write_frame(
    stream: Any,
    value: dict[str, Any],
    max_frame_bytes: int = MAX_RESPONSE_FRAME_BYTES,
) -> None:
    try:
        text = json.dumps(
            value,
            ensure_ascii=True,
            allow_nan=False,
            separators=(",", ":"),
        )
    except (TypeError, ValueError) as exc:
        raise ProtocolError("Could not serialize protocol response.") from exc

    payload = text.encode("utf-8")
    if len(payload) > max_frame_bytes:
        raise ProtocolError(
            f"Protocol response frame exceeds {max_frame_bytes} bytes."
        )

    stream.write(struct.pack("<I", len(payload)))
    stream.write(payload)
    stream.flush()


def _validate_request_id(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value) > MAX_REQUEST_ID_CHARS:
        raise ProtocolError("requestId must be a non-empty bounded string.")
    return value


def _validate_execute_request(message: dict[str, Any]) -> tuple[str, str, int]:
    if message.get("type") != "execute":
        raise ProtocolError("Unsupported protocol message type.")

    request_id = _validate_request_id(message.get("requestId"))
    code = message.get("code")
    max_output_chars = message.get("maxOutputChars")

    if not isinstance(code, str):
        raise ProtocolError("code must be a string.")

    if len(code.encode("utf-8", errors="strict")) > MAX_CODE_UTF8_BYTES:
        raise ProtocolError(
            f"code exceeds {MAX_CODE_UTF8_BYTES} UTF-8 bytes."
        )

    if (
        not isinstance(max_output_chars, int)
        or isinstance(max_output_chars, bool)
        or max_output_chars < 1
        or max_output_chars > MAX_OUTPUT_CHARS
    ):
        raise ProtocolError(
            f"maxOutputChars must be between 1 and {MAX_OUTPUT_CHARS}."
        )

    return request_id, code, max_output_chars


def _bounded_text(value: str, limit: int) -> str:
    return value if len(value) <= limit else value[:limit]


def _safe_exception_message(exc: BaseException) -> str:
    try:
        return _bounded_text(str(exc), MAX_EXCEPTION_MESSAGE_CHARS)
    except BaseException:
        return f"<unprintable {type(exc).__name__}>"


def _safe_traceback(exc: BaseException) -> str:
    try:
        rendered = "".join(
            traceback.format_exception(type(exc), exc, exc.__traceback__)
        )
        return _bounded_text(rendered, MAX_TRACEBACK_CHARS)
    except BaseException as formatting_error:
        return _bounded_text(
            f"<traceback formatting failed: {type(formatting_error).__name__}>",
            MAX_TRACEBACK_CHARS,
        )


def _execute(code: str, max_output_chars: int) -> dict[str, Any]:
    state = _CaptureState(max_output_chars)
    token = _capture_state.set(state)

    _install_worker_streams()
    status = "completed"
    exception: dict[str, str] | None = None

    try:
        exec(compile(code, "<loom-python>", "exec"), _namespace)
    except BaseException as exc:
        status = "exception"
        exception = {
            "type": _bounded_text(type(exc).__name__, 512),
            "message": _safe_exception_message(exc),
            "traceback": _safe_traceback(exc),
        }
    finally:
        _capture_state.reset(token)
        state.close()
        _install_worker_streams()

    stdout, stdout_truncated = state.stdout.snapshot()
    stderr, stderr_truncated = state.stderr.snapshot()

    return {
        "status": status,
        "stdout": stdout,
        "stderr": stderr,
        "stdoutTruncated": stdout_truncated,
        "stderrTruncated": stderr_truncated,
        "exception": exception,
    }


def _encode_outputs(outputs: Any) -> list[dict[str, str]]:
    if not isinstance(outputs, list):
        raise ProtocolError("Python bridge returned an invalid output collection.")
    if len(outputs) > MAX_OUTPUT_COUNT:
        raise ProtocolError(
            f"Python bridge returned more than {MAX_OUTPUT_COUNT} typed outputs."
        )

    encoded: list[dict[str, str]] = []
    total_bytes = 0
    for value in outputs:
        if not isinstance(value, bytes):
            raise ProtocolError("Python image output must be bytes.")
        if len(value) == 0 or len(value) > MAX_IMAGE_BYTES:
            raise ProtocolError(
                f"Python image output must contain between 1 and {MAX_IMAGE_BYTES} bytes."
            )
        total_bytes += len(value)
        if total_bytes > MAX_TOTAL_OUTPUT_BYTES:
            raise ProtocolError(
                f"Python execution outputs exceed {MAX_TOTAL_OUTPUT_BYTES} aggregate bytes."
            )
        encoded.append(
            {
                "kind": "image",
                "data": base64.b64encode(value).decode("ascii"),
            }
        )

    return encoded


def _bridge_transport(
    stream: Any,
    request_id: str,
    call_id: str,
    method: str,
    arguments: dict[str, Any],
) -> dict[str, Any]:
    envelope = {
        "type": "bridge_call",
        "requestId": request_id,
        "callId": call_id,
        "method": method,
        "arguments": arguments,
    }

    try:
        encoded = json.dumps(
            envelope,
            ensure_ascii=True,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        return {
            "ok": False,
            "result": None,
            "error": {
                "code": "invalid_argument",
                "message": "Python bridge arguments are not strict JSON.",
                "retryable": False,
                "details": None,
            },
        }

    if len(encoded) > MAX_BRIDGE_CALL_FRAME_BYTES:
        return {
            "ok": False,
            "result": None,
            "error": {
                "code": "unsupported",
                "message": (
                    "Python bridge call exceeds "
                    f"{MAX_BRIDGE_CALL_FRAME_BYTES} bytes."
                ),
                "retryable": False,
                "details": None,
            },
        }

    _write_frame(
        stream,
        envelope,
        MAX_BRIDGE_CALL_FRAME_BYTES,
    )

    response = _read_frame(
        stream,
        MAX_BRIDGE_RESULT_FRAME_BYTES,
    )
    if response is None:
        raise ProtocolError(
            "Unexpected EOF while waiting for Python bridge result."
        )

    if response.get("type") != "bridge_result":
        raise ProtocolError(
            "Expected Python bridge result frame."
        )
    if response.get("requestId") != request_id:
        raise ProtocolError(
            "Python bridge result requestId does not match the active request."
        )
    if response.get("callId") != call_id:
        raise ProtocolError(
            "Python bridge result callId does not match the active call."
        )

    ok = response.get("ok")
    if not isinstance(ok, bool):
        raise ProtocolError(
            "Python bridge result ok field must be a boolean."
        )

    if ok:
        if response.get("error") is not None:
            raise ProtocolError(
                "Successful Python bridge result must not contain error metadata."
            )
        return {
            "ok": True,
            "result": response.get("result"),
            "error": None,
        }

    error = response.get("error")
    if not isinstance(error, dict):
        raise ProtocolError(
            "Failed Python bridge result must contain error metadata."
        )

    code = error.get("code")
    message = error.get("message")
    retryable = error.get("retryable")
    details = error.get("details")

    if not isinstance(code, str) or not code:
        raise ProtocolError("Python bridge error code is invalid.")
    if not isinstance(message, str):
        raise ProtocolError("Python bridge error message is invalid.")
    if retryable is not None and not isinstance(retryable, bool):
        raise ProtocolError("Python bridge error retryable flag is invalid.")
    if details is not None and not isinstance(details, dict):
        raise ProtocolError("Python bridge error details are invalid.")

    return {
        "ok": False,
        "result": None,
        "error": {
            "code": code,
            "message": message,
            "retryable": retryable,
            "details": details,
        },
    }


def _load_bridge(bridge_script: str, stream: Any) -> Any:
    if not os.path.isfile(bridge_script):
        raise ProtocolError(
            f"Python bridge asset does not exist: {bridge_script}"
        )

    spec = importlib.util.spec_from_file_location(
        "_loomlci_bridge",
        bridge_script,
    )
    if spec is None or spec.loader is None:
        raise ProtocolError(
            "Could not create Python bridge module spec."
        )

    module = importlib.util.module_from_spec(spec)
    sys.modules["_loomlci_bridge"] = module
    spec.loader.exec_module(module)

    install = getattr(module, "_install", None)
    begin_execution = getattr(module, "_begin_execution", None)
    end_execution = getattr(module, "_end_execution", None)
    if not callable(install) or not callable(begin_execution) or not callable(end_execution):
        raise ProtocolError(
            "Python bridge asset is missing required entry points."
        )

    install(
        lambda request_id, call_id, method, arguments: _bridge_transport(
            stream,
            request_id,
            call_id,
            method,
            arguments,
        )
    )
    return module


def _connect(pipe_name: str) -> Any:
    path = rf"\\.\pipe\{pipe_name}"
    return open(path, "r+b", buffering=0)


def _run(
    pipe_name: str,
    bridge_script: str,
    package_site: str | None = None,
) -> int:
    if "" not in sys.path:
        sys.path.insert(0, "")

    if package_site is not None:
        if not os.path.isdir(package_site):
            _original_stderr.write(
                f"loom python worker package site does not exist: {package_site}\n"
            )
            _original_stderr.flush()
            return _EXIT_PROTOCOL_ERROR
        site.addsitedir(package_site)

    _install_worker_streams()

    try:
        stream = _connect(pipe_name)
    except BaseException as exc:
        _original_stderr.write(
            f"loom python worker could not connect to control pipe: {exc}\n"
        )
        _original_stderr.flush()
        return _EXIT_PROTOCOL_ERROR

    try:
        bridge = _load_bridge(
            bridge_script,
            stream,
        )

        _write_frame(
            stream,
            {
                "type": "hello",
                "protocolVersion": PROTOCOL_VERSION,
                "pid": os.getpid(),
                "pythonVersion": ".".join(map(str, sys.version_info[:3])),
            },
        )

        while True:
            message = _read_frame(
                stream,
                MAX_REQUEST_FRAME_BYTES,
            )
            if message is None:
                return _EXIT_OK

            request_id, code, max_output_chars = _validate_execute_request(message)
            bridge_token = bridge._begin_execution(request_id)
            outputs: list[bytes] = []
            try:
                result = _execute(code, max_output_chars)
            finally:
                outputs = bridge._end_execution(
                    request_id,
                    bridge_token,
                )

            result.update(
                {
                    "type": "result",
                    "requestId": request_id,
                    "outputs": _encode_outputs(outputs),
                }
            )
            _write_frame(
                stream,
                result,
                MAX_RESPONSE_FRAME_BYTES,
            )
    except (BrokenPipeError, ConnectionResetError):
        return _EXIT_OK
    except ProtocolError as exc:
        _original_stderr.write(f"loom python worker protocol error: {exc}\n")
        _original_stderr.flush()
        return _EXIT_PROTOCOL_ERROR
    except BaseException as exc:
        _original_stderr.write(
            f"loom python worker fatal error: {type(exc).__name__}: {exc}\n"
        )
        _original_stderr.flush()
        return _EXIT_PROTOCOL_ERROR
    finally:
        try:
            stream.close()
        except BaseException:
            pass


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--pipe-name", required=True)
    parser.add_argument("--bridge-script", required=True)
    parser.add_argument("--package-site")
    args = parser.parse_args()
    return _run(
        args.pipe_name,
        args.bridge_script,
        args.package_site,
    )


if __name__ == "__main__":
    os._exit(main())
