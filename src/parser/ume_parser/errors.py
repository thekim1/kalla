"""Error codes of the contract (parser design §7.5): HTTP status, default `retry` and the fixed `detail` sentence.

`detail` is fixed per code and never a library message, because those can quote document content. A slot reports
an error over its pipe as `ParserError.to_wire()`; the API process turns it back into a `ParserError` and answers
with `ErrorResponse`.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:  # models imports limits, which raises ParserError
    from ume_parser.models import ErrorCode, Retry, UnsupportedReason


@dataclass(frozen=True)
class Spec:
    status: int
    retry: Retry
    detail: str


SPECS: dict[ErrorCode, Spec] = {
    "invalid_request": Spec(400, "none", "The request does not match the convert-request schema."),
    "duplicate_in_flight": Spec(409, "later", "A request with this request_id is already converting."),
    "too_large": Spec(413, "none", "The request body exceeds the size limit."),
    "format_deferred": Spec(415, "none", "This format is not supported yet (email)."),
    "unsupported_format": Spec(415, "none", "This format is not supported."),
    "encrypted": Spec(422, "none", "The document needs a password to open."),
    "corrupt": Spec(422, "none", "The document cannot be opened."),
    "too_many_pages": Spec(422, "none", "The document or the requested range has too many pages."),
    "limit_exceeded": Spec(422, "none", "The document exceeds a safety limit."),
    "ocr_failed": Spec(422, "same", "Text recognition failed."),
    "conversion_failed": Spec(422, "same", "The conversion failed."),
    "timeout": Spec(422, "smaller_range", "The deadline passed before any page was converted."),
    "worker_crashed": Spec(500, "smaller_range", "The conversion process stopped unexpectedly."),
    "resource_limit": Spec(507, "smaller_range", "The conversion used more memory than allowed."),
    "busy": Spec(503, "later", "Every conversion slot is busy."),
    "not_ready": Spec(503, "later", "The parser has no warm process for this request yet."),
    "not_implemented": Spec(501, "none", "This endpoint is not available in this build."),
}


class ParserError(Exception):
    def __init__(
        self,
        code: ErrorCode,
        *,
        retry: Retry | None = None,
        limit: str | None = None,
        reason: UnsupportedReason | None = None,
        retry_after_s: int | None = None,
    ) -> None:
        super().__init__(code)
        self.code: ErrorCode = code
        self.spec = SPECS[code]
        self.retry: Retry = retry or self.spec.retry
        self.limit = limit
        self.reason = reason
        self.retry_after_s = retry_after_s

    def to_wire(self) -> dict[str, Any]:
        return {"code": self.code, "retry": self.retry, "limit": self.limit, "reason": self.reason}

    @classmethod
    def from_wire(cls, wire: dict[str, Any]) -> ParserError:
        return cls(wire["code"], retry=wire["retry"], limit=wire["limit"], reason=wire["reason"])
