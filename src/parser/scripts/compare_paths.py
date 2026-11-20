"""Text-layer fast path against Docling, page by page (weakness review "Lower-ranked"; to-verify #132).

For each PDF of a URL list it asks a running parser for `/v1/inspect`, sorts every page into the worker's strategies
(processing design §3.1, rules 1 to 6), and converts the `text-layer` pages twice: through the fast path
(`pipeline: text_layer`) and through Docling's layout pipeline (OCR off, tables `accurate`). Per page it compares the
body text (similarity of the whitespace-normalised text, which also drops when the reading order differs, as on
two-column pages), the heading counts and whether Docling finds a table the fast path cannot have. The Markdown report
gives the share of pages per strategy, the agreement on fast-path pages, both paths' throughput per slot and the
pages that disagree most, as numbers and URLs only (no document text), for setting the routing threshold.

The list file has one URL per line (`#` starts a comment); a local path or `file://` URL is read from disk, which is
how the tests run it on the fixture PDFs. Downloads are cached by URL hash, capped at 50 MB and identified by a
`User-Agent` naming the platform.

    uv run python -m scripts.compare_paths --urls umea-pdfs.txt --url http://localhost:8000 --out compare.md
"""

import argparse
import difflib
import hashlib
import statistics
import sys
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from scripts.parser_http import HttpParser, Parser, ParserHttpError

MAX_BYTES = 50 * 1024 * 1024
RANGE_PAGES = 20
USER_AGENT = "kalla-compare-paths/1 (Umeå kommun RAG platform; parser evaluation)"
AGREE = 0.98
DISAGREE = 0.90


def strategy(page: dict[str, Any]) -> str:
    """The worker's per-page strategy from inspect signals (processing design §3.1; first matching rule wins)."""
    chars, coverage, paths = page["text_chars"], page["image_coverage"], page["path_objects"]
    garble, bad = page["garbled_score"], page["bad_chars"]
    if chars < 50 and coverage < 0.1 and paths < 200:
        return "blank"
    if chars < 50 and paths >= 200:
        return "docling-ocr-force"
    if garble >= 0.4 or (chars and bad / chars >= 0.1):
        return "docling-ocr-force"
    clean = (chars >= 200 or (chars >= 50 and coverage < 0.3)) and garble < 0.2
    if clean and paths >= 40:
        return "docling-tables"
    return "text-layer" if clean else "docling-ocr"


def runs(pages: list[int], size: int = RANGE_PAGES) -> list[tuple[int, int]]:
    """Consecutive page numbers as ranges of at most `size` pages."""
    out: list[tuple[int, int]] = []
    for page in pages:
        if out and page == out[-1][1] + 1 and page - out[-1][0] < size:
            out[-1] = (out[-1][0], page)
        else:
            out.append((page, page))
    return out


def page_texts(result: dict[str, Any]) -> dict[int, str]:
    texts: dict[int, list[str]] = {}
    for block in result["blocks"]:
        if block["layer"] == "body" and block["page"] is not None and block["kind"] != "table":
            texts.setdefault(block["page"], []).append(block["md"].lstrip("#").strip())
    return {page: " ".join(" ".join(parts).split()) for page, parts in texts.items()}


def count_by_page(result: dict[str, Any], kind: str) -> dict[int, int]:
    counts: dict[int, int] = {}
    for block in result["blocks"]:
        if block["kind"] == kind and block["page"] is not None:
            counts[block["page"]] = counts.get(block["page"], 0) + 1
    return counts


@dataclass
class PageComparison:
    document: int
    page: int
    similarity: float
    fast_headings: int
    docling_headings: int
    docling_tables: int


@dataclass
class Results:
    documents: int = 0
    failures: dict[str, int] = field(default_factory=dict)
    strategies: dict[str, int] = field(default_factory=dict)
    compared: list[PageComparison] = field(default_factory=list)
    fast_pages: int = 0
    fast_s: float = 0.0
    docling_pages: int = 0
    docling_s: float = 0.0
    inspect_pages: int = 0
    inspect_s: float = 0.0

    def fail(self, reason: str) -> None:
        self.failures[reason] = self.failures.get(reason, 0) + 1


def fetch(url: str, cache: Path) -> bytes:
    """The document at `url`: a local path or `file://` URL from disk, else downloaded once into `cache`."""
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme in ("", "file"):
        return Path(urllib.parse.unquote(parsed.path)).read_bytes()
    if parsed.scheme not in ("http", "https"):
        raise ValueError("unsupported URL scheme")
    target = cache / hashlib.sha256(url.encode()).hexdigest()
    if target.is_file():
        return target.read_bytes()
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})  # noqa: S310 (http(s) checked above)
    with urllib.request.urlopen(request, timeout=60) as response:  # noqa: S310
        data = response.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("larger than 50 MB")
    cache.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    return data


def _timed(call, *args) -> tuple[dict[str, Any], float]:
    started = time.monotonic()
    result = call(*args)
    return result, time.monotonic() - started


