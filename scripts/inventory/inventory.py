"""Pilot-start source inventory (P1a item 1; to-verify #139, postponed Z1; connectors design §5.1, §5.4, §8.1).
Standard library only, one file, like scripts/capacity/ and scripts/grading/.

Usage:
  python3 -I scripts/inventory/inventory.py sitemap SOURCE [--pages PAGE ...] [--min-main-chars 200] [--delay 2]
  python3 -I scripts/inventory/inventory.py js-need PAGE ... [--min-main-chars 200] [--delay 2]

sitemap  reads a sitemap index or sitemap (an http(s) URL, a site's robots.txt URL whose Sitemap lines name the
         indexes, or a local file; gzip detected by its magic bytes), walks child sitemaps down to index depth 2, and
         classifies every URL by the extension of its last path segment into the P1 formats: HTML, PDF, modern Office,
         ODF, text, images, video, legacy Office/RTF/e-mail (Z1 deferred), macro-enabled Office and archives (Z1
         unsupported), other. It reports counts and shares, the lastmod coverage and age distribution, and the hosts.
         A local index reads its children from its own directory, by the last path segment of each child's loc.
js-need  extracts the visible text of raw HTML (no JavaScript) from local files or URLs, finds the main root as the
         worker's generic preset does (main, article, [role=main], else body) and flags a page whose main text is
         shorter than --min-main-chars as "may need JavaScript" (connectors design §8.1, simplification pass §13.2).

Network use is polite and minimal: User-Agent "UmeaKommunBot/1.0 (inventory)", robots.txt fetched first per origin and
honoured (RFC 9309 as the worker reads it: 4xx allows all, 5xx, 429 or unreachable disallows all), at least --delay
seconds between the end of one request and the start of the next (a larger Crawl-delay is honoured, capped at 60 s),
one request at a time, no cookies. The sitemap command fetches only robots.txt and sitemap files, never pages.

Each run writes <out-dir>/<UTC time>-<command>.md (the report, with the request log) and .json; a network sitemap run
also saves the fetched sitemap files to <out-dir>/<UTC time>-sitemaps/ so the inventory can be recomputed offline.
Exit 0 when done and no page is flagged, 1 when a page is flagged, 2 when the run could not be done.
"""

import argparse
import gzip
import html.parser
import http.client
import io
import json
import re
import ssl
import statistics
import sys
import time
import urllib.parse
import zlib
from collections import Counter
from collections.abc import Callable
from dataclasses import asdict, dataclass, field
from datetime import UTC, datetime, timedelta
from pathlib import Path
from xml.parsers import expat

USER_AGENT = "UmeaKommunBot/1.0 (inventory)"
ROBOTS_AGENT = "umeakommunbot"  # the robots.txt group name, compared case-insensitively (connectors design §5.1)
DEFAULT_OUT = Path(__file__).resolve().parent / "results"
DEFAULT_DELAY_S = 2.0
MAX_CRAWL_DELAY_S = 60.0
REQUEST_TIMEOUT_S = 30.0
MAX_REDIRECTS = 5
MAX_ENTRIES = 50_000  # per sitemap file (sitemaps.org; SitemapParser.MaxEntries)
MAX_UNCOMPRESSED = 50 * 1024 * 1024  # per sitemap file (SitemapParser.MaxUncompressedBytes)
MAX_HTML_BYTES = 10 * 1024 * 1024  # MediaTypes.BodyLimit for HTML
MAX_ROBOTS_BYTES = 500 * 1024  # RFC 9309 §2.5
MAX_INDEX_DEPTH = 2  # an index's children are read only while the index is above this depth (connectors §5.1)
DEFAULT_MAX_SITEMAPS = 50
DEFAULT_MIN_MAIN_CHARS = 200

REFUSED_DTD = "sitemap.dtd"
REFUSED_SIZE = "sitemap.too_large"
REFUSED_SYNTAX = "sitemap.invalid"
REFUSED_DEPTH = "sitemap.depth"
REFUSED_MISSING = "sitemap.missing"
REFUSED_ROBOTS = "robots.disallowed"

# --------------------------------------------------------------------------------------------------------------------
# Formats: the P1 classes by extension, as CrawlScope.TypesByExtension maps them (connectors §5.2, parser §3, Z1).
# --------------------------------------------------------------------------------------------------------------------

FORMATS: list[tuple[str, str, tuple[str, ...]]] = [
    ("html", "HTML", ("html", "htm", "xhtml", "php", "asp", "aspx", "jsp")),
    ("pdf", "PDF", ("pdf",)),
    ("office", "Modern Office (docx, xlsx, pptx)", ("docx", "xlsx", "pptx")),
    ("odf", "ODF (odt, ods, odp)", ("odt", "ods", "odp")),
    ("text", "Text (txt, md, csv)", ("txt", "md", "csv")),
    (
        "image",
        "Images",
        ("jpg", "jpeg", "jfif", "png", "gif", "webp", "svg", "bmp", "tif", "tiff", "ico", "avif", "heic"),
    ),
    ("video", "Video", ("mp4", "webm", "mov", "avi", "mkv", "m4v", "mpg", "mpeg", "wmv")),
    ("deferred", "Legacy Office, RTF, e-mail (Z1 deferred)", ("doc", "xls", "ppt", "rtf", "eml", "msg")),
    ("macro", "Macro-enabled Office (Z1 unsupported)", ("docm", "xlsm", "pptm")),
    ("archive", "Archives (Z1 unsupported)", ("zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz")),
    ("other", "Other", ()),
]
# Sitevision file archives (umea.se: /images/18.<id>/<time>/<name>, /download/18.<id>/<time>/<name>): files, not pages.
FILE_PATH_PREFIXES = ("/images/", "/download/", "/webdav/files/")
FORMAT_LABELS = {key: label for key, label, _ in FORMATS}
CLASS_BY_EXTENSION = {ext: key for key, _, exts in FORMATS for ext in exts}
# Non-HTML document classes: the denominator of the Z1 share among documents.
DOCUMENT_CLASSES = ("pdf", "office", "odf", "text", "deferred", "macro", "archive")
AGE_BUCKETS: list[tuple[str, int | None]] = [
    ("≤ 30 d", 30),
    ("31-90 d", 90),
    ("91-365 d", 365),
    ("1-2 y", 730),
    ("2-5 y", 1826),
    ("> 5 y", None),
]


