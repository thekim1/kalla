import pypdfium2 as pdfium

from helpers import fixture_bytes
from ume_parser import selftest, textlayer


def _convert(data: bytes, first: int, last: int):
    pdf = pdfium.PdfDocument(data)
    try:
        return textlayer.convert(pdf, first, last)
    finally:
        pdf.close()


def test_headings_by_font_size_paragraphs_by_gap_and_footers_as_furniture():
    pages, blocks = _convert(fixture_bytes("text-layer.pdf"), 1, 2)
    assert [p.facts.page for p in pages] == [1, 2]
    shape = [(b["page"], b["kind"], b["level"], b["layer"]) for b in blocks]
    assert shape == [
        (1, "heading", 1, "body"),
        (1, "paragraph", None, "body"),
        (1, "heading", 2, "body"),
        (1, "paragraph", None, "body"),
        (1, "paragraph", None, "body"),
        (1, "heading", 2, "body"),
        (1, "paragraph", None, "body"),
        (1, "page_footer", None, "furniture"),
        (2, "heading", 2, "body"),
        (2, "paragraph", None, "body"),
        (2, "paragraph", None, "body"),
        (2, "heading", 2, "body"),
        (2, "paragraph", None, "body"),
        (2, "page_footer", None, "furniture"),
    ]
    assert blocks[0]["md"] == "# Riktlinjer för bygglov i Umeå kommun"
    assert blocks[2]["md"] == "## 1 Ansökan"
    assert blocks[1]["md"].startswith("De här riktlinjerna beskriver hur Umeå kommun handlägger")
    assert blocks[1]["md"].endswith("inom kommunens gränser.")  # three lines joined into one paragraph
    assert blocks[7]["md"] == "Sida 1 av 3"
    assert all(b["level_source"] == "font_size" for b in blocks if b["kind"] == "heading")
    assert all(b["ref"] is None and b["table"] is None for b in blocks)
    # Top-left origin: the title is above the footer.
    assert blocks[0]["bbox"][1] < blocks[7]["bbox"][1]


def test_a_ruled_table_is_read_as_text_rows():
    # Nine ruling lines are below the table-dense threshold, so the fast path may take the page; the rows survive as
    # text, the structure does not (to-verify #150).
    pages, blocks = _convert(fixture_bytes("text-layer.pdf"), 3, 3)
    assert pages[0].facts.clean
    assert pages[0].facts.path_objects == 9
    assert [b["md"] for b in blocks if b["kind"] == "paragraph"][1:] == [
        "Åtgärd Avgift Handläggningstid",
        "Bygglov för enbostadshus 25 000 kr 10 veckor",
        "Marklov 8 000 kr 10 veckor",
        "Rivningslov 4 500 kr 4 veckor",
    ]


def test_a_range_keeps_absolute_page_numbers():
    pages, blocks = _convert(fixture_bytes("text-layer.pdf"), 2, 2)
    assert [p.facts.page for p in pages] == [2]
    assert {b["page"] for b in blocks} == {2}
    # Levels rank the sizes seen in the request: page 2 alone has one heading size.
    assert blocks[0]["md"] == "# 3 Startbesked och slutbesked"


def test_scanned_page_gives_no_blocks():
    pages, blocks = _convert(fixture_bytes("scanned.pdf"), 1, 1)
    assert blocks == []
    assert pages[0].lines == []


def test_self_test_page_passes_the_text_layer_check():
    _, blocks = _convert(selftest.make_pdf(), 1, 1)
    assert selftest.check_text_layer([b["md"] for b in blocks]) == []


def test_committed_fixtures_match_the_generator():
    from helpers import REPO_FIXTURES
    from scripts import make_fixtures

    if not REPO_FIXTURES.is_dir():
        return
    for name, make in make_fixtures.FIXTURES.items():
        assert (REPO_FIXTURES / name).read_bytes() == make(), f"{name}: run scripts/make_fixtures.py"
