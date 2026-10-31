"""The static page copy R1 crawls, and the OCR batch (repo-and-deployment §8.3), served by nginx on the runner.

Usage: python3 -I build_site.py generate --out DIR [--pages 2000] [--pdf-every 10] [--ocr 50] [--base-url URL]
       python3 -I build_site.py index --out DIR --base-url URL

generate: a PLACEHOLDER until the real copy of 2 000 umea.se pages exists (live item, take-copy.sh). Swedish pages of
about 600 words (processing design §16's typical page) in a link graph the crawler must traverse (a start page, section
pages, each page linking on), every --pdf-every-th page linking a PDF made from tests/fixtures/parser/text-layer.pdf,
plus robots.txt and sitemap.xml. ocr-batch/ holds --ocr scanned PDFs from tests/fixtures/parser/scanned.pdf for the
upload. Each PDF copy gets a unique comment after %%EOF, so content hashes differ and nothing is deduplicated away.
Nothing is fetched from the network.

index: robots.txt and sitemap.xml for an existing copy (take-copy.sh), every .html and .pdf below DIR except ocr-batch/.
"""

import argparse
import random
import sys
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[3]
FIXTURES = ROOT / "tests" / "fixtures" / "parser"
SECTIONS = [
    "bygga-bo-och-miljo",
    "forskola-och-skola",
    "omsorg-och-hjalp",
    "kultur-och-fritid",
    "trafik-och-resor",
    "kommun-och-politik",
    "arbete-och-foretag",
    "uppleva-och-gora",
]
WORDS = [
    "ansökan",
    "bygglov",
    "handläggningstid",
    "avgift",
    "beslut",
    "kommunen",
    "förvaltning",
    "tillstånd",
    "blankett",
    "kontakt",
    "nämnden",
    "planbestämmelser",
    "fastighet",
    "detaljplan",
    "grannar",
    "samråd",
    "förskola",
    "skolskjuts",
    "omsorg",
    "hemtjänst",
    "stöd",
    "bidrag",
    "biblioteket",
    "öppettider",
    "parkering",
    "gatubelysning",
    "återvinning",
    "sophämtning",
    "taxa",
    "överklagande",
    "e-tjänst",
    "medborgarservice",
    "handlingar",
    "ritning",
    "situationsplan",
    "tekniska",
    "samrådet",
    "startbesked",
    "slutbesked",
]


def paragraph(rng: random.Random, words: int) -> str:
    text = " ".join(rng.choice(WORDS) for _ in range(words))
    return text[0].upper() + text[1:] + "."


def unique_pdf(source: Path, target: Path, tag: str) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(source.read_bytes() + f"\n% ume-rag release-test copy {tag}\n".encode())


def page_html(title: str, crumbs: list[tuple[str, str]], body: list[str], links: list[tuple[str, str]]) -> str:
    nav = " › ".join(f'<a href="{href}">{escape(name)}</a>' for href, name in crumbs)
    items = "\n".join(f'      <li><a href="{href}">{escape(name)}</a></li>' for href, name in links)
    paragraphs = "\n".join(f"      <p>{escape(p)}</p>" for p in body)
    return f"""<!DOCTYPE html>
<html lang="sv">
<head><meta charset="utf-8"><title>{escape(title)} - Umeå kommun (testkopia)</title></head>
<body>
  <header><a href="/">Umeå kommun</a></header>
  <nav class="breadcrumb">{nav}</nav>
  <main>
    <article>
      <h1>{escape(title)}</h1>
{paragraphs}
    </article>
    <ul class="related">
{items}
    </ul>
  </main>
  <footer>Testkopia för R1-testet, inte umea.se.</footer>
</body>
</html>
"""


def generate(out: Path, pages: int, pdf_every: int, ocr: int, base_url: str) -> None:
    rng = random.Random(20261031)  # noqa: S311 - the same site on every run, not a secret
    text_pdf, scanned_pdf = FIXTURES / "text-layer.pdf", FIXTURES / "scanned.pdf"
    paths = [f"/{SECTIONS[i % len(SECTIONS)]}/sida-{i:04d}.html" for i in range(pages)]
    for i, path in enumerate(paths):
        section = SECTIONS[i % len(SECTIONS)]
        title = f"{section.replace('-', ' ').capitalize()}: sida {i}"
        # Each page links the next pages of its section and two random ones, so every page is reachable from /.
        links = [(paths[j], f"Sida {j}") for j in (i + len(SECTIONS), i + 2 * len(SECTIONS)) if j < pages]
        links += [(paths[j], f"Sida {j}") for j in rng.sample(range(pages), k=min(2, pages))]
        if pdf_every and i % pdf_every == 0:
            pdf = f"/{section}/dokument/blankett-{i:04d}.pdf"
            unique_pdf(text_pdf, out / pdf.lstrip("/"), f"page-{i}")
            links.append((pdf, f"Blankett {i} (pdf)"))
        body = [paragraph(rng, 60) for _ in range(10)]
        crumbs = [("/", "Start"), (f"/{section}/", section.replace("-", " ").capitalize())]
        target = out / path.lstrip("/")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(page_html(title, crumbs, body, links), encoding="utf-8")
    for s, section in enumerate(SECTIONS):
        first = [(paths[i], f"Sida {i}") for i in range(s, pages, len(SECTIONS))][:2]
        (out / section).mkdir(parents=True, exist_ok=True)
        (out / section / "index.html").write_text(
            page_html(section.replace("-", " ").capitalize(), [("/", "Start")], [paragraph(rng, 40)], first),
            encoding="utf-8",
        )
    start = [(f"/{section}/", section.replace("-", " ").capitalize()) for section in SECTIONS]
    (out / "index.html").write_text(page_html("Umeå kommun", [], [paragraph(rng, 40)], start), encoding="utf-8")
    for n in range(1, ocr + 1):
        unique_pdf(scanned_pdf, out / "ocr-batch" / f"skannad-{n:02d}.pdf", f"ocr-{n}")
    index(out, base_url)


def index(out: Path, base_url: str) -> None:
    base = base_url.rstrip("/")
    files = sorted(p for p in out.rglob("*") if p.suffix in (".html", ".pdf") and "ocr-batch" not in p.parts)
    urls = []
    for p in files:
        rel = p.relative_to(out).as_posix()
        rel = rel.removesuffix("index.html")
        urls.append(f"  <url><loc>{escape(f'{base}/{rel}')}</loc></url>")
    (out / "sitemap.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
        + "\n".join(urls)
        + "\n</urlset>\n",
        encoding="utf-8",
    )
    (out / "robots.txt").write_text(f"User-agent: *\nAllow: /\nSitemap: {base}/sitemap.xml\n", encoding="utf-8")
    print(f"{out}: {len(urls)} URLs in sitemap.xml")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", choices=["generate", "index"])
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--base-url", default="http://r1-site.test:8088", help="the site as the crawler reaches it")
    parser.add_argument("--pages", type=int, default=2000)
    parser.add_argument("--pdf-every", type=int, default=10)
    parser.add_argument("--ocr", type=int, default=50)
    args = parser.parse_args(argv)
    args.out.mkdir(parents=True, exist_ok=True)
    if args.command == "generate":
        generate(args.out, args.pages, args.pdf_every, args.ocr, args.base_url)
    else:
        index(args.out, args.base_url)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
