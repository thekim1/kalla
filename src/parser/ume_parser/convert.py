"""Conversion inside a slot process (parser design §3, §7 to §9). Imported only in slot processes, never by the API.

`run_job` takes one `/v1/convert` job and dispatches on the format the API process sniffed (a zip is named here, after
the bomb pre-check):

- **PDF**: opened with pypdfium2 (`corrupt`, `encrypted`, page limits); the text-layer fast path when it was asked for
  and every requested page has a clean text layer, otherwise Docling on the range.
- **JPEG, PNG, TIFF**: Docling's image input with OCR always on (`off` is read as `auto`, with a warning). A TIFF range
  is cut out of the file first, so only its frames are decoded, and its pages are renumbered to the original frames.
- **DOCX, PPTX, XLSX, ODT, ODS, ODP**: Docling's declarative backends (no models, no ranges).

It returns the slot's part of `result.json` (the API process adds the request, input and build fields) and writes
`docling.json.gz` into the job directory when asked for.

Docling pipelines for PDF and images are kept per option set (OCR mode, table mode, image scale) in an LRU per slot
(§10.2). Docling's `DocumentConverter` caches one pipeline per hash of its options and never evicts, so each option
set gets its own converter, and the per-request `document_timeout` is set on the pipeline's own copy of its options:
changing the options the converter hashes would build a new pipeline for every deadline. PDF and images share a set's
pipeline (both run Docling's standard PDF pipeline on the same options object).
"""

import gzip
import io
import json
import math
import time
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c
from docling.datamodel.accelerator_options import AcceleratorDevice, AcceleratorOptions
from docling.datamodel.base_models import ConversionStatus, InputFormat
from docling.datamodel.document import ConversionResult
from docling.datamodel.pipeline_options import (
    OcrMode,
    PdfPipelineOptions,
    TableFormerMode,
    TableStructureOptions,
    TesseractCliOcrOptions,
)
from docling.document_converter import DocumentConverter, ImageFormatOption, PdfFormatOption
from docling_core.types.doc import DoclingDocument
from docling_core.types.io import DocumentStream

from ume_parser import limits, signals, sniff, textlayer
from ume_parser.assemble import (
    Assembled,
    area,
    assemble,
    markdown,
    page_entry,
    page_groups,
    place_on_groups,
    truncate_cells,
)
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest, Format

OCR_LANGUAGES = ["swe"]
DEFAULT_IMAGE_SCALE = 2.0
MIN_IMAGE_SCALE = 0.5
PIPELINE_CACHE_SIZE = 3
# Docling stops at deadline - GRACE_S and returns what it has (`partial`); the supervisor kills at deadline + 10 s.
GRACE_S = 10.0
DOCLING_JSON = "docling.json.gz"
_DECLARATIVE_FORMATS = [
    InputFormat.DOCX,
    InputFormat.PPTX,
    InputFormat.XLSX,
    InputFormat.ODT,
    InputFormat.ODS,
    InputFormat.ODP,
]
_STREAM_EXTENSIONS = {"jpeg": "jpg"}


@dataclass(frozen=True)
class OptionSet:
    ocr: str = "auto"
    tables: str = "accurate"
    image_scale: float = DEFAULT_IMAGE_SCALE


DEFAULT_OPTIONS = OptionSet()


def pdf_options(models_dir: Path, threads: int, option_set: OptionSet = DEFAULT_OPTIONS) -> PdfPipelineOptions:
    """Docling options for one option set; the default (§10.2) is OCR `auto` with Tesseract `swe`, tables
    `accurate`, scale 2.0.

    The OCR engine is always set explicitly. Docling's default `OcrAutoOptions` never picks Tesseract (§9.2).
    """
    return PdfPipelineOptions(
        artifacts_path=models_dir,
        accelerator_options=AcceleratorOptions(num_threads=threads, device=AcceleratorDevice.CPU),
        do_ocr=option_set.ocr != "off",
        ocr_options=TesseractCliOcrOptions(
            lang=OCR_LANGUAGES, mode=OcrMode.FULL_PAGE if option_set.ocr == "force" else OcrMode.DEFAULT
        ),
        do_table_structure=option_set.tables != "off",
        table_structure_options=TableStructureOptions(
            mode=TableFormerMode.FAST if option_set.tables == "fast" else TableFormerMode.ACCURATE
        ),
        images_scale=option_set.image_scale,
    )


