"""`POST /v1/convert`, `POST /v1/inspect` and `POST /v1/ner` (parser design §6.2, §7.1 to §7.5, §7.8).

The API process reads the request, validates the options, sniffs the file's first bytes (and an OLE file's stream
names) and writes the file into a job directory. It parses nothing itself: a worker process opens the document.

- `/v1/convert`: multipart (at most 60 MB, two parts: `file` and `options`) to a conversion slot; the answer is
  `multipart/mixed`: `result.json`, then `docling.json.gz` when Docling ran and `outputs.docling_json` is set.
- `/v1/inspect`: the same request shape, `options` optional, to `inspect-0` under the 10 s inspect deadline; the
  answer is `InspectResult` JSON.
- `/v1/ner`: a JSON `NerRequest` to `ner-0`, which up to `NER_QUEUE` requests may wait for; the answer is `NerResult`.

Errors are `ErrorResponse` JSON with a fixed `detail` per code.
"""

import hashlib
import logging
import secrets
import shutil
import time
import uuid
from collections.abc import Iterator
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response, StreamingResponse
from pydantic import BaseModel, ValidationError
from starlette.background import BackgroundTask
from starlette.datastructures import FormData, UploadFile
from starlette.formparsers import MultiPartException
from starlette.types import Message

from ume_parser import limits, sniff
from ume_parser.errors import ParserError
from ume_parser.models import (
    CONTRACT,
    Build,
    ConvertInput,
    ConvertRequest,
    ConvertResult,
    ConvertWarning,
    ErrorBody,
    ErrorResponse,
    Format,
    InspectRequest,
    InspectResult,
    InspectTimings,
    NerRequest,
    NerResult,
    NerTimings,
    Timings,
)
from ume_parser.supervisor import CONVERT, INSPECT, NER, NOT_READY_RETRY_AFTER_S, ClientDisconnectedError, Supervisor

log = logging.getLogger(__name__)

OPTIONS_MAX_BYTES = 64 * 1024
NER_MAX_BODY_BYTES = 1024 * 1024
CHUNK = 1024 * 1024
DOCLING_JSON = "docling.json.gz"  # written by the slot (convert.DOCLING_JSON); the API process imports no Docling
# Codes a non-rangeable input cannot recover from with a smaller range (design §8.1: a DOCX that times out).
_RANGE_RETRIES = {"timeout", "worker_crashed", "resource_limit"}
_INSPECT_FIELDS = (
    "format",
    "media_type_sniffed",
    "page_count",
    "rangeable",
    "encrypted",
    "has_javascript",
    "has_embedded_files",
    "has_xfa",
    "pages",
    "refusal",
)

router = APIRouter()


class _TooLargeError(Exception):
    pass


def error_response(exc: ParserError, request_id: str | None, build: Build) -> JSONResponse:
    body = ErrorResponse(
        error=ErrorBody(
            code=exc.code,
            detail=exc.spec.detail,
            retry=exc.retry,
            limit=exc.limit,
            reason=exc.reason,
            pages_completed=None,
        ),
        request_id=request_id,
        build=build,
    )
    headers = {"Retry-After": str(exc.retry_after_s)} if exc.retry_after_s is not None else None
    return JSONResponse(body.model_dump(mode="json"), status_code=exc.spec.status, headers=headers)


def _check_length(request: Request, limit: int) -> None:
    length = request.headers.get("content-length", "")
    if length.isdigit() and int(length) > limit:
        raise ParserError("too_large")


async def _read_body(request: Request, limit: int) -> bytes:
    _check_length(request, limit)
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > limit:
            raise ParserError("too_large")
    return bytes(body)


async def _read_form(request: Request) -> FormData:
    """The multipart body, refused as `too_large` once more than the limit has arrived (chunked bodies too)."""
    _check_length(request, limits.MAX_BODY_BYTES)
    received = 0

    async def receive() -> Message:
        nonlocal received
        message = await request.receive()
        if message["type"] == "http.request":
            received += len(message.get("body", b""))
            if received > limits.MAX_BODY_BYTES:
                raise _TooLargeError
        return message

    try:
        return await Request(request.scope, receive).form(max_files=1, max_fields=1, max_part_size=OPTIONS_MAX_BYTES)
    except _TooLargeError:
        raise ParserError("too_large") from None
    except MultiPartException:
        raise ParserError("invalid_request") from None


def _options[T: BaseModel](form: FormData, model: type[T], *, required: bool = True) -> T:
    raw = form.get("options")
    if raw is None and not required:
        return model()
    if not isinstance(raw, str):
        raise ParserError("invalid_request")
    try:
        return model.model_validate_json(raw)
    except ValidationError:
        raise ParserError("invalid_request") from None


