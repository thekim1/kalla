import json

import pypdfium2 as pdfium
import pytest

from helpers import REPO_FIXTURES, fixture_bytes
from ume_parser import signals

_VECTORS = REPO_FIXTURES / "garbled" / "vectors.json"


def _vectors() -> list:
    if not _VECTORS.is_file():
        return []  # the image has no repository; the runner's job runs these
    return [pytest.param(v, id=v["name"]) for v in json.loads(_VECTORS.read_text(encoding="utf-8"))["vectors"]]


@pytest.mark.skipif(not _VECTORS.is_file(), reason="needs tests/fixtures/parser/garbled/ (not in the image)")
@pytest.mark.parametrize("vector", _vectors())
def test_garbled_score_vectors(vector):
    assert vector["low"] <= signals.garbled_score(vector["text"]) <= vector["high"]


def test_bigram_model_separates_words_from_garble():
    assert signals.word_log_prob("kommunfullmäktige") > signals.IMPLAUSIBLE_LOG_PROB
    assert signals.word_log_prob("throughput") > signals.IMPLAUSIBLE_LOG_PROB
    assert signals.word_log_prob("nrppxqixoopçnwljh") < signals.IMPLAUSIBLE_LOG_PROB


def test_mojibake_counts_double():
    one_in_four = "kommunen beslutade UmeÃ¥ budgeten"
    assert signals.garbled_score(one_in_four) == 0.5


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
