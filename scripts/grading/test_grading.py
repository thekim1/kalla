"""Tests for grading.py against an in-process fake chat API (streamed and not, citations, not-found, errors) and for
the totals on the hand-made graded sample in tests/fixtures/grading/. Standard library only:

    python3 -I -m unittest discover -s scripts/grading -v
"""

import contextlib
import csv
import io
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import ClassVar

sys.path.insert(0, str(Path(__file__).resolve().parent))
import grading

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures" / "grading"
SCRIPT = Path(__file__).resolve().parent / "grading.py"
KEY = "ume-rag-sk-test-6f1c0e9d2b7a4c58a1e3"
SLUG = "bygglov"
ALTAN = "https://www.umea.se/bygga/bygglov/altan"
AVGIFT = "https://www.umea.se/bygga/avgifter"
NOT_FOUND = grading.DEFAULT_NOT_FOUND_TEXTS[0] + "\n\n- [Kontaktcenter](https://www.umea.se/kontakt)"
CUSTOM_NOT_FOUND = "Det vet jag tyvärr inte. Ring bygglovsjouren."
UNAVAILABLE = "Sökningen är tillfälligt otillgänglig. Försök igen om en stund."

QUESTIONS = [
    {"id": "q-altan", "question": "Behöver jag bygglov för en altan?", "expectedDocuments": ["Altan"]},
    {"id": "q-carport", "question": "Bygglov för carport?", "expectedDocuments": ["Carport"]},
    {"id": "q-vader", "question": "Vad blir det för väder?", "expectedDocuments": []},
    {"id": "q-egen", "question": "En egen fråga utan svar", "expectedDocuments": []},
    {"id": "q-fel", "question": "Ger fel hos servern", "expectedDocuments": ["X"]},
    {"id": "q-trangsel", "question": "Trängsel först", "expectedDocuments": ["Altan"]},
    {"id": "q-avbrott", "question": "Avbrott i strömmen", "expectedDocuments": ["Altan"]},
    {"id": "q-sokning", "question": "Sökning nere", "expectedDocuments": ["Altan"]},
    {"id": "q-formel", "question": '=HYPERLINK("https://evil")', "expectedDocuments": []},
]


def annotation(url: str, title: str, start: int) -> dict:
    return {
        "type": "url_citation",
        "url_citation": {"url": url, "title": title, "start_index": start, "end_index": start + 3},
    }


def chunk(delta: dict, finish: str | None = None) -> dict:
    return {
        "id": "chatcmpl-1",
        "object": "chat.completion.chunk",
        "created": 1764000123,
        "model": SLUG,
        "choices": [{"index": 0, "delta": delta, "finish_reason": finish}],
    }


def source_event(url: str, name: str) -> dict:
    return {
        "event": {
            "type": "source",
            "data": {
                "source": {"id": url, "name": name, "url": url},
                "document": ["…"],
                "metadata": [{"source": url, "name": name, "external": True}],
            },
        }
    }


