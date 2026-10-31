"""Tests for inventory.py on the local fixture sitemaps in tests/fixtures/inventory/ (an index, a nested index, an index
past the depth limit, a gzip sitemap, image and video extensions), on the two synthetic pages (one server-rendered,
one empty JavaScript shell), on robots.txt rules and on the polite fetcher against an in-process site. Standard
library only:

    python3 -I -m unittest discover -s scripts/inventory -v
"""

import gzip
import io
import json
import sys
import tempfile
import threading
import unittest
from contextlib import redirect_stderr, redirect_stdout
from datetime import UTC, datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import ClassVar

sys.path.insert(0, str(Path(__file__).resolve().parent))
import inventory

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures" / "inventory"
PAGES = FIXTURES / "pages"
NOW = datetime(2026, 10, 31, 12, 0, tzinfo=UTC)


def urlset(*locs: str) -> bytes:
    body = "".join(f"<url><loc>{loc}</loc></url>" for loc in locs)
    return f'<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{body}</urlset>'.encode()


def run_main(argv: list[str]) -> int:
    with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
        return inventory.main(argv)


class ClassifyTests(unittest.TestCase):
    def test_classes_by_extension_of_the_last_segment(self):
        cases = {
            "https://www.umea.se/a/digitalomstallningochit.4.88db09b1724ee319ca45d.html": "html",
            "https://www.umea.se/kontakt/": "html",
            "https://www.umea.se/kontakt": "html",
            "https://www.umea.se/download/18.1/2/Taxa.PDF": "pdf",
            "https://www.umea.se/x.pdf?properties=1": "pdf",
            "https://www.umea.se/x.docx": "office",
            "https://www.umea.se/x.odp": "odf",
            "https://www.umea.se/x.csv": "text",
            "https://www.umea.se/images/18.1/bild.JPEG": "image",
            "https://www.umea.se/x.webm": "video",
            "https://www.umea.se/x.doc": "deferred",
            "https://www.umea.se/x.xls": "deferred",
            "https://www.umea.se/x.ppt": "deferred",
            "https://www.umea.se/x.rtf": "deferred",
            "https://www.umea.se/x.eml": "deferred",
            "https://www.umea.se/x.msg": "deferred",
            "https://www.umea.se/x.pptm": "macro",
            "https://www.umea.se/x.7z": "archive",
            "https://www.umea.se/x.mp3": "other",
            "https://www.umea.se/x.pdf/": "html",
            "https://www.umea.se/images/18.1e902c49184793096754ad6/1669975972529/Bild%2015": "other",
            "https://www.umea.se/download/18.6/1583228180954/saml-keystore": "other",
            "https://www.umea.se/images/18.1/2/foto.jfif": "image",
            "https://www.umea.se/images/": "html",
        }
        for url, expected in cases.items():
            with self.subTest(url=url):
                self.assertEqual(expected, inventory.classify(url))

    def test_lastmod_formats(self):
        self.assertEqual(datetime(2026, 1, 1, tzinfo=UTC), inventory.parse_lastmod("2026"))
        self.assertEqual(datetime(2026, 3, 1, tzinfo=UTC), inventory.parse_lastmod("2026-03"))
        self.assertEqual(datetime(2026, 3, 4, tzinfo=UTC), inventory.parse_lastmod("2026-03-04"))
        self.assertEqual(
            datetime(2026, 1, 15, 8, 24, 54, tzinfo=UTC), inventory.parse_lastmod("2026-01-15T09:24:54+01:00")
        )
        self.assertEqual(datetime(2026, 1, 15, 8, 0, tzinfo=UTC), inventory.parse_lastmod("2026-01-15T08:00:00Z"))
        self.assertIsNone(inventory.parse_lastmod("not a date"))
        self.assertIsNone(inventory.parse_lastmod(None))