@dataclass
class Pipeline:
    converter: DocumentConverter
    run_options: PdfPipelineOptions  # the pipeline's own copy: document_timeout is set per request


def build_pipeline(models_dir: Path, threads: int, option_set: OptionSet = DEFAULT_OPTIONS) -> Pipeline:
    options = pdf_options(models_dir, threads, option_set)
    converter = DocumentConverter(
        allowed_formats=[InputFormat.PDF, InputFormat.IMAGE],
        format_options={
            InputFormat.PDF: PdfFormatOption(pipeline_options=options),
            InputFormat.IMAGE: ImageFormatOption(pipeline_options=options),
        },
    )
    converter.initialize_pipeline(InputFormat.PDF)
    (pipeline,) = converter.initialized_pipelines.values()
    pipeline.pipeline_options = pipeline.pipeline_options.model_copy(deep=True)
    return Pipeline(converter, pipeline.pipeline_options)


def build_declarative() -> DocumentConverter:
    """The converter of the declarative formats: Docling's own backends with their default options, no models."""
    return DocumentConverter(allowed_formats=_DECLARATIVE_FORMATS)


class Pipelines:
    """The warm pipelines of a slot: at most `cache_size` PDF/image option sets, least recently used evicted first,
    and the declarative converter, built on first use."""

    def __init__(
        self, models_dir: Path, threads: int, default: Pipeline | None = None, cache_size: int = PIPELINE_CACHE_SIZE
    ) -> None:
        self._models_dir, self._threads, self._cache_size = models_dir, threads, cache_size
        self._cache: OrderedDict[OptionSet, Pipeline] = OrderedDict()
        self._declarative: DocumentConverter | None = None
        if default is not None:
            self._cache[DEFAULT_OPTIONS] = default

    def get(self, option_set: OptionSet) -> Pipeline:
        if option_set in self._cache:
            self._cache.move_to_end(option_set)
            return self._cache[option_set]
        pipeline = build_pipeline(self._models_dir, self._threads, option_set)
        self._cache[option_set] = pipeline
        while len(self._cache) > self._cache_size:
            self._cache.popitem(last=False)
        return pipeline

    def declarative(self) -> DocumentConverter:
        if self._declarative is None:
            self._declarative = build_declarative()
        return self._declarative


# --- Helpers --------------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class PageMeta:
    """What is known of a page before Docling runs: its size in points and, for PDF, its text-layer facts."""

    width: float
    height: float
    text_layer_chars: int | None = None
    image_coverage: float | None = None


def _open(path: Path) -> pdfium.PdfDocument:
    try:
        return pdfium.PdfDocument(path)
    except pdfium.PdfiumError as exc:
        if exc.err_code == pdfium_c.FPDF_ERR_PASSWORD:
            raise ParserError("encrypted") from exc
        raise ParserError("corrupt") from exc


def page_range(options: ConvertRequest, page_count: int) -> tuple[int, int]:
    """The 1-based inclusive range to convert, checked against the page limits (§11.1)."""
    if page_count > limits.MAX_PAGES_PER_DOCUMENT:
        raise ParserError("too_many_pages", retry="none")
    if page_count == 0:
        raise ParserError("corrupt")
    first, last = (options.pages.from_, options.pages.to) if options.pages else (1, page_count)
    if last > page_count:
        raise ParserError("invalid_request")
    if last - first + 1 > limits.MAX_PAGES_PER_REQUEST:
        raise ParserError("too_many_pages", retry="smaller_range")
    return first, last


def render_scale(requested: float, largest_page_area: float) -> float:
    """`requested`, lowered in steps of 0.25 (to `MIN_IMAGE_SCALE`) until the largest page renders within
    `MAX_RENDER_PIXELS` (§11)."""
    if largest_page_area <= 0 or largest_page_area * requested**2 <= limits.MAX_RENDER_PIXELS:
        return requested
    fitting = math.sqrt(limits.MAX_RENDER_PIXELS / largest_page_area)
    return max(MIN_IMAGE_SCALE, math.floor(fitting * 4) / 4)


