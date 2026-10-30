"""Request limits (parser design §11.1), reported by `/v1/info`."""

MAX_BODY_BYTES = 60 * 1024 * 1024
MAX_PAGES_PER_REQUEST = 20
MAX_PAGES_PER_DOCUMENT = 2000
MAX_DEADLINE_MS = 120_000
INSPECT_DEADLINE_MS = 10_000


def as_dict() -> dict[str, int]:
    return {
        "max_body_bytes": MAX_BODY_BYTES,
        "max_pages_per_request": MAX_PAGES_PER_REQUEST,
        "max_pages_per_document": MAX_PAGES_PER_DOCUMENT,
        "max_deadline_ms": MAX_DEADLINE_MS,
        "inspect_deadline_ms": INSPECT_DEADLINE_MS,
    }
