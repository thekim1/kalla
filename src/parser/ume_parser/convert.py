"""Conversion inside a slot process (parser design §7 to §9). Imported only in slot processes, never by the API process.

`run_job` takes one `/v1/convert` job: it opens the PDF with pypdfium2 (`corrupt`, `encrypted`, page limits), takes
the text-layer fast path when it was asked for and every requested page has a clean text layer, and otherwise
converts the range with Docling. It returns the slot's part of `result.json` (the API process adds the request,
input and build fields) and writes `docling.json.gz` into the job directory when asked for.

Docling pipelines are kept per option set (OCR mode, table mode, image scale) in an LRU of three per slot (§10.2).
Docling's `DocumentConverter` caches one pipeline per hash of its options and never evicts, so each option set gets
its own converter, and the per-request `document_timeout` is set on the pipeline's own copy of its options: changing
the options the converter hashes would build a new pipeline for every deadline.
"""

import gzip
import io
import json
import statistics
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
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling_core.types.doc import ContentLayer, DocItemLabel, DoclingDocument, ListItem, SectionHeaderItem, TableItem
from docling_core.types.io import DocumentStream

from ume_parser import limits, signals, textlayer
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest

OCR_LANGUAGES = ["swe"]
DEFAULT_IMAGE_SCALE = 2.0
PIPELINE_CACHE_SIZE = 3
# Docling stops at deadline - GRACE_S and returns what it has (`partial`); the supervisor kills at deadline + 10 s.
GRACE_S = 10.0
DOCLING_JSON = "docling.json.gz"


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
    converter = DocumentConverter(
        allowed_formats=[InputFormat.PDF],
        format_options={
            InputFormat.PDF: PdfFormatOption(pipeline_options=pdf_options(models_dir, threads, option_set))
        },
    )
    converter.initialize_pipeline(InputFormat.PDF)
    (pipeline,) = converter.initialized_pipelines.values()
    pipeline.pipeline_options = pipeline.pipeline_options.model_copy(deep=True)
    return Pipeline(converter, pipeline.pipeline_options)


class Pipelines:
    """The warm pipelines of a slot, at most `PIPELINE_CACHE_SIZE`, least recently used evicted first."""

    def __init__(self, models_dir: Path, threads: int, default: Pipeline | None = None) -> None:
        self._models_dir, self._threads = models_dir, threads
        self._cache: OrderedDict[OptionSet, Pipeline] = OrderedDict()
        if default is not None:
            self._cache[DEFAULT_OPTIONS] = default

    def get(self, option_set: OptionSet) -> Pipeline:
        if option_set in self._cache:
            self._cache.move_to_end(option_set)
            return self._cache[option_set]
        pipeline = build_pipeline(self._models_dir, self._threads, option_set)
        self._cache[option_set] = pipeline
        if len(self._cache) > PIPELINE_CACHE_SIZE:
            self._cache.popitem(last=False)
        return pipeline


# --- Docling document → contract ------------------------------------------------------------------------------------

_KINDS = {
    DocItemLabel.TITLE: "title",
    DocItemLabel.SECTION_HEADER: "heading",
    DocItemLabel.LIST_ITEM: "list_item",
    DocItemLabel.TABLE: "table",
    DocItemLabel.PICTURE: "picture",
    DocItemLabel.CHART: "picture",
    DocItemLabel.CAPTION: "caption",
    DocItemLabel.CODE: "code",
    DocItemLabel.FORMULA: "formula",
    DocItemLabel.FOOTNOTE: "footnote",
    DocItemLabel.PAGE_HEADER: "page_header",
    DocItemLabel.PAGE_FOOTER: "page_footer",
}
_LAYERS = {ContentLayer.BODY, ContentLayer.FURNITURE}


@dataclass
class Assembled:
    blocks: list[dict[str, Any]]
    texts: dict[str, str]  # block id → plain text, for character counts and the garble score
    tables: list[dict[str, Any]]
    pictures: list[dict[str, Any]]


def _bbox(doc: DoclingDocument, prov: Any) -> list[float] | None:
    page = doc.pages.get(prov.page_no)
    if page is None:
        return None
    box = prov.bbox.to_top_left_origin(page.size.height)
    return [round(box.l, 1), round(box.t, 1), round(box.r, 1), round(box.b, 1)]


def _table(item: TableItem, doc: DoclingDocument, table_id: str) -> dict[str, Any]:
    cells = sorted(item.data.table_cells, key=lambda c: (c.start_row_offset_idx, c.start_col_offset_idx))
    header_rows = 0
    for r in range(item.data.num_rows):
        if not any(c.column_header for c in cells if c.start_row_offset_idx == r):
            break
        header_rows += 1
    return {
        "id": table_id,
        "rows": item.data.num_rows,
        "cols": item.data.num_cols,
        "header_rows": header_rows,
        "caption": item.caption_text(doc) or None,
        "cells": [
            {
                "r": c.start_row_offset_idx,
                "c": c.start_col_offset_idx,
                "rs": c.row_span,
                "cs": c.col_span,
                "header": c.column_header or c.row_header,
                "text": c.text,
            }
            for c in cells
        ],
    }


