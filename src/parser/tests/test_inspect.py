"""`inspect-0`'s work on the fixtures (parser design §6.2): no models, so it runs everywhere."""

import pytest

from helpers import fixture_bytes
from scripts import make_fixtures
from ume_parser import inspect
from ume_parser.errors import ParserError


def _inspect(tmp_path, name: str, fmt: str | None, data: bytes | None = None) -> dict:
    path = tmp_path / "input"
    path.write_bytes(data if data is not None else fixture_bytes(name))
    return inspect.inspect({"request_id": "i-1", "input": str(path), "dir": str(tmp_path), "format": fmt})


def test_pdf_pages_carry_text_layer_facts(tmp_path):
    out = _inspect(tmp_path, "text-layer.pdf", "pdf")
    assert (out["format"], out["page_count"], out["rangeable"], out["encrypted"], out["refusal"]) == (
        "pdf",
        3,
        True,
        "none",
        None,
    )
    assert (out["has_javascript"], out["has_embedded_files"], out["has_xfa"]) == (False, False, False)
    assert [p["page"] for p in out["pages"]] == [1, 2, 3]
    first = out["pages"][0]
    assert first["text_chars"] > 200 and first["garbled_score"] == 0 and first["image_coverage"] == 0
    assert out["pages"][2]["path_objects"] >= 9  # the ruled table: rule 4's signal


def test_scanned_page_has_no_text_and_full_image_coverage(tmp_path):
    (page,) = _inspect(tmp_path, "scanned.pdf", "pdf")["pages"]
    assert (page["text_chars"], page["image_coverage"]) == (0, 1.0)


@pytest.mark.parametrize(
    ("name", "field"), [("javascript.pdf", "has_javascript"), ("attachment.pdf", "has_embedded_files")]
)
def test_active_content_is_reported(tmp_path, name, field):
    out = _inspect(tmp_path, name, "pdf")
    assert out[field] is True
    assert out["refusal"] is None
    assert out["pages"][0]["text_chars"] > 0


def test_refusals_keep_the_format(tmp_path):
    assert _inspect(tmp_path, "corrupt.pdf", "pdf")["refusal"] == "corrupt"
    too_long = _inspect(tmp_path, "", "pdf", make_fixtures.long_pdf())
    assert (too_long["page_count"], too_long["refusal"], too_long["pages"]) == (2001, "too_many_pages", [])
    huge = _inspect(tmp_path, "huge.png", "png")
    assert (huge["format"], huge["refusal"], huge["rangeable"]) == ("png", "limit_exceeded", False)
    bomb = _inspect(tmp_path, "bomb.docx", None)
    assert (bomb["format"], bomb["refusal"]) == ("docx", "limit_exceeded")


def test_tiff_frames_are_pages_with_their_resolution(tmp_path):
    out = _inspect(tmp_path, "frames.tiff", "tiff")
    assert (out["page_count"], out["rangeable"]) == (3, True)
    assert {p["dpi"] for p in out["pages"]} == {150.0}
    assert out["pages"][0]["width"] == pytest.approx(420, abs=1)


@pytest.mark.parametrize(
    ("name", "fmt", "pages"),
    [
        ("text.docx", "docx", None),
        ("text.odt", "odt", None),
        ("slides.pptx", "pptx", 2),
        ("slides.odp", "odp", 2),
        ("sheets.xlsx", "xlsx", 2),
        ("sheets.ods", "ods", 1),
    ],
)
def test_packages_count_slides_and_sheets(tmp_path, name, fmt, pages):
    out = _inspect(tmp_path, name, None)
    assert (out["format"], out["page_count"], out["rangeable"], out["encrypted"]) == (fmt, pages, False, "none")
    assert out["pages"] == []


def test_a_zip_that_is_no_document_has_no_format(tmp_path):
    with pytest.raises(ParserError) as raised:
        _inspect(tmp_path, "archive.zip", None)
    assert (raised.value.code, raised.value.reason) == ("unsupported_format", "archive")
