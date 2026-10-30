"""A conversion slot: a spawned process that holds warm Docling pipelines (parser design §10.1, §10.2).

The supervisor starts `run` in a fresh process. The slot builds the default pipeline from the baked models, converts
the self-test PDF through Docling and through the text-layer fast path, and reports `("ready", report)` or
`("failed", report)` over its pipe. Then it serves jobs: for `("convert", job)` it answers `("result", payload)` or
`("error", ParserError.to_wire())`. It exits when the supervisor sends `"stop"` or closes the pipe; a job that runs
past its deadline is not cancelled here but killed by the supervisor.
"""

import io
import logging
import os
import time
from multiprocessing.connection import Connection
from pathlib import Path
from typing import TYPE_CHECKING, Any

from ume_parser import selftest
from ume_parser.errors import ParserError

if TYPE_CHECKING:
    from ume_parser.convert import Pipeline, Pipelines

log = logging.getLogger(__name__)


def warm_up(models_dir: Path, threads: int) -> tuple["Pipeline", list[str]]:
    """Builds the default pipeline and converts the self-test PDF both ways; returns it with the problems found."""
    import pypdfium2 as pdfium
    from docling_core.types.io import DocumentStream

    from ume_parser import textlayer
    from ume_parser.convert import build_pipeline

    pipeline = build_pipeline(models_dir, threads)
    data = selftest.make_pdf()
    document = pipeline.converter.convert(DocumentStream(name="selftest.pdf", stream=io.BytesIO(data))).document
    problems = selftest.check(document.export_to_markdown(), len(document.tables))

    pdf = pdfium.PdfDocument(data)
    try:
        _, blocks = textlayer.convert(pdf, 1, 1)
    finally:
        pdf.close()
    problems += selftest.check_text_layer([b["md"] for b in blocks])
    return pipeline, problems


def _convert(job: dict[str, Any], pipelines: "Pipelines") -> tuple[str, dict[str, Any]]:
    from ume_parser.convert import run_job

    try:
        return "result", run_job(job, pipelines)
    except ParserError as exc:
        return "error", exc.to_wire()
    except Exception as exc:
        # Library messages can quote document content; at INFO only the type (parser design §13.2).
        log.error("job %s: raised %s", job["options"].get("request_id"), type(exc).__name__)
        log.debug("job error", exc_info=True)
        return "error", ParserError("conversion_failed").to_wire()


def _serve(conn: Connection, pipelines: "Pipelines") -> None:
    """Serves jobs until the supervisor sends `"stop"` or closes the pipe."""
    while True:
        try:
            message = conn.recv()
        except EOFError:
            return
        if message == "stop":
            return
        kind, job = message
        if kind == "convert":
            conn.send(_convert(job, pipelines))


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    os.environ["OMP_NUM_THREADS"] = str(threads)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s[slot-%(process)d] %(message)s")
    started = time.monotonic()
    try:
        pipeline, problems = warm_up(Path(models_dir), threads)
    except Exception as exc:
        log.error("slot %d: warm-up raised %s", slot_id, type(exc).__name__)
        log.debug("slot %d: warm-up error", slot_id, exc_info=True)
        pipeline, problems = None, [f"raised {type(exc).__name__}"]
    report = {"warmup_ms": round((time.monotonic() - started) * 1000), "problems": problems}
    conn.send(("failed" if problems else "ready", report))
    if not problems:
        from ume_parser.convert import Pipelines

        _serve(conn, Pipelines(Path(models_dir), threads, default=pipeline))
