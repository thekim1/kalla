"""Write the synthetic parser fixtures of tests/fixtures/parser/ (parser design §15.3).

Every fixture is generated here from fixed text, so it holds no personal data, and every run writes the same bytes:
pdfium's creation date and random document id are replaced by fixed values, the JPEG encoder is deterministic, the
noise is seeded, and every Office and OpenDocument package is re-zipped with fixed entry dates and fixed document
dates. The text is invented Swedish municipal prose; listed in tests/fixtures/parser/MANIFEST.csv.

`ocr_sample` writes the synthetic part of the OCR acceptance sample (§9.3) to a directory: page images with their
ground truth. It is generated where it is used (`scripts/ocr_check.py`, the OCR acceptance test), not committed.

    uv run python -m scripts.make_fixtures            # writes ../../tests/fixtures/parser/
    uv run python -m scripts.make_fixtures --out DIR
"""

import argparse
import hashlib
import io
import os
import random
import re
import struct
import sys
import textwrap
import time
import zipfile
import zlib
from pathlib import Path

import pypdfium2 as pdfium
from PIL import Image, ImageFilter

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
    """The OLE signature and an empty header: no root CLSID or known stream, refused as `format_deferred`."""
    return bytes.fromhex("d0cf11e0a1b11ae1") + bytes(504)


# --- Office and OpenDocument ----------------------------------------------------------------------------------------

ZIP_DATE = (2026, 10, 30, 0, 0, 0)
FIXED_ISO = "2026-10-30T00:00:00Z"
_PACKAGE_DATES = (
    re.compile(rb"(<dcterms:(created|modified)[^>]*>)[^<]*(</dcterms:\2>)"),
    re.compile(rb"(<meta:(creation-date)>)[^<]*(</meta:\2>)"),
    re.compile(rb"(<dc:(date)>)[^<]*(</dc:\2>)"),
)
DOC_TITLE = "Riktlinjer för bygglov i Umeå kommun"
DOC_SECTIONS = (
    (
        "1 Ansökan",
        "En ansökan om bygglov lämnas digitalt via kommunens e-tjänst. Handläggningen börjar först när ansökan är "
        "komplett, och beslut fattas normalt inom tio veckor.",
    ),
    (
        "2 Avgifter",
        "Avgiften för bygglov beräknas enligt kommunens taxa och tas ut även om ansökan avslås.",
    ),
)
DOC_LIST = ("Situationsplan", "Fasadritningar", "Planritningar")
DOC_TABLE = (("Åtgärd", "Avgift"), ("Bygglov för enbostadshus", "25 000 kr"), ("Marklov", "8 000 kr"))
SLIDES = (
    ("Bygglov i Umeå", "Information för sökande"),
    ("Så går ansökan till", "Fyll i e-tjänsten\nBifoga ritningar\nBetala avgiften"),
)
SLIDE_NOTES = "Talarens anteckningar: nämn tidsplanen."
SHEET_ROWS = (("Åtgärd", "Antal", "Avgift"), ("Bygglov", 3, 25000), ("Marklov", 2, 8000), ("Rivningslov", 1, 4500))
HIDDEN_SHEET_ROWS = (("Intern kod", "Konto"), ("BL-1", 3410))
# The total cell: openpyxl writes formulas without a cached value, so the fixture adds the value Excel would store.
TOTAL_FORMULA, TOTAL_VALUE = "SUM(C2:C4)", "37500"


def _rezip(data: bytes, replace: dict[str, bytes] | None = None, add: dict[str, bytes] | None = None) -> bytes:
    """A package re-zipped with fixed entry dates and document dates, entries in their order, compression kept."""
    source = zipfile.ZipFile(io.BytesIO(data))
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as target:
        entries = [(i.filename, i.compress_type, source.read(i)) for i in source.infolist()]
        entries += [(name, zipfile.ZIP_DEFLATED, body) for name, body in (add or {}).items()]
        for name, compression, body in entries:
            body = (replace or {}).get(name, body)
            for pattern in _PACKAGE_DATES:
                body = pattern.sub(rb"\g<1>" + FIXED_ISO.encode() + rb"\g<3>", body)
            info = zipfile.ZipInfo(name, date_time=ZIP_DATE)
            info.compress_type = compression
            info.external_attr = 0o644 << 16
            target.writestr(info, body)
    return out.getvalue()


