import time

from fastapi.testclient import TestClient

from ume_parser.app import create_app
from ume_parser.settings import Settings


def test_slot_without_models_fails_its_self_test_and_stays_not_ready(tmp_path):
    settings = Settings(models_dir=tmp_path / "no-models", jobs_dir=tmp_path / "jobs", workers=1, threads=1)
    app = create_app(settings)
    supervisor = app.state.supervisor
    with TestClient(app) as client:
        deadline = time.monotonic() + 120
        while supervisor._slots[0].failures == 0 and time.monotonic() < deadline:
            time.sleep(0.2)
        assert supervisor._slots[0].failures >= 1
        assert supervisor._slots[0].problems
        assert client.get("/health/live").status_code == 200
        assert client.get("/health/ready").status_code == 503