class FakeChatApi(BaseHTTPRequestHandler):
    """POST /v1/chat/completions as chat-api design §3, §9 and §10 describe it, keyed by the question's words."""

    requests: ClassVar[list[dict]] = []
    seen_crowded: ClassVar[bool] = False

    def log_message(self, *args) -> None:
        pass

    def _json(self, status: int, payload: dict, headers: dict | None = None) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("x-request-id", f"req-{len(self.requests)}")
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    def _error(self, status: int, code: str, message: str, headers: dict | None = None) -> None:
        self._json(status, {"error": {"message": message, "type": "api_error", "code": code}}, headers)

    def _sse(self, events: list, done: bool = True) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("x-request-id", f"req-{len(self.requests)}")
        self.end_headers()
        for event in events:
            self.wfile.write(f"data: {json.dumps(event, ensure_ascii=False)}\n\n".encode())
            self.wfile.flush()
        if done:
            self.wfile.write(b"data: [DONE]\n\n")

    def _answer(self, stream: bool, parts: list[tuple[str, list[dict]]], usage: dict | None, sources=None) -> None:
        content = "".join(text for text, _ in parts)
        if not stream:
            message = {"role": "assistant", "content": content, "annotations": [a for _, ann in parts for a in ann]}
            payload = {
                "id": "chatcmpl-1",
                "object": "chat.completion",
                "model": SLUG,
                "choices": [{"index": 0, "finish_reason": "stop", "message": message}],
            }
            if usage is not None:
                payload["usage"] = usage
            self._json(200, payload)
            return
        events = [chunk({"role": "assistant", "content": ""})]
        for index, (text, annotations) in enumerate(parts):
            if sources and index < len(sources):
                events.append(source_event(*sources[index]))
                annotations = []
            delta = {"content": text}
            if annotations:
                delta["annotations"] = annotations
            events.append(chunk(delta))
        events.append(chunk({}, "stop"))
        if usage is not None:
            events.append({"id": "chatcmpl-1", "object": "chat.completion.chunk", "choices": [], "usage": usage})
        self._sse(events)

    def do_POST(self) -> None:
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        FakeChatApi.requests.append({"path": self.path, "body": body, "auth": self.headers.get("Authorization")})
        if self.path != "/v1/chat/completions":
            self._error(404, "not_found", "no route")
            return
        if self.headers.get("Authorization") != f"Bearer {KEY}":
            self._error(401, "invalid_key", "invalid key")
            return
        if body.get("model") != SLUG:
            self._error(404, "model_not_found", "unknown model")
            return
        question = body["messages"][-1]["content"]
        stream = bool(body.get("stream"))
        used = {"prompt_tokens": 5120, "completion_tokens": 42, "total_tokens": 5162}
        zero = {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0}
        if "altan" in question:
            self._answer(
                stream,
                [
                    ("Du behöver bygglov över 1,8 m [1].", [annotation(ALTAN, "Altan och uteplats", 31)]),
                    (" Avgiften följer taxan [2]", [annotation(AVGIFT, "Bygglovsavgifter", 22)]),
                    (" och gäller i hela kommunen [1].", [annotation(ALTAN, "Altan och uteplats", 28)]),
                ],
                used,
            )
        elif "carport" in question:
            self._answer(
                stream,
                [("Carport kräver bygglov [1].", [annotation(ALTAN, "Carport", 23)])],
                used,
                sources=[(ALTAN, "Carport - När behövs bygglov?")],
            )
        elif "väder" in question or question.startswith("="):
            self._answer(stream, [(NOT_FOUND, [])], zero)
        elif "egen" in question:
            self._answer(stream, [(CUSTOM_NOT_FOUND, [])], None)
        elif "fel" in question:
            # A server that echoes the credential: the script must never write or print it.
            self._error(502, "upstream_error", f"upstream failed for {self.headers.get('Authorization')}")
        elif "Trängsel" in question:
            if not FakeChatApi.seen_crowded:
                FakeChatApi.seen_crowded = True
                self._error(429, "rate_limited", "slow down", {"Retry-After": "0"})
            else:
                self._answer(stream, [("Svar efter väntan [1].", [annotation(ALTAN, "Altan", 18)])], used)
        elif "Avbrott" in question:
            if stream:
                self._sse(
                    [
                        chunk({"role": "assistant", "content": ""}),
                        chunk({"content": "Ett halvt svar"}),
                        {"error": {"message": "interrupted", "type": "api_error", "code": "stream_interrupted"}},
                    ]
                )
            else:
                self._error(502, "upstream_error", "broken")
        elif "Sökning" in question:
            self._answer(stream, [(UNAVAILABLE, [])], zero)
        else:
            self._error(400, "invalid_request", "unexpected")


class FakeServer:
    def __enter__(self) -> str:
        FakeChatApi.requests = []
        FakeChatApi.seen_crowded = False
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeChatApi)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        return f"http://127.0.0.1:{self.server.server_port}"

    def __exit__(self, *exc) -> None:
        self.server.shutdown()
        self.server.server_close()


def read_csv(path: Path) -> dict[str, dict]:
    text = path.read_text(encoding="utf-8-sig")
    return {row["id"]: row for row in csv.DictReader(io.StringIO(text), delimiter=";")}


def run_main(argv: list[str]) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = grading.main(argv)
    return code, out.getvalue(), err.getvalue()


class AnswerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp())
        self.questions = self.dir / "questions.json"
        self.questions.write_text(json.dumps(QUESTIONS, ensure_ascii=False), encoding="utf-8")
        self.key_file = self.dir / "key"
        self.key_file.write_text(KEY + "\n", encoding="utf-8")
        self.out = self.dir / "answers.csv"

    def answer(self, base: str, *extra: str) -> tuple[int, str, str]:
        return run_main(
            [
                "answer",
                "--questions",
                str(self.questions),
                "--base-url",
                base,
                "--assistant",
                SLUG,
                "--key-file",
                str(self.key_file),
                "--out",
                str(self.out),
                *extra,
            ]
        )

    def test_streamed_answers_with_citations_not_found_and_errors(self) -> None:
        with FakeServer() as base:
            code, _, err = self.answer(base + "/v1")
        self.assertEqual(code, 0, err)
        rows = read_csv(self.out)
        self.assertEqual(list(rows), [q["id"] for q in QUESTIONS])
        self.assertEqual(list(next(iter(rows.values()))), list(grading.COLUMNS))

        altan = rows["q-altan"]
        self.assertEqual(altan["outcome"], "answered")
        self.assertEqual(altan["citation_count"], "2")  # [1] cited twice: one document, display order kept
        self.assertEqual(
            altan["citations"].splitlines(), [f"[1] Altan och uteplats - {ALTAN}", f"[2] Bygglovsavgifter - {AVGIFT}"]
        )
        self.assertIn("Avgiften följer taxan [2]", altan["answer"])
        self.assertTrue(altan["ttft_ms"].isdigit() and altan["latency_ms"].isdigit())
        self.assertEqual(altan["kind"], "answerable")
        self.assertTrue(altan["request_id"].startswith("req-"))
        for column in grading.GRADE_COLUMNS:
            self.assertEqual(altan[column], "")

        carport = rows["q-carport"]  # Open WebUI source events instead of annotations
        self.assertEqual(carport["citations"], f"[1] Carport - När behövs bygglov? - {ALTAN}")
        self.assertEqual(rows["q-vader"]["outcome"], "not-found")
        self.assertEqual(rows["q-vader"]["kind"], "off-topic")
        self.assertEqual(rows["q-vader"]["citation_count"], "0")
        self.assertEqual(rows["q-egen"]["outcome"], "answered")  # unknown text, no usage: not guessed as not-found
        self.assertEqual(rows["q-fel"]["outcome"], "error")
        self.assertTrue(rows["q-fel"]["error"].startswith("http 502 upstream_error"))
        self.assertIn("Bearer ***", rows["q-fel"]["error"])
        self.assertEqual(rows["q-trangsel"]["outcome"], "answered")  # 429 retried after Retry-After
        self.assertEqual(rows["q-avbrott"]["outcome"], "error")
        self.assertEqual(rows["q-avbrott"]["error"], "stream stream_interrupted: interrupted")
        self.assertEqual(rows["q-avbrott"]["answer"], "Ett halvt svar")  # partial text kept
        self.assertEqual(rows["q-sokning"]["outcome"], "unavailable")  # never counted as not-found
        self.assertEqual(rows["q-formel"]["question"], '\'=HYPERLINK("https://evil")')

        bodies = [r["body"] for r in FakeChatApi.requests]
        self.assertEqual(len(bodies), len(QUESTIONS) + 1)  # one retry
        for body in bodies:
            self.assertEqual(body["model"], SLUG)
            self.assertTrue(body["stream"])
            self.assertEqual(body["stream_options"], {"include_usage": True})
            self.assertEqual([m["role"] for m in body["messages"]], ["user"])  # a new conversation per question

    def test_not_streamed(self) -> None:
        with FakeServer() as base:
            code, _, err = self.answer(base, "--no-stream")
        self.assertEqual(code, 0, err)
        rows = read_csv(self.out)
        self.assertEqual(rows["q-altan"]["citation_count"], "2")
        self.assertEqual(rows["q-altan"]["ttft_ms"], "")
        self.assertEqual(rows["q-vader"]["outcome"], "not-found")
        self.assertEqual(rows["q-sokning"]["outcome"], "unavailable")
        self.assertEqual(rows["q-avbrott"]["error"], "http 502 upstream_error: broken")
        self.assertTrue(all(r["body"]["stream"] is False for r in FakeChatApi.requests))

    def test_assistant_not_found_text_is_recognised(self) -> None:
        with FakeServer() as base:
            code, _, _ = self.answer(base, "--not-found-text", CUSTOM_NOT_FOUND)
        self.assertEqual(code, 0)
        self.assertEqual(read_csv(self.out)["q-egen"]["outcome"], "not-found")

    def test_wrong_key_aborts_the_run(self) -> None:
        self.key_file.write_text("ume-rag-sk-wrong", encoding="utf-8")
        with FakeServer() as base:
            code, _, err = self.answer(base)
        self.assertEqual(code, 2)
        self.assertIn("ABORTED: http 401 invalid_key", err)
        self.assertEqual(len(FakeChatApi.requests), 1)
        self.assertEqual(read_csv(self.out), {})  # header only

    def test_unknown_assistant_aborts_the_run(self) -> None:
        with FakeServer() as base:
            code, _, err = run_main(
                [
                    "answer",
                    "--questions",
                    str(self.questions),
                    "--base-url",
                    base,
                    "--assistant",
                    "okand",
                    "--key-file",
                    str(self.key_file),
                    "--out",
                    str(self.out),
                ]
            )
        self.assertEqual(code, 2)
        self.assertIn("model_not_found", err)

    def test_key_never_appears_in_output(self) -> None:
        """A real process, key from the environment and from a file; the fake server echoes it in an error."""
        for via_env in (True, False):
            with self.subTest(via_env=via_env), FakeServer() as base:
                env = {k: v for k, v in os.environ.items() if k != grading.KEY_ENV}
                argv = [
                    sys.executable,
                    "-I",
                    str(SCRIPT),
                    "answer",
                    "--questions",
                    str(self.questions),
                    "--base-url",
                    base,
                    "--assistant",
                    SLUG,
                    "--out",
                    str(self.out),
                ]
                if via_env:
                    env[grading.KEY_ENV] = KEY
                else:
                    argv += ["--key-file", str(self.key_file)]
                # A fixed argv: this interpreter and the script under test.
                result = subprocess.run(argv, env=env, capture_output=True, text=True, timeout=60, check=False)  # noqa: S603
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(all(r["auth"] == f"Bearer {KEY}" for r in FakeChatApi.requests))
                written = self.out.read_text(encoding="utf-8-sig")
                self.assertIn("Bearer ***", written)
                for text in (result.stdout, result.stderr, written):
                    self.assertNotIn(KEY, text)
                    self.assertNotIn(KEY[len("ume-rag-sk-") :], text)

    def test_missing_key_is_refused(self) -> None:
        with mock_env(grading.KEY_ENV, None), self.assertRaises(SystemExit) as raised:
            run_main(
                ["answer", "--questions", str(self.questions), "--base-url", "http://127.0.0.1:9", "--assistant", SLUG]
            )
        self.assertIn(grading.KEY_ENV, str(raised.exception))