class SitemapParseTests(unittest.TestCase):
    def test_gzip_is_detected_by_magic_bytes_and_reads_like_the_plain_file(self):
        data = (FIXTURES / "sitemap1.xml.gz").read_bytes()
        parsed = inventory.parse_sitemap(data)
        plain = inventory.parse_sitemap(gzip.decompress(data))
        self.assertTrue(parsed.compressed)
        self.assertFalse(plain.compressed)
        self.assertIsNone(parsed.error)
        self.assertEqual(14, len(parsed.entries))
        self.assertEqual(plain.entries, parsed.entries)
        self.assertEqual(("https://www.example.se/download/18.7/arende.msg", None), parsed.entries[7])

    def test_extension_locs_are_counted_not_taken_as_the_entry(self):
        parsed = inventory.parse_sitemap((FIXTURES / "sitemap2.xml").read_bytes())
        self.assertEqual(
            [
                ("https://www.example.se/nyheter/nyhet.html", "2026-10-01"),
                ("https://www.example.se/uppleva/film.html", "2026-10-01"),
            ],
            parsed.entries,
        )
        self.assertEqual((2, 1, 5), (parsed.images, parsed.videos, parsed.nested_locs))

    def test_a_dtd_is_refused_without_expanding_entities(self):
        xml = b'<?xml version="1.0"?><!DOCTYPE urlset [<!ENTITY a "aaaa">]><urlset><url><loc>&a;</loc></url></urlset>'
        parsed = inventory.parse_sitemap(xml)
        self.assertEqual(inventory.REFUSED_DTD, parsed.error)
        self.assertEqual([], parsed.entries)

    def test_broken_xml_keeps_the_entries_read_before_the_error(self):
        parsed = inventory.parse_sitemap(urlset("https://a.se/1.html")[: -len("</urlset>")] + b"<oops")
        self.assertEqual(inventory.REFUSED_SYNTAX, parsed.error)
        self.assertEqual([("https://a.se/1.html", None)], parsed.entries)
        self.assertEqual(inventory.REFUSED_SYNTAX, inventory.parse_sitemap(b"\x1f\x8b not gzip").error)

    def test_at_most_50000_entries_per_file(self):
        parsed = inventory.parse_sitemap(urlset(*(f"https://a.se/{i}.html" for i in range(50_001))))
        self.assertTrue(parsed.truncated)
        self.assertEqual(50_000, len(parsed.entries))

    def test_at_most_50_mb_uncompressed(self):
        bomb = gzip.compress(b"<urlset>" + b" " * inventory.MAX_UNCOMPRESSED + b"</urlset>", mtime=0)
        self.assertLess(len(bomb), 100_000)
        self.assertEqual(inventory.REFUSED_SIZE, inventory.parse_sitemap(bomb).error)


class InventoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inventory = inventory.walk(str(FIXTURES / "sitemapindex.xml"), None, NOW)
        cls.summary = inventory.summarise(cls.inventory)

    def test_walks_the_index_to_depth_2_and_reports_each_file(self):
        files = {Path(f.source).name: f for f in self.inventory.files}
        self.assertEqual(
            ["sitemapindex.xml", "sitemap1.xml.gz", "sitemap2.xml", "nested-index.xml", "missing.xml", "sitemap3.xml",
             "deep-index.xml"],
            [Path(f.source).name for f in self.inventory.files],
        )  # fmt: skip
        index, gz = files["sitemapindex.xml"], files["sitemap1.xml.gz"]
        self.assertEqual(("index", 0, 4), (index.kind, index.depth, index.entries))
        self.assertEqual(("urlset", 1, 14, True), (gz.kind, gz.depth, gz.entries, gz.compressed))
        self.assertEqual(inventory.REFUSED_MISSING, files["missing.xml"].error)
        self.assertEqual(2, files["sitemap3.xml"].depth)
        self.assertEqual(inventory.REFUSED_DEPTH, files["deep-index.xml"].error)
        self.assertNotIn("https://www.example.se/should-not-be-counted.html", self.inventory.urls)

    def test_counts_and_shares_per_format(self):
        s = self.summary
        self.assertEqual(17, s["total"])
        self.assertEqual(1, s["duplicates"])
        counts = {f["class"]: f["count"] for f in s["formats"]}
        expected = {"html": 4, "pdf": 2, "office": 1, "odf": 1, "text": 1, "image": 1, "video": 1, "deferred": 3}
        expected |= {"macro": 1, "archive": 1, "other": 1}
        self.assertEqual(expected, counts)
        self.assertEqual(sum(counts.values()), s["total"])
        self.assertEqual(round(4 / 17, 4), next(f["share"] for f in s["formats"] if f["class"] == "html"))

    def test_z1_shares(self):
        z1 = self.summary["z1"]
        self.assertEqual(3, z1["deferred"])
        self.assertEqual(10, z1["documents"])
        self.assertEqual(0.3, z1["deferred_share_of_documents"])
        self.assertEqual(round(3 / 17, 4), z1["deferred_share_of_all"])
        self.assertEqual((5, 0.5), (z1["all_z1"], z1["all_z1_share_of_documents"]))

    def test_lastmod_coverage_and_ages(self):
        lm = self.summary["lastmod"]
        self.assertEqual((15, 1, 1), (lm["valid"], lm["invalid"], lm["missing"]))
        self.assertEqual(round(15 / 17, 4), lm["coverage"])
        self.assertEqual({"≤ 30 d": 1, "future": 1, "31-90 d": 2}, lm["ages"]["html"])
        self.assertEqual({"91-365 d": 1, "2-5 y": 1}, lm["ages"]["pdf"])
        self.assertEqual({"> 5 y": 1, "invalid": 1, "no lastmod": 1}, lm["ages"]["deferred"])
        self.assertEqual({"1-2 y": 1}, lm["ages"]["archive"])
        self.assertEqual("2019-05-05T00:00:00+00:00", lm["oldest"])
        self.assertEqual("2026-12-24T00:00:00+00:00", lm["newest"])

    def test_hosts_are_lower_case(self):
        self.assertEqual([("www.example.se", 16), ("sok.example.se", 1)], self.summary["hosts"])

    def test_the_report_names_formats_z1_and_lastmod(self):
        report = inventory.render_report("sitemap", NOW, self.inventory, self.summary, None, None, 200, 2.0)
        self.assertIn("**17 distinct URLs** (1 duplicate entries not counted).", report)
        self.assertIn("| Legacy Office, RTF, e-mail (Z1 deferred) | 3 | 17.6 % |", report)
        self.assertIn("**30.0 %** of the 10 non-HTML documents", report)
        self.assertIn("- Coverage: **88.2 %** (15 valid, 1 invalid, 1 missing)", report)
        self.assertIn("| www.example.se | 16 |", report)


