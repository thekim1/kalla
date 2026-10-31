"""Gold-set grading for the pilot's go-live gate (P1b item 16, hand-off L8; weakness review §2 "Hand grading",
ingestion plan §7). Standard library only, one file, so it can be copied to any host that reaches the chat API.

Usage:
  python3 -I scripts/grading/grading.py answer --questions FILE --base-url URL --assistant SLUG [--key-file FILE]
  python3 -I scripts/grading/grading.py totals GRADED.csv [--min-correct 0.80] [--min-citation-precision 0.90]
                                               [--max-false-not-found 0.10] [--min-off-topic-not-found 0.80]

answer  sends each `human` evaluation question, one new conversation each, through the chat API's
        POST /v1/chat/completions as the pilot assistant (model = the assistant's slug; chat-api design §3.2), and
        writes one CSV row per question: question, expected answer and documents, the delivered answer, its citations
        (url_citation annotations or Open WebUI source events, chat-api design §9.3), the detected outcome (answered,
        degraded, not-found, unavailable, error; chat-api design §10), latency, and empty grading columns.
totals  reads the CSV after the reviewers graded it and computes the four go-live measures against their thresholds
        (P1b README §1.1): correct answers on answerable questions >= 80 %, citation precision >= 90 %, false not-found
        <= 10 %, off-topic questions answered "not found" >= 80 %. Exit 0 when every measure passes, 1 when one fails,
        2 when the file is not completely or not consistently graded.

The API key (an integration key, ume-rag-sk-...) comes from --key-file or the UME_RAG_API_KEY environment variable,
never from an argument; it is never printed or written, and any server text that echoes it is redacted.
"""

import argparse
import csv
import http.client
import io
import json
import os
import ssl
import sys
import time
import urllib.parse
from dataclasses import dataclass, field
from datetime import UTC, datetime
from fractions import Fraction
from pathlib import Path

KEY_ENV = "UME_RAG_API_KEY"
DEFAULT_OUT = Path(__file__).resolve().parent / "results"
CONNECT_TIMEOUT_S = 10.0
MAX_ATTEMPTS = 4  # per question, for 429 and 503 (chat-api design §3.6 passes the gateway's Retry-After on)
MAX_RETRY_AFTER_S = 30.0
FATAL_STATUSES = (401, 403, 404)  # wrong key, client not allowed, or the slug is not an assistant this key may use

ANSWERABLE = "answerable"
OFF_TOPIC = "off-topic"  # a human question with no expected documents expects "not found" (data-model §2.2)

# Fixed texts the chat API delivers without a model call (chat-api design §10.1, §10.2). The not-found text is the
# assistant's own (Assistants.NotFound); these are the design's defaults, and --not-found-text adds the assistant's.
DEFAULT_NOT_FOUND_TEXTS = (
    "Jag hittar inget svar på det i de källor jag har tillgång till.",
    "I cannot find an answer to that in the sources I have access to.",
)
UNAVAILABLE_TEXTS = (
    "Sökningen är tillfälligt otillgänglig.",
    "Search is temporarily unavailable.",
)
DEGRADED_CAVEATS = (
    "Sökningen fungerar just nu med begränsad precision",
    "Search is running with reduced precision right now",
)

# The CSV handed to the reviewers (L8). Machine columns first, then the grading columns, which the script leaves empty.
MACHINE_COLUMNS = (
    "id",
    "kind",
    "question",
    "expected_answer",
    "expected_documents",
    "answer",
    "outcome",
    "citations",
    "citation_count",
    "latency_ms",
    "ttft_ms",
    "assistant",
    "asked_at",
    "request_id",
    "error",
)
GRADE_CORRECT = "grade_correct"
GRADE_NOT_FOUND = "grade_not_found"
GRADE_SUPPORTING = "grade_supporting_citations"
GRADE_COLUMNS = (GRADE_CORRECT, GRADE_NOT_FOUND, GRADE_SUPPORTING, "reviewer_note")
COLUMNS = MACHINE_COLUMNS + GRADE_COLUMNS

