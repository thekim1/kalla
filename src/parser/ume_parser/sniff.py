"""Format sniffing (parser design §3): the parser trusts its own result, not the declared type.

Two steps. `sniff` runs in the API process on the first `HEAD_BYTES` and parses nothing: it names PDF, JPEG, PNG and
TIFF, refuses RTF, e-mail, archives, executables and unknown inputs, and leaves an OLE compound file or a zip for the
next step. `sniff_ole` (API process) scans an OLE file for the `EncryptedPackage` stream name, which tells a
password-protected OOXML package (`encrypted`) from a legacy Office or `.msg` file (`format_deferred`); it searches
bytes and reads no OLE structure. `classify_zip` reads a zip's central directory and runs in a slot or the inspect
process, never in the API process (a hostile directory can be large): it names DOCX, PPTX, XLSX, ODT, ODS and ODP and
refuses macro-enabled OOXML and every other zip.

Legacy Office (OLE), RTF and e-mail are P2 (Z1) and refused as `format_deferred`.
"""

import mmap
import re
import zipfile
from dataclasses import dataclass
from pathlib import Path

from ume_parser.errors import ParserError
from ume_parser.models import Format

HEAD_BYTES = 1024

_OLE = bytes.fromhex("d0cf11e0a1b11ae1")
_ARCHIVES = (b"7z\xbc\xaf\x27\x1c", b"Rar!\x1a\x07", b"\x1f\x8b", b"BZh", b"\xfd7zXZ\x00")
_EXECUTABLES = (b"MZ", b"\x7fELF", b"\xfe\xed\xfa", b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"#!")
_ZIP = b"PK\x03\x04"
_JPEG = b"\xff\xd8\xff"
_PNG = b"\x89PNG\r\n\x1a\n"
_TIFF = (b"II*\x00", b"MM\x00*")
# An RFC 5322 message starts with header fields; these are the ones mail clients and servers write first.
_EMAIL_HEADERS = re.compile(
    rb"^(Return-Path|Received|From|To|Subject|Date|Message-ID|MIME-Version|Delivered-To|X-[A-Za-z-]+):[ \t]",
    re.IGNORECASE,
)
# The OLE directory stores stream names in UTF-16LE.
_ENCRYPTED_PACKAGE = "EncryptedPackage".encode("utf-16-le")

MEDIA_TYPES: dict[Format, str] = {
    "pdf": "application/pdf",
    "docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    "pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "odt": "application/vnd.oasis.opendocument.text",
    "ods": "application/vnd.oasis.opendocument.spreadsheet",
    "odp": "application/vnd.oasis.opendocument.presentation",
    "jpeg": "image/jpeg",
    "png": "image/png",
    "tiff": "image/tiff",
}
EXTENSIONS: dict[Format, set[str]] = {f: {f} for f in MEDIA_TYPES} | {"jpeg": {"jpeg", "jpg"}, "tiff": {"tiff", "tif"}}
# Formats whose pages can be requested as ranges (§8): PDF pages and TIFF frames.
RANGEABLE: frozenset[Format] = frozenset({"pdf", "tiff"})
DECLARATIVE: frozenset[Format] = frozenset({"docx", "pptx", "xlsx", "odt", "ods", "odp"})
IMAGES: frozenset[Format] = frozenset({"jpeg", "png", "tiff"})

_OOXML_MAIN: dict[str, Format] = {
    "word/document.xml": "docx",
    "ppt/presentation.xml": "pptx",
    "xl/workbook.xml": "xlsx",
}
_ODF_MIMETYPES: dict[str, Format] = {MEDIA_TYPES[f]: f for f in ("odt", "ods", "odp")}
_MACRO_ENTRIES = re.compile(r"(^|/)(vbaProject\.bin|vbaData\.xml)$", re.IGNORECASE)
_MIMETYPE_MAX_BYTES = 128


@dataclass(frozen=True)
class Sniffed:
    """`format` is None for a zip: `classify_zip` names it once a slot has the file."""

    format: Format | None
    media_type: str | None


ZIP = Sniffed(None, None)


def of(fmt: Format) -> Sniffed:
    return Sniffed(fmt, MEDIA_TYPES[fmt])


def sniff(head: bytes) -> Sniffed:
    """The format of a file from its first bytes, or `ParserError` when they alone refuse it."""
    if b"%PDF-" in head[:HEAD_BYTES]:
        return of("pdf")
    if head.startswith(_JPEG):
        return of("jpeg")
    if head.startswith(_PNG):
        return of("png")
    if head.startswith(_TIFF):
        return of("tiff")
    if head.startswith(_OLE) or head.startswith(b"{\\rtf") or _EMAIL_HEADERS.match(head):
        raise ParserError("format_deferred")
    if head.startswith(_ZIP):
        return ZIP
    if head.startswith(_ARCHIVES) or head[257:262] == b"ustar":
        raise ParserError("unsupported_format", reason="archive")
    if head.startswith(_EXECUTABLES):
        raise ParserError("unsupported_format", reason="executable")
    raise ParserError("unsupported_format", reason="unknown")


def sniff_file(path: Path, head: bytes) -> Sniffed:
    """`sniff`, with an OLE file told apart by `sniff_ole` first."""
    if head.startswith(_OLE):
        sniff_ole(path)
    return sniff(head)


def sniff_ole(path: Path) -> None:
    """Raises `encrypted` for a password-protected OOXML package, else `format_deferred` (legacy Office, `.msg`)."""
    with path.open("rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as data:
        if data.find(_ENCRYPTED_PACKAGE) >= 0:
            raise ParserError("encrypted")
    raise ParserError("format_deferred")


def classify_zip(archive: zipfile.ZipFile) -> Sniffed:
    """The document format of an open zip from its entry names (and an ODF file's `mimetype`), or a refusal."""
    names = archive.namelist()
    if any(_MACRO_ENTRIES.search(name) for name in names):
        raise ParserError("unsupported_format", reason="macro_enabled")
    present = set(names)
    if "[Content_Types].xml" in present:
        for main, fmt in _OOXML_MAIN.items():
            if main in present:
                return of(fmt)
        raise ParserError("unsupported_format", reason="unknown")
    if "mimetype" in present:
        info = archive.getinfo("mimetype")
        if info.file_size <= _MIMETYPE_MAX_BYTES:
            mimetype = archive.read(info).decode("ascii", errors="replace").strip()
            if mimetype in _ODF_MIMETYPES:
                return of(_ODF_MIMETYPES[mimetype])
        raise ParserError("unsupported_format", reason="unknown")
    raise ParserError("unsupported_format", reason="archive")


def declared_mismatch(sniffed: Format, file_ext: str | None, media_type: str | None) -> bool:
    """True when the worker's declared extension or media type differs from the sniffed format (a warning only)."""
    if file_ext is not None and file_ext.removeprefix(".") not in EXTENSIONS[sniffed]:
        return True
    return media_type is not None and media_type != MEDIA_TYPES[sniffed]
