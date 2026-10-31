"""Page ranges, `partial` and the TIFF frame cut (parser design §7.3, §8, §9.1, §11) with a fake Docling converter.

The conversion logic around Docling runs for real (pypdfium2, Pillow, the result mapping); only the converter call is
replaced, so a deadline that stops Docling after some pages can be shown without timing a real conversion.
"""

import io
from types import SimpleNamespace

import pytest
from docling.datamodel.base_models import ConversionStatus
from docling_core.types.doc import BoundingBox, DocItemLabel, DoclingDocument, ProvenanceItem, Size
from PIL import Image

from helpers import fixture_bytes
from ume_parser import convert, limits
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest


class FakeConverter:
    """Answers like Docling: pages `done` (numbers as Docling sees them) converted, one paragraph each."""

    def __init__(self, done: list[int], status=ConversionStatus.SUCCESS, timeout: bool = False) -> None:
        self.done, self.status, self.timeout = done, status, timeout
        self.calls: list[dict] = []

    def convert(self, stream, raises_on_error=True, page_range=None):
        self.calls.append({"name": stream.name, "data": stream.stream.getvalue(), "page_range": page_range})
        doc = DoclingDocument(name="fake")
        for n in self.done:
            doc.add_page(page_no=n, size=Size(width=595, height=842))
            box = BoundingBox(l=50, t=800, r=300, b=780)
            doc.add_text(
                label=DocItemLabel.TEXT,
                text=f"Sidan {n} handlar om bygglov.",
                prov=ProvenanceItem(page_no=n, bbox=box, charspan=(0, 10)),
            )
        layout = SimpleNamespace(clusters=[])
        pages = [
            SimpleNamespace(page_no=n, assembled=object(), predictions=SimpleNamespace(layout=layout))
            for n in self.done
        ]
        return SimpleNamespace(
            status=self.status if self.done else ConversionStatus.FAILURE,
            pages=pages,
            document=doc,
            errors=[],
            has_timeout_errors=lambda: self.timeout,
        )


class FakePipelines(convert.Pipelines):
    def __init__(self, converter: FakeConverter) -> None:
        super().__init__(models_dir=None, threads=1)  # type: ignore[arg-type]
        self.converter, self.option_sets = converter, []

    def get(self, option_set):
        self.option_sets.append(option_set)
        return convert.Pipeline(self.converter, SimpleNamespace(document_timeout=None))


def _run(tmp_path, name: str, fmt: str, converter: FakeConverter, **options) -> tuple[dict, FakePipelines]:
    path = tmp_path / "input"
    path.write_bytes(fixture_bytes(name))
    request = ConvertRequest.model_validate({"request_id": "r-1", "file_ext": "." + name.rsplit(".", 1)[1], **options})
    job = {
        "request_id": "r-1",
        "input": str(path),
        "dir": str(tmp_path),
        "format": fmt,
        "options": request.model_dump(mode="json", by_alias=True),
    }
    pipelines = FakePipelines(converter)
    return convert.run_job(job, pipelines), pipelines


def test_a_deadline_after_some_pages_answers_partial(tmp_path):
    out, _ = _run(tmp_path, "text-layer.pdf", "pdf", FakeConverter([1, 2], ConversionStatus.PARTIAL_SUCCESS))
    assert out["status"] == "partial"
    assert (out["pages_requested"], out["pages_completed"]) == ({"from": 1, "to": 3}, {"from": 1, "to": 2})
    assert [p["page"] for p in out["pages"]] == [1, 2]
    assert {b["page"] for b in out["blocks"]} == {1, 2}
    assert {"code": "deadline_partial", "page": 3} in out["warnings"]


def test_a_range_keeps_absolute_page_numbers_and_is_passed_to_docling(tmp_path):
    converter = FakeConverter([2, 3])
    out, _ = _run(tmp_path, "text-layer.pdf", "pdf", converter, pages={"from": 2, "to": 3})
    assert converter.calls[0]["page_range"] == (2, 3)
    assert (out["status"], out["page_count"], out["pages_requested"]) == ("success", 3, {"from": 2, "to": 3})
    assert [p["page"] for p in out["pages"]] == [2, 3]