def _spool(upload: UploadFile, target: Path) -> tuple[bytes, str, int]:
    """Copies the upload to `target`; returns its first bytes, SHA-256 and size."""
    digest, size, head = hashlib.sha256(), 0, b""
    upload.file.seek(0)
    with target.open("wb") as out:
        while chunk := upload.file.read(CHUNK):
            if len(head) < sniff.HEAD_BYTES:
                head += chunk[: sniff.HEAD_BYTES - len(head)]
            digest.update(chunk)
            size += len(chunk)
            out.write(chunk)
    return head, digest.hexdigest(), size


def _mismatch(fmt: Format, file_ext: str | None, media_type: str | None) -> list[ConvertWarning]:
    if sniff.declared_mismatch(fmt, file_ext, media_type):
        return [ConvertWarning(code="media_type_mismatch", page=None)]
    return []


def _multipart(result_json: bytes, docling: Path | None, boundary: str) -> Iterator[bytes]:
    yield (
        f"--{boundary}\r\nContent-Type: application/json; charset=utf-8\r\nContent-ID: result\r\n\r\n".encode()
        + result_json
        + b"\r\n"
    )
    if docling is not None:
        yield (
            f"--{boundary}\r\nContent-Type: application/vnd.docling.document+json\r\n"
            "Content-Encoding: gzip\r\nContent-ID: docling\r\n\r\n"
        ).encode()
        with docling.open("rb") as f:
            while chunk := f.read(CHUNK):
                yield chunk
        yield b"\r\n"
    yield f"--{boundary}--\r\n".encode()


class _Upload:
    """A request's file spooled into its own job directory and sniffed; `close` removes the directory."""

    def __init__(self, jobs_dir: Path, request_id: str, upload: UploadFile) -> None:
        self.dir = jobs_dir / f"{request_id}-{uuid.uuid4().hex[:8]}"
        self.dir.mkdir(parents=True)
        self.input = self.dir / "input"
        try:
            head, self.sha256, self.size = _spool(upload, self.input)
            self.sniffed = sniff.sniff_file(self.input, head)
        except BaseException:
            self.close()
            raise

    def job(self, request_id: str) -> dict[str, Any]:
        return {"request_id": request_id, "input": str(self.input), "dir": str(self.dir), "format": self.sniffed.format}

    def close(self) -> None:
        shutil.rmtree(self.dir, ignore_errors=True)


async def _run(
    request: Request, lane: str, request_id: str, kind: str, job: dict[str, Any], deadline_s: float
) -> tuple[dict[str, Any], int]:
    """Runs a job on a free process of `lane`; returns the worker's payload and the milliseconds waited for one."""
    supervisor: Supervisor = request.app.state.supervisor
    queued = time.monotonic()
    if lane == NER:
        reserved = await supervisor.acquire(request_id, lane, deadline_s)
    else:
        reserved = supervisor.reserve(request_id, lane)
    wait_ms = round((time.monotonic() - queued) * 1000)
    try:
        return await supervisor.run(reserved, kind, job, deadline_s, request.is_disconnected), wait_ms
    finally:
        supervisor.release(reserved, request_id)


@router.post("/v1/convert")
async def convert(request: Request) -> Response:
    started = time.monotonic()
    state = request.app.state
    request_id: str | None = None
    upload: _Upload | None = None
    form: FormData | None = None
    rangeable = True
    try:
        state.supervisor.check_capacity(CONVERT)
        form = await _read_form(request)
        options = _options(form, ConvertRequest)
        request_id = options.request_id
        file = form.get("file")
        if not isinstance(file, UploadFile):
            raise ParserError("invalid_request")
        if options.pages is not None and options.pages.count > limits.MAX_PAGES_PER_REQUEST:
            raise ParserError("too_many_pages", retry="smaller_range")

        upload = _Upload(Path(state.settings.jobs_dir), request_id, file)
        await form.close()
        form = None
        rangeable = upload.sniffed.format in sniff.RANGEABLE
        job = {**upload.job(request_id), "options": options.model_dump(mode="json", by_alias=True)}
        payload, wait_ms = await _run(request, CONVERT, request_id, "convert", job, options.deadline_ms / 1000)

        result = ConvertResult(
            contract=CONTRACT,
            request_id=request_id,
            status=payload["status"],
            input=ConvertInput(
                format=payload["format"],
                media_type_sniffed=payload["media_type_sniffed"],
                sha256=upload.sha256,
                bytes=upload.size,
                page_count=payload["page_count"],
                pages_requested=payload["pages_requested"],
                pages_completed=payload["pages_completed"],
                pipeline=payload["pipeline"],
            ),
            pages=payload["pages"],
            blocks=payload["blocks"],
            tables=payload["tables"],
            pictures=payload["pictures"],
            warnings=[*_mismatch(payload["format"], options.file_ext, options.media_type), *payload["warnings"]],
            timings=Timings(
                total_ms=round((time.monotonic() - started) * 1000),
                wait_ms=wait_ms,
                docling_ms=payload["docling_ms"],
                text_layer_ms=payload["text_layer_ms"],
                signals_ms=payload["signals_ms"],
            ),
            build=state.build,
            markdown=payload["markdown"],
        )
        completed = result.input.pages_completed
        log.info(
            "convert %s: %s %s/%s, %d bytes, pages %s of %s, %d ms",
            request_id,
            result.input.format,
            result.status,
            result.input.pipeline,
            upload.size,
            f"{completed.from_}-{completed.to}" if completed else "-",
            result.input.page_count if result.input.page_count is not None else "-",
            result.timings.total_ms,
        )
        boundary = secrets.token_hex(16)
        docling = upload.dir / DOCLING_JSON if payload["docling_json"] else None
        response = StreamingResponse(
            _multipart(result.model_dump_json().encode("utf-8"), docling, boundary),
            media_type=f"multipart/mixed; boundary={boundary}",
            background=BackgroundTask(upload.close),
        )
        upload = None  # removed by the response's background task once it is sent
        return response
    except ParserError as exc:
        if exc.code in _RANGE_RETRIES and not rangeable:
            exc.retry = "none"
        log.info("convert %s: %s", request_id, exc.code)
        return error_response(exc, request_id, state.build)
    except ClientDisconnectedError:
        log.info("convert %s: client disconnected, slot killed", request_id)
        return Response(status_code=499)
    finally:
        if form is not None:
            await form.close()
        if upload is not None:
            upload.close()