def extension(url: str) -> str:
    """The lower-case extension of the URL's last path segment, or "" when it has none."""
    path = urllib.parse.urlsplit(url).path
    segment = path.rsplit("/", 1)[-1]
    return segment.rsplit(".", 1)[-1].lower() if "." in segment else ""


def classify(url: str) -> str:
    """A URL's format class. A last segment without an extension counts as HTML (the worker admits it and the content
    type decides), except below a file-archive path, where it is a file of unknown type ("other")."""
    ext = extension(url)
    if ext:
        return CLASS_BY_EXTENSION.get(ext, "other")
    path = urllib.parse.urlsplit(url).path
    return "other" if path.startswith(FILE_PATH_PREFIXES) and not path.endswith("/") else "html"


def parse_lastmod(value: str | None) -> datetime | None:
    """W3C datetime as sitemaps use it (YYYY, YYYY-MM, YYYY-MM-DD or a full date-time), UTC when no zone."""
    if not value:
        return None
    try:
        if re.fullmatch(r"\d{4}", value):
            return datetime(int(value), 1, 1, tzinfo=UTC)
        if re.fullmatch(r"\d{4}-\d{2}", value):
            return datetime(int(value[:4]), int(value[5:]), 1, tzinfo=UTC)
        parsed = datetime.fromisoformat(value)
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)


# --------------------------------------------------------------------------------------------------------------------
# Sitemap files: gzip by magic bytes, at most 50 MB uncompressed and 50,000 entries, no DTD (SitemapParser semantics).
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class ParsedSitemap:
    is_index: bool = False
    entries: list[tuple[str, str | None]] = field(default_factory=list)
    compressed: bool = False
    xml_bytes: int = 0
    truncated: bool = False
    error: str | None = None
    images: int = 0  # <image:image> extension elements inside <url>
    videos: int = 0  # <video:video> extension elements inside <url>
    nested_locs: int = 0  # loc elements below an entry's own loc level (image:loc, video:player_loc, ...)


class _Refused(Exception):
    def __init__(self, reason: str):
        super().__init__(reason)
        self.reason = reason


class _Full(Exception):
    pass


class _SitemapHandler:
    """Reads <url> or <sitemap> entries of any namespace: loc and lastmod are taken only directly below the entry."""

    def __init__(self, result: ParsedSitemap):
        self.result = result
        self.stack: list[str] = []
        self.loc: str | None = None
        self.lastmod: str | None = None
        self.text: list[str] | None = None

    def start(self, name: str, _attrs) -> None:
        local = name.rsplit(" ", 1)[-1]
        depth = len(self.stack)
        self.stack.append(local)
        if depth == 0:
            self.result.is_index = local == "sitemapindex"
        elif depth == 1 and local in ("url", "sitemap"):
            self.loc = None
            self.lastmod = None
        elif depth == 2 and self.stack[1] in ("url", "sitemap") and local in ("loc", "lastmod"):
            self.text = []
        elif depth == 2 and local == "image":
            self.result.images += 1
        elif depth == 2 and local == "video":
            self.result.videos += 1
        elif depth >= 3 and local.endswith("loc"):
            self.result.nested_locs += 1

    def data(self, text: str) -> None:
        if self.text is not None:
            self.text.append(text)

    def end(self, _name: str) -> None:
        local = self.stack.pop()
        depth = len(self.stack)
        if depth == 2 and self.text is not None:
            value = "".join(self.text).strip()
            self.text = None
            if local == "loc":
                self.loc = value
            else:
                self.lastmod = value
        elif depth == 1 and local in ("url", "sitemap") and self.loc:
            if len(self.result.entries) == MAX_ENTRIES:
                self.result.truncated = True
                raise _Full
            self.result.entries.append((self.loc, self.lastmod))
            self.loc = None


def _refuse_dtd(*_args) -> None:
    raise _Refused(REFUSED_DTD)


def parse_sitemap(data: bytes) -> ParsedSitemap:
    """Parses one sitemap file; entries read before an error are kept, as SitemapParser keeps them."""
    result = ParsedSitemap(compressed=data[:2] == b"\x1f\x8b")
    if result.compressed:
        try:
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as stream:
                xml = stream.read(MAX_UNCOMPRESSED + 1)
        except (OSError, EOFError, zlib.error):
            result.error = REFUSED_SYNTAX
            return result
    else:
        xml = data
    result.xml_bytes = len(xml)
    if len(xml) > MAX_UNCOMPRESSED:
        result.error = REFUSED_SIZE
        return result

    handler = _SitemapHandler(result)
    parser = expat.ParserCreate(namespace_separator=" ")
    parser.SetParamEntityParsing(expat.XML_PARAM_ENTITY_PARSING_NEVER)
    parser.StartDoctypeDeclHandler = _refuse_dtd
    parser.EntityDeclHandler = _refuse_dtd
    parser.StartElementHandler = handler.start
    parser.EndElementHandler = handler.end
    parser.CharacterDataHandler = handler.data
    try:
        parser.Parse(xml, True)
    except _Refused as refused:
        result.error = refused.reason
    except _Full:
        pass
    except expat.ExpatError:
        result.error = REFUSED_SYNTAX
    return result


# --------------------------------------------------------------------------------------------------------------------
# robots.txt: the worker's RobotsTxt (RFC 9309) ported: the UmeaKommunBot groups else "*", longest match, Allow on ties.
# --------------------------------------------------------------------------------------------------------------------