YES = {"ja", "j", "yes", "y", "x", "1", "true", "sant"}
NO = {"nej", "n", "no", "0", "false", "falskt"}
FORMULA_PREFIXES = ("=", "+", "-", "@", "\t", "\r")  # cells a spreadsheet would run as a formula


class RunAborted(Exception):
    """A configuration fault: every further question would fail the same way."""


class GradingInvalid(Exception):
    """The graded file is incomplete or contradicts itself."""


# --------------------------------------------------------------------------------------------------------------------
# Questions
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class Question:
    id: str
    question: str
    expected_answer: str
    expected_documents: list[str]

    @property
    def kind(self) -> str:
        return ANSWERABLE if self.expected_documents else OFF_TOPIC


def _first(record: dict, *names: str):
    for name in names:
        if name in record:
            return record[name]
    return None


def _document_label(value) -> str:
    if isinstance(value, dict):
        parts = [str(value[k]) for k in ("title", "url", "id") if value.get(k)]
        return " | ".join(parts)
    return str(value).strip()


def _records(data) -> list[dict]:
    """A JSON list of questions, one admin keyset page `{ items, next }` (admin design §4.1), or a list of pages."""
    if isinstance(data, dict) and isinstance(data.get("items"), list):
        return data["items"]
    if isinstance(data, list):
        if data and all(isinstance(page, dict) and isinstance(page.get("items"), list) for page in data):
            return [item for page in data for item in page["items"]]
        return data
    raise SystemExit("--questions JSON must be a list of questions, an admin page { items, next } or a list of pages")


def sniff_delimiter(text: str) -> str:
    header = text.splitlines()[0] if text else ""
    return max((";", ",", "\t"), key=header.count)


def load_questions(path: Path) -> tuple[list[Question], int]:
    """Reads the human questions; returns them and how many records were skipped (other layers, retired)."""
    text = path.read_text(encoding="utf-8-sig")
    if path.suffix.lower() == ".csv":
        records = list(csv.DictReader(io.StringIO(text), delimiter=sniff_delimiter(text)))
    else:
        records = _records(json.loads(text))
    questions: list[Question] = []
    skipped = 0
    seen: set[str] = set()
    for number, record in enumerate(records, 1):
        if not isinstance(record, dict):
            raise SystemExit(f"--questions record {number} is not an object")
        layer = _first(record, "layer")
        state = _first(record, "state")
        if (layer and layer != "human") or state == "retired" or _first(record, "retiredAt", "retired_at"):
            skipped += 1
            continue
        question = str(_first(record, "question", "text") or "").strip()
        if not question:
            raise SystemExit(f"--questions record {number} has no question text")
        documents = _first(
            record, "expectedDocuments", "expected_documents", "expectedDocumentIds", "expected_document_ids"
        )
        if documents is None:
            raise SystemExit(
                f"--questions record {number} has no expected documents field: an empty list means 'not found' is "
                "expected, a missing field cannot be told apart from an export without them"
            )
        if isinstance(documents, str):
            documents = [d for d in documents.replace("|", "\n").splitlines() if d.strip()]
        identifier = str(_first(record, "id", "questionId", "question_id") or f"q{number:03d}").strip()
        if identifier in seen:
            raise SystemExit(f"--questions has id '{identifier}' twice")
        seen.add(identifier)
        questions.append(
            Question(
                identifier,
                question,
                str(_first(record, "expectedAnswer", "expected_answer") or "").strip(),
                [label for label in (_document_label(d) for d in documents) if label],
            )
        )
    if not questions:
        raise SystemExit(f"{path} holds no human questions")
    return questions, skipped


# --------------------------------------------------------------------------------------------------------------------
# Chat API client
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class Citation:
    url: str
    title: str


@dataclass
class Answer:
    content: str = ""
    citations: list[Citation] = field(default_factory=list)
    usage: dict | None = None
    status: int | None = None
    request_id: str = ""
    error: str = ""
    latency_ms: int | None = None
    ttft_ms: int | None = None

    def cite(self, url: str | None, title: str | None) -> None:
        """Citations in display order: the first citation of a document is [1] (chat-api design §9.2); annotations
        come once per occurrence, so later occurrences of the same link are dropped."""
        if url and all(c.url != url for c in self.citations):
            self.citations.append(Citation(url, (title or "").strip()))


