"""Write the JSON Schemas of the parser contract to contracts/parser/v1/ (parser design §7, §15.1).

The schemas are generated from `ume_parser/models.py`; CI runs this script and fails when `git diff` shows a change
in `contracts/`, so the committed schemas always match the code. Request schemas describe what the parser accepts
(validation mode), result schemas what it sends (serialisation mode).

    uv run python -m scripts.export_schema              # writes ../../contracts/parser/v1/
    uv run python -m scripts.export_schema --out DIR
"""

import argparse
import json
import sys
from pathlib import Path
from typing import Literal

from pydantic import BaseModel

from ume_parser.models import (
    ConvertRequest,
    ConvertResult,
    ErrorResponse,
    Info,
    InspectRequest,
    InspectResult,
    NerRequest,
    NerResult,
)

OUT = Path(__file__).resolve().parent.parent.parent.parent / "contracts" / "parser" / "v1"
BASE_ID = "https://kalla.local/contracts/parser/v1/"

SCHEMAS: dict[str, tuple[type[BaseModel], Literal["validation", "serialization"]]] = {
    "convert-request": (ConvertRequest, "validation"),
    "convert-result": (ConvertResult, "serialization"),
    "inspect-request": (InspectRequest, "validation"),
    "inspect-result": (InspectResult, "serialization"),
    "ner-request": (NerRequest, "validation"),
    "ner-result": (NerResult, "serialization"),
    "info": (Info, "serialization"),
    "error": (ErrorResponse, "serialization"),
}


def schema(model: type[BaseModel], mode: Literal["validation", "serialization"], name: str) -> dict:
    body = model.model_json_schema(by_alias=True, mode=mode)
    return {"$schema": "https://json-schema.org/draft/2020-12/schema", "$id": f"{BASE_ID}{name}.schema.json", **body}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=OUT)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for name, (model, mode) in SCHEMAS.items():
        path = args.out / f"{name}.schema.json"
        path.write_text(json.dumps(schema(model, mode, name), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(path.name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
