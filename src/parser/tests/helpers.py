"""Shared test helpers: the fixtures of tests/fixtures/parser/ and multipart helpers."""

import json
from email import message_from_bytes, policy
from email.message import EmailMessage
from pathlib import Path

from scripts import make_fixtures

# The repository root; in the image, which has no repository, a path where nothing is found. The committed fixtures
# and contracts are compared only outside the image; inside it the tests generate the fixtures.
REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent
REPO_FIXTURES = REPO_ROOT / "tests" / "fixtures" / "parser"


def fixture_bytes(name: str) -> bytes:
    """A fixture of tests/fixtures/parser/, generated: `scripts/make_fixtures.py` writes the same bytes."""
    return make_fixtures.FIXTURES[name]()


def options(request_id: str, **overrides) -> str:
    return json.dumps({"request_id": request_id, "file_ext": ".pdf", **overrides})


def parts(content_type: str, body: bytes) -> list[EmailMessage]:
    """The parts of a `multipart/mixed` response."""
    head = b"Content-Type: " + content_type.encode() + b"\r\n\r\n"
    message = message_from_bytes(head + body, policy=policy.HTTP)
    return list(message.iter_parts())
