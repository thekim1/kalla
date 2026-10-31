"""Real conversions of the fixtures with Docling and the text-layer fast path (parser design §15.1, integration).

Needs the baked models and Tesseract, so it runs in the image (`docker build --target test`) and is skipped
elsewhere. Covers to-verify #58 (absolute page numbers under a range), #61 (OCR confidence after conversion), #62
(content layers, heading levels, JSON pointers) and #150 (fast path against Docling on clean pages), and the image
inputs: PNG, JPEG and TIFF frames through Docling's OCR.
"""

import difflib
import gzip
import json
import re
import shutil

import pytest

from helpers import fixture_bytes
from ume_parser import convert, slot
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest
from ume_parser.settings import Settings
from ume_parser.sniff import sniff

pytestmark = pytest.mark.skipif(
    not (Settings().models_dir / "docling-project--docling-layout-heron").is_dir() or shutil.which("tesseract") is None,
    reason="needs the baked models and Tesseract (run in the image)",
)


@pytest.fixture(scope="module")
def pipelines():
    pipeline, problems = slot.warm_up(Settings().models_dir, threads=2)
    assert problems == []
    return convert.Pipelines(Settings().models_dir, 2, default=pipeline)


def _job(tmp_path, name: str, **options) -> dict:
    path = tmp_path / "input"
    data = fixture_bytes(name)
    path.write_bytes(data)
    request = ConvertRequest.model_validate({"request_id": "t-1", "file_ext": "." + name.rsplit(".", 1)[1], **options})
    return {
        "request_id": "t-1",
        "input": str(path),
        "dir": str(tmp_path),
        "format": sniff(data[:1024]).format,
        "options": request.model_dump(mode="json", by_alias=True),
    }


def _body_text(out: dict, page: int) -> str:
    texts = [re.sub(r"^#+ ", "", b["md"]) for b in out["blocks"] if b["page"] == page and b["layer"] == "body"]
    return " ".join(" ".join(texts).split())


def test_fast_path_on_a_clean_text_layer(tmp_path, pipelines):
    out = convert.run_job(_job(tmp_path, "text-layer.pdf", pipeline="text_layer"), pipelines)
    assert out["pipeline"] == "text_layer"
    assert out["status"] == "success"
    assert out["docling_ms"] == 0 and not out["docling_json"]
    assert [p["signals"]["ocr_applied"] for p in out["pages"]] == ["none", "none", "none"]
    assert not (tmp_path / convert.DOCLING_JSON).exists()


def test_fast_path_agrees_with_docling_on_clean_pages(tmp_path, pipelines):
    fast = convert.run_job(_job(tmp_path, "text-layer.pdf", pipeline="text_layer"), pipelines)
    docling = convert.run_job(_job(tmp_path, "text-layer.pdf", ocr={"mode": "off"}), pipelines)
    assert docling["pipeline"] == "docling"
    for page in (1, 2):
        ratio = difflib.SequenceMatcher(None, _body_text(fast, page), _body_text(docling, page)).ratio()
        assert ratio >= 0.98, (page, ratio)
    # Docling labels the page footers as furniture too.
    assert [b["kind"] for b in docling["blocks"] if b["layer"] == "furniture"] == ["page_footer"] * 3
    assert all(b["ref"].startswith(("#/texts/", "#/tables/")) for b in docling["blocks"])
    # The ruled table on page 3: one table block pointing at a table with its header row.
    (table_block,) = [b for b in docling["blocks"] if b["kind"] == "table"]
    (table,) = docling["tables"]
    assert (table_block["page"], table_block["table"], table["id"]) == (3, "t0", "t0")
    assert (table["rows"], table["cols"], table["header_rows"]) == (4, 3, 1)
    assert [c["text"] for c in table["cells"] if c["r"] == 0] == ["Åtgärd", "Avgift", "Handläggningstid"]
    # TableFormer marks the first column as row headers and leaves the corner cell unmarked.
    assert {c["text"] for c in table["cells"] if c["header"]} == {
        "Avgift",
        "Handläggningstid",
        "Bygglov för enbostadshus",
        "Marklov",
        "Rivningslov",
    }
    assert docling["pages"][2]["signals"]["table_count"] == 1


