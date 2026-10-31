"""A minimal client of a running parser for the scripts (`update_goldens`, `ocr_check`, `compare_paths`).

Standard library only. `HttpParser` posts to a parser's URL; the tests give the scripts any object with the same
`convert` and `inspect` methods (a FastAPI test client in the image).
"""

import json
import secrets
import urllib.error
import urllib.request
from email import message_from_bytes, policy
from typing import Any, Protocol

TIMEOUT_S = 180


class Parser(Protocol):
    def convert(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]: ...

    def inspect(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]: ...


class ParserHttpError(Exception):
    def __init__(self, status: int, body: dict[str, Any] | None) -> None:
        code = (body or {}).get("error", {}).get("code")
        super().__init__(f"parser answered {status} {code}")
        self.status, self.code = status, code


def multipart_form(fields: dict[str, tuple[str | None, str, bytes]]) -> tuple[bytes, str]:
    boundary = secrets.token_hex(16)
    body = b""
    for name, (filename, content_type, data) in fields.items():
        disposition = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        body += f"--{boundary}\r\nContent-Disposition: {disposition}\r\nContent-Type: {content_type}\r\n\r\n".encode()
        body += data + b"\r\n"
    return body + f"--{boundary}--\r\n".encode(), f"multipart/form-data; boundary={boundary}"


def result_part(content_type: str, body: bytes) -> dict[str, Any]:
    """`result.json` of a `multipart/mixed` convert answer."""
    message = message_from_bytes(f"Content-Type: {content_type}\r\n\r\n".encode() + body, policy=policy.HTTP)
    (result,) = [p for p in message.iter_parts() if p["Content-ID"] == "result"]
    return json.loads(result.get_payload(decode=True))


class HttpParser:
    def __init__(self, url: str) -> None:
        self.url = url.rstrip("/")

    def _post(self, path: str, data: bytes, options: dict[str, Any]) -> tuple[str, bytes]:
        body, content_type = multipart_form(
            {
                "file": ("input", "application/octet-stream", data),
                "options": (None, "application/json", json.dumps(options).encode()),
            }
        )
        request = urllib.request.Request(  # noqa: S310 (the URL is the operator's parser)
            self.url + path, data=body, headers={"Content-Type": content_type}, method="POST"
        )
        try:
            with urllib.request.urlopen(request, timeout=TIMEOUT_S) as response:  # noqa: S310
                return response.headers["Content-Type"], response.read()
        except urllib.error.HTTPError as exc:
            try:
                error = json.loads(exc.read())
            except ValueError:
                error = None
            raise ParserHttpError(exc.code, error) from None

    def convert(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]:
        return result_part(*self._post("/v1/convert", data, options))

    def inspect(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]:
        return json.loads(self._post("/v1/inspect", data, options)[1])