class TextYieldTests(unittest.TestCase):
    def test_a_server_rendered_page_carries_its_main_text(self):
        y = inventory.text_yield("server-rendered.html", (PAGES / "server-rendered.html").read_bytes())
        self.assertFalse(y.flagged)
        self.assertEqual("main", y.root)
        self.assertGreater(y.main_chars, 300)
        self.assertLess(y.main_chars, y.visible_chars)  # header and footer are outside main
        self.assertEqual(len("avgifter för bygglov"), y.main_link_chars)
        self.assertEqual([], y.hints)

    def test_hidden_screen_reader_nav_and_controls_are_not_text(self):
        y = inventory.text_yield("server-rendered.html", (PAGES / "server-rendered.html").read_bytes())
        parser = inventory._TextParser()
        parser.feed(inventory.decode_html((PAGES / "server-rendered.html").read_bytes()))
        text = " ".join(" ".join(parser.visible).split())
        for absent in (
            "kakor",
            "Länken öppnas",
            "Huvudmeny",
            "Omsorg och stöd",
            "Dela",
            "dataLayer",
            "Bygglov för altan -",
        ):
            self.assertNotIn(absent, text)
        self.assertIn("Telefon 090-16 10 00", text)
        self.assertEqual(len(text), y.visible_chars)

    def test_an_empty_shell_is_flagged_with_its_hints(self):
        y = inventory.text_yield("empty-shell.html", (PAGES / "empty-shell.html").read_bytes())
        self.assertTrue(y.flagged)
        self.assertEqual(("body", 0, 0), (y.root, y.main_chars, y.visible_chars))
        self.assertGreater(y.script_chars, 100)
        self.assertEqual(["empty mount point #app", "noscript text asks for JavaScript", "Nuxt marker"], y.hints)

    def test_root_order_is_main_then_article_then_role_main(self):
        page = b"<body><div role=main>role text</div><article>article text</article><p>outside</p></body>"
        y = inventory.text_yield("p", page, min_main_chars=5)
        self.assertEqual(("article", len("article text")), (y.root, y.main_chars))
        y = inventory.text_yield("p", b"<div role='main'><p>only role</p></div><main>main</main>", 5)
        self.assertEqual(("main", 4, True), (y.root, y.main_chars, y.flagged))

    def test_unclosed_elements_end_with_their_parent(self):
        page = b"<main><p>first<ul><li hidden>gone<li>kept</ul><p>last</main><footer>foot</footer>"
        y = inventory.text_yield("p", page, 1)
        self.assertEqual(len("first kept last"), y.main_chars)
        self.assertEqual(len("first kept last foot"), y.visible_chars)

    def test_charset_from_meta(self):
        page = '<meta charset="iso-8859-1"><main>Umeå kommun</main>'.encode("iso-8859-1")
        self.assertEqual(len("Umeå kommun"), inventory.text_yield("p", page, 1).main_chars)

    def test_the_umea_fixture_pages_are_not_flagged(self):
        for page in sorted((ROOT / "tests" / "fixtures" / "umea" / "pages").glob("*.html")):
            with self.subTest(page=page.name):
                y = inventory.text_yield(page.name, page.read_bytes())
                self.assertFalse(y.flagged, f"{page.name}: {y.main_chars} chars in {y.root}")


