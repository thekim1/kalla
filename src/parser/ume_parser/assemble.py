"""A converted `DoclingDocument` as the contract's blocks, tables, pictures and pages (parser design §6.4, §7.3, §7.4).

Imported only in slot processes. `assemble` walks the document in Docling's reading order over the body and furniture
layers (hidden sheets and slides, comments and notes are other layers and are left out), `page_entry` computes one
`pages[]` entry with its signals, and `markdown` joins the blocks' Markdown with page markers. `truncate_cells` keeps
the first `MAX_SPREADSHEET_CELLS` non-empty cells of a workbook (§7.4).
"""

import re
import statistics
from dataclasses import dataclass, field
from typing import Any

from docling_core.types.doc import (
    ContentLayer,
    DocItemLabel,
    DoclingDocument,
    GroupItem,
    GroupLabel,
    ListItem,
    SectionHeaderItem,
    TableItem,
)

from ume_parser import limits, signals

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
_ODS_SHEET = "sheet: "
_ODP_SLIDE = re.compile(r"slide-\d+")


@dataclass
class Assembled:
    blocks: list[dict[str, Any]]
    texts: dict[str, str]  # block id → plain text, for character counts and the garble score
    tables: list[dict[str, Any]]
    pictures: list[dict[str, Any]]
    warnings: list[dict[str, Any]] = field(default_factory=list)


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


def _table_md(table: dict[str, Any]) -> str:
    """A pipe table of `table`'s cells (spans repeated), for a table cut by `truncate_cells`."""
    grid = [[""] * table["cols"] for _ in range(table["rows"])]
    for cell in table["cells"]:
        for r in range(cell["r"], min(cell["r"] + cell["rs"], table["rows"])):
            for c in range(cell["c"], min(cell["c"] + cell["cs"], table["cols"])):
                grid[r][c] = cell["text"].replace("|", "\\|").replace("\n", " ")
    if not grid:
        return ""
    lines = ["| " + " | ".join(grid[0]) + " |", "|" + "---|" * table["cols"]]
    lines += ["| " + " | ".join(row) + " |" for row in grid[1:]]
    return "\n".join(lines)


def assemble(doc: DoclingDocument, level_source: str) -> Assembled:
    """Blocks, tables and pictures of a converted document, in Docling's reading order (§7.3). `level_source` is
    `layout` for PDF and image pages, `style` for the declarative formats."""
    out = Assembled([], {}, [], [])
    for item, _level in doc.iterate_items(included_content_layers=_LAYERS):
        label = getattr(item, "label", None)
        if label is None:
            continue
        kind = _KINDS.get(label, "paragraph")
        block_id = f"b{len(out.blocks)}"
        text = getattr(item, "text", "") or ""
        level, block_level_source, table_id = None, None, None
        md = text
        if isinstance(item, SectionHeaderItem):
            level, block_level_source = item.level, level_source
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
                "level_source": block_level_source,
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


def truncate_cells(out: Assembled, max_cells: int | None = None) -> None:
    """Keeps the first `max_cells` non-empty table cells in reading order (§7.4): the table that crosses the limit
    keeps its whole rows below it, later tables are dropped, and warning `truncated_cells` is added."""
    budget = limits.MAX_SPREADSHEET_CELLS if max_cells is None else max_cells
    by_id = {t["id"]: t for t in out.tables}
    dropped: set[str] = set()
    truncated = False
    for block in out.blocks:
        table = by_id.get(block["table"]) if block["table"] else None
        if table is None:
            continue
        filled = [c for c in table["cells"] if c["text"].strip()]
        if not truncated and len(filled) <= budget:
            budget -= len(filled)
            continue
        if truncated or budget == 0:
            dropped.add(table["id"])
            truncated = True
            continue
        rows = sorted({c["r"] for c in filled})
        keep_rows = 0
        for r in rows:
            in_row = sum(1 for c in filled if c["r"] == r)
            if in_row > budget:
                break
            budget -= in_row
            keep_rows = r + 1
        table["cells"] = [c for c in table["cells"] if c["r"] < keep_rows]
        table["rows"] = keep_rows
        table["header_rows"] = min(table["header_rows"], keep_rows)
        block["md"] = _table_md(table)
        out.texts[block["id"]] = "\n".join(c["text"] for c in table["cells"])
        truncated = True
        if keep_rows == 0:
            dropped.add(table["id"])
    if not truncated:
        return
    out.blocks = [b for b in out.blocks if b["table"] not in dropped]
    out.tables = [t for t in out.tables if t["id"] not in dropped]
    out.texts = {b["id"]: out.texts[b["id"]] for b in out.blocks}
    out.warnings.append({"code": "truncated_cells", "page": None})


@dataclass(frozen=True)
class PageGroup:
    number: int
    label: str | None
    hidden: bool
    group: GroupItem


