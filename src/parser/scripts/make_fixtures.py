"""Write the synthetic parser fixtures of tests/fixtures/parser/ (parser design §15.3).

Every fixture is generated here from fixed text, so it holds no personal data, and every run writes the same bytes:
pdfium's creation date and random document id are replaced by fixed values, the JPEG encoder is deterministic and
the noise is seeded. The text is
invented Swedish municipal prose; listed in tests/fixtures/parser/MANIFEST.csv.

    uv run python -m scripts.make_fixtures            # writes ../../tests/fixtures/parser/
    uv run python -m scripts.make_fixtures --out DIR
"""

import argparse
import hashlib
import io
import os
import random
import re
import sys
import time
from pathlib import Path

import pypdfium2 as pdfium

from ume_parser.selftest import draw_line, draw_text

OUT = Path(__file__).resolve().parent.parent.parent.parent / "tests" / "fixtures" / "parser"
A4 = (595, 842)
LEFT = 64
BODY_SIZE, BODY_LEADING = 11, 15
PARAGRAPH_GAP = 10

# (kind, lines): "title" 20 pt, "heading" 14 pt, "body" 11 pt. Lines are pre-wrapped to fit A4 at 11 pt Helvetica.
TEXT_PAGES = (
    (
        ("title", ["Riktlinjer för bygglov i Umeå kommun"]),
        (
            "body",
            [
                "De här riktlinjerna beskriver hur Umeå kommun handlägger ansökningar om bygglov,",
                "marklov och rivningslov. De gäller för alla som vill bygga nytt, bygga till eller",
                "ändra användningen av en byggnad inom kommunens gränser.",
            ],
        ),
        ("heading", ["1 Ansökan"]),
        (
            "body",
            [
                "En ansökan om bygglov lämnas digitalt via kommunens e-tjänst. Till ansökan ska",
                "situationsplan, fasadritningar och planritningar bifogas. Handläggningen börjar",
                "först när ansökan är komplett, och beslut fattas normalt inom tio veckor.",
            ],
        ),
        (
            "body",
            [
                "Byggnadsnämnden kan begära kompletteringar. Om kompletteringen inte kommer in",
                "inom den tid som anges i föreläggandet kan ansökan avvisas eller avslås.",
            ],
        ),
        ("heading", ["2 Avgifter"]),
        (
            "body",
            [
                "Avgiften för bygglov beräknas enligt kommunens taxa och tas ut även om ansökan",
                "avslås. Kostnaden för ett enbostadshus är för närvarande cirka 25 000 kronor.",
            ],
        ),
    ),
    (
        ("heading", ["3 Startbesked och slutbesked"]),
        (
            "body",
            [
                "Innan arbetet får påbörjas krävs ett startbesked. Det tekniska samrådet hålls när",
                "byggnadsnämnden bedömer att det behövs, och kontrollplanen fastställs där.",
            ],
        ),
        (
            "body",
            [
                "När åtgärden är genomförd ska byggherren begära slutbesked. Byggnaden får inte",
                "tas i bruk förrän slutbesked har lämnats, om inte nämnden beslutar något annat.",
            ],
        ),
        ("heading", ["4 Överklagande"]),
        (
            "body",
            [
                "Den som berörs av ett beslut kan överklaga det till Länsstyrelsen i Västerbotten.",
                "Överklagandet ska ha kommit in till kommunen inom tre veckor från den dag då",
                "beslutet kungjordes i Post- och Inrikes Tidningar.",
            ],
        ),
    ),
    (
        ("heading", ["Bilaga: Avgifter och handläggningstider"]),
        ("body", ["Tabellen visar avgifter enligt taxan för år 2027 och normala handläggningstider."]),
        ("table", []),
    ),
)
FEE_TABLE = (
    ("Åtgärd", "Avgift", "Handläggningstid"),
    ("Bygglov för enbostadshus", "25 000 kr", "10 veckor"),
    ("Marklov", "8 000 kr", "10 veckor"),
    ("Rivningslov", "4 500 kr", "4 veckor"),
)
TABLE_COLUMNS = (200, 110, 140)
TABLE_ROW_HEIGHT = 24
SIZES = {"title": 20, "heading": 14, "body": BODY_SIZE}

SCANNED_LINES = (
    ("heading", "Kallelse till samråd"),
    ("body", "Samhällsbyggnadsnämnden i Umeå kommun bjuder in till samråd om"),
    ("body", "förslaget till ny detaljplan för kvarteret Älgen på Öst på stan."),
    ("body", ""),
    ("body", "Planförslaget finns att läsa på kommunens webbplats och i Väven."),
    ("body", "Synpunkter lämnas skriftligen senast den 15 december till"),
    ("body", "samhällsbyggnadsnämnden. Välkommen med dina synpunkter!"),
)
SCAN_DPI = 200


_TRAILER_ID = re.compile(rb"/ID\[<([0-9A-F]{32})><([0-9A-F]{32})>\]")
_CREATION_DATE = re.compile(rb"/CreationDate\(D:\d{14}\+00'00'\)")
FIXED_DATE = b"/CreationDate(D:20261030000000+00'00')"
# pdfium writes the creation date in local time with its offset; in UTC the offset is `+00'00'` and the fixed date
# has the same length.
os.environ["TZ"] = "UTC"
time.tzset()


