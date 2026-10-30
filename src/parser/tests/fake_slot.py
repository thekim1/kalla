"""A slot process with a fake converter, for supervisor and API tests without Docling (parser design §15.1).

It speaks the slot's pipe protocol (`ume_parser.slot`): reports ready at once, then answers each job by the prefix
of its `request_id`: `ok` converts, `sleep` hangs, `crash` dies, `corrupt` reports a contract error.
"""

import os
import time
from multiprocessing.connection import Connection
from typing import Any

from ume_parser.errors import ParserError


def payload() -> dict[str, Any]:
    signals = {
        "chars": 5,
        "furniture_chars": 0,
        "garbled_score": 0.0,
        "text_layer_chars": 5,
        "image_coverage": 0.0,
        "ocr_applied": "none",
        "ocr_confidence": None,
        "table_count": 0,
        "table_area_ratio": 0.0,
        "picture_area_ratio": 0.0,
        "empty": False,
        "ms": 1,
    }
    block = {
        "id": "b0",
        "kind": "paragraph",
        "level": None,
        "level_source": None,
        "page": 1,
        "page_end": 1,
        "bbox": [0.0, 0.0, 10.0, 10.0],
        "layer": "body",
        "md": "Hallå",
        "ref": None,
        "table": None,
    }
    return {
        "status": "success",
        "pipeline": "text_layer",
        "page_count": 1,
        "pages_requested": {"from": 1, "to": 1},
        "pages_completed": {"from": 1, "to": 1},
        "pages": [{"page": 1, "label": None, "width": 595.0, "height": 842.0, "signals": signals}],
        "blocks": [block],
        "tables": [],
        "pictures": [],
        "warnings": [],
        "docling_ms": 0,
        "text_layer_ms": 1,
        "signals_ms": 1,
        "docling_json": False,
        "markdown": None,
    }


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    conn.send(("ready", {"warmup_ms": 1, "problems": []}))
    while True:
        try:
            message = conn.recv()
        except EOFError:
            return
        if message == "stop":
            return
        _, job = message
        request_id = job["options"]["request_id"]
        if request_id.startswith("sleep"):
            time.sleep(600)
        elif request_id.startswith("crash"):
            os._exit(3)
        elif request_id.startswith("corrupt"):
            conn.send(("error", ParserError("corrupt").to_wire()))
        else:
            conn.send(("result", payload()))


def run_never_ready(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """A slot that is still warming up."""
    time.sleep(600)