def assemble(doc: DoclingDocument) -> Assembled:
    """Blocks, tables and pictures of a converted document, in Docling's reading order (§7.3)."""
    out = Assembled([], {}, [], [])
    for item, _level in doc.iterate_items(included_content_layers=_LAYERS):
        label = getattr(item, "label", None)
        if label is None:
            continue
        kind = _KINDS.get(label, "paragraph")
        block_id = f"b{len(out.blocks)}"
        text = getattr(item, "text", "") or ""
        level, level_source, table_id = None, None, None
        md = text
        if isinstance(item, SectionHeaderItem):
            level, level_source = item.level, "layout"
            md = "#" * level + " " + text
        elif kind == "title":
            md = "# " + text
        elif isinstance(item, ListItem):
            md = f"{item.marker if item.enumerated and item.marker else '-'} {text}"
        elif isinstance(item, TableItem):
            table_id = f"t{len(out.tables)}"
            table = _table(item, doc, table_id)
            out.tables.append(table)
            md = item.export_to_markdown(doc)
            text = "\n".join(c["text"] for c in table["cells"])
        elif kind == "picture":
            # Picture children (captions, text inside the figure) are not traversed: the caption goes here.
            md = text = item.caption_text(doc) or ""
        elif kind == "code":
            md = f"```\n{text}\n```"
        provs = getattr(item, "prov", None) or []
        bbox = _bbox(doc, provs[0]) if provs else None
        page = provs[0].page_no if provs else None
        if kind == "picture":
            picture_id = f"p{len(out.pictures)}"
            out.pictures.append(
                {"id": picture_id, "block": block_id, "page": page, "bbox": bbox, "caption": md or None, "ocr_chars": 0}
            )
        out.blocks.append(
            {
                "id": block_id,
                "kind": kind,
                "level": level,
                "level_source": level_source,
                "page": page,
                "page_end": provs[-1].page_no if provs else None,
                "bbox": bbox,
                "layer": "furniture" if item.content_layer == ContentLayer.FURNITURE else "body",
                "md": md,
                "ref": item.self_ref,
                "table": table_id,
            }
        )
        out.texts[block_id] = text
    return out


def _area(bbox: list[float] | None) -> float:
    return 0.0 if bbox is None else max(0.0, bbox[2] - bbox[0]) * max(0.0, bbox[3] - bbox[1])


def _ocr_cells(result: ConversionResult) -> dict[int, list[Any]]:
    """OCR'd text cells per page, from the layout clusters (the parsed pages are dropped after conversion)."""
    cells: dict[int, list[Any]] = {}
    for page in result.pages:
        layout = page.predictions.layout
        clusters = layout.clusters if layout is not None else []
        cells[page.page_no] = [c for cluster in clusters for c in cluster.cells if c.from_ocr]
    return cells


def _p10(values: list[float]) -> float:
    ordered = sorted(values)
    return ordered[int(0.1 * (len(ordered) - 1))]


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


def page_entry(
    number: int,
    width: float,
    height: float,
    facts: signals.PageFacts | None,
    blocks: list[dict[str, Any]],
    texts: dict[str, str],
    *,
    ocr_applied: str = "none",
    ocr_confidences: list[float] | None = None,
    picture_area: float = 0.0,
    ms: int | None = None,
) -> dict[str, Any]:
    """A `pages[]` entry with its signals (§6.4); character counts are non-whitespace characters."""
    on_page = [b for b in blocks if b["page"] == number]
    body = "\n".join(texts[b["id"]] for b in on_page if b["layer"] == "body")
    furniture = "\n".join(texts[b["id"]] for b in on_page if b["layer"] == "furniture")
    tables = [b for b in on_page if b["kind"] == "table"]
    page_area = max(width * height, 1.0)
    chars = signals.non_space_chars(body)
    confidence = None
    if ocr_confidences:
        confidence = {"mean": round(statistics.fmean(ocr_confidences), 3), "p10": round(_p10(ocr_confidences), 3)}
    return {
        "page": number,
        "label": None,
        "width": round(width, 2),
        "height": round(height, 2),
        "signals": {
            "chars": chars,
            "furniture_chars": signals.non_space_chars(furniture),
            "garbled_score": signals.garbled_score(body),
            "text_layer_chars": facts.text_chars if facts else None,
            "image_coverage": facts.image_coverage if facts else None,
            "ocr_applied": ocr_applied if ocr_confidences else "none",
            "ocr_confidence": confidence,
            "table_count": len(tables),
            "table_area_ratio": round(min(1.0, sum(_area(b["bbox"]) for b in tables) / page_area), 3),
            "picture_area_ratio": round(min(1.0, picture_area / page_area), 3),
            "empty": chars == 0,
            "ms": ms,
        },
    }


