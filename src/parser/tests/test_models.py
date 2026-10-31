import json

import pytest
from pydantic import ValidationError

from helpers import REPO_ROOT
from scripts import export_schema
from ume_parser.models import ConvertRequest, InspectRequest, NerRequest

CONTRACTS = REPO_ROOT / "contracts" / "parser" / "v1"


def test_request_defaults():
    request = ConvertRequest.model_validate({"request_id": "job-1-r0", "file_ext": ".pdf"})
    assert request.pipeline == "docling"
    assert request.ocr.mode == "auto"
    assert request.ocr.languages == ["swe"]
    assert request.tables.mode == "accurate"
    assert request.image_scale == 2.0
    assert request.deadline_ms == 110_000
    assert request.pages is None


@pytest.mark.parametrize(
    "body",
    [
        {"request_id": "r", "file_ext": ".pdf", "colour": "blue"},  # unknown property (§7.2)
        {"request_id": "r", "file_ext": ".pdf", "ocr": {"mode": "auto", "engine": "easyocr"}},
        {"request_id": "r", "file_ext": ".pdf", "ocr": {"languages": ["eng"]}},
        {"request_id": "r", "file_ext": ".pdf", "pages": {"from": 5, "to": 4}},
        {"request_id": "r", "file_ext": ".pdf", "deadline_ms": 120_001},
        {"request_id": "../etc", "file_ext": ".pdf"},
        {"request_id": "r", "file_ext": "report.pdf"},
        {"file_ext": ".pdf"},
    ],
)
def test_request_rejects(body):
    with pytest.raises(ValidationError):
        ConvertRequest.model_validate(body)


def test_page_range_uses_the_from_alias():
    request = ConvertRequest.model_validate({"request_id": "r", "file_ext": ".pdf", "pages": {"from": 41, "to": 60}})
    assert request.pages.count == 20
    assert request.model_dump(by_alias=True)["pages"] == {"from": 41, "to": 60}


def test_inspect_options_are_all_optional():
    assert InspectRequest.model_validate({}).request_id is None
    with pytest.raises(ValidationError):
        InspectRequest.model_validate({"pipeline": "docling"})


def test_ner_request_defaults_to_persons_only():
    request = NerRequest.model_validate({"request_id": "set-1", "texts": [{"id": "c1:0", "text": "Anna"}]})
    assert (request.labels, request.min_score, request.deadline_ms) == (["person"], 0.0, 15_000)


@pytest.mark.skipif(not CONTRACTS.is_dir(), reason="needs the repository's contracts/ (not in the image)")
@pytest.mark.parametrize("name", sorted(export_schema.SCHEMAS))
def test_committed_schema_matches_the_models(name):
    model, mode = export_schema.SCHEMAS[name]
    committed = json.loads((CONTRACTS / f"{name}.schema.json").read_text(encoding="utf-8"))
    assert committed == export_schema.schema(model, mode, name), "run scripts/export_schema.py"