def text_docx() -> bytes:
    """A DOCX with a title, numbered headings in Word's heading styles, paragraphs, a bullet list and a table."""
    import docx

    document = docx.Document()
    document.add_heading(DOC_TITLE, level=0)
    for heading, body in DOC_SECTIONS:
        document.add_heading(heading, level=1)
        document.add_paragraph(body)
    for item in DOC_LIST:
        document.add_paragraph(item, style="List Bullet")
    table = document.add_table(rows=len(DOC_TABLE), cols=2)
    for r, row in enumerate(DOC_TABLE):
        for c, value in enumerate(row):
            table.cell(r, c).text = value
    buffer = io.BytesIO()
    document.save(buffer)
    return _rezip(buffer.getvalue())


def slides_pptx() -> bytes:
    """A PPTX with a title slide and a bulleted slide whose speaker notes must not reach the result."""
    import pptx

    presentation = pptx.Presentation()
    for i, (title, body) in enumerate(SLIDES):
        slide = presentation.slides.add_slide(presentation.slide_layouts[0 if i == 0 else 1])
        slide.shapes.title.text = title
        slide.placeholders[1].text = body
        if i == 1:
            slide.notes_slide.notes_text_frame.text = SLIDE_NOTES
    buffer = io.BytesIO()
    presentation.save(buffer)
    return _rezip(buffer.getvalue())


def _workbook(hidden: bool = True) -> bytes:
    import openpyxl

    workbook = openpyxl.Workbook()
    sheet = workbook.active
    sheet.title = "Avgifter"
    for row in SHEET_ROWS:
        sheet.append(row)
    sheet.append(("Summa", None, f"={TOTAL_FORMULA}"))
    if hidden:
        internal = workbook.create_sheet("Intern")
        for row in HIDDEN_SHEET_ROWS:
            internal.append(row)
        internal.sheet_state = "hidden"
    buffer = io.BytesIO()
    workbook.save(buffer)
    return buffer.getvalue()


def sheets_xlsx() -> bytes:
    """An XLSX with a fee sheet (header row, a formula with its cached total) and a hidden sheet."""
    data = _workbook()
    sheet = zipfile.ZipFile(io.BytesIO(data)).read("xl/worksheets/sheet1.xml")
    cached = sheet.replace(
        f"<f>{TOTAL_FORMULA}</f><v></v>".encode(), f"<f>{TOTAL_FORMULA}</f><v>{TOTAL_VALUE}</v>".encode()
    )
    assert cached != sheet, "openpyxl's formula cell changed shape"  # noqa: S101
    return _rezip(data, replace={"xl/worksheets/sheet1.xml": cached})


def text_odt() -> bytes:
    """The DOCX's content as an OpenDocument text."""
    from odfdo import Document, Header, List, Paragraph, Table

    document = Document("text")
    body = document.body
    body.append(Header(1, DOC_TITLE))
    for heading, text in DOC_SECTIONS:
        body.append(Header(2, heading))
        body.append(Paragraph(text))
    body.append(List(list(DOC_LIST)))
    table = Table("Avgifter", width=2, height=len(DOC_TABLE))
    for r, row in enumerate(DOC_TABLE):
        for c, value in enumerate(row):
            table.set_value((c, r), value)
    body.append(table)
    buffer = io.BytesIO()
    document.save(buffer)
    return _rezip(buffer.getvalue())