def _normalise_path(value: str) -> str:
    """Upper-case percent escapes and percent-encode non-ASCII as UTF-8, so /ö and /%c3%b6 compare equal."""
    value = re.sub(r"%[0-9a-fA-F]{2}", lambda m: m.group(0).upper(), value)
    return "".join(c if ord(c) < 128 else urllib.parse.quote(c) for c in value)


def _glob_matches(pattern: str, path: str) -> bool:
    """`*` is any run, a final `$` anchors the end, else a prefix match; greedy with one backtrack point."""
    anchored = pattern.endswith("$")
    glob = pattern[:-1] if anchored else pattern + "*"
    p = i = 0
    star, mark = -1, 0
    while i < len(path):
        if p < len(glob) and glob[p] != "*" and glob[p] == path[i]:
            p += 1
            i += 1
        elif p < len(glob) and glob[p] == "*":
            star = p
            p += 1
            mark = i
        elif star >= 0:
            p = star + 1
            mark += 1
            i = mark
        else:
            return False
    while p < len(glob) and glob[p] == "*":
        p += 1
    return p == len(glob)


@dataclass
class Robots:
    rules: list[tuple[bool, str]] = field(default_factory=list)  # (allow, normalised pattern)
    disallow_all: bool = False
    crawl_delay: float | None = None
    sitemaps: list[str] = field(default_factory=list)
    group: str = "none"  # which group applied: our product token, "*", or none

    @classmethod
    def parse(cls, content: bytes) -> "Robots":
        text = content[:MAX_ROBOTS_BYTES].decode("utf-8", errors="replace").removeprefix("\ufeff")
        groups: list[dict] = []
        sitemaps: list[str] = []
        current: dict | None = None
        last_was_agent = False
        for raw in text.split("\n"):
            line = raw.split("#", 1)[0]
            if ":" not in line:
                continue
            key, value = (part.strip() for part in line.split(":", 1))
            key = key.lower()
            if key == "user-agent":
                if current is None or not last_was_agent:
                    current = {"agents": [], "rules": [], "delay": None}
                    groups.append(current)
                current["agents"].append(value)
                last_was_agent = True
                continue
            if key in ("allow", "disallow") and current is not None and value:
                anchored = value.endswith("$")
                body = value[:-1] if anchored else value
                pattern = "*".join(_normalise_path(part) for part in body.split("*")) + ("$" if anchored else "")
                current["rules"].append((key == "allow", pattern))
            elif key == "crawl-delay" and current is not None:
                try:
                    delay = float(value)
                except ValueError:
                    delay = -1.0
                if delay >= 0:
                    current["delay"] = delay
            elif key == "sitemap" and value:
                sitemaps.append(value)
            last_was_agent = False

        def names_us(agent: str) -> bool:
            return re.split(r"[/ ]", agent, maxsplit=1)[0].lower() == ROBOTS_AGENT

        applying = [g for g in groups if any(names_us(a) for a in g["agents"])]
        group = "UmeaKommunBot"
        if not applying:
            applying = [g for g in groups if "*" in g["agents"]]
            group = "*" if applying else "none"
        delays = [g["delay"] for g in applying if g["delay"] is not None]
        return cls(
            rules=[rule for g in applying for rule in g["rules"]],
            crawl_delay=delays[0] if delays else None,
            sitemaps=sitemaps,
            group=group,
        )

    def allowed(self, url: str) -> bool:
        parts = urllib.parse.urlsplit(url)
        target = _normalise_path((parts.path or "/") + (f"?{parts.query}" if parts.query else ""))
        if target == "/robots.txt":
            return True
        if self.disallow_all:
            return False
        best: tuple[bool, str] | None = None
        for allow, pattern in self.rules:
            if _glob_matches(pattern, target) and (
                best is None or len(pattern) > len(best[1]) or (len(pattern) == len(best[1]) and allow and not best[0])
            ):
                best = (allow, pattern)
        return best[0] if best else True


# --------------------------------------------------------------------------------------------------------------------
# Polite fetcher: robots first per origin, one request at a time, a pause between requests, every request logged.
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class Response:
    url: str
    status: int
    body: bytes
    content_type: str = ""
    error: str | None = None


@dataclass
class RobotsResult:
    origin: str
    url: str
    status: int | None
    verdict: str  # parsed, allow-all (4xx), disallow-all (5xx, 429, unreachable)
    group: str
    crawl_delay: float | None
    sitemaps: list[str]
    rules: int


