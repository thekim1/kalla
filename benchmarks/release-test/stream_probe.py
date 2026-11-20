"""Streamed chat probe of the R1 release test (repo-and-deployment §8.3, chat design §16.2): time to first token.

k6 cannot read a response body while it streams, so it cannot measure time to first token (the gateway's
benchmarks/compare/latency.sh says the same and uses its own stream probe). This probe sends streamed
POST /v1/chat/completions at a fixed arrival rate next to k6's load and measures, per request, the time to the
response headers, to the first event, to the first non-empty content delta (TTFT) and to the end of the stream, and the
gaps between content events. Standard library only (Python 3.12+), like scripts/capacity/capacity.py.

Usage: python3 -I stream_probe.py --base-url URL --model MODEL --request FILE --rate N --duration SECONDS --out FILE
         [--key-file FILE] [--header 'Name: value' ...] [--ca-file FILE] [--timeout SECONDS] [--label NAME]

A request counts as an error when the status is not 200, an event carries "error", the stream ends without
"data: [DONE]", no answer text arrived, or the connection failed or timed out. The summary JSON (milliseconds) is read
by report.py.
"""

import argparse
import http.client
import json
import math
import ssl
import sys
import threading
import time
import urllib.parse
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

CONNECT_TIMEOUT_S = 10.0
LATE_START_S = 0.25  # a request sent this much after its slot means the probe, not the target, was the limit


@dataclass
class Sample:
    ok: bool
    error: str | None = None
    headers_s: float | None = None
    first_event_s: float | None = None
    first_token_s: float | None = None
    total_s: float | None = None
    gaps_s: list[float] = field(default_factory=list)


class Template:
    """A request file of requests/: headers and body with {model}, {question} and {uuid} filled in per request."""

    def __init__(self, path: Path, model: str) -> None:
        spec = json.loads(path.read_text(encoding="utf-8"))
        self.name = path.stem
        self.model = model
        self.questions: list[str] = spec["questions"]
        self.headers: dict[str, str] = spec.get("headers", {})
        self.body: dict = spec["body"]
        self._count = 0
        self._lock = threading.Lock()

    def render(self) -> tuple[dict[str, str], bytes]:
        with self._lock:
            question = self.questions[self._count % len(self.questions)]
            self._count += 1

        def fill(value):
            if isinstance(value, str):
                value = value.replace("{model}", self.model).replace("{question}", question)
                while "{uuid}" in value:
                    value = value.replace("{uuid}", str(uuid.uuid4()), 1)
                return value
            if isinstance(value, list):
                return [fill(v) for v in value]
            if isinstance(value, dict):
                return {k: fill(v) for k, v in value.items()}
            return value

        return fill(self.headers), json.dumps(fill(self.body), ensure_ascii=False).encode()


class Target:
    def __init__(self, base_url: str, headers: dict[str, str], ca_file: str | None, timeout_s: float) -> None:
        url = urllib.parse.urlsplit(base_url.rstrip("/").removesuffix("/v1"))
        if url.scheme not in ("http", "https") or not url.hostname:
            raise SystemExit(f"--base-url must be http(s)://host[:port][/path], got '{base_url}'")
        self._url = url
        self.path = url.path.rstrip("/") + "/v1/chat/completions"
        self._headers = headers
        self._context = ssl.create_default_context(cafile=ca_file) if url.scheme == "https" else None
        self._timeout_s = timeout_s
        self._local = threading.local()

    def _connection(self) -> http.client.HTTPConnection:
        connection = getattr(self._local, "connection", None)
        if connection is None:
            if self._context is not None:
                connection = http.client.HTTPSConnection(
                    self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S, context=self._context
                )
            else:
                connection = http.client.HTTPConnection(self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S)
            self._local.connection = connection
        if connection.sock is None:
            # Connect (and TLS) outside the timed request, as a client's pooled connection; then the read timeout.
            connection.connect()
            connection.sock.settimeout(self._timeout_s)
        return connection

    def _reset(self) -> None:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            connection.close()
        self._local.connection = None

    def send(self, headers: dict[str, str], body: bytes) -> Sample:
        try:
            connection = self._connection()
        except OSError as exc:
            self._reset()
            return Sample(False, f"connect: {type(exc).__name__}: {exc}")
        start = time.perf_counter()
        try:
            connection.request(
                "POST", self.path, body=body, headers={"Content-Type": "application/json", **self._headers, **headers}
            )
            raw = connection.getresponse()
            sample = Sample(True, headers_s=time.perf_counter() - start)
            if raw.status != 200:
                text = raw.read()[:300].decode("utf-8", "replace")
                return Sample(False, f"status {raw.status}: {text}", headers_s=sample.headers_s)
            return self._read_stream(raw, start, sample)
        except (OSError, http.client.HTTPException, ValueError) as exc:
            self._reset()  # a connection left mid-request cannot be reused
            return Sample(False, f"{type(exc).__name__}: {exc}")

    @staticmethod
    def _read_stream(raw: http.client.HTTPResponse, start: float, sample: Sample) -> Sample:
        done = False
        last_token: float | None = None
        while line := raw.readline():
            if not line.startswith(b"data:"):
                continue
            data = line[5:].strip()
            if data == b"[DONE]":
                done = True
                break
            event = json.loads(data)
            if "error" in event:
                raw.read()
                return Sample(False, f"error event: {json.dumps(event['error'], ensure_ascii=False)[:300]}")
            for choice in event.get("choices") or []:
                now = time.perf_counter()
                if sample.first_event_s is None:
                    sample.first_event_s = now - start
                if (choice.get("delta") or {}).get("content"):
                    if last_token is None:
                        sample.first_token_s = now - start
                    else:
                        sample.gaps_s.append(now - last_token)
                    last_token = now
        raw.read()  # drain, so the connection can be reused
        sample.total_s = time.perf_counter() - start
        if not done:
            return Sample(False, "stream ended without data: [DONE]")
        if sample.first_token_s is None:
            return Sample(False, "no answer text in the stream")
        return sample