def _annotations(annotations) -> list[tuple[str | None, str | None]]:
    found = []
    for annotation in annotations or []:
        if not isinstance(annotation, dict) or annotation.get("type") != "url_citation":
            continue
        detail = annotation.get("url_citation") or annotation
        found.append((detail.get("url"), detail.get("title")))
    return found


def _source_event(event: dict) -> tuple[str | None, str | None]:
    """An Open WebUI `source` event (chat-api design §9.1, §9.3): link in source.url or metadata[].source."""
    data = event.get("data") or {}
    source = data.get("source") or {}
    metadata = (data.get("metadata") or [{}])[0] or {}
    return source.get("url") or metadata.get("source") or source.get("id"), metadata.get("name") or source.get("name")


class ChatClient:
    def __init__(self, base_url: str, key: str, assistant: str, stream: bool, timeout_s: float, ca_file: str | None):
        url = urllib.parse.urlsplit(base_url.rstrip("/").removesuffix("/v1"))
        if url.scheme not in ("http", "https") or not url.hostname:
            raise SystemExit(f"--base-url must be http(s)://host[:port][/path], got '{base_url}'")
        self._url = url
        self._path = url.path.rstrip("/") + "/v1/chat/completions"
        self._key = key
        self.assistant = assistant
        self.stream = stream
        self._timeout_s = timeout_s
        self._context = ssl.create_default_context(cafile=ca_file) if url.scheme == "https" else None

    def redact(self, text: str) -> str:
        return text.replace(self._key, "***") if self._key else text

    def _connection(self) -> http.client.HTTPConnection:
        if self._context is not None:
            connection = http.client.HTTPSConnection(
                self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S, context=self._context
            )
        else:
            connection = http.client.HTTPConnection(self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S)
        connection.connect()
        connection.sock.settimeout(self._timeout_s)
        return connection

    def ask(self, question: str) -> Answer:
        """One question as a new conversation; 429 and 503 are retried after Retry-After."""
        payload = {"model": self.assistant, "messages": [{"role": "user", "content": question}], "stream": self.stream}
        if self.stream:
            payload["stream_options"] = {"include_usage": True}
        body = json.dumps(payload, ensure_ascii=False).encode()
        headers = {
            "Authorization": f"Bearer {self._key}",
            "Content-Type": "application/json",
            "Accept": "text/event-stream" if self.stream else "application/json",
        }
        for attempt in range(1, MAX_ATTEMPTS + 1):
            answer, wait_s = self._attempt(body, headers)
            if wait_s is None or attempt == MAX_ATTEMPTS:
                return answer
            time.sleep(wait_s)
        raise AssertionError("unreachable")

    def _attempt(self, body: bytes, headers: dict[str, str]) -> tuple[Answer, float | None]:
        answer = Answer()
        try:
            connection = self._connection()
        except ConnectionRefusedError as exc:
            raise RunAborted(f"{self._url.geturl()} refused the connection") from exc
        except OSError as exc:
            answer.error = f"connect: {type(exc).__name__}"
            return answer, None
        try:
            start = time.perf_counter()
            connection.request("POST", self._path, body=body, headers=headers)
            raw = connection.getresponse()
            answer.status = raw.status
            answer.request_id = raw.getheader("x-request-id") or ""
            if raw.status != 200:
                return self._failed(raw, answer)
            content_type = (raw.getheader("content-type") or "").lower()
            if content_type.startswith("text/event-stream"):
                self._read_stream(raw, answer, start)
            else:
                self._read_json(raw.read(), answer)
            answer.latency_ms = round((time.perf_counter() - start) * 1000)
        except TimeoutError:
            answer.error = "timeout"
        except (OSError, http.client.HTTPException) as exc:
            answer.error = f"connection: {type(exc).__name__}"
        except ValueError as exc:
            answer.error = f"malformed response: {self.redact(str(exc))[:200]}"
        finally:
            connection.close()
        return answer, None

    def _failed(self, raw: http.client.HTTPResponse, answer: Answer) -> tuple[Answer, float | None]:
        data = raw.read()
        try:
            error = json.loads(data).get("error") or {}
            detail = f"{error.get('code')}: {error.get('message')}"
        except (ValueError, AttributeError):
            detail = data[:200].decode(errors="replace")
        answer.error = self.redact(f"http {raw.status} {detail}")
        if raw.status in FATAL_STATUSES:
            raise RunAborted(f"{answer.error} (check the key, its client and --assistant)")
        if raw.status in (429, 503):
            try:
                wait_s = float(raw.getheader("retry-after") or "2")
            except ValueError:
                wait_s = 2.0
            return answer, min(MAX_RETRY_AFTER_S, max(0.0, wait_s))
        return answer, None

    def _read_json(self, data: bytes, answer: Answer) -> None:
        parsed = json.loads(data)
        message = ((parsed.get("choices") or [{}])[0] or {}).get("message") or {}
        answer.content = message.get("content") or ""
        answer.usage = parsed.get("usage")
        for url, title in _annotations(message.get("annotations")):
            answer.cite(url, title)

    def _read_stream(self, raw: http.client.HTTPResponse, answer: Answer, start: float) -> None:
        parts: list[str] = []
        lines: list[str] = []
        done = False
        while True:
            line = raw.readline()
            if not line or line in (b"\n", b"\r\n"):
                # End of one SSE event (a blank line) or of the stream.
                if lines:
                    data = "\n".join(lines)
                    lines = []
                    if data == "[DONE]":
                        done = True
                        break
                    self._stream_event(json.loads(data), answer, parts, start)
                if not line:
                    break
                continue
            text = line.decode("utf-8").rstrip("\r\n")
            if text.startswith("data:"):
                lines.append(text[5:].removeprefix(" "))
        answer.content = "".join(parts)
        if not done and not answer.error:
            answer.error = "stream ended without [DONE]"

    def _stream_event(self, event: dict, answer: Answer, parts: list[str], start: float) -> None:
        if isinstance(event.get("error"), dict) and not event.get("choices"):
            # An error after the first byte (chat-api design §9.6); the partial answer is kept.
            error = event["error"]
            answer.error = self.redact(f"stream {error.get('code')}: {error.get('message')}")
            return
        if isinstance(event.get("event"), dict) and event["event"].get("type") == "source":
            answer.cite(*_source_event(event["event"]))
        if event.get("usage"):
            answer.usage = event["usage"]
        for choice in event.get("choices") or []:
            delta = choice.get("delta") or {}
            if delta.get("content"):
                if answer.ttft_ms is None:
                    answer.ttft_ms = round((time.perf_counter() - start) * 1000)
                parts.append(delta["content"])
            for url, title in _annotations(delta.get("annotations")):
                answer.cite(url, title)