class Fetcher:
    def __init__(
        self,
        delay: float = DEFAULT_DELAY_S,
        timeout: float = REQUEST_TIMEOUT_S,
        ca_file: str | None = None,
        sleep: Callable[[float], None] = time.sleep,
        clock: Callable[[], float] = time.monotonic,
    ):
        self.delay = delay
        self.timeout = timeout
        self.context = ssl.create_default_context(cafile=ca_file)
        self.sleep = sleep
        self.clock = clock
        self.log: list[dict] = []
        self.robots: dict[str, Robots] = {}
        self.robots_results: list[RobotsResult] = []
        self._last_end: float | None = None

    @staticmethod
    def origin(url: str) -> str:
        parts = urllib.parse.urlsplit(url)
        return f"{parts.scheme.lower()}://{parts.netloc.lower()}"

    def _pause(self, origin: str) -> None:
        delay = self.delay
        robots = self.robots.get(origin)
        if robots and robots.crawl_delay is not None:
            delay = max(delay, min(robots.crawl_delay, MAX_CRAWL_DELAY_S))  # honoured only when it lowers our rate
        if self._last_end is not None:
            wait = self._last_end + delay - self.clock()
            if wait > 0:
                self.sleep(wait)

    def _request(self, url: str, purpose: str, limit: int) -> Response:
        parts = urllib.parse.urlsplit(url)
        self._pause(self.origin(url))
        started = datetime.now(UTC)
        t0 = self.clock()
        entry = {"time": started.isoformat(timespec="milliseconds"), "url": url, "purpose": purpose}
        try:
            if parts.scheme == "https":
                conn = http.client.HTTPSConnection(
                    parts.hostname, parts.port, timeout=self.timeout, context=self.context
                )
            else:
                conn = http.client.HTTPConnection(parts.hostname, parts.port, timeout=self.timeout)
            try:
                target = (parts.path or "/") + (f"?{parts.query}" if parts.query else "")
                conn.request(
                    "GET",
                    target,
                    headers={"User-Agent": USER_AGENT, "Accept-Encoding": "identity", "Accept": "*/*"},
                )
                answer = conn.getresponse()
                body = answer.read(limit + 1)
                response = Response(url, answer.status, body, answer.getheader("Content-Type", "") or "")
                location = answer.getheader("Location")
                if location and answer.status in (301, 302, 303, 307, 308):
                    response.error = "redirect:" + urllib.parse.urljoin(url, location)
            finally:
                conn.close()
        except (OSError, http.client.HTTPException) as exc:
            response = Response(url, 0, b"", error=f"unreachable: {type(exc).__name__}: {exc}")
        self._last_end = self.clock()
        entry.update(
            status=response.status,
            bytes=len(response.body),
            ms=round((self._last_end - t0) * 1000),
            error=response.error,
        )
        self.log.append(entry)
        return response

    def _robots_for(self, url: str) -> Robots:
        origin = self.origin(url)
        if origin in self.robots:
            return self.robots[origin]
        robots_url = origin + "/robots.txt"
        response = self._follow(robots_url, "robots", MAX_ROBOTS_BYTES, check_robots=False)
        status = response.status
        if 200 <= status < 300:
            robots, verdict = Robots.parse(response.body), "parsed"
        elif 400 <= status < 500 and status != 429:
            robots, verdict = Robots(), "allow-all"
        else:
            robots, verdict = Robots(disallow_all=True), "disallow-all"
        self.robots[origin] = robots
        self.robots_results.append(
            RobotsResult(
                origin,
                robots_url,
                status or None,
                verdict,
                robots.group,
                robots.crawl_delay,
                robots.sitemaps,
                len(robots.rules),
            )
        )
        return robots

    def _follow(self, url: str, purpose: str, limit: int, check_robots: bool = True) -> Response:
        for _ in range(MAX_REDIRECTS + 1):
            if urllib.parse.urlsplit(url).scheme not in ("http", "https"):
                return Response(url, 0, b"", error="scheme")
            if check_robots and not self._robots_for(url).allowed(url):
                self.log.append(
                    {
                        "time": datetime.now(UTC).isoformat(timespec="milliseconds"),
                        "url": url,
                        "purpose": purpose,
                        "status": None,
                        "bytes": 0,
                        "ms": 0,
                        "error": f"{REFUSED_ROBOTS} (not fetched)",
                    }
                )
                return Response(url, 0, b"", error=REFUSED_ROBOTS)
            response = self._request(url, purpose, limit)
            if response.error and response.error.startswith("redirect:"):
                url = response.error.removeprefix("redirect:")
                continue
            return response
        return Response(url, 0, b"", error="too many redirects")

    def get(self, url: str, purpose: str, limit: int) -> Response:
        return self._follow(url, purpose, limit)

    def robots_txt(self, url: str) -> Robots:
        return self._robots_for(url)


# --------------------------------------------------------------------------------------------------------------------
# Inventory: walk the index, classify, measure lastmod.
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class SitemapFile:
    source: str
    depth: int
    kind: str  # index, urlset or "-" when refused before parsing
    entries: int
    compressed: bool
    raw_bytes: int
    xml_bytes: int
    truncated: bool
    error: str | None
    images: int
    videos: int
    nested_locs: int


@dataclass
class Inventory:
    source: str
    now: datetime
    files: list[SitemapFile] = field(default_factory=list)
    urls: dict[str, str | None] = field(default_factory=dict)  # loc -> lastmod as written (first occurrence)
    duplicates: int = 0
    robots_sitemaps: list[str] = field(default_factory=list)


def _is_url(value: str) -> bool:
    return urllib.parse.urlsplit(value).scheme in ("http", "https")


def walk(
    source: str,
    fetcher: Fetcher | None,
    now: datetime,
    max_sitemaps: int = DEFAULT_MAX_SITEMAPS,
    save_dir: Path | None = None,
) -> Inventory:
    """Reads the source and its child sitemaps breadth first; each file once, at most max_sitemaps files."""
    inventory = Inventory(source, now)
    queue: list[tuple[str, int, Path | None]] = []
    if _is_url(source):
        if fetcher is None:
            raise ValueError("a URL source needs a fetcher")
        robots = fetcher.robots_txt(source)
        if urllib.parse.urlsplit(source).path == "/robots.txt":
            inventory.robots_sitemaps = list(robots.sitemaps)
            queue = [(url, 0, None) for url in robots.sitemaps]
        else:
            queue = [(source, 0, None)]
    else:
        path = Path(source)
        queue = [(str(path), 0, path.parent)]

    seen: set[str] = set()
    while queue and len(inventory.files) < max_sitemaps:
        location, depth, local_dir = queue.pop(0)
        if location in seen:
            continue
        seen.add(location)
        data, error = _read(location, local_dir, fetcher)
        if save_dir is not None and data is not None:
            save_dir.mkdir(parents=True, exist_ok=True)
            (save_dir / _basename(location)).write_bytes(data)
        if data is None:
            inventory.files.append(SitemapFile(location, depth, "-", 0, False, 0, 0, False, error, 0, 0, 0))
            continue
        parsed = parse_sitemap(data)
        kind = "index" if parsed.is_index else "urlset"
        error = parsed.error
        if parsed.is_index:
            if depth < MAX_INDEX_DEPTH:
                queue.extend((loc, depth + 1, local_dir) for loc, _ in parsed.entries)
            elif parsed.entries:
                error = error or REFUSED_DEPTH
        else:
            for loc, lastmod in parsed.entries:
                if loc in inventory.urls:
                    inventory.duplicates += 1
                else:
                    inventory.urls[loc] = lastmod
        inventory.files.append(
            SitemapFile(
                location,
                depth,
                kind,
                len(parsed.entries),
                parsed.compressed,
                len(data),
                parsed.xml_bytes,
                parsed.truncated,
                error,
                parsed.images,
                parsed.videos,
                parsed.nested_locs,
            )
        )
    return inventory


