"""The parser needs no network (parser design §11, repo-and-deployment §2.4).

Needs the baked models and Tesseract, so it runs in the image (`docker build --target test`, then
`docker run --network none`) and is skipped elsewhere. Every Python process of the test refuses IP connections and
name lookups (`offline_guard/sitecustomize.py`), on top of the container having no network.
"""

import shutil
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

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
    _converter, problems = slot.warm_up(Settings().models_dir, threads=2)
    assert problems == []


def test_app_becomes_ready_without_network(monkeypatch, settings):
    monkeypatch.setenv("PYTHONPATH", str(GUARD_DIR))
    guard.install(monkeypatch.setattr)
    app = create_app(settings)
    with TestClient(app) as client:
        assert client.get("/health/live").status_code == 200
        deadline = time.monotonic() + settings.warmup_timeout_s
        while client.get("/health/ready").status_code != 200:
            assert time.monotonic() < deadline, "slot did not warm up"
            assert app.state.supervisor._slots[0].failures == 0, app.state.supervisor._slots[0].problems
            time.sleep(1)
        body = client.get("/v1/info").json()
    assert body["capacity"]["slots_ready"] == 1
    assert body["build_hash"].startswith("sha256:")
    assert body["tesseract"]["languages"] == ["eng", "osd", "swe"]
