"""A conversion slot: a spawned process that holds a warm Docling pipeline (parser design §10.1, §10.2).

The supervisor starts `run` in a fresh process. The slot builds the default converter from the baked models,
converts the self-test PDF and reports `("ready", report)` or `("failed", report)` over its pipe, then waits for
work. It exits when the supervisor sends `"stop"` or closes the pipe.
"""

import io
import logging
import os
import time
from multiprocessing.connection import Connection
from pathlib import Path
from typing import TYPE_CHECKING

from ume_parser import selftest

if TYPE_CHECKING:
    from docling.document_converter import DocumentConverter

log = logging.getLogger(__name__)


def warm_up(models_dir: Path, threads: int) -> tuple["DocumentConverter", list[str]]:
    """Builds the default converter and converts the self-test PDF; returns it with the problems found."""
    from docling_core.types.io import DocumentStream

    from ume_parser.convert import build_converter

    converter = build_converter(models_dir, threads)
    stream = DocumentStream(name="selftest.pdf", stream=io.BytesIO(selftest.make_pdf()))
    document = converter.convert(stream).document
    return converter, selftest.check(document.export_to_markdown(), len(document.tables))


def _serve(conn: Connection, converter: "DocumentConverter") -> None:
    """Keeps `converter` (the warm pipeline) alive until the supervisor sends `"stop"` or closes the pipe."""
    while True:
        try:
            message = conn.recv()
        except EOFError:
            return
        if message == "stop":
            return


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    os.environ["OMP_NUM_THREADS"] = str(threads)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s[slot-%(process)d] %(message)s")
    started = time.monotonic()
    try:
        converter, problems = warm_up(Path(models_dir), threads)
    except Exception as exc:
        # Library messages can quote document content; at INFO only the type (parser design §13.2).
        log.error("slot %d: warm-up raised %s", slot_id, type(exc).__name__)
        log.debug("slot %d: warm-up error", slot_id, exc_info=True)
        converter, problems = None, [f"raised {type(exc).__name__}"]
    report = {"warmup_ms": round((time.monotonic() - started) * 1000), "problems": problems}
    conn.send(("failed" if problems else "ready", report))
    if not problems:
        _serve(conn, converter)
