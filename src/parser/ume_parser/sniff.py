"""Format sniffing from the first bytes (parser design §3): the parser trusts its own result, not the declared type.

Runs in the API process on the first `HEAD_BYTES` only; it parses nothing. P0 converts PDF only (`/v1/info`
`formats.supported`): legacy Office (OLE) and RTF are refused as `format_deferred`, everything else as
`unsupported_format` with a reason.
"""

from dataclasses import dataclass

from ume_parser.errors import ParserError
from ume_parser.models import Format

HEAD_BYTES = 1024

_OLE = bytes.fromhex("d0cf11e0a1b11ae1")
_ARCHIVES = (b"7z\xbc\xaf\x27\x1c", b"Rar!\x1a\x07", b"\x1f\x8b", b"BZh", b"\xfd7zXZ\x00")
_EXECUTABLES = (b"MZ", b"\x7fELF", b"\xfe\xed\xfa", b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"#!")
_ZIP = b"PK\x03\x04"
# The first entry of an OOXML package or an ODF file, so a zip that is a document is not called an archive.
_ZIP_DOCUMENT_ENTRIES = (b"[Content_Types].xml", b"mimetype", b"_rels/", b"docProps/", b"word/", b"xl/", b"ppt/")
_ZIP_MACRO_MARKERS = (b"vbaProject.bin",)


@dataclass(frozen=True)
class Sniffed:
    format: Format
    media_type: str


def _zip_first_entry(head: bytes) -> bytes:
    """The name of the first local file header (offset 26: name length, offset 30: name)."""
    if len(head) < 30:
        return b""
    length = int.from_bytes(head[26:28], "little")
    return head[30 : 30 + length]


def sniff(head: bytes) -> Sniffed:
    """The format of a file from its first bytes, or `ParserError` when this build does not convert it."""
    if b"%PDF-" in head[:HEAD_BYTES]:
        return Sniffed("pdf", "application/pdf")
    if head.startswith(_OLE) or head.startswith(b"{\\rtf"):
        raise ParserError("format_deferred")
    if head.startswith(_ZIP):
        if any(marker in head for marker in _ZIP_MACRO_MARKERS):
            raise ParserError("unsupported_format", reason="macro_enabled")
        entry = _zip_first_entry(head)
        is_document = any(entry.startswith(name) for name in _ZIP_DOCUMENT_ENTRIES)
        raise ParserError("unsupported_format", reason="unknown" if is_document else "archive")
    if head.startswith(_ARCHIVES) or head[257:262] == b"ustar":
        raise ParserError("unsupported_format", reason="archive")
    if head.startswith(_EXECUTABLES):
        raise ParserError("unsupported_format", reason="executable")
    raise ParserError("unsupported_format", reason="unknown")
