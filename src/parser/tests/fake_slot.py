"""Worker processes with fake work, for supervisor and API tests without Docling or torch (parser design §15.1).

They speak the workers' pipe protocol (`ume_parser.slot`): report ready at once, then answer each job by the prefix
of its `request_id`: `ok` succeeds, `sleep` hangs, `crash` dies, `corrupt` reports a contract error, `alloc` holds
`ALLOC_MB` of memory and hangs (for the RSS watchdog). A success answers in the shape of the job's kind.
"""

import os
import time
from multiprocessing.connection import Connection
from typing import Any

from ume_parser.errors import ParserError

ALLOC_MB = 400


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
        "format": "pdf",
        "media_type_sniffed": "application/pdf",
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


def inspect_payload(job: dict[str, Any]) -> dict[str, Any]:
    return {
        "format": job["format"] or "docx",
        "media_type_sniffed": "application/pdf",
        "page_count": 1,
        "rangeable": True,
        "encrypted": "none",
        "has_javascript": False,
        "has_embedded_files": False,
        "has_xfa": False,
        "pages": [
            {
                "page": 1,
                "width": 595.0,
                "height": 842.0,
                "rotation": 0,
                "text_chars": 300,
                "bad_chars": 0,
                "garbled_score": 0.0,
                "image_coverage": 0.0,
                "path_objects": 0,
                "dpi": None,
            }
        ],
        "refusal": None,
        "inspect_ms": 1,
    }


def ner_payload(job: dict[str, Any]) -> dict[str, Any]:
    first = job["texts"][0]
    return {"entities": [{"text_id": first["id"], "start": 0, "end": 4, "label": "person", "score": 0.99}], "ner_ms": 1}


def answer(kind: str, job: dict[str, Any]) -> dict[str, Any]:
    if kind == "inspect":
        return inspect_payload(job)
    if kind == "ner":
        return ner_payload(job)
    return payload()


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    conn.send(("ready", {"warmup_ms": 1, "problems": []}))
    held = []
    while True:
        try:
            message = conn.recv()
        except EOFError:
            return
        if message == "stop":
            return
        kind, job = message
        request_id = job["request_id"]
        if request_id.startswith("sleep"):
            time.sleep(600)
        elif request_id.startswith("crash"):
            os._exit(3)
        elif request_id.startswith("corrupt"):
            conn.send(("error", ParserError("corrupt").to_wire()))
        elif request_id.startswith("alloc"):
            held.append(bytearray(b"\x01") * (ALLOC_MB * 1024 * 1024))
            time.sleep(600)
        else:
            conn.send(("result", answer(kind, job)))


def run_never_ready(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """A worker that is still warming up."""
    time.sleep(600)


def run_failing(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """A worker whose self-test fails."""
    conn.send(("failed", {"warmup_ms": 1, "problems": ["self-test: fake failure"]}))


FAKE = {"convert": run, "inspect": run, "ner": run}
