"""`inspect-0`: cheap facts and per-page signals of a document, no models (parser design §6.2).

One spawned process with its own lane, so an inspect never waits behind a conversion, under an address-space limit
(`RLIMIT_AS`, `ADDRESS_SPACE_BYTES`) and a 10 s deadline the supervisor enforces. `inspect` reads a PDF with pypdfium2
(no rendering), an image's frames with Pillow (no pixels decoded) and an OOXML or ODF package's directory and its
small index part. It answers with the `InspectResult` fields the slot knows; a document that convert would refuse
while its format is known gets `refusal` (the error code), and an input whose format cannot be named is an error.
"""

import logging
import mmap
import re
import time
import zipfile
from multiprocessing.connection import Connection
from pathlib import Path
from typing import Any

from ume_parser import limits, signals, slot, sniff
from ume_parser.errors import ParserError
from ume_parser.models import Format

log = logging.getLogger(__name__)

ADDRESS_SPACE_BYTES = 1024 * 1024 * 1024
_PPTX_SLIDE = re.compile(r"^ppt/slides/slide\d+\.xml$")
_INDEX_PART_MAX_BYTES = 16 * 1024 * 1024
# Raw markers of PDF JavaScript, besides the document-level actions pdfium counts (an `/OpenAction` or an annotation
# action is not in the names tree). Only reported, never executed (pdfium is built without V8).
_JAVASCRIPT_MARKERS = (b"/JavaScript", b"/JS ", b"/JS(", b"/JS<", b"/JS\n", b"/JS\r")


def _empty(fmt: Format, media_type: str) -> dict[str, Any]:
    return {
        "format": fmt,
        "media_type_sniffed": media_type,
        "page_count": None,
        "rangeable": fmt in sniff.RANGEABLE,
        "encrypted": None,
        "has_javascript": None,
        "has_embedded_files": None,
        "has_xfa": None,
        "pages": [],
        "refusal": None,
    }


