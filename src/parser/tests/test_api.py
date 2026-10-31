"""`/v1/convert`, `/v1/inspect` and `/v1/ner` against the real supervisor with fake workers (`fake_slot.py`)."""

import dataclasses
import json
import threading
import time

import pytest
from fastapi.testclient import TestClient

import fake_slot
from helpers import fixture_bytes, options, parts
from ume_parser import limits, supervisor
from ume_parser.app import create_app
from ume_parser.errors import ParserError
from ume_parser.models import ConvertResult, ErrorResponse, InspectResult, NerResult
from ume_parser.settings import NerSettings
from ume_parser.supervisor import Supervisor

PDF = fixture_bytes("text-layer.pdf")


def _wait_ready(client, ner: bool = True) -> None:
    supervisor = client.app.state.supervisor
    deadline = time.monotonic() + 30
    while client.get("/health/ready").status_code != 200 or (ner and not supervisor.ner_ready):
        assert time.monotonic() < deadline, "fake workers did not become ready"
        time.sleep(0.05)
    while supervisor.slots("inspect")[0].state != "ready":
        assert time.monotonic() < deadline, "fake inspect process did not become ready"
        time.sleep(0.05)


@pytest.fixture
def client(settings):
    app = create_app(settings, Supervisor(settings, targets=fake_slot.FAKE))
    with TestClient(app) as client:
        _wait_ready(client)
        yield client


def _post(client, request_id: str, data: bytes = PDF, **overrides):
    return client.post(
        "/v1/convert",
        files={"file": ("input", data, "application/octet-stream")},
        data={"options": options(request_id, **overrides)},
    )


def _short_grace(client, grace_s: float = 0.5) -> None:
    """Kills a convert slot `grace_s` after the deadline instead of 10 s."""
    lanes = client.app.state.supervisor.lanes
    lanes["convert"] = dataclasses.replace(lanes["convert"], kill_grace_s=grace_s)
    for s in client.app.state.supervisor.slots("convert"):
        s.lane = lanes["convert"]


def _error(response) -> dict:
    body = ErrorResponse.model_validate(response.json())
    assert body.error.detail  # a fixed sentence, never a library message
    return response.json()["error"]


def test_convert_answers_multipart_with_the_result(client, settings):
    response = _post(client, "ok-1", media_type="application/pdf")
    assert response.status_code == 200
    content_type = response.headers["content-type"]
    assert content_type.startswith("multipart/mixed; boundary=")
    (part,) = parts(content_type, response.content)
    assert part["Content-ID"] == "result"
    result = ConvertResult.model_validate_json(part.get_payload(decode=True))
    assert result.contract == "parser/v1"
    assert result.request_id == "ok-1"
    assert result.input.format == "pdf"
    assert result.input.bytes == len(PDF)
    assert result.warnings == []
    assert result.build.build_hash.startswith("sha256:")
    assert list(settings.jobs_dir.iterdir()) == []  # the job directory is removed after the response


def test_declared_type_mismatch_is_a_warning(client):
    response = _post(client, "ok-2", file_ext=".docx")
    (part,) = parts(response.headers["content-type"], response.content)
    assert [w["code"] for w in json.loads(part.get_payload(decode=True))["warnings"]] == ["media_type_mismatch"]


@pytest.mark.parametrize(
    ("data", "overrides", "status", "code", "retry"),
    [
        (PDF, {"colour": "blue"}, 400, "invalid_request", "none"),
        (PDF, {"pages": {"from": 1, "to": 21}}, 422, "too_many_pages", "smaller_range"),
        (fixture_bytes("legacy.doc"), {"file_ext": ".doc"}, 415, "format_deferred", "none"),
        (fixture_bytes("legacy.rtf"), {"file_ext": ".rtf"}, 415, "format_deferred", "none"),
        (fixture_bytes("message.eml"), {"file_ext": ".eml"}, 415, "format_deferred", "none"),
        (fixture_bytes("encrypted.docx"), {"file_ext": ".docx"}, 422, "encrypted", "none"),
        (b"\x1f\x8b\x08\x00", {"file_ext": ".gz"}, 415, "unsupported_format", "none"),
    ],
)
def test_refusals_before_a_slot(client, data, overrides, status, code, retry):
    response = _post(client, "ok-3", data, **overrides)
    assert response.status_code == status
    error = _error(response)
    assert (error["code"], error["retry"]) == (code, retry)