@contextlib.contextmanager
def mock_env(name: str, value: str | None):
    old = os.environ.pop(name, None)
    if value is not None:
        os.environ[name] = value
    try:
        yield
    finally:
        os.environ.pop(name, None)
        if old is not None:
            os.environ[name] = old


class QuestionTests(unittest.TestCase):
    def test_admin_pages_keep_active_human_questions(self) -> None:
        questions, skipped = grading.load_questions(FIXTURES / "questions.json")
        self.assertEqual(skipped, 2)  # one generated, one retired
        self.assertEqual([q.kind for q in questions], ["answerable", "answerable", "off-topic"])
        self.assertEqual(questions[0].expected_documents, ["Altan och uteplats | 0193b0d2-0000-7000-8000-0000000000d1"])

    def test_csv_questions(self) -> None:
        path = Path(tempfile.mkdtemp()) / "q.csv"
        path.write_text(
            "id;question;expected_answer;expected_documents\nx1;Fråga?;Fakta;Doc A|Doc B\nx2;Väder?;;\n",
            encoding="utf-8-sig",
        )
        questions, _ = grading.load_questions(path)
        self.assertEqual([q.expected_documents for q in questions], [["Doc A", "Doc B"], []])

    def test_missing_expected_documents_field_is_refused(self) -> None:
        path = Path(tempfile.mkdtemp()) / "q.json"
        path.write_text(json.dumps([{"id": "1", "question": "Fråga?"}]), encoding="utf-8")
        with self.assertRaises(SystemExit):
            grading.load_questions(path)


def graded_rows() -> tuple[list[str], list[dict]]:
    text = (FIXTURES / "graded-sample.csv").read_text(encoding="utf-8-sig")
    reader = csv.DictReader(io.StringIO(text), delimiter=";")
    return list(reader.fieldnames or []), list(reader)


def write_rows(fields: list[str], rows: list[dict], delimiter: str = ";") -> Path:
    path = Path(tempfile.mkdtemp()) / "graded.csv"
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields, delimiter=delimiter)
        writer.writeheader()
        writer.writerows(rows)
    return path


def verdicts(path: Path) -> tuple[int, dict[str, bool]]:
    code, out, _ = run_main(["totals", str(path)])
    measures, _, _, _ = grading.grade(
        path,
        {
            "correct": grading.Fraction("0.8"),
            "citation_precision": grading.Fraction("0.9"),
            "false_not_found": grading.Fraction("0.1"),
            "off_topic": grading.Fraction("0.8"),
        },
    )
    return code, {m.name: m.passed for m in measures} | {"_out": out}


