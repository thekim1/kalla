"""The warm-up self-test of a conversion slot (parser design §10.2).

`make_pdf` builds a one-page PDF in memory: a heading and a paragraph in the text layer, a small ruled table, and a
"scanned" strip, a JPEG of rendered text with no text layer, so the page needs OCR. `check` lists what a conversion
of it got wrong. The text uses å, ä and ö, so a wrong Tesseract language shows up here and not in production.
"""

import ctypes
import io

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c

PAGE_WIDTH, PAGE_HEIGHT = 595, 842
HEADING = "Självtest för tolken"
PARAGRAPH = "Umeå kommun tar emot ansökningar om bygglov och marklov."
TABLE = [("Avgift", "Belopp"), ("Bygglov", "1 200 kr"), ("Marklov", "800 kr")]
# Two lines: Tesseract's orientation detection, which Docling runs first, needs this much text.
SCANNED = (
    "Välkommen till Umeå kommun. Här finns information om bygglov,",
    "förskola, äldreomsorg och återvinning för alla som bor här.",
)

# Expected in the Markdown export: text layer, table cells, OCR of the strip.
EXPECTED_TEXT = ("Umeå kommun tar emot", "Bygglov", "Marklov", "Välkommen", "äldreomsorg och återvinning")

_SCAN_DPI = 200
_SCAN_BOX = (60, 300, 480, 90)  # x, y (bottom), width, height in points


def _text(pdf: pdfium.PdfDocument, page: pdfium.PdfPage, text: str, x: float, y: float, size: float) -> None:
    raw = pdfium_c.FPDFPageObj_NewTextObj(pdf, b"Helvetica", ctypes.c_float(size))
    wide = ctypes.create_string_buffer((text + "\0").encode("utf-16-le"))
    if not pdfium_c.FPDFText_SetText(raw, ctypes.cast(wide, ctypes.POINTER(pdfium_c.FPDF_WCHAR))):
        raise RuntimeError("FPDFText_SetText failed")
    obj = pdfium.PdfObject(raw, pdf=pdf)
    obj.set_matrix(pdfium.PdfMatrix().translate(x, y))
    page.insert_obj(obj)


def _line(pdf: pdfium.PdfDocument, page: pdfium.PdfPage, x0: float, y0: float, x1: float, y1: float) -> None:
    raw = pdfium_c.FPDFPageObj_CreateNewPath(ctypes.c_float(x0), ctypes.c_float(y0))
    pdfium_c.FPDFPath_LineTo(raw, ctypes.c_float(x1), ctypes.c_float(y1))
    pdfium_c.FPDFPath_SetDrawMode(raw, pdfium_c.FPDF_FILLMODE_NONE, True)
    pdfium_c.FPDFPageObj_SetStrokeWidth(raw, ctypes.c_float(0.8))
    pdfium_c.FPDFPageObj_SetStrokeColor(raw, 0, 0, 0, 255)
    page.insert_obj(pdfium.PdfObject(raw, pdf=pdf))


def _table(pdf: pdfium.PdfDocument, page: pdfium.PdfPage, x: float, top: float) -> None:
    col_width, row_height = 160, 24
    rows, cols = len(TABLE), len(TABLE[0])
    for r in range(rows + 1):
        y = top - r * row_height
        _line(pdf, page, x, y, x + cols * col_width, y)
    for c in range(cols + 1):
        _line(pdf, page, x + c * col_width, top, x + c * col_width, top - rows * row_height)
    for r, row in enumerate(TABLE):
        for c, cell in enumerate(row):
            _text(pdf, page, cell, x + c * col_width + 6, top - (r + 1) * row_height + 8, 11)


def _scan_jpeg() -> bytes:
    """`SCANNED` rendered by pdfium into a grey JPEG, so the image needs no font files."""
    _, _, width, height = _SCAN_BOX
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(width, height)
    for i, line in enumerate(SCANNED):
        _text(pdf, page, line, 10, height * (0.6 - 0.4 * i), 14)
    page.gen_content()
    image = page.render(scale=_SCAN_DPI / 72, grayscale=True).to_pil()
    pdf.close()
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=85, dpi=(_SCAN_DPI, _SCAN_DPI))
    return buffer.getvalue()


def make_pdf() -> bytes:
    pdf = pdfium.PdfDocument.new()
    page = pdf.new_page(PAGE_WIDTH, PAGE_HEIGHT)
    _text(pdf, page, HEADING, 60, 760, 18)
    _text(pdf, page, PARAGRAPH, 60, 720, 11)
    _table(pdf, page, 60, 660)

    x, y, width, height = _SCAN_BOX
    image = pdfium.PdfImage.new(pdf)
    image.load_jpeg(io.BytesIO(_scan_jpeg()), inline=True)
    image.set_matrix(pdfium.PdfMatrix().scale(width, height).translate(x, y))
    page.insert_obj(image)

    page.gen_content()
    buffer = io.BytesIO()
    pdf.save(buffer)
    pdf.close()
    return buffer.getvalue()


def check(markdown: str, table_count: int) -> list[str]:
    """Problems found in the conversion of `make_pdf()`; empty when the slot works."""
    problems = [f"missing text: {text}" for text in EXPECTED_TEXT if text not in markdown]
    if table_count < 1:
        problems.append("no table detected")
    return problems