def test_options_must_be_present(client):
    response = client.post("/v1/convert", files={"file": ("input", PDF, "application/pdf")})
    assert response.status_code == 400
    assert response.json()["request_id"] is None


def test_body_over_the_limit_is_too_large(client, monkeypatch):
    monkeypatch.setattr(limits, "MAX_BODY_BYTES", 1000)
    response = _post(client, "ok-4", bytes(2000))
    assert response.status_code == 413
    assert _error(response)["code"] == "too_large"


def test_slot_errors_keep_their_code(client):
    response = _post(client, "corrupt-1")
    assert response.status_code == 422
    assert _error(response)["code"] == "corrupt"


def test_crashed_slot_is_worker_crashed_and_respawned(client):
    response = _post(client, "crash-1")
    assert response.status_code == 500
    assert _error(response)["retry"] == "smaller_range"
    deadline = time.monotonic() + 30
    while client.get("/health/ready").status_code != 200:
        assert time.monotonic() < deadline, "slot was not respawned"
        time.sleep(0.1)
    assert _post(client, "ok-5").status_code == 200


def test_deadline_kills_the_slot(client):
    _short_grace(client)
    started = time.monotonic()
    response = _post(client, "sleep-1", deadline_ms=1000)
    assert response.status_code == 422
    assert _error(response)["code"] == "timeout"
    assert time.monotonic() - started < 10


def test_busy_and_duplicate_while_a_slot_converts(client):
    _short_grace(client, 2.0)
    first = threading.Thread(target=_post, args=(client, "sleep-2"), kwargs={"deadline_ms": 1000})
    first.start()
    try:
        deadline = time.monotonic() + 10
        while client.app.state.supervisor.slots_ready and not client.app.state.supervisor._slots[0].busy:
            assert time.monotonic() < deadline
            time.sleep(0.02)
        busy = _post(client, "ok-6")
        assert busy.status_code == 503
        assert busy.headers["Retry-After"] == str(supervisor.BUSY_RETRY_AFTER_S)
        assert _error(busy)["code"] == "busy"
        with pytest.raises(ParserError) as duplicate:
            client.app.state.supervisor.reserve("sleep-2")
        assert duplicate.value.code == "duplicate_in_flight"
        # Probes answer while every slot converts (no --limit-concurrency, §10.1).
        assert client.get("/health/ready").status_code == 200
    finally:
        first.join()


def test_non_rangeable_input_that_times_out_is_not_retried_smaller(client):
    _short_grace(client)
    response = _post(client, "sleep-3", fixture_bytes("text.docx"), file_ext=".docx", deadline_ms=1000)
    assert response.status_code == 422
    assert (_error(response)["code"], _error(response)["retry"]) == ("timeout", "none")


def test_not_ready_before_warm_up(settings):
    app = create_app(settings, Supervisor(settings, targets={**fake_slot.FAKE, "convert": fake_slot.run_never_ready}))
    with TestClient(app) as client:
        response = _post(client, "ok-7")
    assert response.status_code == 503
    assert response.headers["Retry-After"] == str(supervisor.NOT_READY_RETRY_AFTER_S)
    assert _error(response)["code"] == "not_ready"


def _inspect(client, data: bytes = PDF, options: dict | None = None):
    form = {"options": json.dumps(options)} if options is not None else None
    return client.post("/v1/inspect", files={"file": ("input", data, "application/octet-stream")}, data=form)


def test_inspect_answers_from_its_own_lane(client):
    response = _inspect(client, options={"request_id": "ok-i1", "file_ext": ".pdf"})
    assert response.status_code == 200
    result = InspectResult.model_validate(response.json())
    assert (result.request_id, result.format, result.page_count, result.warnings) == ("ok-i1", "pdf", 1, [])
    assert result.build.build_hash.startswith("sha256:")


