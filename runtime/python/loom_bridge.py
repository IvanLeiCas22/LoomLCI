from __future__ import annotations

import contextvars
import json
import sys
import threading
import types
from typing import Any, Callable

BRIDGE_VERSION = 2
MAX_OUTPUT_COUNT = 4
MAX_IMAGE_BYTES = 6 * 1024 * 1024
MAX_TOTAL_OUTPUT_BYTES = 6 * 1024 * 1024

_request_id: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "loom_bridge_request_id",
    default=None,
)

_state_lock = threading.Lock()
_transaction_lock = threading.Lock()
_active_request_id: str | None = None
_active_output_state: "_OutputState | None" = None
_transport: Callable[
    [str, str, str, dict[str, Any]],
    dict[str, Any],
] | None = None
_call_counter = 0


class LoomError(RuntimeError):
    def __init__(
        self,
        code: str,
        message: str,
        retryable: bool | None = None,
        details: dict[str, Any] | None = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = retryable
        self.details = details

    def __str__(self) -> str:
        return f"{self.code}: {self.message}"


class _OutputState:
    def __init__(self) -> None:
        self._outputs: list[bytes] = []
        self._total_bytes = 0
        self._closed = False

    def add_image(self, data: bytes) -> None:
        if self._closed:
            raise LoomError(
                "output_unavailable",
                "The Python execution output collector is no longer active.",
                False,
                None,
            )
        if len(self._outputs) >= MAX_OUTPUT_COUNT:
            raise LoomError(
                "unsupported",
                f"Python execution supports at most {MAX_OUTPUT_COUNT} image outputs.",
                False,
                {"reason": "too_many_python_outputs"},
            )
        if len(data) == 0:
            raise LoomError(
                "invalid_argument",
                "Image output must contain at least one byte.",
                False,
                None,
            )
        if len(data) > MAX_IMAGE_BYTES:
            raise LoomError(
                "unsupported",
                f"Image output exceeds {MAX_IMAGE_BYTES} bytes.",
                False,
                {
                    "reason": "python_image_too_large",
                    "image_bytes": len(data),
                    "max_image_bytes": MAX_IMAGE_BYTES,
                },
            )
        new_total = self._total_bytes + len(data)
        if new_total > MAX_TOTAL_OUTPUT_BYTES:
            raise LoomError(
                "unsupported",
                f"Python execution outputs exceed {MAX_TOTAL_OUTPUT_BYTES} aggregate bytes.",
                False,
                {
                    "reason": "python_outputs_too_large",
                    "aggregate_bytes": new_total,
                    "max_aggregate_bytes": MAX_TOTAL_OUTPUT_BYTES,
                },
            )
        self._outputs.append(data)
        self._total_bytes = new_total

    def close_and_snapshot(self) -> list[bytes]:
        self._closed = True
        return list(self._outputs)


def _install(
    transport: Callable[
        [str, str, str, dict[str, Any]],
        dict[str, Any],
    ]
) -> types.ModuleType:
    global _transport
    _transport = transport

    loom = types.ModuleType("loom")
    loom.__path__ = []
    loom.__dict__.update(
        {
            "__bridge_version__": BRIDGE_VERSION,
            "LoomError": LoomError,
            "capabilities": capabilities,
            "display_image": display_image,
            "_bridge_call": _bridge_call,
        }
    )

    fs = _create_filesystem_module()
    process = _create_process_module()
    loom.fs = fs
    loom.process = process
    loom.__all__ = [
        "LoomError",
        "capabilities",
        "display_image",
        "fs",
        "process",
    ]

    sys.modules["loom"] = loom
    sys.modules["loom.fs"] = fs
    sys.modules["loom.process"] = process
    return loom


def _begin_execution(request_id: str) -> contextvars.Token[str | None]:
    global _active_request_id, _active_output_state

    if not isinstance(request_id, str) or not request_id:
        raise RuntimeError("bridge execution request id is invalid")

    with _transaction_lock:
        with _state_lock:
            if _active_request_id is not None or _active_output_state is not None:
                raise RuntimeError("bridge execution is already active")
            _active_request_id = request_id
            _active_output_state = _OutputState()
        return _request_id.set(request_id)


def _end_execution(
    request_id: str,
    token: contextvars.Token[str | None],
) -> list[bytes]:
    global _active_request_id, _active_output_state

    with _transaction_lock:
        with _state_lock:
            if _active_request_id != request_id or _active_output_state is None:
                raise RuntimeError(
                    "bridge execution state does not match the active request"
                )
            output_state = _active_output_state
            _active_request_id = None
            _active_output_state = None
        outputs = output_state.close_and_snapshot()
        _request_id.reset(token)
        return outputs


def display_image(data: bytes | bytearray | memoryview) -> None:
    """Attach one in-memory image to the active python_execute result."""
    if isinstance(data, bytes):
        byte_count = len(data)
        raw = data
    elif isinstance(data, bytearray):
        byte_count = len(data)
        if byte_count > MAX_IMAGE_BYTES:
            raw = b""
        else:
            raw = bytes(data)
    elif isinstance(data, memoryview):
        byte_count = data.nbytes
        if byte_count > MAX_IMAGE_BYTES:
            raw = b""
        else:
            raw = data.tobytes()
    else:
        raise TypeError("data must be bytes, bytearray, or memoryview")

    if byte_count > MAX_IMAGE_BYTES:
        raise LoomError(
            "unsupported",
            f"Image output exceeds {MAX_IMAGE_BYTES} bytes.",
            False,
            {
                "reason": "python_image_too_large",
                "image_bytes": byte_count,
                "max_image_bytes": MAX_IMAGE_BYTES,
            },
        )

    request_id = _request_id.get()
    if request_id is None:
        raise LoomError(
            "output_unavailable",
            "loom.display_image() can only be used during an active python_execute.",
            False,
            None,
        )

    with _transaction_lock:
        with _state_lock:
            if (
                _active_request_id != request_id
                or _active_output_state is None
            ):
                raise LoomError(
                    "output_unavailable",
                    "The Python execution output collector is no longer active.",
                    False,
                    None,
                )
            output_state = _active_output_state
        output_state.add_image(raw)


def _next_call_id() -> str:
    global _call_counter
    _call_counter += 1
    return f"call_{_call_counter}"


def _bridge_call(
    method: str,
    arguments: dict[str, Any] | None = None,
) -> Any:
    if not isinstance(method, str) or not method:
        raise TypeError("method must be a non-empty string")
    if len(method) > 128:
        raise LoomError(
            "invalid_argument",
            "Python bridge method exceeds 128 characters.",
            False,
            None,
        )

    if arguments is None:
        arguments = {}
    if not isinstance(arguments, dict):
        raise TypeError("arguments must be a dict")

    try:
        json.dumps(
            arguments,
            ensure_ascii=True,
            allow_nan=False,
            separators=(",", ":"),
        )
    except (TypeError, ValueError) as exc:
        raise TypeError(
            "arguments must contain only strict JSON-compatible values"
        ) from exc

    request_id = _request_id.get()
    if request_id is None:
        raise LoomError(
            "bridge_unavailable",
            "loom.* can only be used during an active python_execute.",
            False,
            None,
        )

    with _transaction_lock:
        with _state_lock:
            if _active_request_id != request_id:
                raise LoomError(
                    "bridge_unavailable",
                    "The Python bridge execution is no longer active.",
                    False,
                    None,
                )

        transport = _transport
        if transport is None:
            raise LoomError(
                "bridge_unavailable",
                "The Python bridge transport is not configured.",
                False,
                None,
            )

        response = transport(
            request_id,
            _next_call_id(),
            method,
            arguments,
        )

    if not isinstance(response, dict):
        raise RuntimeError("bridge transport returned an invalid response")

    ok = response.get("ok")
    if ok is True:
        return response.get("result")

    if ok is not False:
        raise RuntimeError("bridge response is missing a valid ok flag")

    error = response.get("error")
    if not isinstance(error, dict):
        raise RuntimeError("bridge response is missing error metadata")

    code = error.get("code")
    message = error.get("message")
    retryable = error.get("retryable")
    details = error.get("details")

    if not isinstance(code, str) or not code:
        raise RuntimeError("bridge error code is invalid")
    if not isinstance(message, str):
        raise RuntimeError("bridge error message is invalid")
    if retryable is not None and not isinstance(retryable, bool):
        raise RuntimeError("bridge error retryable flag is invalid")
    if details is not None and not isinstance(details, dict):
        raise RuntimeError("bridge error details are invalid")

    raise LoomError(
        code,
        message,
        retryable,
        details,
    )


def _fs_list_tree(
    path: str = ".",
    *,
    include_generated: bool = False,
    exclude_directories: list[str] | tuple[str, ...] | None = None,
    max_depth: int = 3,
    max_entries: int = 1000,
    cursor: str | None = None,
) -> dict[str, Any]:
    """List a bounded recursive tree relative to the active WorkSession."""
    return _bridge_call(
        "fs.list_tree",
        {
            "path": path,
            "include_generated": include_generated,
            "exclude_directories": exclude_directories,
            "max_depth": max_depth,
            "max_entries": max_entries,
            "cursor": cursor,
        },
    )


def _fs_find_paths(
    path: str,
    queries: list[str] | tuple[str, ...],
    *,
    match_mode: str = "substring",
    type: str = "any",
    include_generated: bool = False,
    exclude_directories: list[str] | tuple[str, ...] | None = None,
    max_depth: int = 12,
    max_results: int = 100,
    cursor: str | None = None,
) -> dict[str, Any]:
    """Find paths by literal fragments or suffixes."""
    return _bridge_call(
        "fs.find_paths",
        {
            "path": path,
            "queries": queries,
            "match_mode": match_mode,
            "type": type,
            "include_generated": include_generated,
            "exclude_directories": exclude_directories,
            "max_depth": max_depth,
            "max_results": max_results,
            "cursor": cursor,
        },
    )


def _fs_search_text(
    path: str,
    queries: list[str] | tuple[str, ...],
    *,
    case_sensitive: bool = False,
    include_generated: bool = False,
    exclude_directories: list[str] | tuple[str, ...] | None = None,
    max_depth: int = 12,
    max_results: int = 100,
    context_lines: int = 1,
    cursor: str | None = None,
) -> dict[str, Any]:
    """Search literal text queries with bounded excerpts and cursors."""
    return _bridge_call(
        "fs.search_text",
        {
            "path": path,
            "queries": queries,
            "case_sensitive": case_sensitive,
            "include_generated": include_generated,
            "exclude_directories": exclude_directories,
            "max_depth": max_depth,
            "max_results": max_results,
            "context_lines": context_lines,
            "cursor": cursor,
        },
    )


def _fs_read_files(
    files: list[dict[str, Any]] | tuple[dict[str, Any], ...],
) -> dict[str, Any]:
    """Read one or more known text files, optionally by 1-based line range."""
    return _bridge_call(
        "fs.read_files",
        {"files": files},
    )


def _fs_apply_patch(
    changes: list[dict[str, Any]] | tuple[dict[str, Any], ...],
) -> dict[str, Any]:
    """Apply validated write/replace/delete/move file changes."""
    return _bridge_call(
        "fs.apply_patch",
        {"changes": changes},
    )


def _fs_manage_directory(
    action: str,
    path: str,
) -> dict[str, Any]:
    """Create a directory tree or delete one existing empty directory."""
    return _bridge_call(
        "fs.manage_directory",
        {
            "action": action,
            "path": path,
        },
    )


def _fs_read_pdf(
    path: str,
    *,
    start_page: int = 1,
    max_pages: int = 10,
) -> dict[str, Any]:
    """Extract bounded text from a PDF without OCR."""
    return _bridge_call(
        "fs.read_pdf",
        {
            "path": path,
            "start_page": start_page,
            "max_pages": max_pages,
        },
    )


def _create_filesystem_module() -> types.ModuleType:
    fs = types.ModuleType("loom.fs")
    fs.__package__ = "loom"
    fs.__dict__.update(
        {
            "list_tree": _fs_list_tree,
            "find_paths": _fs_find_paths,
            "search_text": _fs_search_text,
            "read_files": _fs_read_files,
            "apply_patch": _fs_apply_patch,
            "manage_directory": _fs_manage_directory,
            "read_pdf": _fs_read_pdf,
            "__all__": [
                "list_tree",
                "find_paths",
                "search_text",
                "read_files",
                "apply_patch",
                "manage_directory",
                "read_pdf",
            ],
        }
    )
    return fs


def _process_run(
    executable: str,
    arguments: list[str] | tuple[str, ...] | None = None,
    *,
    working_directory: str | None = None,
    environment: dict[str, str | None] | None = None,
    timeout_seconds: int = 30,
    max_output_chars: int = 65_536,
) -> dict[str, Any]:
    """Run a short Loom-managed pipe process and return bounded output."""
    return _bridge_call(
        "process.run",
        {
            "executable": executable,
            "arguments": arguments,
            "working_directory": working_directory,
            "environment": environment,
            "timeout_seconds": timeout_seconds,
            "max_output_chars": max_output_chars,
        },
    )


def _process_run_many(
    jobs: list[dict[str, Any]] | tuple[dict[str, Any], ...],
    *,
    max_concurrent: int = 4,
    job_timeout_seconds: int = 30,
    batch_timeout_seconds: int = 45,
    max_output_chars: int = 4096,
) -> dict[str, Any]:
    """Run 1-32 one-shot processes concurrently, with bounded results.

    Results preserve input order. Normal job errors do not abort other jobs.
    Requires an active python_execute and its WorkSession.
    """
    return _bridge_call(
        "process.run_many",
        {
            "jobs": jobs,
            "max_concurrent": max_concurrent,
            "job_timeout_seconds": job_timeout_seconds,
            "batch_timeout_seconds": batch_timeout_seconds,
            "max_output_chars": max_output_chars,
        },
    )


def _process_start(
    executable: str,
    arguments: list[str] | tuple[str, ...] | None = None,
    *,
    working_directory: str | None = None,
    environment: dict[str, str | None] | None = None,
    io_mode: str = "pipes",
    terminal_columns: int | None = None,
    terminal_rows: int | None = None,
) -> dict[str, Any]:
    """Start a durable SessionOwned Loom process."""
    return _bridge_call(
        "process.start",
        {
            "executable": executable,
            "arguments": arguments,
            "working_directory": working_directory,
            "environment": environment,
            "io_mode": io_mode,
            "terminal_columns": terminal_columns,
            "terminal_rows": terminal_rows,
        },
    )


def _process_status(
    process_handle: str,
) -> dict[str, Any]:
    """Return current Loom process state and retention metadata."""
    return _bridge_call(
        "process.status",
        {"process_handle": process_handle},
    )


def _process_read(
    process_handle: str,
    *,
    stdout_cursor: int = 0,
    stderr_cursor: int = 0,
    terminal_cursor: int = 0,
    max_chars: int = 65_536,
) -> dict[str, Any]:
    """Read retained output. Treat returned cursors as opaque UTF-16 positions."""
    return _bridge_call(
        "process.read",
        {
            "process_handle": process_handle,
            "stdout_cursor": stdout_cursor,
            "stderr_cursor": stderr_cursor,
            "terminal_cursor": terminal_cursor,
            "max_chars": max_chars,
        },
    )


def _process_write(
    process_handle: str,
    text: str,
) -> bool:
    """Write text verbatim to process stdin or terminal input."""
    return _bridge_call(
        "process.write",
        {
            "process_handle": process_handle,
            "text": text,
        },
    )


def _process_resize(
    process_handle: str,
    columns: int,
    rows: int,
) -> bool:
    """Resize a terminal-mode Loom process."""
    return _bridge_call(
        "process.resize",
        {
            "process_handle": process_handle,
            "columns": columns,
            "rows": rows,
        },
    )


def _process_terminate(
    process_handle: str,
) -> bool:
    """Terminate a Loom process and its managed descendant tree."""
    return _bridge_call(
        "process.terminate",
        {"process_handle": process_handle},
    )


def _process_release(
    process_handle: str,
) -> bool:
    """Release a terminal process handle and discard retained state/output."""
    return _bridge_call(
        "process.release",
        {"process_handle": process_handle},
    )


def _create_process_module() -> types.ModuleType:
    process = types.ModuleType("loom.process")
    process.__package__ = "loom"
    process.__dict__.update(
        {
            "run": _process_run,
            "run_many": _process_run_many,
            "start": _process_start,
            "status": _process_status,
            "read": _process_read,
            "write": _process_write,
            "resize": _process_resize,
            "terminate": _process_terminate,
            "release": _process_release,
            "__all__": [
                "run",
                "run_many",
                "start",
                "status",
                "read",
                "write",
                "resize",
                "terminate",
                "release",
            ],
        }
    )
    return process


def capabilities() -> list[str]:
    value = _bridge_call(
        "bridge.capabilities",
        {},
    )
    if (
        not isinstance(value, list)
        or any(not isinstance(item, str) for item in value)
    ):
        raise RuntimeError(
            "bridge.capabilities returned an invalid value"
        )
    return value