def _normalised(text: str) -> str:
    return " ".join(text.lstrip("*_ \t\r\n").split()).casefold()


def outcome(answer: Answer, not_found_texts: list[str]) -> str:
    """The delivered outcome as the chat API decided it (chat-api design §6.2, §10). The API carries no coverage field:
    a fixed text is recognised by its words, or as an answer without citations and without a model call (usage 0)."""
    if answer.error:
        return "error"
    head = _normalised(answer.content)
    if any(head.startswith(_normalised(t)) for t in UNAVAILABLE_TEXTS):
        return "unavailable"
    if any(head.startswith(_normalised(t)) for t in not_found_texts):
        return "not-found"
    if not answer.citations and answer.usage is not None and answer.usage.get("completion_tokens") == 0:
        return "not-found"
    if any(head.startswith(_normalised(t)) for t in DEGRADED_CAVEATS):
        return "degraded"
    return "answered"


# --------------------------------------------------------------------------------------------------------------------
# answer: questions -> CSV for the reviewers
# --------------------------------------------------------------------------------------------------------------------


def cell(value) -> str:
    """Text as a spreadsheet shows it, never as a formula (an answer or question starting with '=' or '-')."""
    text = "" if value is None else str(value)
    return "'" + text if text.startswith(FORMULA_PREFIXES) else text


