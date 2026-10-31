"""The OCR acceptance check (parser design §9.3; to-verify #131) and the fast-path comparison script (#132) on the
repository's synthetic material, through a real slot's conversion.

Needs the baked models and Tesseract, so it runs in the image and is skipped elsewhere. The real Umeå pages of the
acceptance sample and the 200 umea.se PDFs are measured with the same scripts on the test host (the live block).
"""

import shutil
from pathlib import Path
from typing import Any

import pytest

from helpers import fixture_bytes
from scripts import compare_paths, make_fixtures, ocr_check
from ume_parser import convert, inspect, slot
from ume_parser.models import ConvertRequest
from ume_parser.settings import Settings
from ume_parser.sniff import sniff_file

pytestmark = pytest.mark.skipif(
    not (Settings().models_dir / "docling-project--docling-layout-heron").is_dir() or shutil.which("tesseract") is None,
    reason="needs the baked models and Tesseract (run in the image)",
)


class SlotParser:
    """The scripts' parser interface on a slot's own functions, without HTTP."""

    def __init__(self, work: Path) -> None:
        pipeline, problems = slot.warm_up(Settings().models_dir, threads=2)
        assert problems == []
        self.pipelines = convert.Pipelines(Settings().models_dir, 2, default=pipeline)
        self.work = work

    def _job(self, data: bytes, request_id: str) -> dict[str, Any]:
        job_dir = self.work / request_id
        job_dir.mkdir(parents=True, exist_ok=True)
        path = job_dir / "input"
        path.write_bytes(data)
        return {
            "request_id": request_id,
            "input": str(path),
            "dir": str(job_dir),
            "format": sniff_file(path, data[:1024]).format,
        }

    def convert(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]:
        request = ConvertRequest.model_validate(options)
        job = self._job(data, request.request_id)
        return convert.run_job({**job, "options": request.model_dump(mode="json", by_alias=True)}, self.pipelines)

    def inspect(self, data: bytes, options: dict[str, Any]) -> dict[str, Any]:
        return inspect.inspect(self._job(data, options["request_id"]))


@pytest.fixture(scope="module")
def parser(tmp_path_factory):
    return SlotParser(tmp_path_factory.mktemp("jobs"))


def test_synthetic_ocr_sample_passes_the_acceptance_check(parser, tmp_path, capsys):
    make_fixtures.ocr_sample(tmp_path)
    results = ocr_check.run(parser, tmp_path)
    total = ocr_check.summary(results)
    with capsys.disabled():
        print("\n" + ocr_check.report(results, "synthetic sample (repository)"))
    assert total["pages"] == make_fixtures.OCR_SAMPLE_PAGES
    assert total["letters"] > 500
    assert total["recall"] >= ocr_check.PASS_RECALL


def test_compare_paths_on_the_fixture_pdfs(parser, tmp_path, capsys):
    urls = []
    for name in ("text-layer.pdf", "scanned.pdf", "javascript.pdf"):
        path = tmp_path / name
        path.write_bytes(fixture_bytes(name))
        urls.append(path.as_uri())
    results = compare_paths.run(parser, urls, tmp_path / "cache", max_pages=200)
    with capsys.disabled():
        print("\n" + compare_paths.report(results, urls, "fixture PDFs"))
    assert results.documents == 3 and results.failures == {}
    # text-layer.pdf: three clean pages (its one ruled table has 9 path objects, below rule 4's 40); scanned.pdf:
    # OCR; javascript.pdf: one line of text, a blank page by rule 1.
    assert results.strategies == {"text-layer": 3, "docling-ocr": 1, "blank": 1}
    text_layer = [c for c in results.compared if c.document == 1]
    assert all(c.similarity >= compare_paths.AGREE for c in text_layer if c.page in (1, 2))
    assert [c.docling_tables for c in text_layer if c.page == 3] == [1]  # the table the fast path reads as text