class RobotsTests(unittest.TestCase):
    def test_our_group_wins_over_star_and_longest_match_wins(self):
        robots = inventory.Robots.parse(
            b"User-agent: *\nDisallow: /\n\nUser-agent: Other\nUser-agent: UmeaKommunBot/1.0\n"
            b"Disallow: /private\nAllow: /private/open\nDisallow: /*.html.printable$\nDisallow: /*?sv.state=\n"
            b"Crawl-delay: 5\nSitemap: https://a.se/sitemapindex.xml\n"
        )
        self.assertEqual("UmeaKommunBot", robots.group)
        self.assertEqual(5.0, robots.crawl_delay)
        self.assertEqual(["https://a.se/sitemapindex.xml"], robots.sitemaps)
        self.assertTrue(robots.allowed("https://a.se/sitemapindex.xml"))
        self.assertFalse(robots.allowed("https://a.se/private/x"))
        self.assertTrue(robots.allowed("https://a.se/private/open/x"))
        self.assertFalse(robots.allowed("https://a.se/a/b.html.printable"))
        self.assertTrue(robots.allowed("https://a.se/a/b.html.printable/more"))
        self.assertFalse(robots.allowed("https://a.se/sida?sv.state=1"))
        self.assertTrue(robots.allowed("https://a.se/robots.txt"))

    def test_star_group_ties_go_to_allow_and_paths_compare_percent_normalised(self):
        robots = inventory.Robots.parse("User-agent: *\nDisallow: /ö\nAllow: /%C3%B6\nDisallow: /tmp\n".encode())
        self.assertEqual("*", robots.group)
        self.assertTrue(robots.allowed("https://a.se/%c3%b6/x"))
        self.assertFalse(robots.allowed("https://a.se/tmp/x"))
        self.assertTrue(inventory.Robots.parse(b"User-agent: Other\nDisallow: /\n").allowed("https://a.se/x"))


class FakeSite(BaseHTTPRequestHandler):
    files: ClassVar[dict[str, tuple[int, bytes]]] = {}
    seen: ClassVar[list[tuple[str, str]]] = []

    def do_GET(self):
        FakeSite.seen.append((self.path, self.headers.get("User-Agent", "")))
        status, body = FakeSite.files.get(self.path, (404, b"not found"))
        self.send_response(status)
        if status in (301, 302):
            self.send_header("Location", body.decode())
            body = b""
        self.send_header("Content-Type", "text/html" if self.path.endswith(".html") else "application/xml")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


class FetcherTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeSite)
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        FakeSite.seen.clear()
        self.waits: list[float] = []

    def fetcher(self) -> inventory.Fetcher:
        return inventory.Fetcher(delay=2.0, timeout=5, sleep=self.waits.append)

    def serve(self, files: dict[str, tuple[int, bytes]]) -> None:
        FakeSite.files = files

    def test_robots_first_then_index_and_sitemaps_politely_and_never_a_disallowed_file(self):
        index = (
            '<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'
            f"<sitemap><loc>{self.base}/s1.xml.gz</loc></sitemap><sitemap><loc>{self.base}/old</loc></sitemap>"
            f"<sitemap><loc>{self.base}/private/s2.xml</loc></sitemap></sitemapindex>"
        ).encode()
        self.serve(
            {
                "/robots.txt": (200, f"User-agent: *\nDisallow: /private\nSitemap: {self.base}/index.xml\n".encode()),
                "/index.xml": (200, index),
                "/s1.xml.gz": (200, gzip.compress(urlset(f"{self.base}/a.html", f"{self.base}/b.pdf"), mtime=0)),
                "/old": (301, f"{self.base}/s3.xml".encode()),
                "/s3.xml": (200, urlset(f"{self.base}/c.doc")),
                "/private/s2.xml": (200, urlset(f"{self.base}/secret.html")),
            }
        )
        fetcher = self.fetcher()
        with tempfile.TemporaryDirectory() as tmp:
            result = inventory.walk(f"{self.base}/robots.txt", fetcher, NOW, save_dir=Path(tmp))
            saved = sorted(p.name for p in Path(tmp).iterdir())
        self.assertEqual(["/robots.txt", "/index.xml", "/s1.xml.gz", "/old", "/s3.xml"], [p for p, _ in FakeSite.seen])
        self.assertTrue(all(ua == inventory.USER_AGENT for _, ua in FakeSite.seen))
        self.assertEqual(len(FakeSite.seen) - 1, len(self.waits))
        self.assertTrue(all(w > 1.9 for w in self.waits), self.waits)
        self.assertEqual([f"{self.base}/index.xml"], result.robots_sitemaps)
        self.assertEqual({f"{self.base}/a.html", f"{self.base}/b.pdf", f"{self.base}/c.doc"}, set(result.urls))
        refused = next(f for f in result.files if f.source.endswith("/private/s2.xml"))
        self.assertEqual(inventory.REFUSED_ROBOTS, refused.error)
        self.assertEqual(["index.xml", "old", "s1.xml.gz"], saved)
        self.assertEqual("parsed", fetcher.robots_results[0].verdict)
        self.assertEqual(f"{inventory.REFUSED_ROBOTS} (not fetched)", fetcher.log[-1]["error"])

    def test_a_larger_crawl_delay_is_honoured_up_to_60_s(self):
        self.serve({"/robots.txt": (200, b"User-agent: *\nCrawl-delay: 90\n"), "/s.xml": (200, urlset())})
        inventory.walk(f"{self.base}/s.xml", self.fetcher(), NOW)
        self.assertEqual(1, len(self.waits))
        self.assertGreater(self.waits[0], 59)
        self.assertLessEqual(self.waits[0], 60)

    def test_robots_4xx_allows_all_and_5xx_disallows_all(self):
        self.serve({"/s.xml": (200, urlset(f"{self.base}/a.html"))})
        allowed = inventory.walk(f"{self.base}/s.xml", self.fetcher(), NOW)
        self.assertEqual(1, len(allowed.urls))
        self.serve({"/robots.txt": (503, b"down"), "/s.xml": (200, urlset(f"{self.base}/a.html"))})
        FakeSite.seen.clear()
        fetcher = self.fetcher()
        refused = inventory.walk(f"{self.base}/s.xml", fetcher, NOW)
        self.assertEqual(["/robots.txt"], [p for p, _ in FakeSite.seen])
        self.assertEqual(
            ("disallow-all", inventory.REFUSED_ROBOTS), (fetcher.robots_results[0].verdict, refused.files[0].error)
        )

    def test_page_urls_are_read_for_the_yield_check(self):
        self.serve({"/p.html": (200, (PAGES / "server-rendered.html").read_bytes()), "/x.xml": (200, urlset())})
        results = inventory.read_pages([f"{self.base}/p.html", f"{self.base}/x.xml"], self.fetcher(), 200)
        self.assertFalse(results[0].flagged)
        self.assertTrue(results[1].error.startswith("not HTML"))


class CliTests(unittest.TestCase):
    def test_sitemap_with_pages_writes_the_report_and_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            code = run_main(
                ["sitemap", str(FIXTURES / "sitemapindex.xml"), "--now", "2026-10-31T12:00:00Z", "--out-dir", tmp,
                 "--pages", str(PAGES / "server-rendered.html")]
            )  # fmt: skip
            reports = list(Path(tmp).glob("*-sitemap.md"))
            data = json.loads(next(Path(tmp).glob("*-sitemap.json")).read_text(encoding="utf-8"))
            report = reports[0].read_text(encoding="utf-8")
        self.assertEqual(0, code)
        self.assertEqual(17, data["summary"]["total"])
        self.assertIn("**No page flagged** (1 read)", report)
        self.assertNotIn("Request log", report)

    def test_js_need_exits_1_when_a_page_is_flagged(self):
        with tempfile.TemporaryDirectory() as tmp:
            pages = [str(PAGES / "server-rendered.html"), str(PAGES / "empty-shell.html")]
            self.assertEqual(1, run_main(["js-need", *pages, "--out-dir", tmp]))
            report = next(Path(tmp).glob("*-js-need.md")).read_text(encoding="utf-8")
        self.assertIn("**1 of 2 pages flagged**", report)
        self.assertIn("| `empty-shell.html` |", report)

    def test_a_missing_source_exits_2_and_the_delay_is_at_least_2_s(self):
        self.assertEqual(2, run_main(["sitemap", str(FIXTURES / "nope.xml"), "--out-dir", tempfile.gettempdir()]))
        with self.assertRaises(SystemExit):
            run_main(["js-need", "x.html", "--delay", "1"])


if __name__ == "__main__":
    unittest.main()
