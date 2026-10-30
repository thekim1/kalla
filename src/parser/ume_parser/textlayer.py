"""The text-layer fast path, `pipeline: text_layer` (parser design §3): pypdfium2 text extraction, no models.

Per page, pdfium's text page gives the characters in content order, with generated `\\r\\n` between lines. Lines
become paragraphs by vertical gap; a line set in a size of at least 1.25 times the page's median body size, in at
most two lines of at most 150 characters, becomes a `heading` (`level_source: font_size`), its level the rank of its
size among the heading sizes of the request (at most 3). Lines in the top or bottom band of the page that look like
a page number, or repeat on another page of the request, are furniture (`page_header`, `page_footer`).

The size of a character is the height of its loose box (the font's ascent to descent, scaled by the text matrix),
not `FPDFText_GetFontSize`: that returns the `Tf` operand without the text matrix, so a PDF that sets `1 Tf` and
scales with `Tm` would report 1 pt for every character.

No table structure, no OCR, no rendering. Column order is pdfium's content order (to-verify #150).
"""

import re
import statistics
import time
from dataclasses import dataclass, field
from typing import Any

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c

from ume_parser import signals

HEADING_SIZE_RATIO = 1.25
HEADING_MAX_LINES = 2
HEADING_MAX_CHARS = 150
MAX_HEADING_LEVEL = 3
PARAGRAPH_GAP_RATIO = 0.5  # a vertical gap above this share of the body size starts a new paragraph
FURNITURE_BAND = 0.08  # top and bottom share of the page height
_PAGE_NUMBER = re.compile(r"^\s*(sida|page|s\.)?\s*\d{1,4}(\s*(av|of|/)\s*\d{1,4})?\s*$", re.IGNORECASE)
_DIGITS = re.compile(r"\d+")


@dataclass
class Line:
    text: str
    left: float
    top: float  # top-left origin, points
    right: float
    bottom: float
    size: float


@dataclass
class TextPage:
    facts: signals.PageFacts
    lines: list[Line] = field(default_factory=list)
    body_size: float = 0.0
    ms: int = 0


def _lines(textpage: pdfium.PdfTextPage, height: float) -> list[Line]:
    lines: list[Line] = []
    chars: list[str] = []
    boxes: list[tuple[float, float, float, float]] = []

    def flush() -> None:
        text = "".join(chars).strip()
        if text and boxes:
            lines.append(
                Line(
                    text=re.sub(r"\s+", " ", text),
                    left=min(b[0] for b in boxes),
                    top=height - max(b[3] for b in boxes),
                    right=max(b[2] for b in boxes),
                    bottom=height - min(b[1] for b in boxes),
                    size=statistics.median(b[3] - b[1] for b in boxes),
                )
            )
        chars.clear()
        boxes.clear()

    for i in range(textpage.count_chars()):
        ch = chr(pdfium_c.FPDFText_GetUnicode(textpage, i))
        if ch in "\r\n":
            if ch == "\n":
                flush()
            continue
        chars.append(ch)
        if ch.isspace() or pdfium_c.FPDFText_IsGenerated(textpage, i) == 1:
            continue
        left, bottom, right, top = textpage.get_charbox(i, loose=True)
        if top > bottom:
            boxes.append((left, bottom, right, top))
    flush()
    return lines


def read_page(page: pdfium.PdfPage, number: int) -> TextPage:
    started = time.monotonic()
    facts = signals.page_facts(page, number)
    textpage = page.get_textpage()
    try:
        lines = _lines(textpage, facts.height)
    finally:
        textpage.close()
    weights = [line.size for line in lines for _ in range(len(line.text))]
    body = statistics.median(weights) if weights else 0.0
    return TextPage(facts=facts, lines=lines, body_size=body, ms=round((time.monotonic() - started) * 1000))


def _furniture_key(text: str) -> str:
    return _DIGITS.sub("#", text.strip().lower())


def _furniture(pages: list[TextPage]) -> set[tuple[int, int]]:
    """(page index, line index) of header and footer lines."""
    banded: list[tuple[int, int, str]] = []
    for p, page in enumerate(pages):
        band = page.facts.height * FURNITURE_BAND
        for i, line in enumerate(page.lines):
            if line.bottom <= band or line.top >= page.facts.height - band:
                banded.append((p, i, _furniture_key(line.text)))
    pages_by_key: dict[str, set[int]] = {}
    for p, _, key in banded:
        pages_by_key.setdefault(key, set()).add(p)
    return {(p, i) for p, i, key in banded if _PAGE_NUMBER.match(pages[p].lines[i].text) or len(pages_by_key[key]) > 1}


def _is_heading_line(line: Line, body_size: float) -> bool:
    return body_size > 0 and line.size >= HEADING_SIZE_RATIO * body_size and len(line.text) <= HEADING_MAX_CHARS


def _size_key(size: float) -> float:
    return round(size * 2) / 2


def convert(pdf: pdfium.PdfDocument, first: int, last: int) -> tuple[list[TextPage], list[dict[str, Any]]]:
    """Pages `first`..`last` (1-based, inclusive) as text pages and contract blocks (`models.Block` fields)."""
    pages = [read_page(pdf[n - 1], n) for n in range(first, last + 1)]
    furniture = _furniture(pages)

    # Runs of lines: furniture lines alone, heading lines of one size together, body lines split by gap.
    runs: list[tuple[int, str, list[Line]]] = []  # (page index, role, lines)
    for p, page in enumerate(pages):
        current: tuple[int, str, list[Line]] | None = None
        for i, line in enumerate(page.lines):
            if (p, i) in furniture:
                role = "page_header" if line.bottom <= page.facts.height / 2 else "page_footer"
                runs.append((p, role, [line]))
                current = None
                continue
            role = "heading" if _is_heading_line(line, page.body_size) else "paragraph"
            if current is not None and current[1] == role:
                previous = current[2][-1]
                gap = line.top - previous.bottom
                same_size = _size_key(line.size) == _size_key(previous.size)
                if same_size and gap <= PARAGRAPH_GAP_RATIO * page.body_size:
                    current[2].append(line)
                    continue
            current = (p, role, [line])
            runs.append(current)

    # A heading run longer than two lines is a paragraph set large, not a heading.
    runs = [(p, "paragraph" if r == "heading" and len(ls) > HEADING_MAX_LINES else r, ls) for p, r, ls in runs]
    heading_sizes = sorted({_size_key(ls[0].size) for _, r, ls in runs if r == "heading"}, reverse=True)

    blocks: list[dict[str, Any]] = []
    for p, role, lines in runs:
        text = " ".join(line.text for line in lines)
        level = None
        md = text
        if role == "heading":
            level = min(heading_sizes.index(_size_key(lines[0].size)) + 1, MAX_HEADING_LEVEL)
            md = "#" * level + " " + text
        page_no = pages[p].facts.page
        blocks.append(
            {
                "id": f"b{len(blocks)}",
                "kind": role,
                "level": level,
                "level_source": "font_size" if role == "heading" else None,
                "page": page_no,
                "page_end": page_no,
                "bbox": [
                    round(min(ln.left for ln in lines), 1),
                    round(min(ln.top for ln in lines), 1),
                    round(max(ln.right for ln in lines), 1),
                    round(max(ln.bottom for ln in lines), 1),
                ],
                "layer": "furniture" if role in ("page_header", "page_footer") else "body",
                "md": md,
                "ref": None,
                "table": None,
            }
        )
    return pages, blocks