def _completed(result: ConversionResult, first: int, last: int, offset: int) -> int:
    """The last page of the contiguous run of converted pages from `first`, or `first - 1` when there is none."""
    done = {p.page_no + offset for p in result.pages if p.assembled is not None}
    last_done = first - 1
    while last_done < last and last_done + 1 in done:
        last_done += 1
    return last_done


def _ocr_cells(result: ConversionResult, offset: int) -> dict[int, list[Any]]:
    """OCR'd text cells per page, from the layout clusters (the parsed pages are dropped after conversion)."""
    cells: dict[int, list[Any]] = {}
    for page in result.pages:
        layout = page.predictions.layout
        clusters = layout.clusters if layout is not None else []
        cells[page.page_no + offset] = [c for cluster in clusters for c in cluster.cells if c.from_ocr]
    return cells


def _picture_ocr_chars(pictures: list[dict[str, Any]], ocr: dict[int, list[Any]], doc: DoclingDocument) -> None:
    for picture in pictures:
        box, page_no = picture["bbox"], picture["page"]
        if box is None or page_no not in doc.pages:
            continue
        height = doc.pages[page_no].size.height
        for cell in ocr.get(page_no, []):
            c = cell.to_bounding_box().to_top_left_origin(height)
            x, y = (c.l + c.r) / 2, (c.t + c.b) / 2
            if box[0] <= x <= box[2] and box[1] <= y <= box[3]:
                picture["ocr_chars"] += signals.non_space_chars(cell.text)


def _shift_pages(doc: DoclingDocument, offset: int) -> None:
    """Renumbers a document converted from a cut-out range, so its pages carry their numbers in the original file."""
    if offset == 0:
        return
    for item in [*doc.texts, *doc.tables, *doc.pictures, *doc.key_value_items, *doc.form_items]:
        for prov in item.prov:
            prov.page_no += offset
    doc.pages = {n + offset: page.model_copy(update={"page_no": n + offset}) for n, page in doc.pages.items()}