def test_scanned_page_takes_docling_with_ocr(tmp_path, pipelines):
    out = convert.run_job(_job(tmp_path, "scanned.pdf", outputs={"markdown": True}), pipelines)
    assert out["pipeline"] == "docling"
    (page,) = out["pages"]
    assert page["signals"]["ocr_applied"] == "bitmap"
    assert page["signals"]["text_layer_chars"] == 0
    assert page["signals"]["image_coverage"] == 1.0
    confidence = page["signals"]["ocr_confidence"]
    assert 0 < confidence["p10"] <= confidence["mean"] <= 1
    text = _body_text(out, 1)
    for expected in ("Kallelse till samråd", "Samhällsbyggnadsnämnden", "Älgen", "Väven"):
        assert expected in text
    assert out["markdown"].startswith("<!-- page: 1 -->\n\n")
    document = json.loads(gzip.decompress((tmp_path / convert.DOCLING_JSON).read_bytes()))
    assert document["schema_name"] == "DoclingDocument"


def test_text_layer_request_for_a_scanned_page_falls_back_to_docling(tmp_path, pipelines):
    out = convert.run_job(_job(tmp_path, "scanned.pdf", pipeline="text_layer"), pipelines)
    assert out["pipeline"] == "docling"
    assert out["warnings"] == [{"code": "text_layer_rejected", "page": 1}]
    assert out["pages"][0]["signals"]["ocr_applied"] == "bitmap"


def test_page_range_keeps_absolute_page_numbers(tmp_path, pipelines):
    out = convert.run_job(_job(tmp_path, "text-layer.pdf", pages={"from": 2, "to": 2}, ocr={"mode": "off"}), pipelines)
    assert out["pages_completed"] == {"from": 2, "to": 2}
    assert [p["page"] for p in out["pages"]] == [2]
    assert {b["page"] for b in out["blocks"]} == {2}


@pytest.mark.parametrize(
    ("name", "options", "code"),
    [
        ("corrupt.pdf", {}, "corrupt"),
        ("text-layer.pdf", {"pages": {"from": 3, "to": 4}}, "invalid_request"),
    ],
)
def test_errors(tmp_path, pipelines, name, options, code):
    with pytest.raises(ParserError) as raised:
        convert.run_job(_job(tmp_path, name, **options), pipelines)
    assert raised.value.code == code


def test_docling_stops_at_the_request_deadline(tmp_path, pipelines):
    # deadline 1 s: Docling's own timeout is 0.5 s, shorter than the OCR of the scanned page.
    with pytest.raises(ParserError) as raised:
        convert.run_job(_job(tmp_path, "scanned.pdf", deadline_ms=1000, ocr={"mode": "force"}), pipelines)
    assert raised.value.code == "timeout"
    # The per-request timeout is set on the pipeline's own options copy, not on the options Docling hashes.
    pipeline = pipelines.get(convert.OptionSet("force", "accurate", 2.0))
    assert pipeline.run_options.document_timeout == 0.5
    assert len(pipeline.converter.initialized_pipelines) == 1


@pytest.mark.parametrize("name", ["scan.png", "scan.jpg"])
def test_images_are_ocrd(tmp_path, pipelines, name):
    out = convert.run_job(_job(tmp_path, name, outputs={"markdown": True}), pipelines)
    assert (out["format"], out["pipeline"], out["page_count"], out["status"]) == (
        name.rsplit(".", 1)[1].replace("jpg", "jpeg"),
        "docling",
        1,
        "success",
    )
    (page,) = out["pages"]
    assert page["signals"]["ocr_applied"] == "full-page"
    assert page["signals"]["ocr_confidence"]["mean"] > 0.5
    text = _body_text(out, 1)
    for expected in ("Kungörelse", "granskning", "Byggnadsnämnden", "Björken", "Ålidhem"):
        assert expected in text


def test_a_tiff_range_reads_only_its_frames_with_original_numbers(tmp_path, pipelines):
    out = convert.run_job(_job(tmp_path, "frames.tiff", pages={"from": 2, "to": 3}), pipelines)
    assert (out["page_count"], out["pages_completed"]) == (3, {"from": 2, "to": 3})
    assert [p["page"] for p in out["pages"]] == [2, 3]
    assert "Sökanden" in _body_text(out, 2) and "förslaget" in _body_text(out, 2)
    assert "förhandsbesked" in _body_text(out, 3)
    document = json.loads(gzip.decompress((tmp_path / convert.DOCLING_JSON).read_bytes()))
    assert sorted(int(n) for n in document["pages"]) == [2, 3]