def _save(pdf: pdfium.PdfDocument) -> bytes:
    """The saved PDF with pdfium's creation date fixed and its random trailer `/ID` replaced by a hash of the file.

    Both replacements have the original length, so the cross-reference offsets stay valid.
    """
    buffer = io.BytesIO()
    pdf.save(buffer)
    pdf.close()
    data = _CREATION_DATE.sub(FIXED_DATE, buffer.getvalue())
    blank = _TRAILER_ID.sub(b"/ID[<" + b"0" * 32 + b"><" + b"0" * 32 + b">]", data)
    file_id = hashlib.md5(blank, usedforsecurity=False).hexdigest().upper().encode("ascii")
    return _TRAILER_ID.sub(b"/ID[<" + file_id + b"><" + file_id + b">]", data)


def _fee_table(pdf: pdfium.PdfDocument, page: pdfium.PdfPage, top: float) -> float:
    """`FEE_TABLE` as a ruled table with its top edge at `top`; returns the bottom edge."""
    width = sum(TABLE_COLUMNS)
    bottom = top - len(FEE_TABLE) * TABLE_ROW_HEIGHT
    for r in range(len(FEE_TABLE) + 1):
        draw_line(pdf, page, LEFT, top - r * TABLE_ROW_HEIGHT, LEFT + width, top - r * TABLE_ROW_HEIGHT)
    x = LEFT
    for column in (0, *TABLE_COLUMNS):
        x += column
        draw_line(pdf, page, x, top, x, bottom)
    for r, row in enumerate(FEE_TABLE):
        x = LEFT
        for column, cell in zip(TABLE_COLUMNS, row, strict=True):
            draw_text(pdf, page, cell, x + 6, top - (r + 1) * TABLE_ROW_HEIGHT + 8, BODY_SIZE)
            x += column
    return bottom


def text_layer_pdf() -> bytes:
    """Three pages with a text layer: a title, numbered headings, body paragraphs, page footers and, on the last
    page, a ruled table (the fast path reads it as text, Docling as a table)."""
    pdf = pdfium.PdfDocument.new()
    for number, blocks in enumerate(TEXT_PAGES, start=1):
        page = pdf.new_page(*A4)
        y = A4[1] - 90
        for kind, lines in blocks:
            if kind == "table":
                y = _fee_table(pdf, page, y) - PARAGRAPH_GAP
                continue
            size = SIZES[kind]
            if kind != "body":
                y -= 8
            for line in lines:
                draw_text(pdf, page, line, LEFT, y, size)
                y -= max(BODY_LEADING, size * 1.35)
            y -= PARAGRAPH_GAP
        draw_text(pdf, page, f"Sida {number} av {len(TEXT_PAGES)}", A4[0] / 2 - 25, 40, 9)
        page.gen_content()
    return _save(pdf)


def _scan_image() -> bytes:
    """A whole A4 page of text rendered to a grey 200 dpi JPEG with seeded noise: a scan with no text layer."""
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(*A4)
    y = A4[1] - 110
    for kind, line in SCANNED_LINES:
        size = 18 if kind == "heading" else 12
        if line:
            draw_text(pdf, page, line, LEFT, y, size)
        y -= size * 1.6 if kind == "heading" else 18
    page.gen_content()
    image = page.render(scale=SCAN_DPI / 72, grayscale=True).to_pil()
    pdf.close()

    rng = random.Random(4711)  # noqa: S311 (fixture noise, not security)
    pixels = image.load()
    for _ in range(image.width * image.height // 150):
        x, y = rng.randrange(image.width), rng.randrange(image.height)
        pixels[x, y] = rng.choice((0, 96, 160))
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=70, dpi=(SCAN_DPI, SCAN_DPI))
    return buffer.getvalue()


def scanned_pdf() -> bytes:
    """One page that is only an image: Docling with OCR has to read it."""
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(*A4)
    image = pdfium.PdfImage.new(pdf)
    image.load_jpeg(io.BytesIO(_scan_image()), inline=False)
    image.set_matrix(pdfium.PdfMatrix().scale(*A4))
    page.insert_obj(image)
    page.gen_content()
    return _save(pdf)


def corrupt_pdf() -> bytes:
    """A PDF header followed by bytes that are no PDF structure: `corrupt`."""
    rng = random.Random(1234)  # noqa: S311 (fixture bytes, not security)
    return b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n" + bytes(rng.randrange(256) for _ in range(2048)) + b"\n%%EOF\n"


def legacy_doc() -> bytes:
    """The OLE compound-file signature and an empty header: a legacy Office file, refused as `format_deferred`."""
    return bytes.fromhex("d0cf11e0a1b11ae1") + bytes(504)


FIXTURES = {
    "text-layer.pdf": text_layer_pdf,
    "scanned.pdf": scanned_pdf,
    "corrupt.pdf": corrupt_pdf,
    "legacy.doc": legacy_doc,
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=OUT)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for name, make in FIXTURES.items():
        data = make()
        (args.out / name).write_bytes(data)
        print(f"{name}: {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
