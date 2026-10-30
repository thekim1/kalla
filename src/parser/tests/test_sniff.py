import pytest

from helpers import fixture_bytes
from ume_parser.errors import ParserError
from ume_parser.sniff import sniff


def _zip(first_entry: bytes, extra: bytes = b"") -> bytes:
    header = b"PK\x03\x04" + bytes(22) + len(first_entry).to_bytes(2, "little") + bytes(2)
    return header + first_entry + extra


def test_pdf_is_sniffed_also_after_leading_bytes():
    assert sniff(fixture_bytes("text-layer.pdf")).format == "pdf"
    assert sniff(b"\x00" * 100 + b"%PDF-1.7\n").media_type == "application/pdf"


@pytest.mark.parametrize(
    ("head", "code", "reason"),
    [
        (fixture_bytes("legacy.doc"), "format_deferred", None),
        (b"{\\rtf1\\ansi ...", "format_deferred", None),
        (_zip(b"[Content_Types].xml"), "unsupported_format", "unknown"),
        (_zip(b"mimetype", b"application/vnd.oasis.opendocument.text"), "unsupported_format", "unknown"),
        (_zip(b"[Content_Types].xml", b"word/vbaProject.bin"), "unsupported_format", "macro_enabled"),
        (_zip(b"photos/a.jpg"), "unsupported_format", "archive"),
        (b"7z\xbc\xaf\x27\x1c\x00\x04", "unsupported_format", "archive"),
        (b"\x1f\x8b\x08\x00", "unsupported_format", "archive"),
        (b"MZ\x90\x00", "unsupported_format", "executable"),
        (b"\x7fELF\x02\x01", "unsupported_format", "executable"),
        (b"\x89PNG\r\n\x1a\n", "unsupported_format", "unknown"),
        (b"hello", "unsupported_format", "unknown"),
    ],
)
def test_refusals(head, code, reason):
    with pytest.raises(ParserError) as raised:
        sniff(head)
    assert raised.value.code == code
    assert raised.value.reason == reason