@router.post("/v1/inspect")
async def inspect(request: Request) -> Response:
    started = time.monotonic()
    state = request.app.state
    request_id: str | None = None
    upload: _Upload | None = None
    form: FormData | None = None
    try:
        state.supervisor.check_capacity(INSPECT)
        form = await _read_form(request)
        options = _options(form, InspectRequest, required=False)
        request_id = options.request_id
        file = form.get("file")
        if not isinstance(file, UploadFile):
            raise ParserError("invalid_request")
        key = request_id or f"inspect-{uuid.uuid4().hex}"
        upload = _Upload(Path(state.settings.jobs_dir), key, file)
        await form.close()
        form = None
        payload, wait_ms = await _run(
            request, INSPECT, key, "inspect", upload.job(key), limits.INSPECT_DEADLINE_MS / 1000
        )
        result = InspectResult(
            contract=CONTRACT,
            request_id=request_id,
            **{k: payload[k] for k in _INSPECT_FIELDS},
            warnings=_mismatch(payload["format"], options.file_ext, options.media_type),
            timings=InspectTimings(
                total_ms=round((time.monotonic() - started) * 1000), wait_ms=wait_ms, inspect_ms=payload["inspect_ms"]
            ),
            build=state.build,
        )
        log.info(
            "inspect %s: %s, %s pages, refusal %s, %d ms",
            key,
            result.format,
            result.page_count if result.page_count is not None else "-",
            result.refusal or "-",
            result.timings.total_ms,
        )
        return JSONResponse(result.model_dump(mode="json"))
    except ParserError as exc:
        log.info("inspect %s: %s", request_id, exc.code)
        return error_response(exc, request_id, state.build)
    except ClientDisconnectedError:
        log.info("inspect %s: client disconnected, inspect process killed", request_id)
        return Response(status_code=499)
    finally:
        if form is not None:
            await form.close()
        if upload is not None:
            upload.close()


@router.post("/v1/ner")
async def ner(request: Request) -> Response:
    started = time.monotonic()
    state = request.app.state
    request_id: str | None = None
    try:
        body = await _read_body(request, NER_MAX_BODY_BYTES)
        try:
            ner_request = NerRequest.model_validate_json(body)
        except ValidationError:
            raise ParserError("invalid_request") from None
        request_id = ner_request.request_id
        if state.static_info.ner is None or not state.supervisor.ner_ready:
            raise ParserError("not_ready", retry_after_s=NOT_READY_RETRY_AFTER_S)
        job = {"request_id": request_id, **ner_request.model_dump(mode="json")}
        payload, wait_ms = await _run(request, NER, request_id, "ner", job, ner_request.deadline_ms / 1000)
        result = NerResult(
            contract=CONTRACT,
            request_id=request_id,
            entities=payload["entities"],
            model=state.static_info.ner,
            timings=NerTimings(
                total_ms=round((time.monotonic() - started) * 1000), wait_ms=wait_ms, ner_ms=payload["ner_ms"]
            ),
            build=state.build,
        )
        # Counts only: entity text and spans are never logged (processing design §10).
        log.info(
            "ner %s: %d texts, %d entities, %d ms",
            request_id,
            len(ner_request.texts),
            len(result.entities),
            result.timings.total_ms,
        )
        return JSONResponse(result.model_dump(mode="json"))
    except ParserError as exc:
        log.info("ner %s: %s", request_id, exc.code)
        return error_response(exc, request_id, state.build)
    except ClientDisconnectedError:
        log.info("ner %s: client disconnected, ner-0 killed", request_id)
        return Response(status_code=499)
