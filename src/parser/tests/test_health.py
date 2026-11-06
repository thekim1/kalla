import hashlib
import json

from fastapi.testclient import TestClient

from ume_parser import info
from ume_parser.app import create_app
from ume_parser.settings import NerSettings


class FakeSupervisor:
    """Stands in for the supervisor: no slot processes, readiness set by the test."""

    def __init__(self) -> None:
        self.live = False
        self.ready = False
        self.convert_slots = 1
        self.slots_ready = 0
        self.slots_cold = 0
        self.inspect_slots = 1
        self.ner_ready = False

    async def start(self) -> None:
        self.live = True

    async def stop(self) -> None:
        self.live = False


def test_live_answers_while_the_supervisor_runs(settings):
    supervisor = FakeSupervisor()
    with TestClient(create_app(settings, supervisor)) as client:
        assert client.get("/health/live").status_code == 200
        supervisor.live = False
        assert client.get("/health/live").status_code == 503


def test_ready_is_503_until_a_slot_is_warm(settings):
    supervisor = FakeSupervisor()
    with TestClient(create_app(settings, supervisor)) as client:
        response = client.get("/health/ready")
        assert response.status_code == 503
        assert response.headers["Retry-After"] == "5"
        assert response.json()["status"] == "not_ready"

        supervisor.ready, supervisor.slots_ready = True, 1
        response = client.get("/health/ready")
        assert response.status_code == 200
        assert response.json() == {"status": "ready", "slots_ready": 1, "slots_cold": 0, "convert_slots": 1}


def test_info_hashes_everything_but_capacity(settings):
    supervisor = FakeSupervisor()
    with TestClient(create_app(settings, supervisor)) as client:
        before = client.get("/v1/info").json()
        supervisor.slots_ready = 1
        after = client.get("/v1/info").json()

    assert before["build_hash"] == after["build_hash"]
    assert after["capacity"] == {"convert_slots": 1, "slots_ready": 1, "inspect_slots": 1, "ner_ready": False}

    static = {k: v for k, v in after.items() if k not in ("build_hash", "capacity")}
    canonical = json.dumps(static, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    assert after["build_hash"] == "sha256:" + hashlib.sha256(canonical).hexdigest()
    assert after["parser"]["revision"] == settings.revision
    assert {m["name"] for m in after["models"]} == {"layout", "tableformer", "ner"}
    assert after["pdfium"]["flags"] == []
    assert after["formats"]["supported"] == ["pdf", "docx", "pptx", "xlsx", "odt", "ods", "odp", "jpeg", "png", "tiff"]
    assert after["formats"]["deferred"] == ["doc", "xls", "ppt", "rtf", "eml", "msg"]
    assert after["ner"]["repository"] == "KBLab/bert-base-swedish-cased-ner"
    assert (after["ner"]["person_labels"], after["ner"]["quantised"]) == (["PER", "PRS"], True)
    assert after["limits"]["max_zip_ratio"] == 200


def test_build_hash_covers_the_ner_settings(settings):
    assert info.build_hash(info.collect(settings, NerSettings())) != info.build_hash(
        info.collect(settings, NerSettings(quantize=False))
    )
    assert info.collect(settings, NerSettings(enabled=False)).ner is None


def test_build_hash_changes_with_the_revision(settings):
    static = info.collect(settings)
    changed = static.model_copy(update={"parser": static.parser.model_copy(update={"revision": "other"})})
    assert info.build_hash(static) == info.build_hash(info.collect(settings))
    assert info.build_hash(static) != info.build_hash(changed)
