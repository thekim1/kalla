import pypdfium2 as pdfium

from ume_parser import selftest


def _text_layer(pdf: bytes) -> str:
    document = pdfium.PdfDocument(pdf)
    try:
        return document[0].get_textpage().get_text_range()
    finally:
        document.close()


def test_pdf_has_a_text_layer_and_a_scanned_strip():
    text = _text_layer(selftest.make_pdf())
    assert selftest.HEADING in text
    assert selftest.PARAGRAPH in text
    assert all(cell in text for row in selftest.TABLE for cell in row)
    # The strip is an image only: OCR has to find it.
    assert not any(line in text for line in selftest.SCANNED)


def test_check_passes_a_good_conversion():
    markdown = "\n".join(
        [selftest.PARAGRAPH, "| Bygglov | 1 200 kr |", "| Marklov | 800 kr |", " ".join(selftest.SCANNED)]
    )
    assert selftest.check(markdown, table_count=1) == []


def test_check_reports_missing_ocr_and_table():
    problems = selftest.check(selftest.PARAGRAPH, table_count=0)
    assert "missing text: Välkommen" in problems
    assert "no table detected" in problems
