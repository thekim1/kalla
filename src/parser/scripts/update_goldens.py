"""Write the golden results of tests/fixtures/parser/golden/ from a running parser (parser design §15.2).

Converts every case of tests/fixtures/parser/cases.json ("conversions") and writes the normalised `result.json`:
without `timings`, `build` and the per-page `ms`, which change from run to run. Ume.Rag.ContractTests compares new
results with these: kinds, levels, pages and table shapes exactly, text by similarity. Run it only in a PR that
changes the conversion (a Docling bump, a fast-path change), and review the diff.

    docker run --rm -p 8000:8000 ume-rag/parser:dev
    uv run python -m scripts.update_goldens --url http://localhost:8000

Standard library only.
"""

import argparse
import json
import secrets
import sys
import urllib.request
from email import message_from_bytes, policy
from pathlib import Path
from typing import Any

FIXTURES = Path(__file__).resolve().parent.parent.parent.parent / "tests" / "fixtures" / "parser"
VOLATILE = ("timings", "build")


def request_id(name: str) -> str:
    """The request id of a case; the contract tests send the same, so results compare whole."""
    return f"case-{name}"


def normalise(result: dict[str, Any]) -> dict[str, Any]:
    out = {k: v for k, v in result.items() if k not in VOLATILE}
    out["pages"] = [{**p, "signals": {k: v for k, v in p["signals"].items() if k != "ms"}} for p in result["pages"]]
    return out


def _multipart_form(fields: dict[str, tuple[str | None, str, bytes]]) -> tuple[bytes, str]:
    boundary = secrets.token_hex(16)
    body = b""
    for name, (filename, content_type, data) in fields.items():
        disposition = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        body += f"--{boundary}\r\nContent-Disposition: {disposition}\r\nContent-Type: {content_type}\r\n\r\n".encode()
        body += data + b"\r\n"
    return body + f"--{boundary}--\r\n".encode(), f"multipart/form-data; boundary={boundary}"


def convert(url: str, case: dict[str, Any]) -> dict[str, Any]:
    options = {"request_id": request_id(case["name"]), **case["options"]}
    body, content_type = _multipart_form(
        {
            "file": ("input", "application/octet-stream", (FIXTURES / case["file"]).read_bytes()),
            "options": (None, "application/json", json.dumps(options).encode()),
        }
    )
    request = urllib.request.Request(  # noqa: S310 (the URL is the operator's parser)
        url.rstrip("/") + "/v1/convert", data=body, headers={"Content-Type": content_type}, method="POST"
    )
    with urllib.request.urlopen(request, timeout=180) as response:  # noqa: S310
        raw = response.read()
        header = f"Content-Type: {response.headers['Content-Type']}\r\n\r\n".encode()
    message = message_from_bytes(header + raw, policy=policy.HTTP)
    (result,) = [p for p in message.iter_parts() if p["Content-ID"] == "result"]
    return json.loads(result.get_payload(decode=True))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--url", default="http://localhost:8000")
    args = parser.parse_args()
    cases = json.loads((FIXTURES / "cases.json").read_text(encoding="utf-8"))["conversions"]
    out = FIXTURES / "golden"
    out.mkdir(exist_ok=True)
    for case in cases:
        result = normalise(convert(args.url, case))
        path = out / f"{case['name']}.json"
        path.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{path.name}: {result['input']['pipeline']}, {len(result['blocks'])} blocks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
