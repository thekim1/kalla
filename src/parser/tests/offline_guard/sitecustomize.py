"""Network guard for `test_offline.py`.

Python imports `sitecustomize` at start-up from `PYTHONPATH`, so with this directory on it every process the test
starts, the spawned conversion slots included, refuses IP connections and name lookups. A library that falls back
to a download then fails the test instead of passing on a cached file (parser design §11).
"""

import socket
from collections.abc import Callable
from typing import Any

_IP_FAMILIES = (socket.AF_INET, socket.AF_INET6)
_connect = socket.socket.connect
_connect_ex = socket.socket.connect_ex


class NetworkDisabledError(OSError):
    pass


def _refuse(*_args: Any, **_kwargs: Any) -> Any:
    raise NetworkDisabledError("network access during an offline test")


def _guarded(original: Callable[..., Any]) -> Callable[..., Any]:
    def call(self: socket.socket, address: Any) -> Any:
        if self.family in _IP_FAMILIES:
            _refuse()
        return original(self, address)

    return call


def install(set_attribute: Callable[[object, str, object], None] = setattr) -> None:
    set_attribute(socket.socket, "connect", _guarded(_connect))
    set_attribute(socket.socket, "connect_ex", _guarded(_connect_ex))
    set_attribute(socket, "create_connection", _refuse)
    set_attribute(socket, "getaddrinfo", _refuse)


if __name__ == "sitecustomize":
    install()
