from __future__ import annotations

import contextvars
import json
import sys
import threading
import types
from typing import Any, Callable

BRIDGE_VERSION = 1

_request_id: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "loom_bridge_request_id",
    default=None,
)

_state_lock = threading.Lock()
_transaction_lock = threading.Lock()
_active_request_id: str | None = None
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


def _install(
    transport: Callable[
        [str, str, str, dict[str, Any]],
        dict[str, Any],
    ]
) -> types.ModuleType:
    global _transport
    _transport = transport

    loom = types.ModuleType("loom")
    loom.__dict__.update(
        {
            "__bridge_version__": BRIDGE_VERSION,
            "LoomError": LoomError,
            "capabilities": capabilities,
            "_bridge_call": _bridge_call,
            "__all__": [
                "LoomError",
                "capabilities",
            ],
        }
    )
    sys.modules["loom"] = loom
    return loom


def _begin_execution(request_id: str) -> contextvars.Token[str | None]:
    global _active_request_id

    if not isinstance(request_id, str) or not request_id:
        raise RuntimeError("bridge execution request id is invalid")

    with _transaction_lock:
        with _state_lock:
            if _active_request_id is not None:
                raise RuntimeError("bridge execution is already active")
            _active_request_id = request_id
        return _request_id.set(request_id)


def _end_execution(
    request_id: str,
    token: contextvars.Token[str | None],
) -> None:
    global _active_request_id

    with _transaction_lock:
        with _state_lock:
            if _active_request_id != request_id:
                raise RuntimeError(
                    "bridge execution state does not match the active request"
                )
            _active_request_id = None
        _request_id.reset(token)


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
