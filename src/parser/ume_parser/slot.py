"""The parser's worker processes (parser design §10.1, §10.2): conversion slots, and the shared pipe protocol.

The supervisor starts a lane's entry point in a fresh `spawn` process: `run` for a conversion slot, `inspect.run` for
`inspect-0`, `ner.run` for `ner-0`. Each prepares the process (`prepare`), warms up and reports `("ready", report)`
or `("failed", report)` over its pipe, then `serve`s jobs: for `(kind, job)` it answers `("result", payload)` or
`("error", ParserError.to_wire())`. It exits when the supervisor sends `"stop"` or closes the pipe; a job that runs
past its deadline is not cancelled here but killed by the supervisor. Every job carries its `request_id`.

A conversion slot builds the default pipeline from the baked models and converts the self-test PDF through Docling
and through the text-layer fast path before it reports ready.
"""

import io
import logging
import os
import time
from collections.abc import Callable
from multiprocessing.connection import Connection
from pathlib import Path
from typing import TYPE_CHECKING, Any

from ume_parser import selftest
from ume_parser.errors import ParserError

if TYPE_CHECKING:
    from ume_parser.convert import Pipeline

log = logging.getLogger(__name__)

Handler = Callable[[dict[str, Any]], dict[str, Any]]

# The kernel's OOM killer picks the process with the highest score first: worker processes before the supervisor
# (§10.3). Raising a process's own score needs no capability.
OOM_SCORE_ADJ = 1000


def prepare(name: str, threads: int, *, address_space_bytes: int | None = None) -> None:
    """Logging, thread count, OOM score and (inspect lane) the address-space limit of a worker process."""
    os.environ["OMP_NUM_THREADS"] = str(threads)
    logging.basicConfig(level=logging.INFO, format=f"%(asctime)s %(levelname)s %(name)s[{name}] %(message)s")
    try:
        Path("/proc/self/oom_score_adj").write_text(str(OOM_SCORE_ADJ), encoding="ascii")
    except OSError:
        log.debug("%s: oom_score_adj not set", name)
    if address_space_bytes is not None:
        import resource

        resource.setrlimit(resource.RLIMIT_AS, (address_space_bytes, address_space_bytes))


def handle(kind: str, job: dict[str, Any], handler: Handler) -> tuple[str, Any]:
    """Runs one job; a contract error goes back in its wire form, anything else as `conversion_failed`."""
    try:
        return "result", handler(job)
    except ParserError as exc:
        return "error", exc.to_wire()
    except MemoryError:
        log.error("%s %s: out of memory", kind, job.get("request_id"))
        return "error", ParserError("resource_limit").to_wire()
    except Exception as exc:
        # Library messages can quote document content; at INFO only the type (parser design §13.2).
        log.error("%s %s: raised %s", kind, job.get("request_id"), type(exc).__name__)
        log.debug("job error", exc_info=True)
        return "error", ParserError("conversion_failed").to_wire()


def serve(conn: Connection, handlers: dict[str, Handler]) -> None:
    """Serves jobs until the supervisor sends `"stop"` or closes the pipe."""
    while True:
        try:
            message = conn.recv()
        except EOFError:
            return
        if message == "stop":
            return
        kind, job = message
        if kind in handlers:
            conn.send(handle(kind, job, handlers[kind]))
        else:
            conn.send(("error", ParserError("invalid_request").to_wire()))


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


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """A conversion slot, `convert-slot-N`."""
    prepare(f"convert-slot-{slot_id}", threads)
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
        from ume_parser.convert import Pipelines, run_job
        from ume_parser.settings import Settings

        pipelines = Pipelines(Path(models_dir), threads, default=pipeline, cache_size=Settings().pipeline_cache)
        serve(conn, {"convert": lambda job: run_job(job, pipelines)})