def markdown(blocks: list[dict[str, Any]], first: int, last: int) -> str:
    """The document Markdown (§7.3): blocks' `md` joined by blank lines, `<!-- page: N -->` before each page."""
    parts: list[str] = []
    for number in range(first, last + 1):
        parts.append(f"<!-- page: {number} -->")
        parts.extend(b["md"] for b in blocks if b["page"] == number and b["md"])
    return "\n\n".join(parts) + "\n"


# --- Jobs -----------------------------------------------------------------------------------------------------------


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


def _text_layer_pages(text_pages: list[textlayer.TextPage], blocks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    texts = {b["id"]: b["md"].lstrip("#").lstrip() if b["kind"] == "heading" else b["md"] for b in blocks}
    return [
        page_entry(
            p.facts.page,
            p.facts.width,
            p.facts.height,
            p.facts,
            blocks,
            texts,
            # The fast path detects no pictures; the page's image objects are its picture area.
            picture_area=p.facts.image_coverage * p.facts.width * p.facts.height,
            ms=p.ms,
        )
        for p in text_pages
    ]


def _completed(result: ConversionResult, first: int, last: int) -> int:
    """The last page of the contiguous run of converted pages from `first`, or `first - 1` when there is none."""
    done = {p.page_no for p in result.pages if p.assembled is not None}
    last_done = first - 1
    while last_done < last and last_done + 1 in done:
        last_done += 1
    return last_done


def _docling(
    job: dict[str, Any],
    options: ConvertRequest,
    pipelines: Pipelines,
    first: int,
    last: int,
    facts: dict[int, signals.PageFacts],
) -> dict[str, Any]:
    pipeline = pipelines.get(OptionSet(options.ocr.mode, options.tables.mode, options.image_scale))
    deadline_s = options.deadline_ms / 1000
    pipeline.run_options.document_timeout = max(deadline_s - GRACE_S, deadline_s / 2)

    stream = DocumentStream(name=f"{options.request_id}.pdf", stream=io.BytesIO(Path(job["input"]).read_bytes()))
    started = time.monotonic()
    result = pipeline.converter.convert(stream, raises_on_error=False, page_range=(first, last))
    docling_ms = round((time.monotonic() - started) * 1000)

    completed_to = _completed(result, first, last)
    if result.status == ConversionStatus.FAILURE or completed_to < first:
        if result.has_timeout_errors():
            raise ParserError("timeout")
        if any("ocr" in e.module_name.lower() for e in result.errors):
            raise ParserError("ocr_failed")
        raise ParserError("conversion_failed")

    doc = result.document
    assembled = assemble(doc)
    blocks = [b for b in assembled.blocks if b["page"] is None or b["page"] <= completed_to]
    kept = {b["id"] for b in blocks}
    tables = [t for t in assembled.tables if any(b["table"] == t["id"] for b in blocks)]
    pictures = [p for p in assembled.pictures if p["block"] in kept]
    ocr = _ocr_cells(result)
    _picture_ocr_chars(pictures, ocr, doc)
    ocr_applied = "full-page" if options.ocr.mode == "force" else "bitmap"
    pages = []
    for number in range(first, completed_to + 1):
        page_facts = facts.get(number)
        size = doc.pages[number].size if number in doc.pages else None
        pages.append(
            page_entry(
                number,
                size.width if size else page_facts.width,
                size.height if size else page_facts.height,
                page_facts,
                blocks,
                assembled.texts,
                ocr_applied=ocr_applied,
                ocr_confidences=[c.confidence for c in ocr.get(number, [])],
                picture_area=sum(_area(p["bbox"]) for p in pictures if p["page"] == number),
            )
        )

    if options.outputs.docling_json:
        target = Path(job["dir"]) / DOCLING_JSON
        with target.open("wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as gz:
            gz.write(json.dumps(doc.export_to_dict(), ensure_ascii=False, separators=(",", ":")).encode("utf-8"))

    partial = completed_to < last
    return {
        "status": "partial" if partial else "success",
        "pipeline": "docling",
        "pages_completed": {"from": first, "to": completed_to},
        "pages": pages,
        "blocks": blocks,
        "tables": tables,
        "pictures": pictures,
        "warnings": [{"code": "deadline_partial", "page": completed_to + 1}] if partial else [],
        "docling_ms": docling_ms,
        "docling_json": options.outputs.docling_json,
    }


def run_job(job: dict[str, Any], pipelines: Pipelines) -> dict[str, Any]:
    """One `/v1/convert` job (`input`: the file, `dir`: the job directory, `options`); raises `ParserError`."""
    options = ConvertRequest.model_validate(job["options"])
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
        out = _docling(job, options, pipelines, first, last, facts)
        out["warnings"] = [{"code": "text_layer_rejected", "page": n} for n in rejected] + out["warnings"]

    out.update(
        page_count=page_count,
        pages_requested={"from": first, "to": last},
        text_layer_ms=text_layer_ms,
        signals_ms=signals_ms,
        markdown=markdown(out["blocks"], first, out["pages_completed"]["to"]) if options.outputs.markdown else None,
    )
    return out
