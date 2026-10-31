"""DOCX, PPTX, XLSX, ODT, ODS and ODP through Docling's declarative backends (parser design §3, §7.3, §7.4).

No models and no Tesseract, so these run everywhere. Covers the to-verify items of §19 on the Excel backend (cached
formula values, hidden sheets), PPTX slide = page, DOCX without pages, and the XXE fixtures.
"""

import json

import pytest

from helpers import fixture_bytes
from ume_parser import assemble, convert, limits
from ume_parser.errors import ParserError
from ume_parser.models import ConvertRequest


@pytest.fixture(scope="module")
def pipelines(tmp_path_factory):
    return convert.Pipelines(tmp_path_factory.mktemp("no-models"), 1)


def _run(tmp_path, pipelines, name: str, **options) -> dict:
    path = tmp_path / "input"
    path.write_bytes(fixture_bytes(name))
    ext = "." + name.rsplit(".", 1)[1]
    request = ConvertRequest.model_validate({"request_id": "d-1", "file_ext": ext, **options})
    job = {
        "request_id": "d-1",
        "input": str(path),
        "dir": str(tmp_path),
        "format": None,
        "options": request.model_dump(mode="json", by_alias=True),
    }
    return convert.run_job(job, pipelines)


def _kinds(out: dict) -> list[tuple]:
    return [(b["kind"], b["level"], b["level_source"], b["page"]) for b in out["blocks"]]


@pytest.mark.parametrize("name", ["text.docx", "text.odt"])
def test_text_documents_have_style_headings_lists_and_a_table_and_no_pages(tmp_path, pipelines, name):
    out = _run(tmp_path, pipelines, name, outputs={"markdown": True})
    assert (out["status"], out["pipeline"], out["page_count"], out["pages_requested"]) == (
        "success",
        "docling",
        None,
        None,
    )
    kinds = [k for k, *_ in _kinds(out)]
    assert kinds.count("list_item") == 3 and kinds.count("table") == 1
    headings = [b for b in out["blocks"] if b["kind"] == "heading"]
    assert [b["md"].lstrip("# ") for b in headings][-2:] == ["1 Ansökan", "2 Avgifter"]
    assert {b["level_source"] for b in headings} == {"style"}
    assert {b["page"] for b in out["blocks"]} == {None}
    (page,) = out["pages"]
    assert (page["page"], page["width"], page["signals"]["table_count"]) == (None, None, 1)
    (table,) = out["tables"]
    assert (table["rows"], table["cols"], table["header_rows"]) == (3, 2, 1)
    assert "<!-- page:" not in out["markdown"] and "Åtgärd" in out["markdown"]
    assert (tmp_path / convert.DOCLING_JSON).is_file()


@pytest.mark.parametrize("name", ["slides.pptx", "slides.odp"])
def test_slides_are_pages_and_notes_stay_out(tmp_path, pipelines, name):
    out = _run(tmp_path, pipelines, name, outputs={"markdown": True})
    assert out["page_count"] == 2
    assert out["pages_requested"] == out["pages_completed"] == {"from": 1, "to": 2}
    assert [(p["page"], p["label"]) for p in out["pages"]] == [(1, "1"), (2, "2")]
    assert [b["md"] for b in out["blocks"] if b["kind"] == "title"] == ["# Bygglov i Umeå", "# Så går ansökan till"]
    assert {b["page"] for b in out["blocks"]} == {1, 2}
    assert "anteckningar" not in json.dumps(out, ensure_ascii=False)
    assert out["markdown"].startswith("<!-- page: 1 -->")


def test_workbook_sheets_are_pages_hidden_sheets_skipped_formulas_cached(tmp_path, pipelines):
    out = _run(tmp_path, pipelines, "sheets.xlsx")
    assert out["page_count"] == 2  # the hidden sheet counts, as the Excel backend numbers it
    assert [(p["page"], p["label"]) for p in out["pages"]] == [(1, "Avgifter")]
    (table,) = out["tables"]
    assert (table["rows"], table["cols"], table["header_rows"]) == (5, 3, 1)
    cells = {(c["r"], c["c"]): c["text"] for c in table["cells"]}
    assert cells[(4, 2)] == "37500"  # the formula's cached result, not the formula
    assert "Intern" not in json.dumps(out, ensure_ascii=False)


def test_ods_sheet_is_a_page(tmp_path, pipelines):
    out = _run(tmp_path, pipelines, "sheets.ods")
    assert [(p["page"], p["label"]) for p in out["pages"]] == [(1, "Avgifter")]
    assert [(t["rows"], t["cols"]) for t in out["tables"]] == [(4, 3)]


def test_spreadsheet_cells_beyond_the_limit_are_truncated(tmp_path, pipelines, monkeypatch):
    monkeypatch.setattr(limits, "MAX_SPREADSHEET_CELLS", 8)
    out = _run(tmp_path, pipelines, "sheets.xlsx")
    (table,) = out["tables"]
    assert (table["rows"], len(table["cells"])) == (2, 6)  # whole rows up to the limit
    assert {"code": "truncated_cells", "page": None} in out["warnings"]
    assert out["blocks"][0]["md"].count("\n") == 2  # header, separator, one row


def test_truncation_drops_later_tables():
    out = assemble.Assembled(
        blocks=[
            {"id": f"b{i}", "kind": "table", "page": 1, "layer": "body", "md": "", "table": f"t{i}"} for i in range(2)
        ],
        texts={"b0": "", "b1": ""},
        tables=[
            {
                "id": f"t{i}",
                "rows": 2,
                "cols": 2,
                "header_rows": 1,
                "caption": None,
                "cells": [
                    {"r": r, "c": c, "rs": 1, "cs": 1, "header": r == 0, "text": "x"} for r in (0, 1) for c in (0, 1)
                ],
            }
            for i in range(2)
        ],
        pictures=[],
    )
    assemble.truncate_cells(out, max_cells=4)
    assert [t["id"] for t in out.tables] == ["t0"] and [b["id"] for b in out.blocks] == ["b0"]
    assert out.warnings == [{"code": "truncated_cells", "page": None}]


@pytest.mark.parametrize("name", ["xxe.docx", "xxe.odt"])
def test_external_entities_are_never_resolved(tmp_path, pipelines, name):
    try:
        out = _run(tmp_path, pipelines, name, outputs={"markdown": True})
    except ParserError as exc:
        assert exc.code in ("conversion_failed", "corrupt")
        return
    assert "root:" not in json.dumps(out)


@pytest.mark.parametrize(
    ("name", "code", "detail"),
    [
        ("bomb.docx", "limit_exceeded", "zip_ratio"),
        ("macro.docm", "unsupported_format", "macro_enabled"),
        ("archive.zip", "unsupported_format", "archive"),
    ],
)
def test_package_refusals(tmp_path, pipelines, name, code, detail):
    with pytest.raises(ParserError) as raised:
        _run(tmp_path, pipelines, name)
    assert raised.value.code == code
    assert detail in (raised.value.limit, raised.value.reason)


def test_ranges_are_refused_for_formats_without_them(tmp_path, pipelines):
    with pytest.raises(ParserError) as raised:
        _run(tmp_path, pipelines, "slides.pptx", pages={"from": 1, "to": 1})
    assert raised.value.code == "invalid_request"