def _raw_javascript(path: Path) -> bool:
    with path.open("rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as data:
        return any(data.find(marker) >= 0 for marker in _JAVASCRIPT_MARKERS)


def _pdf(path: Path, out: dict[str, Any]) -> None:
    import pypdfium2 as pdfium
    import pypdfium2.raw as pdfium_c

    out["has_javascript"] = _raw_javascript(path)
    try:
        pdf = pdfium.PdfDocument(path)
    except pdfium.PdfiumError as exc:
        if exc.err_code == pdfium_c.FPDF_ERR_PASSWORD:
            out.update(encrypted="user_password", refusal="encrypted")
        else:
            out["refusal"] = "corrupt"
        return
    try:
        revision = pdfium_c.FPDF_GetSecurityHandlerRevision(pdf)
        out["encrypted"] = "none" if revision < 0 else "owner_only"
        out["has_javascript"] = out["has_javascript"] or pdfium_c.FPDFDoc_GetJavaScriptActionCount(pdf) > 0
        out["has_embedded_files"] = pdfium_c.FPDFDoc_GetAttachmentCount(pdf) > 0
        out["has_xfa"] = pdfium_c.FPDF_GetFormType(pdf) in (
            pdfium_c.FORMTYPE_XFA_FULL,
            pdfium_c.FORMTYPE_XFA_FOREGROUND,
        )
        out["page_count"] = page_count = len(pdf)
        if page_count == 0:
            out["refusal"] = "corrupt"
            return
        if page_count > limits.MAX_PAGES_PER_DOCUMENT:
            out["refusal"] = "too_many_pages"
            return
        for n in range(1, page_count + 1):
            page = pdf[n - 1]
            try:
                facts = signals.page_facts(page, n)
            finally:
                page.close()
            out["pages"].append(
                {
                    "page": n,
                    "width": facts.width,
                    "height": facts.height,
                    "rotation": facts.rotation,
                    "text_chars": facts.text_chars,
                    "bad_chars": facts.bad_chars,
                    "garbled_score": facts.garbled_score,
                    "image_coverage": facts.image_coverage,
                    "path_objects": facts.path_objects,
                    "dpi": None,
                }
            )
    finally:
        pdf.close()


def _image(path: Path, out: dict[str, Any]) -> None:
    try:
        image = limits.open_image(path)
    except ParserError as exc:
        out["refusal"] = exc.code
        return
    with image:
        out["page_count"] = getattr(image, "n_frames", 1)
        for n in range(1, out["page_count"] + 1):
            image.seek(n - 1)
            dpi = image.info.get("dpi")
            dpi_x, dpi_y = (72.0, 72.0) if dpi in (None, (1, 1)) else (float(dpi[0]) or 72.0, float(dpi[1]) or 72.0)
            out["pages"].append(
                {
                    "page": n,
                    "width": round(image.width * 72 / dpi_x, 2),
                    "height": round(image.height * 72 / dpi_y, 2),
                    "rotation": None,
                    "text_chars": None,
                    "bad_chars": None,
                    "garbled_score": None,
                    "image_coverage": None,
                    "path_objects": None,
                    "dpi": round(dpi_x, 2),
                }
            )


def _count_elements(archive: zipfile.ZipFile, part: str, parent: str, child: str) -> int | None:
    """Children named `child` of the first `parent` element in an XML part, read as a stream (no entities)."""
    from defusedxml.ElementTree import iterparse

    try:
        info = archive.getinfo(part)
    except KeyError:
        return None
    count, depth, parent_depth = 0, 0, None
    with archive.open(info) as stream:
        for event, element in iterparse(stream, events=("start", "end")):
            if event == "start":
                depth += 1
                if parent_depth is None and element.tag == parent:
                    parent_depth = depth
                elif parent_depth is not None and depth == parent_depth + 1 and element.tag == child:
                    count += 1
            else:
                depth -= 1
                if parent_depth is not None and depth < parent_depth:
                    break
                element.clear()
    return count


_SHEET_NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
_OFFICE_NS = "{urn:oasis:names:tc:opendocument:xmlns:office:1.0}"
_TABLE_NS = "{urn:oasis:names:tc:opendocument:xmlns:table:1.0}"
_DRAW_NS = "{urn:oasis:names:tc:opendocument:xmlns:drawing:1.0}"


def _package_pages(archive: zipfile.ZipFile, fmt: Format) -> int | None:
    """Slides (PPTX, ODP) or sheets (XLSX, ODS); None for DOCX and ODT, which have no pages before layout."""
    if fmt == "pptx":
        return sum(1 for name in archive.namelist() if _PPTX_SLIDE.match(name))
    if fmt == "xlsx":
        if archive.getinfo("xl/workbook.xml").file_size > _INDEX_PART_MAX_BYTES:
            raise ParserError("limit_exceeded", limit="index_part_bytes")
        return _count_elements(archive, "xl/workbook.xml", f"{_SHEET_NS}sheets", f"{_SHEET_NS}sheet")
    if fmt == "ods":
        return _count_elements(archive, "content.xml", f"{_OFFICE_NS}spreadsheet", f"{_TABLE_NS}table")
    if fmt == "odp":
        return _count_elements(archive, "content.xml", f"{_OFFICE_NS}presentation", f"{_DRAW_NS}page")
    return None


def inspect(job: dict[str, Any]) -> dict[str, Any]:
    """The slot's part of an `InspectResult` for `job` (`input`, `format`: the API's sniff, null for a zip)."""
    started = time.monotonic()
    path = Path(job["input"])
    if job["format"] is None:
        with limits.open_zip(path) as archive:
            sniffed = sniff.classify_zip(archive)
            fmt: Format = sniffed.format  # type: ignore[assignment]
            out = _empty(fmt, sniffed.media_type or "")
            out["encrypted"] = "none"  # an encrypted OOXML package is an OLE file, refused before this
            try:
                limits.check_zip(archive.infolist())
                out["page_count"] = _package_pages(archive, fmt)
            except ParserError as exc:
                out["refusal"] = exc.code
            except (zipfile.BadZipFile, OSError, SyntaxError, ValueError, EOFError):
                out["refusal"] = "corrupt"
    else:
        fmt = job["format"]
        out = _empty(fmt, sniff.MEDIA_TYPES[fmt])
        if fmt == "pdf":
            _pdf(path, out)
        else:
            _image(path, out)
    out["inspect_ms"] = round((time.monotonic() - started) * 1000)
    return out


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """`inspect-0`: no warm-up beyond its imports."""
    slot.prepare(f"inspect-{slot_id}", 1, address_space_bytes=ADDRESS_SPACE_BYTES)
    conn.send(("ready", {"warmup_ms": 0, "problems": []}))
    slot.serve(conn, {"inspect": inspect})