def row_for(question: Question, answer: Answer, client: ChatClient, asked_at: datetime, kind: str) -> dict:
    citations = "\n".join(f"[{n}] {c.title or '(no title)'} - {c.url}" for n, c in enumerate(answer.citations, 1))
    row = {
        "id": cell(question.id),
        "kind": question.kind,
        "question": cell(question.question),
        "expected_answer": cell(question.expected_answer),
        "expected_documents": cell("\n".join(question.expected_documents)),
        "answer": cell(client.redact(answer.content)),
        "outcome": kind,
        "citations": cell(client.redact(citations)),
        "citation_count": len(answer.citations),
        "latency_ms": "" if answer.latency_ms is None else answer.latency_ms,
        "ttft_ms": "" if answer.ttft_ms is None else answer.ttft_ms,
        "assistant": cell(client.assistant),
        "asked_at": asked_at.isoformat(timespec="seconds"),
        "request_id": cell(answer.request_id),
        "error": cell(answer.error),
    }
    return row | dict.fromkeys(GRADE_COLUMNS, "")


def cmd_answer(args: argparse.Namespace, key: str) -> int:
    questions, skipped = load_questions(args.questions)
    client = ChatClient(args.base_url, key, args.assistant, not args.no_stream, args.request_timeout, args.ca_file)
    not_found_texts = [*DEFAULT_NOT_FOUND_TEXTS, *args.not_found_text]
    started = datetime.now(UTC)
    out = args.out or DEFAULT_OUT / f"{started:%Y%m%dT%H%M%SZ}-{args.assistant}.csv"
    out.parent.mkdir(parents=True, exist_ok=True)
    answerable = sum(q.kind == ANSWERABLE for q in questions)
    print(
        f"answer: {len(questions)} human questions ({answerable} answerable, {len(questions) - answerable} off-topic; "
        f"{skipped} other or retired skipped) to {args.base_url} as '{args.assistant}', "
        f"{'streamed' if client.stream else 'not streamed'}",
        file=sys.stderr,
        flush=True,
    )
    counts: dict[str, int] = {}
    aborted: RunAborted | None = None
    with out.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=COLUMNS, delimiter=args.delimiter)
        writer.writeheader()
        for number, question in enumerate(questions, 1):
            asked_at = datetime.now(UTC)
            try:
                answer = client.ask(question.question)
            except RunAborted as exc:
                aborted = exc
                break
            kind = outcome(answer, not_found_texts)
            counts[kind] = counts.get(kind, 0) + 1
            writer.writerow(row_for(question, answer, client, asked_at, kind))
            handle.flush()
            print(
                f"  [{number}/{len(questions)}] {question.id}: {kind}, {len(answer.citations)} citations, "
                f"{answer.latency_ms if answer.latency_ms is not None else '-'} ms"
                f"{' (' + answer.error + ')' if answer.error else ''}",
                file=sys.stderr,
                flush=True,
            )
    summary = ", ".join(f"{k} {v}" for k, v in sorted(counts.items())) or "nothing asked"
    if aborted is not None:
        print(f"ABORTED: {aborted}; {summary}; partial file {out}", file=sys.stderr)
        return 2
    print(f"{len(questions)} questions: {summary}\n  -> {out}")
    return 0


# --------------------------------------------------------------------------------------------------------------------
# totals: graded CSV -> the four go-live measures
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class Measure:
    name: str
    numerator: int
    denominator: int
    threshold: Fraction
    at_most: bool  # False: value >= threshold passes; True: value <= threshold passes

    @property
    def passed(self) -> bool:
        if self.denominator == 0:
            return False  # nothing to measure is not a pass
        value = Fraction(self.numerator, self.denominator)
        return value <= self.threshold if self.at_most else value >= self.threshold

    def line(self) -> str:
        value = f"{100 * self.numerator / self.denominator:5.1f} %" if self.denominator else "    - "
        ratio = f"{self.numerator}/{self.denominator}"
        op = "<=" if self.at_most else ">="
        verdict = "PASS" if self.passed else ("FAIL (no rows to measure)" if not self.denominator else "FAIL")
        return f"  {self.name:<44} {ratio:>9} = {value}  {op} {float(self.threshold) * 100:g} %  {verdict}"