class TotalsTests(unittest.TestCase):
    def test_sample_meets_every_threshold_exactly(self) -> None:
        code, out, _ = run_main(["totals", str(FIXTURES / "graded-sample.csv")])
        self.assertEqual(code, 0, out)
        self.assertIn("16/20 =  80.0 %  >= 80 %  PASS", out)
        self.assertIn("18/20 =  90.0 %  >= 90 %  PASS", out)
        self.assertIn("2/20 =  10.0 %  <= 10 %  PASS", out)
        self.assertIn("4/5 =  80.0 %  >= 80 %  PASS", out)
        self.assertIn("Gate: PASS", out)

    def test_committed_below_sample_fails_correct_only(self) -> None:
        code, result = verdicts(FIXTURES / "graded-sample-below.csv")
        self.assertEqual(code, 1)
        self.assertIn("15/20 =  75.0 %  >= 80 %  FAIL", result.pop("_out"))
        self.assertEqual([name for name, ok in result.items() if not ok], ["correct answers on answerable questions"])

    def test_one_grade_below_each_threshold_fails_that_measure_only(self) -> None:
        def correct(rows):  # 15/20 = 75 %
            rows[0][grading.GRADE_CORRECT] = "nej"

        def precision(rows):  # 17/20 = 85 %
            rows[0][grading.GRADE_SUPPORTING] = "0"

        def false_not_found(rows):  # a wrong answer graded not found: 3/20 = 15 %
            row = next(r for r in rows if r["id"] == "a17")
            row[grading.GRADE_NOT_FOUND] = "ja"

        def off_topic(rows):  # 3/5 = 60 %
            row = next(r for r in rows if r["id"] == "o01")
            row[grading.GRADE_NOT_FOUND] = "nej"

        cases = {
            "correct answers on answerable questions": correct,
            "citation precision": precision,
            "false not-found": false_not_found,
            'off-topic questions answered "not found"': off_topic,
        }
        for measure, change in cases.items():
            with self.subTest(measure=measure):
                fields, rows = graded_rows()
                change(rows)
                code, result = verdicts(write_rows(fields, rows))
                result.pop("_out")
                self.assertEqual(code, 1)
                self.assertEqual([name for name, ok in result.items() if not ok], [measure])

    def test_comma_separated_and_english_grades(self) -> None:
        fields, rows = graded_rows()
        for row in rows:
            for column in (grading.GRADE_CORRECT, grading.GRADE_NOT_FOUND):
                row[column] = {"ja": "Yes", "nej": "no"}.get(row[column], row[column])
        code, out, _ = run_main(["totals", str(write_rows(fields, rows, ","))])
        self.assertEqual(code, 0, out)

    def test_custom_thresholds(self) -> None:
        code, out, _ = run_main(["totals", str(FIXTURES / "graded-sample.csv"), "--min-correct", "0.85"])
        self.assertEqual(code, 1)
        self.assertIn(">= 85 %  FAIL", out)

    def test_incomplete_or_contradictory_grading_is_refused(self) -> None:
        def empty(rows):
            rows[0][grading.GRADE_CORRECT] = ""

        def both(rows):
            rows[0][grading.GRADE_NOT_FOUND] = "ja"

        def too_many(rows):
            rows[0][grading.GRADE_SUPPORTING] = "2"

        def unknown(rows):
            rows[0][grading.GRADE_NOT_FOUND] = "kanske"

        def no_supporting(rows):
            rows[0][grading.GRADE_SUPPORTING] = ""

        expected = {
            empty: "a01: grade_correct is empty",
            both: "a01: graded both correct and not found",
            too_many: "a01: grade_supporting_citations '2' is not a whole number from 0 to 1",
            unknown: "a01: grade_not_found 'kanske' is not ja/nej",
            no_supporting: "a01: grade_supporting_citations is empty for 1 citations",
        }
        for change, message in expected.items():
            with self.subTest(change=change.__name__):
                fields, rows = graded_rows()
                change(rows)
                code, _, err = run_main(["totals", str(write_rows(fields, rows))])
                self.assertEqual(code, 2)
                self.assertIn(message, err)

    def test_no_off_topic_rows_fails_that_measure(self) -> None:
        fields, rows = graded_rows()
        code, result = verdicts(write_rows(fields, [r for r in rows if r["kind"] == "answerable"]))
        self.assertEqual(code, 1)
        self.assertIn("FAIL (no rows to measure)", result.pop("_out"))

    def test_totals_written_for_the_decision(self) -> None:
        out = Path(tempfile.mkdtemp()) / "totals.txt"
        code, printed, _ = run_main(["totals", str(FIXTURES / "graded-sample.csv"), "--out", str(out)])
        self.assertEqual(code, 0)
        self.assertEqual(out.read_text(encoding="utf-8"), printed)


if __name__ == "__main__":
    unittest.main()