def sheets_ods() -> bytes:
    """The XLSX's visible sheet as an OpenDocument spreadsheet."""
    from odfdo import Document, Table

    document = Document("spreadsheet")
    body = document.body
    body.clear()
    table = Table("Avgifter", width=3, height=len(SHEET_ROWS))
    for r, row in enumerate(SHEET_ROWS):
        for c, value in enumerate(row):
            table.set_value((c, r), value)
    body.append(table)
    buffer = io.BytesIO()
    document.save(buffer)
    return _rezip(buffer.getvalue())


def slides_odp() -> bytes:
    """The PPTX's slides as an OpenDocument presentation: a title frame and a text frame per slide."""
    from odfdo import Document, DrawPage, Frame

    document = Document("presentation")
    body = document.body
    body.clear()
    for i, (title, text) in enumerate(SLIDES, start=1):
        page = DrawPage(f"page{i}", name=f"Bild {i}")
        page.append(Frame.text_frame(title, size=("20cm", "3cm"), position=("2cm", "1cm"), presentation_class="title"))
        page.append(Frame.text_frame(text.split("\n"), size=("20cm", "10cm"), position=("2cm", "5cm")))
        body.append(page)
    buffer = io.BytesIO()
    document.save(buffer)
    return _rezip(buffer.getvalue())


def macro_docm() -> bytes:
    """The DOCX with a VBA project part: macro-enabled, refused as `unsupported_format`."""
    return _rezip(text_docx(), add={"word/vbaProject.bin": b"\xcc\x61" + bytes(62)})


def bomb_docx() -> bytes:
    """The DOCX with a 4 MB part of zeros that deflates over 1000:1: the bomb pre-check's ratio limit."""
    return _rezip(text_docx(), add={"word/media/padding.bin": bytes(4 * 1024 * 1024)})


