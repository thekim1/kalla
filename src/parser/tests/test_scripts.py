"""The measuring scripts' own logic: `ocr_check` metrics and `compare_paths` rules, ranges and report."""

import pytest

from scripts import compare_paths, ocr_check


def _page(**signals) -> dict:
    base = {"text_chars": 1500, "image_coverage": 0.0, "path_objects": 3, "garbled_score": 0.0, "bad_chars": 0}
    return {**base, **signals}


@pytest.mark.parametrize(
    ("signals", "expected"),
    [
        ({"text_chars": 10}, "blank"),
        ({"text_chars": 10, "path_objects": 250}, "docling-ocr-force"),
        ({"garbled_score": 0.45}, "docling-ocr-force"),
        ({"bad_chars": 200}, "docling-ocr-force"),
        ({"path_objects": 40}, "docling-tables"),
        ({}, "text-layer"),
        ({"text_chars": 80, "image_coverage": 0.2}, "text-layer"),
        ({"text_chars": 80, "image_coverage": 0.6}, "docling-ocr"),
        ({"garbled_score": 0.3}, "docling-ocr"),
    ],
)
def test_strategy_follows_the_worker_rules(signals, expected):
    assert compare_paths.strategy(_page(**signals)) == expected


def test_runs_cut_consecutive_pages():
    assert compare_paths.runs([1, 2, 3, 5, 6, 9]) == [(1, 3), (5, 6), (9, 9)]
    assert compare_paths.runs(list(range(1, 46))) == [(1, 20), (21, 40), (41, 45)]


def test_read_urls_skips_comments(tmp_path):
    path = tmp_path / "urls.txt"
    path.write_text("# sample\nhttps://example.org/a.pdf\n\n  https://example.org/b.pdf  # second\n", encoding="utf-8")
    assert compare_paths.read_urls(path) == ["https://example.org/a.pdf", "https://example.org/b.pdf"]


class CannedParser:
    """Two text-layer pages and one scanned page; Docling reorders page 2 and finds a table there."""

    def inspect(self, data, options):
        pages = [{"page": n, **_page()} for n in (1, 2)] + [{"page": 3, **_page(text_chars=0, image_coverage=1.0)}]
        return {"format": "pdf", "refusal": None, "pages": pages}

    def convert(self, data, options):
        docling = options.get("pipeline") != "text_layer"
        second = "andra sidan text här" if docling else "här text andra sidan"
        blocks = [
            {"page": 1, "layer": "body", "kind": "heading", "md": "# Rubrik"},
            {"page": 1, "layer": "body", "kind": "paragraph", "md": "första sidan"},
            {"page": 2, "layer": "body", "kind": "paragraph", "md": second},
        ]
        if docling:
            blocks.append({"page": 2, "layer": "body", "kind": "table", "md": "| a |"})
        return {"blocks": blocks}


def test_compare_reports_strategies_agreement_and_disagreement(tmp_path):
    pdf = tmp_path / "a.pdf"
    pdf.write_bytes(b"%PDF-1.7")
    urls = [str(pdf), str(tmp_path / "missing.pdf")]
    results = compare_paths.run(CannedParser(), urls, tmp_path / "cache", max_pages=200)
    assert results.strategies == {"text-layer": 2, "docling-ocr": 1}  # the scanned page goes to OCR (rule 6)
    assert [(c.page, c.similarity == 1.0, c.docling_tables) for c in results.compared] == [(1, True, 0), (2, False, 1)]
    assert results.failures == {"download": 1}
    text = compare_paths.report(results, urls, "fixtures")
    assert "| `text-layer` | 2 | 66.7% |" in text
    assert "## Pages that disagree most" in text and "| download | 1 |" in text
    assert "första" not in text  # numbers and URLs only


def test_letter_recall_and_cer():
    assert ocr_check.letter_hits("Ärendet på Ön", "Ärendet på Ön") == (3, 3)
    assert ocr_check.letter_hits("Ärendet på Ön", "Arendet pa Ön") == (3, 1)
    assert ocr_check.letter_hits("så", "sa extra text") == (1, 0)
    assert ocr_check.levenshtein("kommun", "kommnn") == 1
    page = ocr_check.PageResult("p", letters=4, hits=3, distance=2, length=40, ms=10)
    assert (page.recall, page.cer) == (0.75, 0.05)


def test_ocr_report_passes_at_95_percent():
    good = [ocr_check.PageResult(f"p{i}", 20, 19, 1, 100, 50) for i in range(2)]
    assert "**pass**" in ocr_check.report(good, "t")
    assert "**fail**" in ocr_check.report([ocr_check.PageResult("p", 20, 18, 1, 100, 50)], "t")
