"""`/v1/convert` and `/v1/inspect` against the real supervisor with fake slot processes (`fake_slot.py`)."""

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
from ume_parser.models import ConvertResult, ErrorResponse
from ume_parser.supervisor import Supervisor

PDF = fixture_bytes("text-layer.pdf")


@pytest.fixture
def client(settings):
    app = create_app(settings, Supervisor(settings, target=fake_slot.run))
    with TestClient(app) as client:
        deadline = time.monotonic() + 30
        while client.get("/health/ready").status_code != 200:
            assert time.monotonic() < deadline, "fake slot did not become ready"
            time.sleep(0.05)
        yield client


def _post(client, request_id: str, data: bytes = PDF, **overrides):
    return client.post(
        "/v1/convert",
        files={"file": ("input", data, "application/octet-stream")},
        data={"options": options(request_id, **overrides)},
    )


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
        (b"PK\x03\x04" + bytes(26) + b"x.jpg", {"file_ext": ".zip"}, 415, "unsupported_format", "none"),
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


def test_deadline_kills_the_slot(client, monkeypatch):
    monkeypatch.setattr(supervisor, "KILL_GRACE_S", 0.5)
    started = time.monotonic()
    response = _post(client, "sleep-1", deadline_ms=1000)
    assert response.status_code == 422
    assert _error(response)["code"] == "timeout"
    assert time.monotonic() - started < 10


def test_busy_and_duplicate_while_a_slot_converts(client, monkeypatch):
    monkeypatch.setattr(supervisor, "KILL_GRACE_S", 2.0)
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


def test_not_ready_before_warm_up(settings):
    app = create_app(settings, Supervisor(settings, target=fake_slot.run_never_ready))
    with TestClient(app) as client:
        response = _post(client, "ok-7")
    assert response.status_code == 503
    assert response.headers["Retry-After"] == str(supervisor.NOT_READY_RETRY_AFTER_S)
    assert _error(response)["code"] == "not_ready"


def test_inspect_is_not_implemented_in_p0(client):
    response = client.post("/v1/inspect", files={"file": ("input", PDF, "application/pdf")})
    assert response.status_code == 501
    assert _error(response)["code"] == "not_implemented"