_XXE = (
    b'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    b'<!DOCTYPE w:document [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>\n'
)


def xxe_docx() -> bytes:
    """The DOCX whose document part declares an external entity to /etc/passwd and uses it in a paragraph."""
    source = zipfile.ZipFile(io.BytesIO(text_docx()))
    document = source.read("word/document.xml")
    document = _XXE + document.split(b"?>", 1)[1].lstrip()
    document = document.replace(b"Situationsplan", b"&xxe;", 1)
    return _rezip(text_docx(), replace={"word/document.xml": document})


def xxe_odt() -> bytes:
    """The ODT whose content part declares an external entity to /etc/passwd and uses it in a paragraph."""
    source = zipfile.ZipFile(io.BytesIO(text_odt()))
    content = source.read("content.xml")
    content = _XXE.replace(b"w:document", b"office:document-content") + content.split(b"?>", 1)[1].lstrip()
    content = content.replace(b"Situationsplan", b"&xxe;", 1)
    return _rezip(text_odt(), replace={"content.xml": content})


def archive_zip() -> bytes:
    """A plain zip of a photo: an archive, refused."""
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as archive:
        info = zipfile.ZipInfo("bilder/foto.jpg", date_time=ZIP_DATE)
        archive.writestr(info, _scan_jpeg_small())
    return out.getvalue()


# --- Images ---------------------------------------------------------------------------------------------------------

IMAGE_DPI = 150
IMAGE_LINES = (
    ("heading", "Kungörelse om granskning"),
    ("body", "Byggnadsnämnden i Umeå kommun ställer ut förslaget till"),
    ("body", "detaljplan för kvarteret Björken på Ålidhem för granskning."),
    ("body", "Synpunkter lämnas skriftligen senast den 3 mars 2027."),
)
TIFF_FRAMES = (
    ("Sida ett: Ärendet gäller förhandsbesked för nybyggnad.",),
    ("Sida två: Sökanden har yttrat sig över förslaget.",),
    ("Sida tre: Nämnden beslutar att ge positivt förhandsbesked.",),
)


def _render_lines(lines: tuple[tuple[str, str], ...], size: tuple[float, float], dpi: int, font: bytes = b"Helvetica"):
    """Lines of text rendered by pdfium to a grey image (no font files needed)."""
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(*size)
    y = size[1] - 50
    for kind, line in lines:
        text_size = 16 if kind == "heading" else 11
        if line:
            _draw(pdf, page, line, 36, y, text_size, font)
        y -= text_size * 1.8
    page.gen_content()
    image = page.render(scale=dpi / 72, grayscale=True).to_pil()
    pdf.close()
    return image


def _draw(pdf: pdfium.PdfDocument, page: pdfium.PdfPage, text: str, x: float, y: float, size: float, font: bytes):
    if font == b"Helvetica":
        draw_text(pdf, page, text, x, y, size)
        return
    import ctypes

    import pypdfium2.raw as pdfium_c

    raw = pdfium_c.FPDFPageObj_NewTextObj(pdf, font, ctypes.c_float(size))
    wide = ctypes.create_string_buffer((text + "\0").encode("utf-16-le"))
    pdfium_c.FPDFText_SetText(raw, ctypes.cast(wide, ctypes.POINTER(pdfium_c.FPDF_WCHAR)))
    obj = pdfium.PdfObject(raw, pdf=pdf)
    obj.set_matrix(pdfium.PdfMatrix().translate(x, y))
    page.insert_obj(obj)


def _noise(image, seed: int, share: int = 200):
    rng = random.Random(seed)  # noqa: S311 (fixture noise, not security)
    pixels = image.load()
    for _ in range(image.width * image.height // share):
        pixels[rng.randrange(image.width), rng.randrange(image.height)] = rng.choice((0, 96, 160))
    return image


def _scan_jpeg_small() -> bytes:
    buffer = io.BytesIO()
    _render_lines(IMAGE_LINES[:2], (420, 120), 72).save(buffer, format="JPEG", quality=60)
    return buffer.getvalue()


def scan_png() -> bytes:
    """A half page of text as a grey 150 dpi PNG: an image upload, OCR'd."""
    image = _noise(_render_lines(IMAGE_LINES, (420, 200), IMAGE_DPI), 17, share=800)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", dpi=(IMAGE_DPI, IMAGE_DPI), optimize=False)
    return buffer.getvalue()


def scan_jpeg() -> bytes:
    """The same text as a JPEG photo of a notice (quality 75, no resolution set: Docling assumes 72 dpi)."""
    image = _noise(_render_lines(IMAGE_LINES, (420, 200), IMAGE_DPI), 19, share=800)
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=75)
    return buffer.getvalue()


def frames_tiff() -> bytes:
    """A three-frame 150 dpi TIFF, one sentence per frame: a scanned case file, rangeable by frame."""
    frames = [
        _noise(_render_lines((("body", t[0]),), (420, 90), IMAGE_DPI), 23 + i, share=800)
        for i, t in enumerate(TIFF_FRAMES)
    ]
    buffer = io.BytesIO()
    frames[0].save(
        buffer,
        format="TIFF",
        save_all=True,
        append_images=frames[1:],
        compression="tiff_deflate",
        dpi=(IMAGE_DPI, IMAGE_DPI),
    )
    return buffer.getvalue()


def _png_chunk(kind: bytes, body: bytes) -> bytes:
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))


def huge_png() -> bytes:
    """A PNG header claiming 60 000 by 60 000 pixels (3.6 GP) with one row of data: the pixel limit."""
    header = struct.pack(">IIBBBBB", 60_000, 60_000, 8, 0, 0, 0, 0)
    data = zlib.compress(b"\x00" + bytes(60_000))
    return b"\x89PNG\r\n\x1a\n" + _png_chunk(b"IHDR", header) + _png_chunk(b"IDAT", data) + _png_chunk(b"IEND", b"")


# --- PDFs with active content ----------------------------------------------------------------------------------------


def _pdf(objects: list[bytes], root: int = 1) -> bytes:
    """A PDF from numbered object bodies (1-based), with a correct cross-reference table."""
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode()
    out += b"".join(f"{o:010d} 00000 n \n".encode() for o in offsets)
    out += f"trailer\n<< /Size {len(objects) + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