def _basename(location: str) -> str:
    path = urllib.parse.urlsplit(location).path if _is_url(location) else location
    return Path(path).name or "sitemap.xml"


def _read(location: str, local_dir: Path | None, fetcher: Fetcher | None) -> tuple[bytes | None, str | None]:
    if local_dir is not None:
        path = Path(location) if not _is_url(location) else local_dir / _basename(location)
        if not path.is_file():
            return None, REFUSED_MISSING
        if path.stat().st_size > MAX_UNCOMPRESSED:
            return None, REFUSED_SIZE
        return path.read_bytes(), None
    if fetcher is None:
        raise ValueError("a URL sitemap needs a fetcher")
    response = fetcher.get(location, "sitemap", MAX_UNCOMPRESSED)
    if response.error:
        return None, response.error
    if not 200 <= response.status < 300:
        return None, f"http.{response.status}"
    if len(response.body) > MAX_UNCOMPRESSED:
        return None, REFUSED_SIZE
    return response.body, None


def _age_bucket(age: timedelta) -> str:
    days = age.total_seconds() / 86400
    if days < -1:
        return "future"
    for label, limit in AGE_BUCKETS:
        if limit is None or days <= limit:
            return label
    return AGE_BUCKETS[-1][0]


def summarise(inventory: Inventory) -> dict:
    """Counts and shares per format, Z1 shares, lastmod coverage and ages, hosts, extensions."""
    total = len(inventory.urls)
    classes: Counter[str] = Counter()
    extensions: Counter[str] = Counter()
    hosts: Counter[str] = Counter()
    examples: dict[str, list[str]] = {}
    ages: dict[str, Counter[str]] = {}
    age_days: dict[str, list[float]] = {}
    lastmod_valid = lastmod_invalid = 0
    newest: datetime | None = None
    oldest: datetime | None = None
    for loc, raw in inventory.urls.items():
        cls = classify(loc)
        classes[cls] += 1
        extensions[extension(loc) or "(none)"] += 1
        hosts[(urllib.parse.urlsplit(loc).hostname or "(none)").lower()] += 1
        if cls not in ("html", "pdf", "image", "video") and len(examples.setdefault(cls, [])) < 5:
            examples[cls].append(loc)
        parsed = parse_lastmod(raw)
        bucket_counter = ages.setdefault(cls, Counter())
        if parsed is None:
            bucket_counter["no lastmod" if not raw else "invalid"] += 1
            if raw:
                lastmod_invalid += 1
            continue
        lastmod_valid += 1
        newest = parsed if newest is None or parsed > newest else newest
        oldest = parsed if oldest is None or parsed < oldest else oldest
        age = inventory.now - parsed
        bucket_counter[_age_bucket(age)] += 1
        age_days.setdefault(cls, []).append(age.total_seconds() / 86400)

    def share(n: int, d: int) -> float | None:
        return round(n / d, 4) if d else None

    documents = sum(classes[c] for c in DOCUMENT_CLASSES)
    z1_all = classes["deferred"] + classes["macro"] + classes["archive"]
    return {
        "total": total,
        "duplicates": inventory.duplicates,
        "formats": [
            {"class": key, "label": label, "count": classes[key], "share": share(classes[key], total)}
            for key, label, _ in FORMATS
        ],
        "z1": {
            "deferred": classes["deferred"],
            "deferred_share_of_all": share(classes["deferred"], total),
            "deferred_share_of_documents": share(classes["deferred"], documents),
            "all_z1": z1_all,
            "all_z1_share_of_documents": share(z1_all, documents),
            "documents": documents,
        },
        "lastmod": {
            "valid": lastmod_valid,
            "invalid": lastmod_invalid,
            "missing": total - lastmod_valid - lastmod_invalid,
            "coverage": share(lastmod_valid, total),
            "oldest": oldest.isoformat() if oldest else None,
            "newest": newest.isoformat() if newest else None,
            "ages": {cls: dict(counter) for cls, counter in ages.items()},
            "median_age_days": {cls: round(statistics.median(days)) for cls, days in age_days.items()},
        },
        "hosts": hosts.most_common(),
        "extensions": extensions.most_common(),
        "examples": examples,
    }


# --------------------------------------------------------------------------------------------------------------------
# JavaScript need: visible text of the raw HTML, the main root as the generic preset finds it (HtmlExtractor).
# --------------------------------------------------------------------------------------------------------------------

DROPPED = {
    "script", "style", "noscript", "template", "svg", "iframe", "object",
    "button", "select", "input", "textarea", "nav",
}  # fmt: skip
HIDDEN_CLASSES = {
    "sr-only", "visually-hidden", "visuallyhidden", "screen-reader-text", "sv-visuallyhidden", "env-assistive-text",
}  # fmt: skip
VOID = {
    "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
}  # fmt: skip
ROOT_ORDER = ("main", "article", "[role=main]")  # the generic preset's main-root alternatives, in order
MOUNT_IDS = {"app", "root", "__next", "__nuxt", "___gatsby", "svelte"}
# Implied end tags (HTML parsing rules, simplified): a start tag closes the open element of these kinds found before a
# boundary element, so an unclosed <li>, <p> or <td> does not swallow its siblings.
P_CLOSERS = {
    "p", "div", "ul", "ol", "dl", "table", "pre", "blockquote", "form", "section", "article", "aside", "header",
    "footer", "main", "nav", "figure", "address", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
}  # fmt: skip
IMPLIED: dict[str, tuple[set[str], set[str]]] = {
    "li": ({"li"}, {"ul", "ol", "menu"}),
    "dt": ({"dt", "dd"}, {"dl"}),
    "dd": ({"dt", "dd"}, {"dl"}),
    "tr": ({"tr", "td", "th"}, {"table", "thead", "tbody", "tfoot"}),
    "td": ({"td", "th"}, {"tr", "table"}),
    "th": ({"td", "th"}, {"tr", "table"}),
    "option": ({"option"}, {"select", "datalist"}),
}
P_BOUNDARIES = {"div", "main", "article", "section", "li", "td", "th", "dd", "dt", "blockquote", "body", "html"}
FRAMEWORK_MARKERS = {
    "__NEXT_DATA__": "Next.js",
    "window.__NUXT__": "Nuxt",
    "ng-version=": "Angular",
    "data-reactroot": "React",
    "data-v-app": "Vue",
}