def _write_docling_json(job: dict[str, Any], doc: DoclingDocument) -> None:
    target = Path(job["dir"]) / DOCLING_JSON
    with target.open("wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as gz:
        gz.write(json.dumps(doc.export_to_dict(), ensure_ascii=False, separators=(",", ":")).encode("utf-8"))


def _stream(job: dict[str, Any], options: ConvertRequest, fmt: Format, source: Path | None = None) -> DocumentStream:
    """The input as Docling reads it, named by request id and sniffed extension only (§13.2)."""
    data = (source or Path(job["input"])).read_bytes()
    return DocumentStream(name=f"{options.request_id}.{_STREAM_EXTENSIONS.get(fmt, fmt)}", stream=io.BytesIO(data))


# --- PDF and images through Docling's standard pipeline -------------------------------------------------------------


def _docling(
    job: dict[str, Any],
    options: ConvertRequest,
    pipelines: Pipelines,
    stream: DocumentStream,
    meta: dict[int, PageMeta],
    *,
    ocr_mode: str,
    image_scale: float,
    ocr_applied: str,
    page_range_arg: tuple[int, int] | None,
    offset: int = 0,
) -> dict[str, Any]:
    """Docling's standard pipeline on the pages of `meta` (consecutive, absolute numbers). `page_range_arg` is passed
    to Docling (PDF); `offset` renumbers a cut-out TIFF range."""
    first, last = min(meta), max(meta)
    pipeline = pipelines.get(OptionSet(ocr_mode, options.tables.mode, image_scale))
    deadline_s = options.deadline_ms / 1000
    pipeline.run_options.document_timeout = max(deadline_s - GRACE_S, deadline_s / 2)

    started = time.monotonic()
    if page_range_arg is None:
        result = pipeline.converter.convert(stream, raises_on_error=False)
    else:
        result = pipeline.converter.convert(stream, raises_on_error=False, page_range=page_range_arg)
    docling_ms = round((time.monotonic() - started) * 1000)

    completed_to = _completed(result, first, last, offset)
    if result.status == ConversionStatus.FAILURE or completed_to < first:
        if result.has_timeout_errors():
            raise ParserError("timeout")
        if any("ocr" in e.module_name.lower() for e in result.errors):
            raise ParserError("ocr_failed")
        raise ParserError("conversion_failed")

    doc = result.document
    _shift_pages(doc, offset)
    assembled = assemble(doc, "layout")
    blocks = [b for b in assembled.blocks if b["page"] is None or b["page"] <= completed_to]
    kept = {b["id"] for b in blocks}
    tables = [t for t in assembled.tables if any(b["table"] == t["id"] for b in blocks)]
    pictures = [p for p in assembled.pictures if p["block"] in kept]
    ocr = _ocr_cells(result, offset)
    _picture_ocr_chars(pictures, ocr, doc)
    pages = []
    for number in range(first, completed_to + 1):
        page_meta = meta[number]
        size = doc.pages[number].size if number in doc.pages else None
        pages.append(
            page_entry(
                number,
                size.width if size else page_meta.width,
                size.height if size else page_meta.height,
                blocks,
                assembled.texts,
                text_layer_chars=page_meta.text_layer_chars,
                image_coverage=page_meta.image_coverage,
                ocr_applied=ocr_applied,
                ocr_confidences=[c.confidence for c in ocr.get(number, [])],
                picture_area=sum(area(p["bbox"]) for p in pictures if p["page"] == number),
            )
        )

    if options.outputs.docling_json:
        _write_docling_json(job, doc)

    partial = completed_to < last
    return {
        "status": "partial" if partial else "success",
        "pipeline": "docling",
        "pages_requested": {"from": first, "to": last},
        "pages_completed": {"from": first, "to": completed_to},
        "pages": pages,
        "blocks": blocks,
        "tables": tables,
        "pictures": pictures,
        "warnings": [{"code": "deadline_partial", "page": completed_to + 1}] if partial else [],
        "docling_ms": docling_ms,
        "docling_json": options.outputs.docling_json,
    }


def _text_layer_pages(text_pages: list[textlayer.TextPage], blocks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    texts = {b["id"]: b["md"].lstrip("#").lstrip() if b["kind"] == "heading" else b["md"] for b in blocks}
    return [
        page_entry(
            p.facts.page,
            p.facts.width,
            p.facts.height,
            blocks,
            texts,
            text_layer_chars=p.facts.text_chars,
            image_coverage=p.facts.image_coverage,
            # The fast path detects no pictures; the page's image objects are its picture area.
            picture_area=p.facts.image_coverage * p.facts.width * p.facts.height,
            ms=p.ms,
        )
        for p in text_pages
    ]


def _run_pdf(job: dict[str, Any], options: ConvertRequest, pipelines: Pipelines) -> dict[str, Any]:
    started = time.monotonic()
    pdf = _open(Path(job["input"]))
    try:
        page_count = len(pdf)
        first, last = page_range(options, page_count)
        text_pages: list[textlayer.TextPage] = []
        blocks: list[dict[str, Any]] = []
        text_layer_ms = 0
        if options.pipeline == "text_layer":
            text_pages, blocks = textlayer.convert(pdf, first, last)
            text_layer_ms = round((time.monotonic() - started) * 1000)
            facts = {p.facts.page: p.facts for p in text_pages}
        else:
            facts = {n: signals.page_facts(pdf[n - 1], n) for n in range(first, last + 1)}
        signals_ms = round((time.monotonic() - started) * 1000) - text_layer_ms
    finally:
        pdf.close()

    rejected = [p.facts.page for p in text_pages if not p.facts.clean]
    if options.pipeline == "text_layer" and not rejected:
        out: dict[str, Any] = {
            "status": "success",
            "pipeline": "text_layer",
            "pages_requested": {"from": first, "to": last},
            "pages_completed": {"from": first, "to": last},
            "pages": _text_layer_pages(text_pages, blocks),
            "blocks": blocks,
            "tables": [],
            "pictures": [],
            "warnings": [],
            "docling_ms": 0,
            "docling_json": False,
        }
    else:
        # A page without a clean text layer goes to Docling with the request's OCR and table options.
        meta = {n: PageMeta(f.width, f.height, f.text_chars, f.image_coverage) for n, f in facts.items()}
        scale = render_scale(options.image_scale, max(f.width * f.height for f in facts.values()))
        out = _docling(
            job,
            options,
            pipelines,
            _stream(job, options, "pdf"),
            meta,
            ocr_mode=options.ocr.mode,
            image_scale=scale,
            ocr_applied="full-page" if options.ocr.mode == "force" else "bitmap",
            page_range_arg=(first, last),
        )
        reduced = [{"code": "render_scale_reduced", "page": None}] if scale < options.image_scale else []
        out["warnings"] = [{"code": "text_layer_rejected", "page": n} for n in rejected] + reduced + out["warnings"]

    out.update(page_count=page_count, text_layer_ms=text_layer_ms, signals_ms=signals_ms)
    return out


def _frame_meta(image: Any) -> PageMeta:
    """A frame's size in points from its pixels and resolution (72 dpi when it has none, as Docling assumes)."""
    dpi = image.info.get("dpi")
    dpi_x, dpi_y = (72.0, 72.0) if dpi in (None, (1, 1)) else (float(dpi[0]) or 72.0, float(dpi[1]) or 72.0)
    return PageMeta(image.width * 72 / dpi_x, image.height * 72 / dpi_y, None, 1.0)


def _cut_frames(image: Any, first: int, last: int, target: Path) -> None:
    """Frames `first`..`last` of a multi-frame TIFF as a TIFF of their own, so Docling decodes only those."""
    frames = []
    for n in range(first - 1, last):
        image.seek(n)
        frames.append(image.copy())
    dpi = frames[0].info.get("dpi")
    extra = {"dpi": dpi} if dpi else {}
    frames[0].save(target, format="TIFF", save_all=True, append_images=frames[1:], compression="tiff_deflate", **extra)


def _run_image(job: dict[str, Any], options: ConvertRequest, pipelines: Pipelines, fmt: Format) -> dict[str, Any]:
    started = time.monotonic()
    warnings: list[dict[str, Any]] = []
    source: Path | None = None
    with limits.open_image(Path(job["input"])) as image:
        page_count = getattr(image, "n_frames", 1)
        first, last = page_range(options, page_count)
        meta: dict[int, PageMeta] = {}
        for n in range(first, last + 1):
            image.seek(n - 1)
            meta[n] = _frame_meta(image)
        if fmt == "tiff" and (first, last) != (1, page_count):
            source = Path(job["dir"]) / "frames.tiff"
            _cut_frames(image, first, last, source)
    signals_ms = round((time.monotonic() - started) * 1000)

    ocr_mode = options.ocr.mode
    if ocr_mode == "off":
        ocr_mode = "auto"
        warnings.append({"code": "ocr_off_ignored", "page": None})  # images are always OCR'd (§9.1)
    scale = render_scale(options.image_scale, max(m.width * m.height for m in meta.values()))
    if scale < options.image_scale:
        warnings.append({"code": "render_scale_reduced", "page": None})
    out = _docling(
        job,
        options,
        pipelines,
        _stream(job, options, fmt, source),
        meta,
        ocr_mode=ocr_mode,
        image_scale=scale,
        ocr_applied="full-page",
        page_range_arg=None,
        offset=first - 1 if source is not None else 0,
    )
    out["warnings"] = warnings + out["warnings"]
    out.update(page_count=page_count, text_layer_ms=0, signals_ms=signals_ms)
    return out


# --- Declarative formats --------------------------------------------------------------------------------------------


def _declarative_pages(fmt: Format, doc: DoclingDocument, out: Assembled) -> list[dict[str, Any]]:
    """DOCX and ODT: one entry without a page. PPTX: one per slide, labelled with its number. ODP: one per visible
    slide. Workbooks: one per visible sheet, labelled with its name."""
    if fmt in ("docx", "odt"):
        return [page_entry(None, None, None, out.blocks, out.texts)]
    if fmt == "pptx":
        return [
            page_entry(n, page.size.width, page.size.height, out.blocks, out.texts, label=str(n))
            for n, page in sorted(doc.pages.items())
        ]
    pages = []
    for group in page_groups(doc):
        if group.hidden:
            continue
        size = doc.pages[group.number].size if group.number in doc.pages else None
        width, height = (size.width, size.height) if size else (None, None)
        label = group.label if group.label is not None else str(group.number)
        pages.append(page_entry(group.number, width, height, out.blocks, out.texts, label=label))
    return pages


def _run_declarative(job: dict[str, Any], options: ConvertRequest, pipelines: Pipelines, fmt: Format) -> dict[str, Any]:
    converter = pipelines.declarative()
    started = time.monotonic()
    result = converter.convert(_stream(job, options, fmt), raises_on_error=False)
    docling_ms = round((time.monotonic() - started) * 1000)
    if result.status not in (ConversionStatus.SUCCESS, ConversionStatus.PARTIAL_SUCCESS):
        raise ParserError("conversion_failed" if result.input.valid else "corrupt")

    doc = result.document
    out = assemble(doc, "style")
    page_count: int | None = None
    if fmt == "pptx":
        page_count = len(doc.pages)
    elif fmt in ("xlsx", "ods", "odp"):
        groups = page_groups(doc)
        page_count = len(groups)
        if fmt == "odp":
            place_on_groups(doc, out, groups)
        hidden = {g.number for g in groups if g.hidden}
        out.blocks = [b for b in out.blocks if b["page"] not in hidden]
        if fmt != "odp":
            truncate_cells(out)
    kept_tables = {b["table"] for b in out.blocks if b["table"]}
    out.tables = [t for t in out.tables if t["id"] in kept_tables]
    kept_blocks = {b["id"] for b in out.blocks}
    out.pictures = [p for p in out.pictures if p["block"] in kept_blocks]
    span = {"from": 1, "to": page_count} if page_count else None

    if options.outputs.docling_json:
        _write_docling_json(job, doc)
    return {
        "status": "success",
        "pipeline": "docling",
        "page_count": page_count,
        "pages_requested": span,
        "pages_completed": span,
        "pages": _declarative_pages(fmt, doc, out),
        "blocks": out.blocks,
        "tables": out.tables,
        "pictures": out.pictures,
        "warnings": out.warnings,
        "docling_ms": docling_ms,
        "text_layer_ms": 0,
        "signals_ms": 0,
        "docling_json": options.outputs.docling_json,
    }


# --- Jobs -----------------------------------------------------------------------------------------------------------


def resolve_format(job: dict[str, Any]) -> sniff.Sniffed:
    """The job's format: the API's sniff or, for a zip, the package type after the bomb pre-check (§11)."""
    if job["format"] is not None:
        return sniff.of(job["format"])
    with limits.open_zip(Path(job["input"])) as archive:
        sniffed = sniff.classify_zip(archive)
        limits.check_zip(archive.infolist())
    return sniffed


def run_job(job: dict[str, Any], pipelines: Pipelines) -> dict[str, Any]:
    """One `/v1/convert` job (`input`: the file, `dir`: the job directory, `format`: the API's sniff, null for a zip,
    `options`); raises `ParserError`."""
    options = ConvertRequest.model_validate(job["options"])
    sniffed = resolve_format(job)
    fmt: Format = sniffed.format  # type: ignore[assignment] (resolve_format names every accepted input)
    if options.pages is not None and fmt not in sniff.RANGEABLE:
        raise ParserError("invalid_request")
    if fmt == "pdf":
        out = _run_pdf(job, options, pipelines)
    elif fmt in sniff.IMAGES:
        out = _run_image(job, options, pipelines, fmt)
    else:
        out = _run_declarative(job, options, pipelines, fmt)
    out.update(
        format=fmt,
        media_type_sniffed=sniffed.media_type,
        markdown=markdown(out["blocks"], [p["page"] for p in out["pages"]]) if options.outputs.markdown else None,
    )
    return out