def _text_page_objects(text: str) -> list[bytes]:
    stream = f"BT /F1 12 Tf 72 720 Td ({text}) Tj ET".encode("latin-1")
    return [
        b"<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >>"
        b" /Contents 5 0 R >>",
        b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream",
    ]


def javascript_pdf() -> bytes:
    """One text page whose `/OpenAction` runs JavaScript: reported by inspect, never executed."""
    script = b"<< /Type /Action /S /JavaScript /JS (app.alert\\('hej'\\);) >>"
    catalog = b"<< /Type /Catalog /Pages 2 0 R /OpenAction 6 0 R >>"
    return _pdf([catalog, *_text_page_objects("Kallelse till sammanträde i nämnden."), script])


def attachment_pdf() -> bytes:
    """One text page with an embedded file: reported by inspect, not extracted in P1."""
    body = b"Bilaga: deltagarlista finns inte i denna fil."
    attachment = b"<< /Type /EmbeddedFile /Length " + str(len(body)).encode() + b" >>\nstream\n" + body + b"\nendstream"
    spec = b"<< /Type /Filespec /F (bilaga.txt) /UF (bilaga.txt) /EF << /F 6 0 R >> >>"
    names = b"<< /EmbeddedFiles << /Names [(bilaga.txt) 7 0 R] >> >>"
    catalog = b"<< /Type /Catalog /Pages 2 0 R /Names 8 0 R >>"
    return _pdf([catalog, *_text_page_objects("Protokoll med bilaga."), attachment, spec, names])


def long_pdf(pages: int = 2001) -> bytes:
    """A PDF of `pages` blank pages sharing one page dictionary shape: one more than the 2 000-page limit."""
    kids = " ".join(f"{3 + i} 0 R" for i in range(pages))
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>", f"<< /Type /Pages /Kids [{kids}] /Count {pages} >>".encode()]
    objects += [b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>"] * pages
    return _pdf(objects)


# --- Deferred formats -----------------------------------------------------------------------------------------------


def legacy_rtf() -> bytes:
    """A small RTF document: converted to DOCX by LibreOffice (ume_parser.legacy)."""
    return b"{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Helvetica;}}\\f0\\fs22 Kallelse till sammantr\\'e4de.\\par}"


def message_eml() -> bytes:
    """An RFC 5322 message: e-mail is P2, refused as `format_deferred`."""
    return (
        b"From: registrator@example.org\r\nTo: namnd@example.org\r\nSubject: Kallelse\r\n"
        b"Date: Fri, 30 Oct 2026 08:00:00 +0100\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
        b"Kallelse till sammantr\xc3\xa4de.\r\n"
    )


def _ole(stream_names: tuple[str, ...]) -> bytes:
    """An OLE compound-file header and one directory sector naming `stream_names` (no stream data)."""
    header = bytes.fromhex("d0cf11e0a1b11ae1") + bytes(504)
    sector = b""
    for name in ("Root Entry", *stream_names):
        encoded = (name + "\0").encode("utf-16-le")
        sector += encoded.ljust(64, b"\0") + struct.pack("<H", len(encoded)) + bytes(62)
    return header + sector.ljust(512, b"\0")


def legacy_msg() -> bytes:
    """An Outlook message's OLE header and stream names: refused as `format_deferred`."""
    return _ole(("__substg1.0_0037001F", "__properties_version1.0"))


def encrypted_docx() -> bytes:
    """A password-protected OOXML package: an OLE file with `EncryptionInfo` and `EncryptedPackage`: `encrypted`."""
    return _ole(("EncryptionInfo", "EncryptedPackage"))


# --- OCR acceptance sample (§9.3) -----------------------------------------------------------------------------------

OCR_SAMPLE_TEXT = (
    "Kommunstyrelsens arbetsutskott sammanträdde i Stadshuset den 4 februari.",
    "Närvarande ledamöter: ordföranden, vice ordföranden och tre ersättare.",
    "Ärendet gäller försäljning av tomtmark på Ön och i Röbäck.",
    "Förvaltningen föreslår att köpeskillingen bestäms till 1 250 kronor per kvadratmeter.",
    "Yttrande från miljö- och hälsoskyddsnämnden har inkommit och biläggs handlingarna.",
    "Beslutet får överklagas till förvaltningsrätten inom tre veckor från tillkännagivandet.",
    "Arbetsutskottet återremitterar ärendet för komplettering av kartmaterialet.",
    "Sammanträdet förklaras avslutat och protokollet justeras på måndag.",
    "Mötet behandlade även äldreomsorgens bemanning under sommaren.",
    "Skolförvaltningen redovisar läget för förskolorna på Ålidhem och Ersboda.",
)
OCR_FONTS = (b"Helvetica", b"Times-Roman", b"Courier")
OCR_SAMPLE_PAGES = 20
OCR_LINE_CHARS = 55


def _ocr_page(index: int):
    rng = random.Random(9000 + index)  # noqa: S311 (fixture variation, not security)
    sentences = [OCR_SAMPLE_TEXT[(index + i) % len(OCR_SAMPLE_TEXT)] for i in range(6)]
    # Wrapped so that the widest font (Courier at 13 pt) stays inside the page.
    lines = [line for sentence in sentences for line in textwrap.wrap(sentence, OCR_LINE_CHARS)]
    font = OCR_FONTS[index % len(OCR_FONTS)]
    size = (595, 842)
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(*size)
    text_size = 10 + index % 4
    y = size[1] - 80
    for line in lines:
        _draw(pdf, page, line, 56, y, text_size, font)
        y -= text_size * 2.2
    page.gen_content()
    dpi = 200
    image = page.render(scale=dpi / 72, grayscale=True).to_pil()
    pdf.close()
    angle = rng.uniform(1.0, 3.0) * rng.choice((-1, 1))
    image = image.rotate(angle, resample=Image.Resampling.BICUBIC, expand=False, fillcolor=255)
    if index % 3 == 1:
        image = image.filter(ImageFilter.GaussianBlur(0.6))
    image = _noise(image, 9100 + index, share=600 + 200 * (index % 3))
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=55 + 5 * (index % 4), dpi=(dpi, dpi))
    return buffer.getvalue(), "\n".join(lines) + "\n"


