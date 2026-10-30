"""Quality signals (parser design §6): the garble detector and the per-page text-layer facts of a PDF.

The same `garbled_score` scores text-layer text and converted text, so the worker compares like with like (§6.3).
The score is in [0, 1], the maximum of:

1. the bad-character ratio: U+FFFD, private-use code points, C0 controls and literal `(cid:N)`, per non-space
   character;
2. a low share of letters and digits among non-space characters (below 0.5 rises linearly to 1 at 0);
3. mojibake: the share of tokens with a UTF-8-read-as-Latin-1 pair (`Ã¤`, `Ã¶`, `Ã¥`, …), counted double;
4. scan noise: an average token length below 2 or above 25, or more than 30 % one-character tokens (scored 1 when
   there are at least 20 tokens).

The design's third criterion also scores token plausibility under a Swedish/English character-bigram model; that
table is P1, so P0 catches mojibake but not every implausible token.

`page_facts` reads a PDF page with pypdfium2 (no rendering) and gives the facts the worker's strategy rules and the
text-layer fast path need: `text_chars`, `bad_chars`, `garbled_score`, `image_coverage` and `path_objects`.
"""

import itertools
import re
from dataclasses import dataclass

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c

_CID = re.compile(r"\(cid:\d+\)")
_MOJIBAKE = re.compile(r"Ã[\u0080-¿]|Â[\u0080-¿]")
_TOKEN = re.compile(r"\S+")
_CONTROL_ALLOWED = {"\t", "\n", "\r"}

# Fast-path eligibility (ingestion-worker design-processing §3.1 rules 4 and 5): a clean text layer that is not
# table-dense. The worker plans from the same thresholds; the parser checks them again before taking the fast path.
CLEAN_MIN_CHARS = 200
CLEAN_MIN_CHARS_LOW_IMAGES = 50
CLEAN_MAX_IMAGE_COVERAGE = 0.3
CLEAN_MAX_GARBLE = 0.2
TABLE_DENSE_PATH_OBJECTS = 40


def _is_bad(ch: str) -> bool:
    code = ord(ch)
    if ch == "�":
        return True
    if 0xE000 <= code <= 0xF8FF or 0xF0000 <= code <= 0x10FFFD:
        return True
    return code < 0x20 and ch not in _CONTROL_ALLOWED


def bad_chars(text: str) -> int:
    return sum(1 for ch in text if _is_bad(ch)) + sum(len(m.group()) for m in _CID.finditer(text))


def non_space_chars(text: str) -> int:
    return sum(1 for ch in text if not ch.isspace())


def garbled_score(text: str) -> float:
    visible = non_space_chars(text)
    if visible == 0:
        return 0.0
    bad = min(1.0, bad_chars(text) / visible)

    word_chars = sum(1 for ch in text if ch.isalnum())
    ratio = word_chars / visible
    letters = 0.0 if ratio >= 0.5 else (0.5 - ratio) / 0.5

    tokens = _TOKEN.findall(text)
    long_tokens = [t for t in tokens if len(t) >= 3]
    mojibake = (
        min(1.0, 2 * sum(1 for t in long_tokens if _MOJIBAKE.search(t)) / len(long_tokens)) if long_tokens else 0.0
    )

    noise = 0.0
    if len(tokens) >= 20:
        average = sum(len(t) for t in tokens) / len(tokens)
        single = sum(1 for t in tokens if len(t) == 1) / len(tokens)
        if average < 2 or average > 25 or single > 0.3:
            noise = 1.0

    return round(max(bad, letters, mojibake, noise), 3)


def _union_area(rects: list[tuple[float, float, float, float]]) -> float:
    """Area of the union of axis-aligned rectangles (left, bottom, right, top), by coordinate compression."""
    if not rects:
        return 0.0
    xs = sorted({x for r in rects for x in (r[0], r[2])})
    area = 0.0
    for x0, x1 in itertools.pairwise(xs):
        spans = sorted((r[1], r[3]) for r in rects if r[0] <= x0 and r[2] >= x1)
        covered, top = 0.0, float("-inf")
        for bottom, upper in spans:
            if upper <= top:
                continue
            covered += upper - max(bottom, top)
            top = upper
        area += covered * (x1 - x0)
    return area


@dataclass(frozen=True)
class PageFacts:
    page: int
    width: float
    height: float
    rotation: int
    text: str
    text_chars: int
    bad_chars: int
    garbled_score: float
    image_coverage: float
    path_objects: int

    @property
    def clean(self) -> bool:
        """A clean text layer that the fast path can take (design-processing §3.1 rule 5)."""
        enough_text = self.text_chars >= CLEAN_MIN_CHARS or (
            self.text_chars >= CLEAN_MIN_CHARS_LOW_IMAGES and self.image_coverage < CLEAN_MAX_IMAGE_COVERAGE
        )
        return enough_text and self.garbled_score < CLEAN_MAX_GARBLE and self.path_objects < TABLE_DENSE_PATH_OBJECTS


def page_facts(page: pdfium.PdfPage, number: int) -> PageFacts:
    """Facts of one page from its text layer and page objects; `number` is the 1-based page number.

    Image bounds are taken as pdfium reports them; for an image inside a form XObject they are not transformed by
    the form's matrix, so such pages can under- or over-report coverage (P1 inspect revisits this with fixtures).
    """
    width, height = page.get_size()
    textpage = page.get_textpage()
    try:
        text = textpage.get_text_range()
    finally:
        textpage.close()

    page_area = max(width * height, 1.0)
    images: list[tuple[float, float, float, float]] = []
    paths = 0
    for obj in page.get_objects(filter=[pdfium_c.FPDF_PAGEOBJ_IMAGE, pdfium_c.FPDF_PAGEOBJ_PATH]):
        if obj.type == pdfium_c.FPDF_PAGEOBJ_PATH:
            paths += 1
            continue
        left, bottom, right, top = obj.get_bounds()
        images.append((max(left, 0.0), max(bottom, 0.0), min(right, width), min(top, height)))
    images = [r for r in images if r[2] > r[0] and r[3] > r[1]]

    return PageFacts(
        page=number,
        width=round(width, 2),
        height=round(height, 2),
        rotation=page.get_rotation(),
        text=text,
        text_chars=non_space_chars(text),
        bad_chars=bad_chars(text),
        garbled_score=garbled_score(text),
        image_coverage=round(min(1.0, _union_area(images) / page_area), 3),
        path_objects=paths,
    )
