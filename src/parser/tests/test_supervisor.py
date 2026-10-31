"""The supervisor's lanes, RSS watchdog and recycling (parser design §10), with fake workers where they suffice."""

import time

from fastapi.testclient import TestClient

import fake_slot
from helpers import fixture_bytes, options
from ume_parser import limits
from ume_parser.app import create_app
from ume_parser.settings import NerSettings, Settings
from ume_parser.supervisor import Supervisor

PDF = fixture_bytes("text-layer.pdf")
NER_OFF = NerSettings(enabled=False)


def _wait(condition, timeout_s: float = 30, what: str = "condition") -> None:
    deadline = time.monotonic() + timeout_s
    while not condition():
        assert time.monotonic() < deadline, f"timed out waiting for {what}"
        time.sleep(0.05)


def _post(client, request_id: str):
    return client.post(
        "/v1/convert",
        files={"file": ("input", PDF, "application/octet-stream")},
        data={"options": options(request_id)},
    )


def _ready(client) -> bool:
    return client.get("/health/ready").status_code == 200


def test_slot_without_models_fails_its_self_test_and_stays_not_ready(tmp_path):
    settings = Settings(models_dir=tmp_path / "no-models", jobs_dir=tmp_path / "jobs", workers=1, threads=1)
    app = create_app(settings, Supervisor(settings, NER_OFF), NER_OFF)
    supervisor = app.state.supervisor
    with TestClient(app) as client:
        convert = supervisor.slots("convert")[0]
        _wait(lambda: convert.failures >= 1, 120, "the failed self-test")
        assert convert.problems
        assert client.get("/health/live").status_code == 200
        assert client.get("/health/ready").status_code == 503


def test_readiness_does_not_depend_on_ner(settings):
    supervisor = Supervisor(settings, targets={**fake_slot.FAKE, "ner": fake_slot.run_failing})
    with TestClient(create_app(settings, supervisor)) as client:
        _wait(lambda: _ready(client), what="readiness")
        _wait(lambda: supervisor.slots("ner")[0].failures >= 1, what="the NER self-test failure")
        assert client.get("/v1/info").json()["capacity"]["ner_ready"] is False
        assert _ready(client)


def test_watchdog_kills_a_slot_over_its_rss_cap(tmp_path):
    settings = Settings(jobs_dir=tmp_path / "jobs", workers=1, threads=1, slot_max_rss_mb=fake_slot.ALLOC_MB // 2)
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        started = time.monotonic()
        response = _post(client, "alloc-1")
        assert response.status_code == 507
        error = response.json()["error"]
        assert (error["code"], error["retry"]) == ("resource_limit", "smaller_range")
        assert time.monotonic() - started < 20  # killed by the watchdog, long before the 110 s deadline
        assert supervisor.recycles["rss_limit"] == 1
        _wait(lambda: _ready(client), what="the respawn")
        assert supervisor.slots("convert")[0].failures == 0  # respawned at once, no back-off
        assert _post(client, "ok-1").status_code == 200


def test_slot_is_recycled_after_its_job_limit(tmp_path):
    settings = Settings(jobs_dir=tmp_path / "jobs", workers=1, threads=1, recycle_jobs=2)
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        slot = supervisor.slots("convert")[0]
        first_pid = slot.process.pid
        assert _post(client, "ok-2").status_code == 200
        assert _post(client, "ok-3").status_code == 200
        _wait(lambda: supervisor.recycles["max_jobs"] == 1, what="the recycle")
        _wait(lambda: _ready(client), what="the re-warm")
        assert slot.process.pid != first_pid
        assert slot.failures == 0
        assert _post(client, "ok-4").status_code == 200


def test_idle_rss_growth_recycles(settings):
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        supervisor.slots("convert")[0].warm_rss = 1  # as if warm at one byte: any idle RSS is growth
        _wait(lambda: supervisor.recycles["rss_growth"] >= 1, what="the growth recycle")


def test_inspect_lane_is_killed_at_its_deadline(settings, monkeypatch):
    monkeypatch.setattr(limits, "INSPECT_DEADLINE_MS", 500)
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: supervisor.slots("inspect")[0].state == "ready", what="inspect-0")
        started = time.monotonic()
        response = client.post(
            "/v1/inspect",
            files={"file": ("input", PDF, "application/octet-stream")},
            data={"options": '{"request_id": "sleep-i1"}'},
        )
        assert response.status_code == 422
        assert response.json()["error"]["code"] == "timeout"
        assert time.monotonic() - started < 5
        assert supervisor.slots("inspect")[0].failures == 1