def ocr_sample(out: Path) -> list[Path]:
    """Writes `page-NN.jpg` and its ground truth `page-NN.txt` for each synthetic "print-scanned" page: Helvetica,
    Times and Courier (typewriter) at 10 to 13 pt, 200 dpi, 1 to 3° skew, seeded speckle, some blur, JPEG quality 55
    to 70."""
    out.mkdir(parents=True, exist_ok=True)
    images = []
    for index in range(OCR_SAMPLE_PAGES):
        image, truth = _ocr_page(index)
        path = out / f"page-{index + 1:02d}.jpg"
        path.write_bytes(image)
        path.with_suffix(".txt").write_text(truth, encoding="utf-8")
        images.append(path)
    return images


FIXTURES = {
    "text-layer.pdf": text_layer_pdf,
    "scanned.pdf": scanned_pdf,
    "corrupt.pdf": corrupt_pdf,
    "legacy.doc": legacy_doc,
    "legacy.rtf": legacy_rtf,
    "legacy.msg": legacy_msg,
    "message.eml": message_eml,
    "encrypted.docx": encrypted_docx,
    "text.docx": text_docx,
    "slides.pptx": slides_pptx,
    "sheets.xlsx": sheets_xlsx,
    "text.odt": text_odt,
    "sheets.ods": sheets_ods,
    "slides.odp": slides_odp,
    "scan.png": scan_png,
    "scan.jpg": scan_jpeg,
    "frames.tiff": frames_tiff,
    "macro.docm": macro_docm,
    "bomb.docx": bomb_docx,
    "xxe.docx": xxe_docx,
    "xxe.odt": xxe_odt,
    "archive.zip": archive_zip,
    "huge.png": huge_png,
    "javascript.pdf": javascript_pdf,
    "attachment.pdf": attachment_pdf,
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
