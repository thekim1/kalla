"""Probes (parser design §13.1) and the container health check `python -m ume_parser.health`.

`/health/live` answers 200 while the supervisor's monitor runs; it checks no slot. `/health/ready` answers 200 once
every slot passed its warm-up self-test and while at least one slot is warm or cold (stopped while idle, started by
the next request, `PARSER_IDLE_STOP_S`), else 503. Neither waits on a slot, so a
busy parser still answers its probes.

The health check exits 0 when `/health/ready` answers 200 and 1 otherwise (the .NET images' `--health-check`
convention, repo-and-deployment §5.1), against `FORS_HEALTHCHECK_URL` or this container's own listener.
"""

import os
import ssl
import sys
import urllib.error
import urllib.request

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from ume_parser.settings import Settings

LIVE_PATH = "/health/live"
READY_PATH = "/health/ready"
NOT_READY_RETRY_AFTER_S = "5"

router = APIRouter()


@router.get(LIVE_PATH, include_in_schema=False)
def live(request: Request) -> JSONResponse:
    if request.app.state.supervisor.live:
        return JSONResponse({"status": "live"})
    return JSONResponse({"status": "down"}, status_code=503)


@router.get(READY_PATH, include_in_schema=False)
def ready(request: Request) -> JSONResponse:
    supervisor = request.app.state.supervisor
    body = {
        "slots_ready": supervisor.slots_ready,
        "slots_cold": supervisor.slots_cold,
        "convert_slots": supervisor.convert_slots,
    }
    if supervisor.ready:
        return JSONResponse({"status": "ready", **body})
    return JSONResponse(
        {"status": "not_ready", **body}, status_code=503, headers={"Retry-After": NOT_READY_RETRY_AFTER_S}
    )


def check(url: str, timeout_s: float = 3.0) -> bool:
    context = None
    if url.startswith("https:"):
        # The listener's certificate names the service, not localhost: a local probe checks reachability, not identity.
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
    try:
        with urllib.request.urlopen(url, timeout=timeout_s, context=context) as response:  # noqa: S310
            return response.status == 200
    except (urllib.error.URLError, OSError):
        return False


def main() -> int:
    settings = Settings()
    scheme = "https" if settings.tls else "http"
    url = os.environ.get("FORS_HEALTHCHECK_URL", f"{scheme}://127.0.0.1:{settings.port}{READY_PATH}")
    return 0 if check(url) else 1


if __name__ == "__main__":
    sys.exit(main())
