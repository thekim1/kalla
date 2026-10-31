"""Write the golden results of tests/fixtures/parser/golden/ from a running parser (parser design §15.2).

Converts every case of tests/fixtures/parser/cases.json ("conversions") and writes the normalised `result.json`:
without `timings`, `build` and the per-page `ms`, which change from run to run. Ume.Rag.ContractTests compares new
results with these: kinds, levels, pages and table shapes exactly, text by similarity. Run it only in a PR that
changes the conversion (a Docling bump, a fast-path change), and review the diff.

    docker run --rm -p 8000:8000 ume-rag/parser:dev
    uv run python -m scripts.update_goldens --url http://localhost:8000

Standard library only (with `scripts/parser_http.py`).
"""

import argparse
import json
import sys
from pathlib import Path
from typing import Any

from scripts.parser_http import HttpParser

FIXTURES = Path(__file__).resolve().parent.parent.parent.parent / "tests" / "fixtures" / "parser"
VOLATILE = ("timings", "build")


def request_id(name: str) -> str:
    """The request id of a case; the contract tests send the same, so results compare whole."""
    return f"case-{name}"


def normalise(result: dict[str, Any]) -> dict[str, Any]:
    out = {k: v for k, v in result.items() if k not in VOLATILE}
    out["pages"] = [{**p, "signals": {k: v for k, v in p["signals"].items() if k != "ms"}} for p in result["pages"]]
    return out


def convert(parser: HttpParser, case: dict[str, Any]) -> dict[str, Any]:
    options = {"request_id": request_id(case["name"]), **case["options"]}
    return parser.convert((FIXTURES / case["file"]).read_bytes(), options)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--url", default="http://localhost:8000")
    args = parser.parse_args()
    cases = json.loads((FIXTURES / "cases.json").read_text(encoding="utf-8"))["conversions"]
    client = HttpParser(args.url)
    out = FIXTURES / "golden"
    out.mkdir(exist_ok=True)
    for case in cases:
        result = normalise(convert(client, case))
        path = out / f"{case['name']}.json"
        path.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{path.name}: {result['input']['pipeline']}, {len(result['blocks'])} blocks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