def page_groups(doc: DoclingDocument) -> list[PageGroup]:
    """The sheets of a workbook or the slides of an ODP, from the top-level groups the backends make: XLSX a `sheet`
    group per sheet, ODS a `section` named `sheet: <name>`, ODP a `chapter` named `slide-<index>`. Sheets take the
    next page number in order, as the backends number them; a hidden sheet or slide has its group or items on the
    `invisible` layer."""
    out: list[PageGroup] = []
    for group in doc.groups:
        if group.parent is None or group.parent.cref != "#/body":
            continue
        if group.label == GroupLabel.SHEET:
            label = group.name
        elif group.label == GroupLabel.SECTION and group.name.startswith(_ODS_SHEET):
            label = group.name.removeprefix(_ODS_SHEET)
        elif group.label == GroupLabel.CHAPTER and _ODP_SLIDE.fullmatch(group.name):
            out.append(PageGroup(int(group.name.removeprefix("slide-")) + 1, None, _all_invisible(doc, group), group))
            continue
        else:
            continue
        out.append(PageGroup(len(out) + 1, label, group.content_layer == ContentLayer.INVISIBLE, group))
    return out


def _all_invisible(doc: DoclingDocument, group: GroupItem) -> bool:
    items = [item for item, _ in doc.iterate_items(root=group, included_content_layers=set(ContentLayer))]
    return bool(items) and all(item.content_layer == ContentLayer.INVISIBLE for item in items)


def place_on_groups(doc: DoclingDocument, out: Assembled, groups: list[PageGroup]) -> None:
    """Gives the blocks of a format whose items carry no page (ODP) the number of the group they sit in."""
    for page_group in groups:
        refs = {
            item.self_ref
            for item, _ in doc.iterate_items(root=page_group.group, included_content_layers=set(ContentLayer))
        }
        for block in out.blocks:
            if block["page"] is None and block["ref"] in refs:
                block["page"] = block["page_end"] = page_group.number
        for picture in out.pictures:
            if picture["page"] is None:
                picture["page"] = next((b["page"] for b in out.blocks if b["id"] == picture["block"]), None)


def area(bbox: list[float] | None) -> float:
    return 0.0 if bbox is None else max(0.0, bbox[2] - bbox[0]) * max(0.0, bbox[3] - bbox[1])


def _p10(values: list[float]) -> float:
    ordered = sorted(values)
    return ordered[int(0.1 * (len(ordered) - 1))]


def page_entry(
    number: int | None,
    width: float | None,
    height: float | None,
    blocks: list[dict[str, Any]],
    texts: dict[str, str],
    *,
    label: str | None = None,
    text_layer_chars: int | None = None,
    image_coverage: float | None = None,
    ocr_applied: str = "none",
    ocr_confidences: list[float] | None = None,
    picture_area: float = 0.0,
    ms: int | None = None,
) -> dict[str, Any]:
    """A `pages[]` entry with its signals (§6.4); character counts are non-whitespace characters. `number` is None
    for a document without pages (DOCX, ODT), whose blocks have no page either."""
    on_page = [b for b in blocks if b["page"] == number]
    body = "\n".join(texts[b["id"]] for b in on_page if b["layer"] == "body")
    furniture = "\n".join(texts[b["id"]] for b in on_page if b["layer"] == "furniture")
    tables = [b for b in on_page if b["kind"] == "table"]
    page_area = max(width * height, 1.0) if width and height else None
    chars = signals.non_space_chars(body)
    confidence = None
    if ocr_confidences:
        confidence = {"mean": round(statistics.fmean(ocr_confidences), 3), "p10": round(_p10(ocr_confidences), 3)}

    def ratio(value: float) -> float:
        return round(min(1.0, value / page_area), 3) if page_area else 0.0

    return {
        "page": number,
        "label": label,
        "width": round(width, 2) if width else None,
        "height": round(height, 2) if height else None,
        "signals": {
            "chars": chars,
            "furniture_chars": signals.non_space_chars(furniture),
            "garbled_score": signals.garbled_score(body),
            "text_layer_chars": text_layer_chars,
            "image_coverage": image_coverage,
            "ocr_applied": ocr_applied if ocr_confidences else "none",
            "ocr_confidence": confidence,
            "table_count": len(tables),
            "table_area_ratio": ratio(sum(area(b["bbox"]) for b in tables)),
            "picture_area_ratio": ratio(picture_area),
            "empty": chars == 0,
            "ms": ms,
        },
    }


def markdown(blocks: list[dict[str, Any]], pages: list[int | None]) -> str:
    """The document Markdown (§7.3): blocks' `md` joined by blank lines, `<!-- page: N -->` before each page; a
    document without pages has no markers."""
    parts: list[str] = []
    for number in pages:
        if number is not None:
            parts.append(f"<!-- page: {number} -->")
        parts.extend(b["md"] for b in blocks if b["page"] == number and b["md"])
    return "\n\n".join(parts) + "\n"