def _yes_no(row: dict, column: str, row_id: str, problems: list[str]) -> bool | None:
    value = (row.get(column) or "").strip().casefold()
    if value in YES:
        return True
    if value in NO:
        return False
    problems.append(f"{row_id}: {column} {'is empty' if not value else repr(row.get(column)) + ' is not ja/nej'}")
    return None


def _strip_cell(value: str | None) -> str:
    text = (value or "").strip()
    return text[1:] if text.startswith("'") and text[1:2] in FORMULA_PREFIXES else text


def grade(path: Path, thresholds: dict[str, Fraction]) -> tuple[list[Measure], list[str], int, int]:
    """Returns the four measures, the notes, and the answerable and off-topic counts; GradingInvalid lists every row
    that is not graded or contradicts itself."""
    text = path.read_text(encoding="utf-8-sig")
    reader = csv.DictReader(io.StringIO(text), delimiter=sniff_delimiter(text))
    required = {"id", "kind", "citation_count", "outcome", GRADE_CORRECT, GRADE_NOT_FOUND, GRADE_SUPPORTING}
    missing = required - set(reader.fieldnames or [])
    if missing:
        raise GradingInvalid(f"{path} lacks the columns {', '.join(sorted(missing))}")
    problems: list[str] = []
    answerable = correct = false_not_found = off_topic = off_topic_not_found = 0
    cited = supporting = disagree = errors = 0
    for number, row in enumerate(reader, 2):  # line 1 is the header
        row_id = _strip_cell(row.get("id")) or f"line {number}"
        kind = (row.get("kind") or "").strip()
        if kind not in (ANSWERABLE, OFF_TOPIC):
            problems.append(f"{row_id}: kind {kind!r} is neither {ANSWERABLE} nor {OFF_TOPIC}")
            continue
        not_found = _yes_no(row, GRADE_NOT_FOUND, row_id, problems)
        if kind == ANSWERABLE:
            answerable += 1
            is_correct = _yes_no(row, GRADE_CORRECT, row_id, problems)
            if is_correct and not_found:
                problems.append(f"{row_id}: graded both correct and not found")
            correct += bool(is_correct)
            false_not_found += bool(not_found)
        else:
            off_topic += 1
            off_topic_not_found += bool(not_found)
        try:
            count = int((row.get("citation_count") or "0").strip())
        except ValueError:
            problems.append(f"{row_id}: citation_count {row.get('citation_count')!r} is not a number")
            continue
        raw = (row.get(GRADE_SUPPORTING) or "").strip()
        if count == 0 and raw in ("", "0"):
            pass
        elif not raw:
            problems.append(f"{row_id}: {GRADE_SUPPORTING} is empty for {count} citations")
        elif not raw.isdigit() or int(raw) > count:
            problems.append(f"{row_id}: {GRADE_SUPPORTING} {raw!r} is not a whole number from 0 to {count}")
        else:
            cited += count
            supporting += int(raw)
        detected = (row.get("outcome") or "").strip()
        if not_found is not None and (detected == "not-found") != not_found:
            disagree += 1
        errors += detected == "error"
    if problems:
        raise GradingInvalid("\n".join(f"  {p}" for p in problems))
    measures = [
        Measure("correct answers on answerable questions", correct, answerable, thresholds["correct"], False),
        Measure("citation precision", supporting, cited, thresholds["citation_precision"], False),
        Measure("false not-found", false_not_found, answerable, thresholds["false_not_found"], True),
        Measure(
            'off-topic questions answered "not found"', off_topic_not_found, off_topic, thresholds["off_topic"], False
        ),
    ]
    notes = []
    if disagree:
        notes.append(f"{disagree} rows where grade_not_found differs from the detected outcome (the grade counts)")
    if errors:
        notes.append(f"{errors} rows had a request error (graded as delivered; re-ask them before the gate)")
    return measures, notes, answerable, off_topic


