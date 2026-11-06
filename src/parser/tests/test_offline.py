"""The parser needs no network (parser design §11, repo-and-deployment §2.4).

Needs the baked models and Tesseract, so it runs in the image (`docker build --target test` runs it in a
`RUN --network=none` step) and is skipped elsewhere. Every Python process of the test refuses IP connections and
name lookups (`offline_guard/sitecustomize.py`), on top of the build step having no network. The app test converts
the text-layer fixture through both pipelines, the scanned fixture through Docling with OCR and one fixture of every
other format, inspects a PDF and asks `ner-0` for names (the NER model and its tokenizer load offline too).
"""

import json
import shutil
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from helpers import fixture_bytes, options, parts
from offline_guard import sitecustomize as guard
from ume_parser import slot
from ume_parser.app import create_app
from ume_parser.settings import Settings

GUARD_DIR = Path(guard.__file__).resolve().parent

pytestmark = pytest.mark.skipif(
    not (Settings().models_dir / "docling-project--docling-layout-heron").is_dir() or shutil.which("tesseract") is None,
    reason="needs the baked models and Tesseract (run in the image)",
)


def test_slot_warms_up_without_network(monkeypatch):
    guard.install(monkeypatch.setattr)
    _pipeline, problems = slot.warm_up(Settings().models_dir, threads=2)
    assert problems == []


def _convert(client: TestClient, name: str, pipeline: str = "docling") -> tuple[str, int]:
    """Converts a fixture through `/v1/convert`; returns the pipeline that ran and the OCR'd pages."""
    ext = "." + name.rsplit(".", 1)[1]
    response = client.post(
        "/v1/convert",
        files={"file": ("input", fixture_bytes(name), "application/octet-stream")},
        data={"options": options(f"offline-{name}-{pipeline}", pipeline=pipeline, file_ext=ext)},
    )
    assert response.status_code == 200, response.text
    result = json.loads(parts(response.headers["content-type"], response.content)[0].get_payload(decode=True))
    ocr_pages = sum(1 for page in result["pages"] if page["signals"]["ocr_applied"] != "none")
    return result["input"]["pipeline"], ocr_pages


def test_app_becomes_ready_and_converts_without_network(monkeypatch, settings):
    monkeypatch.setenv("PYTHONPATH", str(GUARD_DIR))
    guard.install(monkeypatch.setattr)
    app = create_app(settings)
    with TestClient(app) as client:
        assert client.get("/health/live").status_code == 200
        deadline = time.monotonic() + settings.warmup_timeout_s
        while client.get("/health/ready").status_code != 200:
            assert time.monotonic() < deadline, "slot did not warm up"
            assert app.state.supervisor.slots("convert")[0].failures == 0, app.state.supervisor.slots("convert")[
                0
            ].problems
            time.sleep(1)
        body = client.get("/v1/info").json()
        assert _convert(client, "text-layer.pdf", "text_layer") == ("text_layer", 0)
        assert _convert(client, "text-layer.pdf", "docling") == ("docling", 0)
        assert _convert(client, "scanned.pdf", "docling") == ("docling", 1)
        for name in ("text.docx", "slides.pptx", "sheets.xlsx", "text.odt", "sheets.ods", "slides.odp"):
            assert _convert(client, name) == ("docling", 0), name
        for name, pages in (("scan.png", 1), ("scan.jpg", 1), ("frames.tiff", 3)):
            assert _convert(client, name) == ("docling", pages), name
        inspected = client.post("/v1/inspect", files={"file": ("input", fixture_bytes("text-layer.pdf"), "x/y")})
        assert inspected.status_code == 200 and inspected.json()["page_count"] == 3
        assert not app.state.supervisor.ner_ready  # NER_IDLE_STOP_S: ner-0 starts on the first request
        texts = [{"id": "t1", "text": "Protokollet justerades av Anna Lindqvist."}]
        woken = client.post("/v1/ner", json={"request_id": "offline-wake", "texts": texts})
        assert woken.status_code == 503 and woken.json()["error"]["code"] == "not_ready"
        while not app.state.supervisor.ner_ready:
            assert time.monotonic() < deadline, "ner-0 did not warm up"
            assert app.state.supervisor.slots("ner")[0].failures == 0, app.state.supervisor.slots("ner")[0].problems
            time.sleep(1)
        texts = [{"id": "t1", "text": "Protokollet justerades av Anna Lindqvist."}]
        found = client.post("/v1/ner", json={"request_id": "offline-ner", "texts": texts, "min_score": 0.5})
        assert found.status_code == 200, found.text
        assert [(e["start"], e["end"], e["label"]) for e in found.json()["entities"]] == [(26, 40, "person")]
        assert found.json()["model"]["labels"][9] == "PER"
        body = client.get("/v1/info").json()
    assert body["capacity"] == {"convert_slots": 1, "slots_ready": 1, "inspect_slots": 1, "ner_ready": True}
    assert body["capacity"]["slots_ready"] == 1
    assert body["build_hash"].startswith("sha256:")
    assert body["tesseract"]["languages"] == ["eng", "osd", "swe"]