def test_inspect_options_are_optional_and_strict(client):
    assert _inspect(client).json()["request_id"] is None
    response = _inspect(client, options={"pages": {"from": 1, "to": 2}})
    assert response.status_code == 400
    assert _error(response)["code"] == "invalid_request"


def test_inspect_refuses_what_has_no_format(client):
    response = _inspect(client, fixture_bytes("legacy.doc"), {"file_ext": ".doc"})
    assert response.status_code == 415
    assert _error(response)["code"] == "format_deferred"


def test_inspect_answers_while_every_convert_slot_is_busy(client):
    _short_grace(client, 2.0)
    first = threading.Thread(target=_post, args=(client, "sleep-4"), kwargs={"deadline_ms": 1000})
    first.start()
    try:
        deadline = time.monotonic() + 10
        while not client.app.state.supervisor.slots("convert")[0].busy:
            assert time.monotonic() < deadline
            time.sleep(0.02)
        assert _post(client, "ok-8").status_code == 503
        assert _inspect(client, options={"request_id": "ok-i2"}).status_code == 200
    finally:
        first.join()


def _ner(client, request_id: str, **overrides):
    body = {"request_id": request_id, "texts": [{"id": "c1:0", "text": "Anna Lindqvist bor i Umeå."}], **overrides}
    return client.post("/v1/ner", json=body)


def test_ner_answers_entities(client):
    response = _ner(client, "ok-n1", labels=["person", "person_ambiguous"], min_score=0.8)
    assert response.status_code == 200
    result = NerResult.model_validate(response.json())
    assert [(e.text_id, e.start, e.end, e.label) for e in result.entities] == [("c1:0", 0, 4, "person")]
    assert result.model.repository == "KBLab/bert-base-swedish-cased-ner"


@pytest.mark.parametrize(
    "overrides",
    [
        {"texts": []},
        {"texts": [{"id": "a", "text": "x" * 4001}]},
        {"texts": [{"id": f"t{i}", "text": "x" * 4000} for i in range(26)]},
        {"texts": [{"id": "a", "text": "x"}, {"id": "a", "text": "y"}]},
        {"labels": ["location"]},
        {"deadline_ms": 15001},
        {"colour": "blue"},
    ],
)
def test_ner_refuses_requests_outside_its_limits(client, overrides):
    response = _ner(client, "ok-n2", **overrides)
    assert response.status_code == 400
    assert _error(response)["code"] == "invalid_request"


def test_ner_waits_in_its_queue_then_is_busy(settings):
    ner = NerSettings(queue=1)
    app = create_app(settings, Supervisor(settings, ner, targets=fake_slot.FAKE), ner)
    with TestClient(app) as client:
        _wait_ready(client)
        hung = threading.Thread(target=_ner, args=(client, "sleep-n1"), kwargs={"deadline_ms": 2000})
        hung.start()
        try:
            deadline = time.monotonic() + 10
            while not client.app.state.supervisor.slots("ner")[0].busy:
                assert time.monotonic() < deadline
                time.sleep(0.02)
            waiting = []
            queued = threading.Thread(target=lambda: waiting.append(_ner(client, "ok-n3", deadline_ms=1000)))
            queued.start()
            while client.app.state.supervisor._waiting["ner"] == 0:
                assert time.monotonic() < deadline
                time.sleep(0.02)
            busy = _ner(client, "ok-n4")
            assert busy.status_code == 503
            assert _error(busy)["code"] == "busy"
            queued.join()
            assert waiting[0].status_code == 503  # waited its deadline for ner-0, then busy
        finally:
            hung.join()


def test_ner_is_refused_when_gated_off(settings):
    off = NerSettings(enabled=False)
    app = create_app(settings, Supervisor(settings, off, targets=fake_slot.FAKE), off)
    with TestClient(app) as client:
        _wait_ready(client, ner=False)
        response = _ner(client, "ok-n5")
        info = client.get("/v1/info").json()
    assert response.status_code == 503
    assert _error(response)["code"] == "not_ready"
    assert response.headers["Retry-After"] == str(supervisor.NOT_READY_RETRY_AFTER_S)
    assert info["ner"] is None
    assert info["capacity"]["ner_ready"] is False