def cmd_totals(args: argparse.Namespace) -> int:
    thresholds = {
        "correct": args.min_correct,
        "citation_precision": args.min_citation_precision,
        "false_not_found": args.max_false_not_found,
        "off_topic": args.min_off_topic_not_found,
    }
    try:
        measures, notes, answerable, off_topic = grade(args.graded, thresholds)
    except GradingInvalid as exc:
        print(f"NOT GRADED: {args.graded}\n{exc}", file=sys.stderr)
        return 2
    passed = all(m.passed for m in measures)
    lines = [
        f"Go-live gate totals: {args.graded.name} ({answerable + off_topic} questions: {answerable} answerable, "
        f"{off_topic} off-topic)",
        *(m.line() for m in measures),
        *(f"  note: {n}" for n in notes),
        f"Gate: {'PASS' if passed else 'FAIL'} (thresholds of P1b README §1.1, weakness review §2)",
    ]
    summary = "\n".join(lines) + "\n"
    print(summary, end="")
    if args.out:
        args.out.write_text(summary, encoding="utf-8")
    return 0 if passed else 1


# --------------------------------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------------------------------


def share(value: str) -> Fraction:
    """A threshold as a share from 0 to 1 ('0.8'), kept exact so 16/20 meets 0.8."""
    try:
        result = Fraction(value)
    except (ValueError, ZeroDivisionError) as exc:
        raise argparse.ArgumentTypeError(f"'{value}' is not a share from 0 to 1") from exc
    if not 0 <= result <= 1:
        raise argparse.ArgumentTypeError(f"'{value}' is not a share from 0 to 1")
    return result


def delimiter(value: str) -> str:
    value = "\t" if value in ("tab", "\\t") else value
    if value not in (";", ",", "\t"):
        raise argparse.ArgumentTypeError("the delimiter is ';', ',' or 'tab'")
    return value


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0], formatter_class=argparse.RawTextHelpFormatter
    )
    commands = parser.add_subparsers(dest="command", required=True)

    answer = commands.add_parser("answer", help="ask every human question and write the CSV for the reviewers")
    answer.add_argument("--questions", required=True, type=Path, help="JSON (admin export or list) or CSV")
    answer.add_argument("--base-url", required=True, help="the chat API (the api service), without /v1")
    answer.add_argument("--assistant", required=True, help="the pilot assistant's slug (the model sent)")
    answer.add_argument(
        "--key-file",
        type=Path,
        help=f"file holding the integration key (ume-rag-sk-...); else ${KEY_ENV}; never printed",
    )
    answer.add_argument(
        "--not-found-text",
        action="append",
        default=[],
        help="the assistant's not-found text (Assistants.NotFound sv/en); repeatable; the design defaults are known",
    )
    answer.add_argument("--no-stream", action="store_true", help="ask without streaming (default: streamed)")
    answer.add_argument("--out", type=Path, help="CSV path (default scripts/grading/results/<UTC time>-<slug>.csv)")
    answer.add_argument(
        "--delimiter", type=delimiter, default=";", help="';' (default, Swedish spreadsheets), ',' or 'tab'"
    )
    answer.add_argument("--request-timeout", type=float, default=120, help="socket timeout per request, seconds")
    answer.add_argument("--ca-file", help="CA bundle for https behind a private CA or the dev certificate")

    totals = commands.add_parser("totals", help="compute the four go-live measures from a graded CSV")
    totals.add_argument("graded", type=Path, help="the CSV after grading (';', ',' or tab separated)")
    totals.add_argument("--min-correct", type=share, default=Fraction("0.80"))
    totals.add_argument("--min-citation-precision", type=share, default=Fraction("0.90"))
    totals.add_argument("--max-false-not-found", type=share, default=Fraction("0.10"))
    totals.add_argument("--min-off-topic-not-found", type=share, default=Fraction("0.80"))
    totals.add_argument("--out", type=Path, help="also write the totals to this file (for the go-live decision)")
    return parser.parse_args(argv)


def read_key(key_file: Path | None) -> str:
    if key_file is not None:
        key = key_file.read_text(encoding="utf-8").strip()
        source = str(key_file)
    else:
        key = os.environ.get(KEY_ENV, "").strip()
        source = f"${KEY_ENV}"
    if not key:
        raise SystemExit(f"no API key: {source} is empty or unset (use --key-file FILE or ${KEY_ENV})")
    return key


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.command == "totals":
        return cmd_totals(args)
    return cmd_answer(args, read_key(args.key_file))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
