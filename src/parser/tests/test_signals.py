import pypdfium2 as pdfium
import pytest

from helpers import fixture_bytes
from ume_parser import signals


@pytest.mark.parametrize(
    ("text", "low", "high"),
    [
        ("", 0.0, 0.0),
        ("Kommunfullmäktige beslutade att anta budgeten för år 2027.", 0.0, 0.0),
        ("Avgift 1 200 kr, marklov 800 kr, rivningslov 2 400 kr.", 0.0, 0.0),
        ("KommunfullmÃ¤ktige beslutade Ã¶ver bÃ¥tplatserna i hamnen", 0.6, 1.0),
        ("(cid:12)(cid:15)(cid:20)(cid:3) Umeå", 0.6, 1.0),
        ("���� abc", 0.5, 1.0),
        (" kommun", 0.25, 1.0),
        (". , ; : - . , ; : - ! ? abc", 0.4, 1.0),
        (" ".join("abcdefghijklmnopqrstuvw"), 0.9, 1.0),  # one letter per token: scan noise
    ],
)
def test_garbled_score(text, low, high):
    assert low <= signals.garbled_score(text) <= high


def test_union_area_counts_overlaps_once():
    assert signals._union_area([(0, 0, 10, 10), (5, 5, 15, 15)]) == pytest.approx(175)
    assert signals._union_area([(0, 0, 10, 10), (0, 0, 10, 10)]) == pytest.approx(100)
    assert signals._union_area([]) == 0


def _facts(name: str) -> list[signals.PageFacts]:
    pdf = pdfium.PdfDocument(fixture_bytes(name))
    try:
        return [signals.page_facts(pdf[i], i + 1) for i in range(len(pdf))]
    finally:
        pdf.close()


def test_text_layer_pages_are_clean():
    facts = _facts("text-layer.pdf")
    assert [f.page for f in facts] == [1, 2, 3]
    assert all(f.clean and f.text_chars > 200 and f.image_coverage == 0 and f.garbled_score == 0 for f in facts)
    assert (facts[0].width, facts[0].height) == (595, 842)


def test_scanned_page_has_no_text_and_full_image_coverage():
    (facts,) = _facts("scanned.pdf")
    assert facts.text_chars == 0
    assert facts.image_coverage == 1.0
    assert not facts.clean
