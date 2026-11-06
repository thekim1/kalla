"""The supervisor's lanes, RSS watchdog and recycling (parser design §10), with fake workers where they suffice."""

import dataclasses
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
    supervisor = Supervisor(
        settings, NerSettings(idle_stop_s=0), targets={**fake_slot.FAKE, "ner": fake_slot.run_failing}
    )
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


def _ner(client, request_id: str):
    return client.post("/v1/ner", json={"request_id": request_id, "texts": [{"id": "t1", "text": "Möte med Anna."}]})


def _idle_for_ages(slot) -> None:
    """Drives the idle clock instead of sleeping: the slot last worked long before any idle stop."""
    slot.used_at = time.monotonic() - 10_000


def _two_polls(slot) -> None:
    """Waits for the monitor to check `slot` twice more (it samples RSS on every check)."""
    for _ in range(2):
        slot.rss = -1
        _wait(lambda: slot.rss != -1, what="a monitor poll")


def test_ner_starts_on_the_first_request_and_stops_when_idle(settings):
    supervisor = Supervisor(settings, NerSettings(), targets=fake_slot.FAKE)  # NER_IDLE_STOP_S 900, the default
    with TestClient(create_app(settings, supervisor)) as client:
        _wait(lambda: _ready(client), what="readiness")
        ner = supervisor.slots("ner")[0]
        assert (ner.state, ner.process) == ("cold", None)  # no NER process at idle start
        assert client.get("/v1/info").json()["capacity"]["ner_ready"] is False

        woken = _ner(client, "ok-w1")
        assert woken.status_code == 503
        assert woken.json()["error"]["code"] == "not_ready"
        _wait(lambda: supervisor.ner_ready, what="ner-0 warm after the wake")
        assert client.get("/v1/info").json()["capacity"]["ner_ready"] is True
        assert _ner(client, "ok-w2").status_code == 200

        _idle_for_ages(ner)
        _wait(lambda: ner.state == "cold", what="the idle stop")
        assert ner.process is None
        assert ner.failures == 0
        assert client.get("/v1/info").json()["capacity"]["ner_ready"] is False
        assert _ready(client)  # readiness does not depend on NER


def test_ner_idle_stop_zero_keeps_ner_warm_from_the_start(settings):
    supervisor = Supervisor(settings, NerSettings(idle_stop_s=0), targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor)) as client:
        _wait(lambda: supervisor.ner_ready, what="ner-0 warm without a request")
        ner = supervisor.slots("ner")[0]
        _idle_for_ages(ner)
        _two_polls(ner)
        assert ner.state == "ready"
        assert _ner(client, "ok-w3").status_code == 200


def test_ner_disabled_never_starts_a_process(settings):
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        assert _ner(client, "ok-w4").json()["error"]["code"] == "not_ready"
        assert supervisor.slots("ner") == []


def test_idle_convert_slot_goes_cold_and_the_next_request_warms_it(settings):
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)  # PARSER_IDLE_STOP_S 900, the default
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        convert, inspect = supervisor.slots("convert")[0], supervisor.slots("inspect")[0]
        _wait(lambda: inspect.state == "ready", what="inspect-0")
        _idle_for_ages(convert)
        _idle_for_ages(inspect)
        _wait(lambda: convert.state == "cold", what="the idle stop")
        assert convert.process is None
        ready = client.get("/health/ready")
        assert ready.status_code == 200  # cold but startable: the container health check and the worker see a parser
        assert ready.json() == {"status": "ready", "slots_ready": 0, "slots_cold": 1, "convert_slots": 1}
        assert inspect.state == "ready"  # inspect stays warm (its 10 s deadline)

        woken = _post(client, "ok-c1")
        assert woken.status_code == 503
        assert woken.json()["error"]["code"] == "not_ready"
        assert woken.headers["Retry-After"] == "5"
        assert _ready(client)  # still ready while the woken slot warms
        _wait(lambda: supervisor.slots_ready == 1, what="the woken slot")
        assert convert.failures == 0
        assert _post(client, "ok-c2").status_code == 200
        assert client.get("/health/ready").json()["slots_cold"] == 0


def test_convert_idle_stop_zero_keeps_the_slot_warm(tmp_path):
    settings = Settings(jobs_dir=tmp_path / "jobs", workers=1, threads=1, idle_stop_s=0)
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        convert = supervisor.slots("convert")[0]
        _idle_for_ages(convert)
        _two_polls(convert)
        assert convert.state == "ready"
        assert _post(client, "ok-c3").status_code == 200


def test_a_failed_wake_is_not_ready(tmp_path):
    settings = Settings(jobs_dir=tmp_path / "jobs", workers=1, threads=1)
    supervisor = Supervisor(settings, NER_OFF, targets=fake_slot.FAKE)
    with TestClient(create_app(settings, supervisor, NER_OFF)) as client:
        _wait(lambda: _ready(client), what="readiness")
        convert = supervisor.slots("convert")[0]
        _idle_for_ages(convert)
        _wait(lambda: convert.state == "cold", what="the idle stop")
        convert.lane = dataclasses.replace(convert.lane, target=fake_slot.run_failing)
        assert _post(client, "ok-c4").json()["error"]["code"] == "not_ready"
        _wait(lambda: convert.failures >= 1, what="the failed warm-up")
        assert client.get("/health/ready").status_code == 503  # down, not cold: an honest not-ready
