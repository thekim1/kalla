"""`POST /v1/convert` and `POST /v1/inspect` (parser design §7.1 to §7.5).

The API process reads the multipart request (at most 60 MB and two parts: `file` and `options`), validates the
options, sniffs the first bytes and writes the file into a job directory. It parses nothing itself: the slot opens
the document. The response is `multipart/mixed`: `result.json`, then `docling.json.gz` when Docling ran and
`outputs.docling_json` is set. Errors are `ErrorResponse` JSON with a fixed `detail` per code.

`/v1/inspect` is P1 (design §16); P0 has only its schema, and the route answers `501 not_implemented`.
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
from pydantic import ValidationError
from starlette.background import BackgroundTask
from starlette.datastructures import FormData, UploadFile
from starlette.formparsers import MultiPartException
from starlette.types import Message

from ume_parser import limits
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
    Timings,
)
from ume_parser.sniff import HEAD_BYTES, Sniffed, sniff
from ume_parser.supervisor import ClientDisconnectedError

log = logging.getLogger(__name__)

OPTIONS_MAX_BYTES = 64 * 1024
CHUNK = 1024 * 1024
DOCLING_JSON = "docling.json.gz"  # written by the slot (convert.DOCLING_JSON); the API process imports no Docling

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


async def _read_form(request: Request) -> FormData:
    """The multipart body, refused as `too_large` once more than the limit has arrived (chunked bodies too)."""
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


def _options(form: FormData) -> ConvertRequest:
    raw = form.get("options")
    if not isinstance(raw, str):
        raise ParserError("invalid_request")
    try:
        return ConvertRequest.model_validate_json(raw)
    except ValidationError:
        raise ParserError("invalid_request") from None


def _spool(upload: UploadFile, target: Path) -> tuple[bytes, str, int]:
    """Copies the upload to `target`; returns its first bytes, SHA-256 and size."""
    digest, size, head = hashlib.sha256(), 0, b""
    upload.file.seek(0)
    with target.open("wb") as out:
        while chunk := upload.file.read(CHUNK):
            if len(head) < HEAD_BYTES:
                head += chunk[: HEAD_BYTES - len(head)]
            digest.update(chunk)
            size += len(chunk)
            out.write(chunk)
    return head, digest.hexdigest(), size


def _mismatch(options: ConvertRequest, sniffed: Sniffed) -> list[ConvertWarning]:
    declared_ext = options.file_ext.removeprefix(".")
    extensions = {"jpeg": {"jpeg", "jpg"}, "tiff": {"tiff", "tif"}}.get(sniffed.format, {sniffed.format})
    mismatch = (options.media_type is not None and options.media_type != sniffed.media_type) or (
        declared_ext not in extensions
    )
    return [ConvertWarning(code="media_type_mismatch", page=None)] if mismatch else []


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


@router.post("/v1/convert")
async def convert(request: Request) -> Response:
    started = time.monotonic()
    state = request.app.state
    supervisor = state.supervisor
    request_id: str | None = None
    job_dir: Path | None = None
    form: FormData | None = None
    try:
        supervisor.check_capacity()
        length = request.headers.get("content-length", "")
        if length.isdigit() and int(length) > limits.MAX_BODY_BYTES:
            raise ParserError("too_large")
        form = await _read_form(request)
        options = _options(form)
        request_id = options.request_id
        upload = form.get("file")
        if not isinstance(upload, UploadFile):
            raise ParserError("invalid_request")
        if options.pages is not None and options.pages.count > limits.MAX_PAGES_PER_REQUEST:
            raise ParserError("too_many_pages", retry="smaller_range")

        job_dir = Path(state.settings.jobs_dir) / f"{request_id}-{uuid.uuid4().hex[:8]}"
        job_dir.mkdir(parents=True)
        input_path = job_dir / "input"
        head, sha256, size = _spool(upload, input_path)
        await form.close()
        form = None
        sniffed = sniff(head)

        queued = time.monotonic()
        reserved = supervisor.reserve(request_id)
        try:
            job: dict[str, Any] = {
                "input": str(input_path),
                "dir": str(job_dir),
                "options": options.model_dump(mode="json", by_alias=True),
            }
            wait_ms = round((time.monotonic() - queued) * 1000)
            payload = await supervisor.convert(reserved, job, options.deadline_ms / 1000, request.is_disconnected)
        finally:
            supervisor.release(reserved, request_id)

        result = ConvertResult(
            contract=CONTRACT,
            request_id=request_id,
            status=payload["status"],
            input=ConvertInput(
                format=sniffed.format,
                media_type_sniffed=sniffed.media_type,
                sha256=sha256,
                bytes=size,
                page_count=payload["page_count"],
                pages_requested=payload["pages_requested"],
                pages_completed=payload["pages_completed"],
                pipeline=payload["pipeline"],
            ),
            pages=payload["pages"],
            blocks=payload["blocks"],
            tables=payload["tables"],
            pictures=payload["pictures"],
            warnings=[*_mismatch(options, sniffed), *payload["warnings"]],
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
        log.info(
            "convert %s: %s/%s, %d bytes, pages %d-%d of %d, %d ms",
            request_id,
            result.status,
            result.input.pipeline,
            size,
            result.input.pages_completed.from_ if result.input.pages_completed else 0,
            result.input.pages_completed.to if result.input.pages_completed else 0,
            result.input.page_count or 0,
            result.timings.total_ms,
        )
        boundary = secrets.token_hex(16)
        docling = job_dir / DOCLING_JSON if payload["docling_json"] else None
        response = StreamingResponse(
            _multipart(result.model_dump_json().encode("utf-8"), docling, boundary),
            media_type=f"multipart/mixed; boundary={boundary}",
            background=BackgroundTask(shutil.rmtree, job_dir, ignore_errors=True),
        )
        job_dir = None  # removed by the response's background task once it is sent
        return response
    except ParserError as exc:
        log.info("convert %s: %s", request_id, exc.code)
        return error_response(exc, request_id, state.build)
    except ClientDisconnectedError:
        log.info("convert %s: client disconnected, slot killed", request_id)
        return Response(status_code=499)
    finally:
        if form is not None:
            await form.close()
        if job_dir is not None:
            shutil.rmtree(job_dir, ignore_errors=True)


@router.post("/v1/inspect")
async def inspect(request: Request) -> JSONResponse:
    return error_response(ParserError("not_implemented"), None, request.app.state.build)
