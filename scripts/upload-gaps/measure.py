"""What the upload gaps cost on a folder of documents, measured through a running parser (upload-quality review U1, U5,
U7; plans/proposals/upload-quality-review.md, "Measurement").

    python3 -I scripts/upload-gaps/measure.py --dir <folder> --url http://127.0.0.1:8000 --out report.md

Per document: pages, body text, headings, pictures and how many have a caption, the text OCR finds inside pictures (the
chunker keeps only a picture's caption, processing §6.4, so that text never reaches retrieval), picture area per page,
pages without a text layer, and table sizes. Prints and writes numbers only, never document text. Standard library
only; reuses the parser scripts' client. PDFs go in ranges of 8 pages as the worker sends them; `not_ready` and `busy`
(a slot recycling or warming) are retried.

Office files go through Docling's declarative backends, which run no OCR: their pictures are counted, but the text in
them is not measured (it is dropped all the same).
"""

import argparse
import json
import pathlib
import re
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "src" / "parser" / "scripts"))
from parser_http import HttpParser, ParserHttpError

RANGE = 8
SKIP_KINDS = ("picture", "page_header", "page_footer")


def convert(parser, data, options):
    for _ in range(30):
        try:
            return parser.convert(data, options)
        except ParserHttpError as e:
            if e.code in ("not_ready", "busy"):
                time.sleep(10)
                continue
            raise
    raise RuntimeError("parser stayed not ready")


def measure(parser, path):
    data, ext = path.read_bytes(), path.suffix.lower()
    ranges = [None]
    if ext == ".pdf":
        n = parser.inspect(data, {"file_ext": ext}).get("page_count") or 1
        ranges = [{"from": a, "to": min(n, a + RANGE - 1)} for a in range(1, n + 1, RANGE)]
    pages, blocks, pictures, tables, errors = [], [], [], [], []
    for i, r in enumerate(ranges):
        try:
            out = convert(parser, data, {"request_id": f"m{i}", "file_ext": ext, "pages": r, "deadline_ms": 120000})
        except (ParserHttpError, RuntimeError) as e:
            errors.append(str(e))
            continue
        pages += out["pages"]
        blocks += out["blocks"]
        pictures += out["pictures"]
        tables += out["tables"]
    body = [b for b in blocks if b["layer"] == "body" and b["kind"] not in SKIP_KINDS]
    areas = [p["signals"].get("picture_area_ratio") or 0 for p in pages]
    return {
        "pages": len(pages),
        "errors": errors,
        "body_chars": sum(len(re.sub(r"\s", "", b["md"] or "")) for b in body),
        "headings": sum(1 for b in body if b["kind"] in ("title", "heading")),
        "pictures": len(pictures),
        "captioned": sum(1 for p in pictures if p.get("caption")),
        "picture_text": sum(p["ocr_chars"] for p in pictures),
        "pages_20": sum(a >= 0.2 for a in areas),
        "max_area": max(areas, default=0),
        "no_text_layer": sum(1 for p in pages if (p["signals"].get("text_layer_chars") or 0) <= 50),
        "table_rows": [t["rows"] for t in tables],
    }


def report(results):
    pages = sum(r["pages"] for r in results.values())
    body = sum(r["body_chars"] for r in results.values())
    pic_text = sum(r["picture_text"] for r in results.values())
    pics = sum(r["pictures"] for r in results.values())
    lines = [
        f"{len(results)} documents, {pages} pages, {pics} pictures "
        f"({sum(r['captioned'] for r in results.values())} with a caption).",
        f"Text inside pictures: {pic_text:,} of {body + pic_text:,} non-space characters "
        f"({pic_text / max(1, body + pic_text):.1%}); pages with pictures over 20 % of the area: "
        f"{sum(r['pages_20'] for r in results.values())}; pages without a text layer: "
        f"{sum(r['no_text_layer'] for r in results.values())}; documents with at most one heading: "
        f"{sum(r['headings'] <= 1 for r in results.values())}.",
        "",
        "| Document | Pages | Pictures | Text in pictures | Max picture area | Headings | No text layer | Table rows |",
        "|---|---|---|---|---|---|---|---|",
    ]

    def share_of(r):
        return r["picture_text"] / max(1, r["body_chars"] + r["picture_text"])

    for name, r in sorted(results.items(), key=lambda kv: -share_of(kv[1])):
        share = share_of(r)
        err = f" ({len(r['errors'])} ranges failed)" if r["errors"] else ""
        lines.append(
            f"| {name}{err} | {r['pages']} | {r['pictures']} | {share:.0%} | {r['max_area']:.2f} | {r['headings']} | "
            f"{r['no_text_layer']} | {', '.join(map(str, r['table_rows'])) or '-'} |"
        )
    return "\n".join(lines) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--dir", required=True, type=pathlib.Path)
    ap.add_argument("--url", default="http://127.0.0.1:8000")
    ap.add_argument("--out", type=pathlib.Path)
    args = ap.parse_args()
    parser = HttpParser(args.url)
    results = {}
    for path in sorted(p for p in args.dir.iterdir() if p.is_file()):
        results[path.name] = measure(parser, path)
        print(path.name, json.dumps(results[path.name]), flush=True)
    text = report(results)
    print(text)
    if args.out:
        args.out.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
