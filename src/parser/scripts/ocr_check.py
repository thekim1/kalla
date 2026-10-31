"""OCR acceptance check (parser design §9.3; to-verify #131): å/ä/ö recall of Tesseract `swe` through the parser.

Converts every page image of a sample directory through a running parser (`/v1/convert`, Docling with OCR, the
production path) and compares the body text with the page's ground truth (`<stem>.txt` next to the image):

- **å/ä/ö recall**: the share of ground-truth å, ä, ö (and Å, Ä, Ö) that come out as the same letter, over all pages;
  letters are matched along an alignment of the two texts (whitespace normalised);
- **CER**: Levenshtein distance ÷ ground-truth length, per page and over the sample;
- **ms/page**: the parser's total time per page.

Pass: recall ≥ 0.95. The report is Markdown with numbers only (no page text), so it may leave the test host when the
sample holds real internal pages. `--generate` first writes the repository's synthetic sample
(`scripts.make_fixtures.ocr_sample`) into the directory.

    uv run python -m scripts.ocr_check --sample /tmp/ocr --generate --url http://localhost:8000 --out ocr-report.md
"""

import argparse
import difflib
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from scripts.parser_http import HttpParser, Parser

LETTERS = "åäöÅÄÖ"
PASS_RECALL = 0.95
IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".pdf"}


@dataclass
class PageResult:
    name: str
    letters: int
    hits: int
    distance: int
    length: int
    ms: int

    @property
    def recall(self) -> float:
        return self.hits / self.letters if self.letters else 1.0

    @property
    def cer(self) -> float:
        return self.distance / self.length if self.length else 0.0


def normalise(text: str) -> str:
    return " ".join(text.split())


def letter_hits(truth: str, ocr: str) -> tuple[int, int]:
    """(ground-truth å/ä/ö letters, of them recognised as the same letter) along an alignment of the texts."""
    total = sum(1 for ch in truth if ch in LETTERS)
    hits = 0
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, truth, ocr, autojunk=False).get_opcodes():
        if tag == "equal" or (tag == "replace" and i2 - i1 == j2 - j1):
            hits += sum(1 for a, b in zip(truth[i1:i2], ocr[j1:j2], strict=True) if a in LETTERS and a == b)
    return total, hits


def levenshtein(a: str, b: str) -> int:
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, start=1):
        current = [i]
        for j, cb in enumerate(b, start=1):
            current.append(min(current[j - 1] + 1, previous[j] + 1, previous[j - 1] + (ca != cb)))
        previous = current
    return previous[-1]


def body_text(result: dict[str, Any]) -> str:
    return "\n".join(b["md"].lstrip("#").strip() for b in result["blocks"] if b["layer"] == "body")


def check_page(parser: Parser, image: Path, index: int) -> PageResult:
    truth = normalise(image.with_suffix(".txt").read_text(encoding="utf-8"))
    options = {"request_id": f"ocr-check-{index}", "file_ext": image.suffix.lower(), "ocr": {"mode": "auto"}}
    started = time.monotonic()
    result = parser.convert(image.read_bytes(), options)
    ms = round((time.monotonic() - started) * 1000)
    ocr = normalise(body_text(result))
    letters, hits = letter_hits(truth, ocr)
    return PageResult(image.name, letters, hits, levenshtein(truth, ocr), len(truth), ms)


def run(parser: Parser, sample: Path) -> list[PageResult]:
    images = sorted(
        p for p in sample.iterdir() if p.suffix.lower() in IMAGE_SUFFIXES and p.with_suffix(".txt").is_file()
    )
    if not images:
        raise SystemExit(f"no page images with ground truth in {sample}")
    return [check_page(parser, image, i) for i, image in enumerate(images)]


def summary(results: list[PageResult]) -> dict[str, float]:
    letters = sum(r.letters for r in results)
    hits = sum(r.hits for r in results)
    return {
        "pages": len(results),
        "letters": letters,
        "recall": hits / letters if letters else 1.0,
        "cer": sum(r.distance for r in results) / max(sum(r.length for r in results), 1),
        "ms_per_page": sum(r.ms for r in results) / len(results),
    }


def report(results: list[PageResult], title: str) -> str:
    total = summary(results)
    verdict = "**pass**" if total["recall"] >= PASS_RECALL else "**fail**"
    lines = [
        f"# OCR acceptance check: {title}",
        "",
        f"Tesseract `swe` through the parser's Docling OCR path; pass at å/ä/ö recall ≥ {PASS_RECALL:.0%}.",
        "",
        "| Pages | å/ä/ö letters | Recall | CER | ms/page | Result |",
        "|---|---|---|---|---|---|",
        f"| {total['pages']} | {total['letters']} | {total['recall']:.3f} | {total['cer']:.3f} "
        f"| {total['ms_per_page']:.0f} | {verdict} |",
        "",
        "| Page | Letters | Recall | CER | ms |",
        "|---|---|---|---|---|",
    ]
    lines += [f"| {r.name} | {r.letters} | {r.recall:.3f} | {r.cer:.3f} | {r.ms} |" for r in results]
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--sample", type=Path, required=True, help="page images with <stem>.txt ground truth")
    parser.add_argument("--generate", action="store_true", help="write the synthetic sample into --sample first")
    parser.add_argument("--url", default="http://localhost:8000")
    parser.add_argument("--title", default="sample")
    parser.add_argument("--out", type=Path, help="Markdown report (default: stdout)")
    args = parser.parse_args()
    if args.generate:
        from scripts import make_fixtures

        make_fixtures.ocr_sample(args.sample)
    results = run(HttpParser(args.url), args.sample)
    text = report(results, args.title)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        print(text, end="")
    return 0 if summary(results)["recall"] >= PASS_RECALL else 1


if __name__ == "__main__":
    sys.exit(main())