@dataclass
class _Frame:
    tag: str
    skip: bool
    head: bool
    link: bool
    roots: tuple[int, ...]
    mounts: tuple[int, ...]
    noscript: bool


class _TextParser(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack: list[_Frame] = [_Frame("#document", False, False, False, (), (), False)]
        self.visible: list[str] = []
        self.root_kinds: list[str] = []  # index = root candidate id, value = main, article or [role=main]
        self.root_text: dict[int, list[str]] = {}
        self.root_link_text: dict[int, list[str]] = {}
        self.mount_names: list[str] = []
        self.mount_text: dict[int, list[str]] = {}
        self.noscript_text: list[str] = []
        self.script_chars = 0

    def handle_starttag(self, tag, attrs):
        a = {k: (v or "") for k, v in attrs}
        if tag == "body":
            self._close("head")
        if tag in P_CLOSERS:
            self._close_implied({"p"}, P_BOUNDARIES)
        if tag in IMPLIED:
            self._close_implied(*IMPLIED[tag])
        parent = self.stack[-1]
        hidden = (
            "hidden" in a
            or a.get("aria-hidden", "").lower() == "true"
            or bool(HIDDEN_CLASSES & set(a.get("class", "").split()))
        )
        roots = parent.roots
        kind = tag if tag in ("main", "article") else "[role=main]" if a.get("role", "").lower() == "main" else None
        if kind:
            roots = (*roots, len(self.root_kinds))
            self.root_kinds.append(kind)
        mounts = parent.mounts
        if a.get("id") in MOUNT_IDS:
            mounts = (*mounts, len(self.mount_names))
            self.mount_names.append(a["id"])
        frame = _Frame(
            tag,
            parent.skip or tag in DROPPED or hidden,
            parent.head or tag == "head",
            parent.link or tag == "a",
            roots,
            mounts,
            parent.noscript or tag == "noscript",
        )
        if tag not in VOID:
            self.stack.append(frame)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in VOID and self.stack[-1].tag == tag:
            self.stack.pop()

    def handle_endtag(self, tag):
        self._close(tag)

    def _close_implied(self, closes: set[str], boundaries: set[str]) -> None:
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag in closes:
                del self.stack[i:]
                return
            if self.stack[i].tag in boundaries:
                return

    def _close(self, tag: str) -> None:
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                return

    def handle_data(self, data):
        frame = self.stack[-1]
        if frame.tag in ("script", "style"):
            self.script_chars += len(data) if frame.tag == "script" else 0
            return
        if frame.noscript:
            self.noscript_text.append(data)
        if frame.skip or frame.head:
            return
        self.visible.append(data)
        for root in frame.roots:
            self.root_text.setdefault(root, []).append(data)
            if frame.link:
                self.root_link_text.setdefault(root, []).append(data)
        for mount in frame.mounts:
            self.mount_text.setdefault(mount, []).append(data)


def _chars(parts: list[str] | None) -> int:
    return len(" ".join(" ".join(parts or []).split()))


def decode_html(data: bytes) -> str:
    if data.startswith(b"\xef\xbb\xbf"):
        return data[3:].decode("utf-8", errors="replace")
    match = re.search(rb"""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9_\-:.]+)""", data[:4096], re.IGNORECASE)
    encoding = match.group(1).decode("ascii") if match else "utf-8"
    try:
        return data.decode(encoding, errors="replace")
    except LookupError:
        return data.decode("utf-8", errors="replace")


@dataclass
class PageYield:
    page: str
    html_bytes: int
    visible_chars: int
    root: str
    main_chars: int
    main_link_chars: int
    script_chars: int
    flagged: bool
    hints: list[str]
    error: str | None = None

    @property
    def main_share(self) -> float:
        return self.main_chars / self.visible_chars if self.visible_chars else 0.0


def text_yield(page: str, data: bytes, min_main_chars: int = DEFAULT_MIN_MAIN_CHARS) -> PageYield:
    text = decode_html(data)
    parser = _TextParser()
    parser.feed(text)
    parser.close()
    root, main_id = "body", None
    for kind in ROOT_ORDER:
        if kind in parser.root_kinds:
            root, main_id = kind, parser.root_kinds.index(kind)
            break
    visible = _chars(parser.visible)
    main = visible if main_id is None else _chars(parser.root_text.get(main_id))
    links = _chars(parser.root_link_text.get(main_id)) if main_id is not None else 0
    hints = []
    for mount_id, name in enumerate(parser.mount_names):
        if _chars(parser.mount_text.get(mount_id)) == 0:
            hints.append(f"empty mount point #{name}")
    if re.search("javascript", " ".join(parser.noscript_text), re.IGNORECASE):
        hints.append("noscript text asks for JavaScript")
    if text and parser.script_chars / len(text) >= 0.5:
        hints.append(f"scripts are {parser.script_chars / len(text):.0%} of the HTML")
    hints.extend(f"{name} marker" for marker, name in FRAMEWORK_MARKERS.items() if marker in text)
    return PageYield(page, len(data), visible, root, main, links, parser.script_chars, main < min_main_chars, hints)


def read_pages(pages: list[str], fetcher: Fetcher | None, min_main_chars: int) -> list[PageYield]:
    results = []
    for page in pages:
        if _is_url(page):
            if fetcher is None:
                raise ValueError("a URL page needs a fetcher")
            response = fetcher.get(page, "page", MAX_HTML_BYTES)
            failure = response.error or (None if 200 <= response.status < 300 else f"http.{response.status}")
            if not failure and not response.content_type.lower().startswith(("text/html", "application/xhtml+xml")):
                failure = f"not HTML ({response.content_type or 'no content type'})"
            if failure:
                results.append(PageYield(page, 0, 0, "-", 0, 0, 0, False, [], failure))
                continue
            data = response.body[:MAX_HTML_BYTES]
        else:
            data = Path(page).read_bytes()
        results.append(text_yield(page, data, min_main_chars))
    return results


# --------------------------------------------------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------------------------------------------------


def _pct(value: float | None) -> str:
    return "-" if value is None else f"{value * 100:.1f} %"


def _cell(value) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ")


def render_report(
    command: str,
    started: datetime,
    inventory: Inventory | None,
    summary: dict | None,
    yields: list[PageYield] | None,
    fetcher: Fetcher | None,
    min_main_chars: int,
    delay: float,
) -> str:
    out = [f"# Source inventory: {command}", ""]
    out.append(f"- Run: {started.isoformat(timespec='seconds')} (UTC), `inventory.py {command}`")
    if fetcher is not None and fetcher.log:
        out.append(f"- User-Agent `{USER_AGENT}`; at least {delay:g} s between requests; {len(fetcher.log)} requests")
    if inventory is not None and summary is not None:
        out += _render_inventory(inventory, summary)
    if yields is not None:
        out += _render_yields(yields, min_main_chars)
    if fetcher is not None and (fetcher.log or fetcher.robots_results):
        out += _render_requests(fetcher)
    return "\n".join(out) + "\n"


def _render_inventory(inventory: Inventory, s: dict) -> list[str]:
    out = [f"- Source: `{inventory.source}`; ages relative to {inventory.now.date().isoformat()}", ""]
    if inventory.robots_sitemaps:
        out += ["robots.txt names: " + ", ".join(f"`{u}`" for u in inventory.robots_sitemaps), ""]
    out += [
        "## Sitemap files",
        "",
        "| File | Depth | Kind | Entries | gzip | Bytes (raw / XML) | image:image | video:video | Nested loc | Error |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for f in inventory.files:
        error = (f.error or "") + (" truncated at 50,000" if f.truncated else "")
        out.append(
            f"| `{_cell(f.source)}` | {f.depth} | {f.kind} | {f.entries:,} | {'yes' if f.compressed else 'no'} | "
            f"{f.raw_bytes:,} / {f.xml_bytes:,} | {f.images:,} | {f.videos:,} | {f.nested_locs:,} | {error or '-'} |"
        )
    total = s["total"]
    out += ["", f"**{total:,} distinct URLs** ({s['duplicates']:,} duplicate entries not counted).", ""]
    out += ["## Formats", "", "| Format | URLs | Share |", "|---|---|---|"]
    out += [f"| {f['label']} | {f['count']:,} | {_pct(f['share'])} |" for f in s["formats"]]
    z1 = s["z1"]
    out += [
        "",
        "## Z1 share",
        "",
        f"- Legacy Office, RTF, e-mail (`format_deferred`): **{z1['deferred']:,}** URLs = "
        f"{_pct(z1['deferred_share_of_all'])} of all URLs, **{_pct(z1['deferred_share_of_documents'])}** of the "
        f"{z1['documents']:,} non-HTML documents (PDF, Office, ODF, text and the Z1 classes)",
        f"- All Z1 classes (deferred, macro-enabled, archives): {z1['all_z1']:,} = "
        f"{_pct(z1['all_z1_share_of_documents'])} of the non-HTML documents",
    ]
    for cls in ("deferred", "macro", "archive", "office", "odf", "text", "other"):
        if s["examples"].get(cls):
            out.append(f"- Examples, {FORMAT_LABELS[cls]}: " + ", ".join(f"`{u}`" for u in s["examples"][cls]))
    lm = s["lastmod"]
    out += [
        "",
        "## lastmod",
        "",
        f"- Coverage: **{_pct(lm['coverage'])}** ({lm['valid']:,} valid, {lm['invalid']:,} invalid, "
        f"{lm['missing']:,} missing); oldest {lm['oldest'] or '-'}, newest {lm['newest'] or '-'}",
        "",
    ]
    buckets = ["future"] + [label for label, _ in AGE_BUCKETS] + ["invalid", "no lastmod"]
    out += ["| Format | " + " | ".join(buckets) + " | Median age (d) |", "|---" * (len(buckets) + 2) + "|"]
    for key, label, _ in FORMATS:
        counts = lm["ages"].get(key)
        if counts:
            cells = " | ".join(f"{counts.get(b, 0):,}" for b in buckets)
            out.append(f"| {label} | {cells} | {lm['median_age_days'].get(key, '-')} |")
    out += ["", "## Hosts", "", "| Host | URLs |", "|---|---|"]
    out += [f"| {_cell(host)} | {count:,} |" for host, count in s["hosts"]]
    out += ["", "## Extensions (top 25)", "", "| Extension | URLs |", "|---|---|"]
    out += [f"| {_cell(ext)} | {count:,} |" for ext, count in s["extensions"][:25]]
    out.append("")
    return out


def _render_yields(yields: list[PageYield], min_main_chars: int) -> list[str]:
    flagged = [y for y in yields if y.flagged]
    failed = [y for y in yields if y.error]
    out = [
        "## JavaScript need (raw text yield, no JavaScript run)",
        "",
        f"Main root as the generic preset finds it (`main`, `article`, `[role=main]`, else `body`); visible text "
        f"without script, style, nav, form controls and hidden or screen-reader-only elements. A page is flagged "
        f"**may need JavaScript** when its main text is under {min_main_chars} characters.",
        "",
        "| Page | HTML bytes | Visible chars | Root | Main chars | of which links | Main / visible | Script chars "
        "| Flag | Hints |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for y in yields:
        if y.error:
            out.append(f"| `{_cell(y.page)}` | - | - | - | - | - | - | - | not read | {_cell(y.error)} |")
            continue
        flag = "**may need JavaScript**" if y.flagged else "ok"
        out.append(
            f"| `{_cell(Path(y.page).name if not _is_url(y.page) else y.page)}` | {y.html_bytes:,} | "
            f"{y.visible_chars:,} | {y.root} | {y.main_chars:,} | {y.main_link_chars:,} | {_pct(y.main_share)} | "
            f"{y.script_chars:,} | {flag} | {_cell('; '.join(y.hints)) or '-'} |"
        )
    read = len(yields) - len(failed)
    verdict = (
        f"**{len(flagged)} of {read} pages flagged**: report them to the main session (renderer decision, "
        f"connectors §8.1)."
        if flagged
        else f"**No page flagged** ({read} read): the raw HTML carries the main text; no renderer needed for these."
    )
    out += ["", verdict, ""]
    return out


def _render_requests(fetcher: Fetcher) -> list[str]:
    out = ["## robots.txt", "", "| Origin | Status | Verdict | Group | Rules | Crawl-delay | Sitemap lines |"]
    out.append("|---|---|---|---|---|---|---|")
    for r in fetcher.robots_results:
        sitemaps = ", ".join(f"`{u}`" for u in r.sitemaps) or "-"
        out.append(
            f"| {r.origin} | {r.status or 'unreachable'} | {r.verdict} | {r.group} | {r.rules} | "
            f"{r.crawl_delay if r.crawl_delay is not None else '-'} | {sitemaps} |"
        )
    out += ["", "## Request log", "", "| Start (UTC) | Purpose | URL | Status | Bytes | ms | Note |", "|---" * 7 + "|"]
    for e in fetcher.log:
        out.append(
            f"| {e['time']} | {e['purpose']} | `{_cell(e['url'])}` | {e['status'] or '-'} | {e['bytes']:,} | "
            f"{e['ms']:,} | {_cell(e['error'] or '')} |"
        )
    out.append("")
    return out


# --------------------------------------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------------------------------------


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="inventory.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    commands = parser.add_subparsers(dest="command", required=True)
    sitemap = commands.add_parser("sitemap", help="inventory a sitemap index: formats, Z1 share, lastmod, hosts")
    sitemap.add_argument("source", help="sitemap or index URL, a robots.txt URL, or a local file")
    sitemap.add_argument("--pages", nargs="+", default=[], help="also run the JavaScript-need check on these")
    sitemap.add_argument("--max-sitemaps", type=int, default=DEFAULT_MAX_SITEMAPS, help="sitemap files read, at most")
    sitemap.add_argument("--no-save", action="store_true", help="do not save fetched sitemap files")
    sitemap.add_argument("--now", help="reference time for ages (ISO 8601; default: now)")
    js_need = commands.add_parser("js-need", help="raw text yield per page; flags pages that may need JavaScript")
    js_need.add_argument("pages", nargs="+", help="local HTML files or http(s) URLs")
    for command in (sitemap, js_need):
        command.add_argument("--min-main-chars", type=int, default=DEFAULT_MIN_MAIN_CHARS)
        command.add_argument("--delay", type=float, default=DEFAULT_DELAY_S, help="seconds between requests, ≥ 2")
        command.add_argument("--out-dir", type=Path, default=DEFAULT_OUT)
        command.add_argument("--ca-file", help="CA bundle for https behind a private CA")
    args = parser.parse_args(argv)
    if args.delay < DEFAULT_DELAY_S:
        parser.error(f"--delay must be at least {DEFAULT_DELAY_S:g} s")
    return args


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    started = datetime.now(UTC)
    stamp = started.strftime("%Y%m%dT%H%M%SZ")
    pages = args.pages
    needs_network = any(_is_url(p) for p in pages) or (args.command == "sitemap" and _is_url(args.source))
    fetcher = Fetcher(args.delay, ca_file=args.ca_file) if needs_network else None
    inventory = summary = None
    if args.command == "sitemap":
        if not _is_url(args.source) and not Path(args.source).is_file():
            print(f"no such file: {args.source}", file=sys.stderr)
            return 2
        now = parse_lastmod(args.now) if args.now else started
        if now is None:
            print(f"--now is not an ISO 8601 time: {args.now}", file=sys.stderr)
            return 2
        save = None if args.no_save or not _is_url(args.source) else args.out_dir / f"{stamp}-sitemaps"
        inventory = walk(args.source, fetcher, now, args.max_sitemaps, save)
        summary = summarise(inventory)
    yields = read_pages(pages, fetcher, args.min_main_chars) if pages else None

    report = render_report(args.command, started, inventory, summary, yields, fetcher, args.min_main_chars, args.delay)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    base = args.out_dir / f"{stamp}-{args.command}"
    base.with_suffix(".md").write_text(report, encoding="utf-8")
    result = {
        "command": args.command,
        "started": started.isoformat(),
        "user_agent": USER_AGENT if fetcher else None,
        "inventory": {"source": inventory.source, "files": [asdict(f) for f in inventory.files]} if inventory else None,
        "summary": summary,
        "pages": [asdict(y) for y in yields] if yields is not None else None,
        "robots": [asdict(r) for r in fetcher.robots_results] if fetcher else [],
        "requests": fetcher.log if fetcher else [],
    }
    base.with_suffix(".json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(report)
    print(f"wrote {base.with_suffix('.md')} and .json", file=sys.stderr)

    if inventory is not None and (not inventory.files or all(f.kind == "-" for f in inventory.files)):
        return 2
    return 1 if yields and any(y.flagged for y in yields) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