def percentile(values: list[float], p: float) -> float | None:
    """Linear interpolation between closest ranks (as numpy's default and capacity.py)."""
    if not values:
        return None
    ordered = sorted(values)
    rank = (len(ordered) - 1) * p / 100
    low, high = math.floor(rank), math.ceil(rank)
    return ordered[low] + (ordered[high] - ordered[low]) * (rank - low)


def stats_ms(values_s: list[float]) -> dict:
    values = [v * 1000 for v in values_s]

    def r(value: float | None) -> float | None:
        return None if value is None else round(value, 1)

    return {
        "n": len(values),
        "p50": r(percentile(values, 50)),
        "p95": r(percentile(values, 95)),
        "p99": r(percentile(values, 99)),
        "max": r(max(values, default=None)),
        "mean": r(sum(values) / len(values) if values else None),
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--base-url", required=True, help="the chat API, e.g. https://kalla-api.example.se")
    parser.add_argument("--model", required=True, help="the assistant slug (or a FakeGateway model in a dry run)")
    parser.add_argument("--request", required=True, type=Path, help="a request file of requests/")
    parser.add_argument("--rate", required=True, type=float, help="requests per second (open model)")
    parser.add_argument("--duration", required=True, type=float, help="seconds")
    parser.add_argument("--out", required=True, type=Path, help="summary JSON")
    parser.add_argument("--key-file", type=Path, help="file holding the bearer key")
    parser.add_argument("--header", action="append", default=[], help="'Name: value', repeatable")
    parser.add_argument("--ca-file", help="CA bundle for https (the deployment's internal CA)")
    parser.add_argument("--timeout", type=float, default=60.0, help="read timeout per request [60]")
    parser.add_argument("--threads", type=int, default=200, help="most requests in flight [200]")
    parser.add_argument("--label", default="probe")
    args = parser.parse_args(argv)
    if args.rate <= 0 or args.duration <= 0:
        parser.error("--rate and --duration must be positive")
    return args


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    headers: dict[str, str] = {}
    for item in args.header:
        name, sep, value = item.partition(":")
        if not sep or not name.strip():
            raise SystemExit(f"--header must be 'Name: value', got '{item}'")
        headers[name.strip()] = value.strip()
    if args.key_file:
        headers["Authorization"] = "Bearer " + args.key_file.read_text(encoding="utf-8").strip()
    template = Template(args.request, args.model)
    target = Target(args.base_url, headers, args.ca_file, args.timeout)

    samples: list[Sample] = []
    lock = threading.Lock()
    late = 0

    def one(slot: float) -> None:
        nonlocal late
        request_headers, body = template.render()
        lag = time.perf_counter() - slot
        sample = target.send(request_headers, body)
        with lock:
            samples.append(sample)
            late += lag > LATE_START_S

    started_at = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    count = math.floor(args.rate * args.duration)
    begin = time.perf_counter()
    with ThreadPoolExecutor(max_workers=args.threads) as pool:
        for i in range(count):
            slot = begin + i / args.rate
            time.sleep(max(0.0, slot - time.perf_counter()))
            pool.submit(one, slot)
    elapsed = time.perf_counter() - begin

    good = [s for s in samples if s.ok]
    errors = [s for s in samples if not s.ok]
    kinds: dict[str, int] = {}
    for s in errors:
        kind = (s.error or "unknown").split(":")[0]
        kinds[kind] = kinds.get(kind, 0) + 1
    summary = {
        "label": args.label,
        "target": urllib.parse.urlsplit(args.base_url).netloc,
        "model": args.model,
        "request": template.name,
        "rate": args.rate,
        "duration_s": args.duration,
        "started_at": started_at,
        "requests": len(samples),
        "streams_per_s": round(len(samples) / elapsed, 2) if elapsed else 0,
        "errors": len(errors),
        "error_kinds": kinds,
        "first_error": errors[0].error if errors else None,
        "late_starts": late,
        "headers_ms": stats_ms([s.headers_s for s in good if s.headers_s is not None]),
        "first_event_ms": stats_ms([s.first_event_s for s in good if s.first_event_s is not None]),
        "ttft_ms": stats_ms([s.first_token_s for s in good if s.first_token_s is not None]),
        "total_ms": stats_ms([s.total_s for s in good if s.total_s is not None]),
        "gap_ms": stats_ms([g for s in good for g in s.gaps_s]),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    ttft = summary["ttft_ms"]
    print(
        f"{args.label}: {summary['requests']} streams, {summary['errors']} errors, "
        f"TTFT p50 {ttft['p50']} ms, p95 {ttft['p95']} ms, late starts {late}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