def test_nothing_converted_before_the_deadline_is_timeout(tmp_path):
    with pytest.raises(ParserError) as raised:
        _run(tmp_path, "text-layer.pdf", "pdf", FakeConverter([], timeout=True))
    assert (raised.value.code, raised.value.retry) == ("timeout", "smaller_range")


def test_a_range_past_the_end_is_refused(tmp_path):
    # The worker plans ranges from /v1/inspect's page count, so a range past the end is a planning bug (P0 case
    # `range-past-the-end`); a range over 20 pages is refused by the API before a slot (test_api).
    with pytest.raises(ParserError) as raised:
        _run(tmp_path, "text-layer.pdf", "pdf", FakeConverter([1]), pages={"from": 3, "to": 4})
    assert (raised.value.code, raised.value.retry) == ("invalid_request", "none")


def test_a_tiff_range_is_cut_out_and_renumbered(tmp_path):
    converter = FakeConverter([1, 2])  # Docling sees the cut-out frames as pages 1 and 2
    out, pipelines = _run(tmp_path, "frames.tiff", "tiff", converter, pages={"from": 2, "to": 3}, ocr={"mode": "off"})
    with Image.open(io.BytesIO(converter.calls[0]["data"])) as cut:
        assert cut.n_frames == 2
    assert converter.calls[0]["name"] == "r-1.tiff" and converter.calls[0]["page_range"] is None
    assert (out["format"], out["page_count"], out["pages_completed"]) == ("tiff", 3, {"from": 2, "to": 3})
    assert [p["page"] for p in out["pages"]] == [2, 3]
    assert [b["page"] for b in out["blocks"]] == [2, 3]
    page = out["pages"][0]
    assert (page["width"], page["signals"]["image_coverage"], page["signals"]["text_layer_chars"]) == (595, 1.0, None)
    # Images are always OCR'd: `off` is read as `auto`, with a warning (§9.1).
    assert pipelines.option_sets[0].ocr == "auto"
    assert {"code": "ocr_off_ignored", "page": None} in out["warnings"]


def test_a_single_image_takes_no_range(tmp_path):
    out, _ = _run(tmp_path, "scan.png", "png", FakeConverter([1]))
    assert (out["format"], out["page_count"], out["pages_requested"]) == ("png", 1, {"from": 1, "to": 1})
    with pytest.raises(ParserError) as raised:
        _run(tmp_path, "scan.png", "png", FakeConverter([1]), pages={"from": 1, "to": 1})
    assert raised.value.code == "invalid_request"


def test_render_scale_is_lowered_for_huge_pages(tmp_path, monkeypatch):
    assert convert.render_scale(2.0, 595 * 842) == 2.0
    assert convert.render_scale(2.0, 5000 * 5000) == 1.5  # 25 MP at 1.5² = 56 MP, under the 60 MP limit
    assert convert.render_scale(4.0, 50_000 * 50_000) == convert.MIN_IMAGE_SCALE
    monkeypatch.setattr(limits, "MAX_RENDER_PIXELS", 1_000_000)
    out, pipelines = _run(tmp_path, "text-layer.pdf", "pdf", FakeConverter([1, 2, 3]))
    assert pipelines.option_sets[0].image_scale == 1.25  # A4 at 1.25² is 0.78 MP
    assert {"code": "render_scale_reduced", "page": None} in out["warnings"]


def test_the_document_page_limit(tmp_path):
    from scripts import make_fixtures

    path = tmp_path / "input"
    path.write_bytes(make_fixtures.long_pdf())
    request = ConvertRequest.model_validate({"request_id": "r-2", "file_ext": ".pdf", "pages": {"from": 1, "to": 1}})
    job = {"request_id": "r-2", "input": str(path), "dir": str(tmp_path), "format": "pdf"}
    with pytest.raises(ParserError) as raised:
        convert.run_job(
            {**job, "options": request.model_dump(mode="json", by_alias=True)}, FakePipelines(FakeConverter([1]))
        )
    assert (raised.value.code, raised.value.retry) == ("too_many_pages", "none")
