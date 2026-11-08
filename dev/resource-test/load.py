"""Retrieval load for the local resource test (dev/resource-test/README.md). Standard library only.

Usage: python3 -I load.py --url https://localhost:7401/v1/retrieve --key-file KEY --clients 8 --seconds 20

Each client sends POST /v1/retrieve back to back (two random Swedish service words plus "hur gör jag", top_k 8)
until the time is up. Prints one line: requests, requests/s, p50/p95/p99 latency and the status codes. The local
AppHost serves a development certificate, so certificate checks are off; the URL must be on localhost.
"""

import argparse
import collections
import json
import random
import ssl
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

WORDS = ["bygglov", "förskola", "avgift", "parkering", "skola", "renhållning", "ansökan", "öppettider",
         "bibliotek", "äldreomsorg", "vatten", "sophämtning", "kontakt", "trafik", "fritid"]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="https://localhost:7401/v1/retrieve")
    parser.add_argument("--key-file", required=True)
    parser.add_argument("--clients", type=int, default=8)
    parser.add_argument("--seconds", type=float, default=20)
    args = parser.parse_args()
    if urllib.parse.urlsplit(args.url).hostname not in ("localhost", "127.0.0.1", "::1"):
        raise SystemExit("load.py is for the local AppHost only: the URL must be on localhost")

    with open(args.key_file, encoding="utf-8") as f:
        key = f.read().strip()
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    latencies: list[float] = []
    codes: collections.Counter = collections.Counter()
    lock = threading.Lock()
    end = time.monotonic() + args.seconds

    def client() -> None:
        rng = random.Random()  # noqa: S311 - query variety, not a secret
        while time.monotonic() < end:
            body = json.dumps({"query": " ".join(rng.sample(WORDS, 2)) + " hur gör jag", "top_k": 8}).encode()
            request = urllib.request.Request(
                args.url, body, {"Authorization": "Bearer " + key, "Content-Type": "application/json"})
            started = time.perf_counter()
            try:
                with urllib.request.urlopen(request, context=context, timeout=30) as response:  # noqa: S310 - localhost only
                    response.read()
                    code = response.status
            except urllib.error.HTTPError as e:
                e.read()
                code = e.code
            except OSError as e:
                code = type(e).__name__
            with lock:
                latencies.append((time.perf_counter() - started) * 1000)
                codes[code] += 1

    threads = [threading.Thread(target=client) for _ in range(args.clients)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    latencies.sort()
    n = len(latencies)
    if n == 0:
        raise SystemExit("no requests completed")
    pick = lambda q: latencies[min(n - 1, int(n * q))]  # noqa: E731
    print(f"clients={args.clients} requests={n} rps={n / args.seconds:.0f} p50={pick(.5):.0f}ms "
          f"p95={pick(.95):.0f}ms p99={pick(.99):.0f}ms codes={dict(codes)}")


if __name__ == "__main__":
    main()