def compare_document(parser: Parser, index: int, data: bytes, results: Results, max_pages: int) -> None:
    inspected, seconds = _timed(parser.inspect, data, {"request_id": f"cmp-{index}-i", "file_ext": ".pdf"})
    if inspected["format"] != "pdf":
        results.fail("not_pdf")
        return
    if inspected["refusal"]:
        results.fail(inspected["refusal"])
        return
    pages = inspected["pages"][:max_pages]
    results.documents += 1
    results.inspect_pages += len(inspected["pages"])
    results.inspect_s += seconds
    fast_pages = []
    for page in pages:
        name = strategy(page)
        results.strategies[name] = results.strategies.get(name, 0) + 1
        if name == "text-layer":
            fast_pages.append(page["page"])

    for first, last in runs(fast_pages):
        base = {"file_ext": ".pdf", "pages": {"from": first, "to": last}, "outputs": {"docling_json": False}}
        fast, fast_s = _timed(
            parser.convert, data, {**base, "request_id": f"cmp-{index}-{first}-t", "pipeline": "text_layer"}
        )
        docling, docling_s = _timed(
            parser.convert, data, {**base, "request_id": f"cmp-{index}-{first}-d", "ocr": {"mode": "off"}}
        )
        count = last - first + 1
        results.fast_pages += count
        results.fast_s += fast_s
        results.docling_pages += count
        results.docling_s += docling_s
        fast_text, docling_text = page_texts(fast), page_texts(docling)
        fast_heads, docling_heads = count_by_page(fast, "heading"), count_by_page(docling, "heading")
        docling_tables = count_by_page(docling, "table")
        for page in range(first, last + 1):
            a, b = fast_text.get(page, ""), docling_text.get(page, "")
            similarity = difflib.SequenceMatcher(None, a, b, autojunk=False).ratio() if a or b else 1.0
            results.compared.append(
                PageComparison(
                    index,
                    page,
                    round(similarity, 3),
                    fast_heads.get(page, 0),
                    docling_heads.get(page, 0),
                    docling_tables.get(page, 0),
                )
            )


def run(parser: Parser, urls: list[str], cache: Path, max_pages: int) -> Results:
    results = Results()
    for index, url in enumerate(urls, start=1):
        try:
            data = fetch(url, cache)
        except (OSError, ValueError):
            results.fail("download")
            continue
        try:
            compare_document(parser, index, data, results, max_pages)
        except ParserHttpError as exc:
            results.fail(exc.code or f"http_{exc.status}")
    return results


def _share(part: int, whole: int) -> str:
    return f"{part / whole:.1%}" if whole else "-"


def _rate(pages: int, seconds: float) -> str:
    return f"{pages / seconds:.1f}" if seconds else "-"


def report(results: Results, urls: list[str], title: str) -> str:
    pages = sum(results.strategies.values())
    compared = results.compared
    similarities = sorted(c.similarity for c in compared)
    lines = [
        f"# Text-layer fast path vs Docling: {title}",
        "",
        f"{len(urls)} URLs, {results.documents} PDFs compared, {pages} pages inspected.",
        "",
        "## Pages per strategy (processing design §3.1)",
        "",
        "| Strategy | Pages | Share |",
        "|---|---|---|",
    ]
    lines += [f"| `{name}` | {count} | {_share(count, pages)} |" for name, count in sorted(results.strategies.items())]
    lines += ["", "## Fast path against Docling on `text-layer` pages", ""]
    if similarities:
        tables = sum(1 for c in compared if c.docling_tables)
        heads = sum(1 for c in compared if c.fast_headings == c.docling_headings)
        lines += [
            "| Pages | Similarity min | p10 | p50 | ≥ 0.98 | < 0.90 | Docling finds a table | Same heading count |",
            "|---|---|---|---|---|---|---|---|",
            f"| {len(compared)} | {similarities[0]:.3f} | {similarities[int(0.1 * (len(similarities) - 1))]:.3f} "
            f"| {statistics.median(similarities):.3f} | {_share(sum(s >= AGREE for s in similarities), len(compared))} "
            f"| {_share(sum(s < DISAGREE for s in similarities), len(compared))} | {_share(tables, len(compared))} "
            f"| {_share(heads, len(compared))} |",
        ]
    else:
        lines.append("No `text-layer` pages.")
    lines += [
        "",
        "## Throughput (one slot)",
        "",
        "| Path | Pages | Pages/s |",
        "|---|---|---|",
        f"| inspect | {results.inspect_pages} | {_rate(results.inspect_pages, results.inspect_s)} |",
        f"| text-layer fast path | {results.fast_pages} | {_rate(results.fast_pages, results.fast_s)} |",
        f"| Docling, OCR off, tables accurate | {results.docling_pages} "
        f"| {_rate(results.docling_pages, results.docling_s)} |",
    ]
    worst = sorted(compared, key=lambda c: c.similarity)[:20]
    if worst:
        lines += [
            "",
            "## Pages that disagree most",
            "",
            "| URL | Page | Similarity | Headings fast/Docling | Docling tables |",
        ]
        lines.append("|---|---|---|---|---|")
        lines += [
            f"| {urls[c.document - 1]} | {c.page} | {c.similarity:.3f} | {c.fast_headings}/{c.docling_headings} "
            f"| {c.docling_tables} |"
            for c in worst
        ]
    if results.failures:
        lines += ["", "## Not compared", "", "| Reason | Documents |", "|---|---|"]
        lines += [f"| {reason} | {count} |" for reason, count in sorted(results.failures.items())]
    return "\n".join(lines) + "\n"


def read_urls(path: Path) -> list[str]:
    lines = (line.split("#", 1)[0].strip() for line in path.read_text(encoding="utf-8").splitlines())
    return [line for line in lines if line]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--urls", type=Path, required=True, help="one PDF URL per line")
    parser.add_argument("--url", default="http://localhost:8000", help="the parser")
    parser.add_argument("--cache", type=Path, default=Path(".compare-cache"), help="downloaded PDFs, by URL hash")
    parser.add_argument("--max-pages", type=int, default=200, help="pages compared per document")
    parser.add_argument("--title", default="sample")
    parser.add_argument("--out", type=Path, help="Markdown report (default: stdout)")
    args = parser.parse_args()
    urls = read_urls(args.urls)
    text = report(run(HttpParser(args.url), urls, args.cache, args.max_pages), urls, args.title)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
