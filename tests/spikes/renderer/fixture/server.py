"""Fixture sites for the renderer spike (A-9). Stdlib only, throwaway.

One server answers for several host names (Compose network aliases): site.test (pages), api.test (JSON API, CORS),
cdn.test (bundle, styles, images, fonts), tracker.test (a "tracker"). Every response waits LATENCY_MS first, to
approximate a remote host. GET /__stats on any host returns and resets the per-host request counts.
"""

import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

ROOT = Path(__file__).resolve().parent / "www"
LATENCY = int(os.environ.get("LATENCY_MS", "100")) / 1000.0
PORT = int(os.environ.get("PORT", "80"))

lock = threading.Lock()
counts: dict[str, int] = {}
news_version = [1]

TYPES = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".mjs": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".png": "image/png",
    ".woff2": "font/woff2",
    ".json": "application/json",
}

PAGE_WORDS = (
    "Kommunen erbjuder service inom skola omsorg bygglov avfall kultur fritid trafik och miljö "
    "för invånare företagare och besökare i hela kommunen"
).split()


def page_text(n: int) -> str:
    words = [PAGE_WORDS[(n * 7 + i) % len(PAGE_WORDS)] for i in range(120)]
    return " ".join(words)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stdout.write("%s %s %s\n" % (self.headers.get("Host", "-"), self.command, self.path))
        sys.stdout.flush()

    def _host(self) -> str:
        return (self.headers.get("Host") or "").split(":")[0]

    def _send(self, status: int, body: bytes, ctype: str, extra: dict | None = None):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _cors(self) -> dict:
        origin = self.headers.get("Origin")
        if origin in ("http://site.test", "https://site.test"):
            return {"Access-Control-Allow-Origin": origin, "Vary": "Origin"}
        return {}

    def do_OPTIONS(self):
        self._count()
        time.sleep(LATENCY)
        headers = self._cors()
        if headers:
            headers.update({
                "Access-Control-Allow-Methods": "GET, POST",
                "Access-Control-Allow-Headers": "x-spike, content-type",
                "Access-Control-Max-Age": "0",
            })
        self._send(204, b"", "text/plain", headers)

    def do_POST(self):
        self._count()
        time.sleep(LATENCY)
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""
        self._send(200, json.dumps({"echo": body.decode("utf-8", "replace")}).encode(), "application/json", self._cors())

    def do_HEAD(self):
        self.do_GET()

    def _count(self):
        with lock:
            key = self._host()
            counts[key] = counts.get(key, 0) + 1

    def do_GET(self):
        url = urlsplit(self.path)
        if url.path == "/__stats":
            with lock:
                snapshot = dict(counts)
                counts.clear()
            return self._send(200, json.dumps(snapshot).encode(), "application/json")
        if url.path == "/__bump":
            news_version[0] += 1
            return self._send(200, str(news_version[0]).encode(), "text/plain")
        self._count()
        time.sleep(LATENCY)
        host = self._host()
        if host == "api.test":
            return self._api(url.path, parse_qs(url.query))
        if host == "tracker.test":
            return self._send(200, b"window.__tracked = true;", TYPES[".js"])
        return self._file(host, url.path)

    def _api(self, path: str, query: dict):
        cors = self._cors()
        m = re.fullmatch(r"/api/page/(\d+)", path)
        if m:
            n = int(m.group(1))
            data = {"title": f"API-TITLE-{n}", "text": f"API-TEXT-{n} " + page_text(n)}
            return self._send(200, json.dumps(data).encode(), "application/json", cors)
        if path == "/api/news":
            v = news_version[0]
            data = {"version": v, "items": [f"NEWS-V{v}-item-{i}" for i in range(5)]}
            return self._send(200, json.dumps(data).encode(), "application/json", {**cors, "Cache-Control": "no-store"})
        m = re.fullmatch(r"/api/items", path)
        if m:
            page = int(query.get("page", ["1"])[0])
            items = [f"SCROLL-ITEM-{(page - 1) * 10 + i}" for i in range(1, 11)]
            data = {"page": page, "items": items, "more": page < 4}
            return self._send(200, json.dumps(data).encode(), "application/json", cors)
        if path == "/api/redirect":
            return self._send(302, b"", "text/plain", {**cors, "Location": "http://api.test/api/page/1"})
        return self._send(404, b"{}", "application/json", cors)

    def _file(self, host: str, path: str):
        if host == "cdn.test" and path == "/old-bundle.js":
            return self._send(301, b"", "text/plain", {"Location": "http://cdn.test/v2/redirected.js"})
        if host == "site.test" and path == "/cookie.html":
            body = (ROOT / "site" / "cookie.html").read_bytes()
            return self._send(200, body, TYPES[".html"], {"Set-Cookie": "spike_session=s3cr3t; Path=/; HttpOnly; SameSite=Lax"})
        if host == "site.test" and path == "/api/session-data":
            cookie = self.headers.get("Cookie") or ""
            if "spike_session=s3cr3t" not in cookie:
                return self._send(401, b"{}", "application/json")
            return self._send(200, json.dumps({"text": "COOKIE-DATA-OK"}).encode(), "application/json")
        if host == "site.test" and path == "/files/report.pdf":
            return self._send(200, b"%PDF-1.4 spike", "application/pdf", {"Content-Disposition": 'attachment; filename="report.pdf"'})
        if host == "site.test" and path == "/moved":
            return self._send(302, b"", "text/plain", {"Location": "/static/1.html"})
        m = re.fullmatch(r"/spa/(\d+)\.html", path)
        if host == "site.test" and m:
            return self._send(200, (ROOT / "site" / "spa.html").read_bytes(), TYPES[".html"])
        m = re.fullmatch(r"/static/(\d+)\.html", path)
        if host == "site.test" and m:
            n = int(m.group(1))
            body = (ROOT / "site" / "static.html").read_text().replace("{{N}}", str(n)).replace("{{TEXT}}", page_text(n))
            return self._send(200, body.encode(), TYPES[".html"])
        file = (ROOT / host.split(".")[0] / path.lstrip("/")).resolve()
        if not str(file).startswith(str(ROOT)) or not file.is_file():
            return self._send(404, b"not found", "text/plain")
        extra = {"Cache-Control": "public, max-age=3600"} if host == "cdn.test" else {}
        extra.update(self._cors())
        return self._send(200, file.read_bytes(), TYPES.get(file.suffix, "application/octet-stream"), extra)


if __name__ == "__main__":
    ThreadingHTTPServer.daemon_threads = True
    print(f"fixture on :{PORT}, latency {LATENCY * 1000:.0f} ms", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
