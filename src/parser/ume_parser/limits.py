"""Request and format limits (parser design §7.4, §7.8, §11, §11.1), reported by `/v1/info`.

`check_zip` is the bomb pre-check for OOXML and ODF packages on the central directory, run before any library opens
the package. `open_image` opens a JPEG, PNG or TIFF lazily with Pillow's pixel limit as a hard error and checks the TIFF
frame count. A breach raises `limit_exceeded` with the limit's name in `limit` (the worker logs security event 5104
with it).
"""

import posixpath
import warnings
import zipfile
from pathlib import Path
from typing import Any

from ume_parser.errors import ParserError

MAX_BODY_BYTES = 60 * 1024 * 1024
MAX_PAGES_PER_REQUEST = 20
MAX_PAGES_PER_DOCUMENT = 2000
MAX_DEADLINE_MS = 120_000
INSPECT_DEADLINE_MS = 10_000

MAX_ZIP_ENTRIES = 10_000
MAX_ZIP_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
MAX_ZIP_RATIO = 200
# Entries below this size are not ratio-checked: a few kilobytes of repeated XML compress far beyond 200:1.
ZIP_RATIO_MIN_BYTES = 1024 * 1024
MAX_IMAGE_PIXELS = 100_000_000
MAX_TIFF_FRAMES = 2000
MAX_RENDER_PIXELS = 60_000_000
MAX_SPREADSHEET_CELLS = 200_000

NER_MAX_TEXTS = 64
NER_MAX_TEXT_CHARS = 4000
NER_MAX_REQUEST_CHARS = 100_000
NER_DEADLINE_MS = 15_000

# Zips allowed inside a package: embedded Office objects, which P1 never opens.
_EMBEDDED_ZIP_DIRS = ("word/embeddings/", "ppt/embeddings/", "xl/embeddings/")
_NESTED_ZIP_SUFFIXES = (".zip", ".jar", ".7z", ".rar", ".gz", ".tar")


def as_dict() -> dict[str, int]:
    return {
        "max_body_bytes": MAX_BODY_BYTES,
        "max_pages_per_request": MAX_PAGES_PER_REQUEST,
        "max_pages_per_document": MAX_PAGES_PER_DOCUMENT,
        "max_deadline_ms": MAX_DEADLINE_MS,
        "inspect_deadline_ms": INSPECT_DEADLINE_MS,
        "max_zip_entries": MAX_ZIP_ENTRIES,
        "max_zip_uncompressed_bytes": MAX_ZIP_UNCOMPRESSED_BYTES,
        "max_zip_ratio": MAX_ZIP_RATIO,
        "max_image_pixels": MAX_IMAGE_PIXELS,
        "max_tiff_frames": MAX_TIFF_FRAMES,
        "max_render_pixels": MAX_RENDER_PIXELS,
        "max_spreadsheet_cells": MAX_SPREADSHEET_CELLS,
        "ner_max_texts": NER_MAX_TEXTS,
        "ner_max_text_chars": NER_MAX_TEXT_CHARS,
        "ner_max_request_chars": NER_MAX_REQUEST_CHARS,
        "ner_deadline_ms": NER_DEADLINE_MS,
    }


def _unsafe_name(name: str) -> bool:
    if name.startswith(("/", "\\")) or "\\" in name or (len(name) > 1 and name[1] == ":"):
        return True
    return ".." in posixpath.normpath(name).split("/")


def _nested_zip(name: str) -> bool:
    lower = name.lower()
    return lower.endswith(_NESTED_ZIP_SUFFIXES) and not lower.startswith(_EMBEDDED_ZIP_DIRS)


def check_zip(entries: list[zipfile.ZipInfo]) -> None:
    """The bomb pre-check of §11 on a package's directory entries; raises `limit_exceeded`."""
    if len(entries) > MAX_ZIP_ENTRIES:
        raise ParserError("limit_exceeded", limit="zip_entries")
    total = 0
    for entry in entries:
        if _unsafe_name(entry.filename):
            raise ParserError("limit_exceeded", limit="zip_entry_name")
        if _nested_zip(entry.filename):
            raise ParserError("limit_exceeded", limit="zip_nested")
        total += entry.file_size
        if total > MAX_ZIP_UNCOMPRESSED_BYTES:
            raise ParserError("limit_exceeded", limit="zip_uncompressed_bytes")
        if entry.file_size >= ZIP_RATIO_MIN_BYTES and entry.file_size > MAX_ZIP_RATIO * max(entry.compress_size, 1):
            raise ParserError("limit_exceeded", limit="zip_ratio")


def open_zip(path: Path) -> zipfile.ZipFile:
    """The zip at `path` with its central directory read (no entry decompressed); `corrupt` when it is none."""
    try:
        return zipfile.ZipFile(path)
    except (zipfile.BadZipFile, OSError, ValueError, EOFError):
        raise ParserError("corrupt") from None


def open_image(path: Path) -> Any:
    """The image at `path` opened by Pillow without decoding pixels, its size and TIFF frame count checked.

    Pillow's `MAX_IMAGE_PIXELS` is set to the limit and its warning made an error, so the limit is hard; past twice
    the limit Pillow raises `DecompressionBombError` itself, mapped the same way.
    """
    from PIL import Image, UnidentifiedImageError

    Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            image = Image.open(path)
    except (Image.DecompressionBombError, Image.DecompressionBombWarning):
        raise ParserError("limit_exceeded", limit="image_pixels") from None
    except (UnidentifiedImageError, OSError, ValueError, SyntaxError):
        raise ParserError("corrupt") from None
    try:
        if image.width * image.height > MAX_IMAGE_PIXELS:
            raise ParserError("limit_exceeded", limit="image_pixels")
        try:
            frames = getattr(image, "n_frames", 1)
        except (OSError, ValueError, EOFError):
            raise ParserError("corrupt") from None
        if frames > MAX_TIFF_FRAMES:
            raise ParserError("limit_exceeded", limit="tiff_frames")
    except BaseException:
        image.close()
        raise
    return image
