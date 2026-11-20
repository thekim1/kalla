"""Measures on-prem model capacity through the LLM gateway (P0 checklist #22, weakness review §5, to-verify #98, #99,
#104, #125, #149). Standard library only, one file, so it can be copied to any host that reaches the gateway.

Usage: python3 -I scripts/capacity/capacity.py <command> --base-url URL --key-file FILE [options]

Commands:
  embed-throughput   BGE-M3 inputs/s and tokens/s by batch size (and client concurrency)          #98, #125
  query-under-load   single-query embedding p50/p95/p99, idle and behind batch-32/64 ingestion load  #99, #125
  condense           condense model latency p50/p95/p99 for short prompts vs the 600 ms timeout     #104
  answer-ttft        answer model TTFT p50/p95/p99 and total time, streamed, full 16k-token prompt  #149
  all                the four above in that order, one result file each

Every request carries Authorization: Bearer <key>, x-ume-max-residency: <ceiling>, x-ume-run-id and x-ume-step, as the
services do (knowledge-api design §5.2, chat-api design §5, §8.1, design-processing §11). Every 2xx answer must carry
x-ume-residency at or below the ceiling; otherwise the run stops, because it would not be measuring on-prem capacity.
Each command writes <out-dir>/<UTC time>-<command>.json and a .txt summary that prints the design estimate next to each
measured number. --smoke marks a run against a fake gateway: its numbers test the script, not the hardware.
"""

import argparse
import contextlib
import http.client
import json
import math
import random
import re
import secrets
import ssl
import sys
import threading
import time
import urllib.parse
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path

CEILINGS = ("OnPrem", "Eu", "External")  # ordered: OnPrem < Eu < External (Kalla.Domain.Residency)
DEFAULT_OUT = Path(__file__).resolve().parent / "results"
CONNECT_TIMEOUT_S = 10.0
HEADERS_TIMEOUT_S = 15.0  # Chat:GatewayHeadersTimeout (chat-api design §8.3)
CONDENSE_TIMEOUT_MS = 600.0  # Chat:CondenseTimeout (chat-api design §5)
CONDENSE_TARGET_MS = 400.0  # condense p95 target (chat-api design §16.1)
QUERY_BUDGET_MS = 60.0  # query embedding, miss (knowledge-api design §11)
QUERY_ATTEMPT_TIMEOUT_MS = 1500.0  # Retrieval:EmbeddingTimeout (knowledge-api design §5.3)
PILOT_TOKENS_PER_S = 10_000  # R1: 10k pages/h x 3,600 tokens (design-engine §18)
BULK_TOKENS_PER_S = 100_000  # 10x bulk (performance pass §2.1)
UMEA_SE_TOKENS = 165e6  # umea.se ≈ 0.37M chunks (performance pass §2)
WORKER_REQUEST_TOKEN_CAP = 16_384  # ≤ 16,384 tokens per embedding request (design-processing §11)
EMBED_ESTIMATE = "8 CPU cores 0.5-1.5k, one L4/A10 8-20k, one A100/H100 30-80k tokens/s (perf §3.9)"

# --------------------------------------------------------------------------------------------------------------------
# Text: realistic Swedish municipal chunk texts, generated (no corpus download). Unique per request, so no provider
# prefix or response cache can answer instead of the model.
# --------------------------------------------------------------------------------------------------------------------

SENTENCES = [
    "Du behöver bygglov för en altan som är högre än 1,8 meter eller ligger närmare tomtgränsen än 4,5 meter.",
    "Ansökan om bygglov lämnas in digitalt via kommunens e-tjänst tillsammans med situationsplan och fasadritningar.",
    "Handläggningstiden är normalt upp till tio veckor från det att ansökan är komplett.",
    "Byggnadsnämnden tar ut en avgift enligt taxan som kommunfullmäktige har beslutat om.",
    "Förskoleplats söker du tidigast sex månader innan du behöver platsen och senast fyra månader innan.",
    "Barn har rätt till förskola från ett års ålder om vårdnadshavarna arbetar, studerar eller söker arbete.",
    "Avgiften för förskola beräknas utifrån hushållets inkomst och följer maxtaxan.",
    "Hushållsavfall sorteras i matavfall, restavfall och förpackningar som lämnas vid återvinningsstationerna.",
    "Grovavfall som möbler och elektronik lämnar du kostnadsfritt på kommunens återvinningscentraler.",
    "Tömning av slamavskiljare sker en gång per år för permanentboende och vartannat år för fritidshus.",
    "Parkeringstillstånd för rörelsehindrade kan beviljas den som har väsentliga svårigheter att förflytta sig.",
    "Boendeparkering gäller i de zoner som anges på skylten och kräver ett giltigt tillstånd i bilen.",
    "Felparkeringsavgiften ska betalas inom åtta dagar, annars skickas en betalningspåminnelse.",
    "Hemtjänst ges efter ett biståndsbeslut och omfattar både service och personlig omvårdnad.",
    "Trygghetslarm kan sökas av den som bor hemma och behöver kunna kalla på hjälp dygnet runt.",
    "Ansökan om särskilt boende görs hos en biståndshandläggare inom äldreomsorgen.",
    "Ekonomiskt bistånd prövas utifrån hushållets samlade inkomster, tillgångar och skäliga utgifter.",
    "Kommunens kontaktcenter svarar på frågor vardagar mellan klockan åtta och sexton.",
    "Gator och cykelvägar plogas enligt en prioriteringsordning där huvudcykelvägar plogas först.",
    "Felanmälan om gatubelysning, potthål eller nedskräpning görs via kommunens felanmälningstjänst.",
    "Strandbaden har badvattenprovtagning varannan vecka under sommarsäsongen.",
    "Biblioteket erbjuder lån av e-böcker och ljudböcker med ditt lånekort och en pinkod.",
    "Simhallens öppettider kan ändras under skollov och helgdagar, se aktuell information.",
    "Den som vill servera alkohol behöver ett serveringstillstånd från kommunens tillståndsenhet.",
    "Livsmedelsverksamheter ska registreras hos miljö- och hälsoskydd minst två veckor innan de startar.",
    "Detaljplanen anger hur mark och vatten får användas och hur bebyggelsen ska utformas.",
    "Under samrådet kan alla som berörs lämna synpunkter på planförslaget skriftligt.",
    "Skolskjuts beviljas elever i grundskolan som har lång eller trafikfarlig väg till skolan.",
    "Modersmålsundervisning erbjuds elever som har ett annat modersmål än svenska och använder det dagligen.",
    "Vuxenutbildningen ger dig möjlighet att läsa in grundskole- och gymnasiekompetens.",
    "Färdtjänst är en form av kollektivtrafik för personer som inte kan resa med vanliga bussar.",
    "Kommunens krisberedskap omfattar planer för störningar i el, vatten och värme.",
    "Vattenläckor och avbrott i vattenförsörjningen meddelas på webbplatsen och via sms till berörda.",
    "Vid eldningsförbud är det inte tillåtet att elda i det fria, inte heller i iordningställda eldstäder.",
    "Föreningsbidrag kan sökas av ideella föreningar som har sin verksamhet i kommunen.",
    "Lokalbokning för idrottshallar görs i bokningssystemet senast tre dagar före önskat datum.",
]
TITLES = [
    "Bygglov för altan",
    "Förskola",
    "Avfall och återvinning",
    "Parkering",
    "Hemtjänst",
    "Ekonomiskt bistånd",
    "Gator och vägar",
    "Bibliotek",
    "Tillstånd",
    "Detaljplaner",
    "Skola",
    "Färdtjänst",
    "Krisberedskap",
]
QUESTIONS = [
    "Behöver jag bygglov för en altan på min tomt?",
    "Hur lång är handläggningstiden för bygglov?",
    "När kan jag söka förskoleplats till mitt barn?",
    "Vad kostar förskolan per månad?",
    "Var lämnar jag grovavfall och gamla möbler?",
    "Hur ofta töms slamavskiljaren vid ett fritidshus?",
    "Hur söker jag parkeringstillstånd för rörelsehindrade?",
    "Vad händer om jag inte betalar felparkeringsavgiften?",
    "Hur ansöker jag om hemtjänst för min mamma?",
    "Vem kan få trygghetslarm?",
    "Vilka öppettider har kontaktcenter?",
    "Vilka cykelvägar plogas först på vintern?",
    "Hur felanmäler jag en trasig gatlampa?",
    "Behöver jag tillstånd för att servera alkohol på min restaurang?",
    "Kan mitt barn få skolskjuts?",
    "Hur bokar jag en idrottshall för min förening?",
]
FOLLOW_UPS = [
    "Vad kostar det?",
    "Hur lång tid tar det?",
    "Gäller det också för fritidshus?",
    "Var ansöker jag om det?",
    "Kan jag överklaga det?",
    "Måste jag göra det digitalt?",
    "Gäller samma regler där?",
    "Och för hen då?",
]
CONDENSE_SYSTEM = (  # chat-api design §5, the condense prompt
    "Du skriver om en följdfråga till en fristående sökfråga.\n"
    "Använd samtalet bara för att lösa upp syftningar (det, den, där, samma, hen) och utelämnade ämnen.\n"
    "Lägg inte till fakta, svara inte på frågan, behåll språket i användarens fråga.\n"
    "Skriv endast den omskrivna frågan på en rad. Är frågan redan fristående: upprepa den oförändrad."
)
ANSWER_FRAME = (
    "Du är kommunens assistent. Svara på svenska, kort och korrekt, endast utifrån källorna nedan. "
    "Hänvisa till källorna med [n]. Finns svaret inte i källorna, säg det."
)


class Corpus:
    """Deterministic generator of chunk texts, queries and prompts; a counter keeps every text unique."""

    def __init__(self, seed: int, chars_per_token: float) -> None:
        self._random = random.Random(seed)  # noqa: S311 - test text, not security
        self._lock = threading.Lock()
        self._counter = 0
        self.chars_per_token = chars_per_token

    def _next(self) -> tuple[int, random.Random]:
        with self._lock:
            self._counter += 1
            return self._counter, random.Random(self._random.random())  # noqa: S311 - test text

    def chunk(self, tokens: int) -> str:
        """A chunk of about <tokens> tokens: a title line, then sentences, as the worker's chunker emits them."""
        n, pick = self._next()
        parts = [f"{pick.choice(TITLES)} - avsnitt {n}."]
        target = tokens * self.chars_per_token
        length = len(parts[0])
        while length < target:
            sentence = pick.choice(SENTENCES)
            parts.append(sentence)
            length += len(sentence) + 1
        return " ".join(parts)

    def query(self) -> str:
        """A chat question of ≈ 30 tokens (performance pass §2.2), normalised as QueryNormalizer would leave it."""
        n, pick = self._next()
        return f"{pick.choice(QUESTIONS)} Ärende {n}, {pick.choice(TITLES).lower()}."

    def condense_messages(self) -> list[dict[str, str]]:
        """System prompt, a transcript of the last turns (≤ 500/600 characters each) and a follow-up (chat §5)."""
        n, pick = self._next()
        turns = []
        for _ in range(pick.randint(1, 3)):
            turns.append(f"Användare: {pick.choice(QUESTIONS)}")
            answer = " ".join(pick.choice(SENTENCES) for _ in range(pick.randint(1, 3)))
            turns.append(f"Assistent: {answer[:600]}")
        transcript = "\n".join(turns)[: 4000 - 200]
        question = f"{pick.choice(FOLLOW_UPS)} (ärende {n})"
        return [
            {"role": "system", "content": CONDENSE_SYSTEM},
            {"role": "user", "content": f"Samtal:\n{transcript}\n\nFöljdfråga: {question}"},
        ]

    def answer_messages(self, prompt_tokens: int) -> list[dict[str, str]]:
        """A full answer prompt of about <prompt_tokens>: frame with a unique nonce first (so no prefix cache helps),
        numbered sources, up to 4,000 tokens of history, the question (chat-api design §6.3, §7)."""
        n, pick = self._next()
        system = f"Sessions-id {secrets.token_hex(8)}-{n}. {ANSWER_FRAME}\n\nKällor:\n"
        question = pick.choice(QUESTIONS)
        history: list[dict[str, str]] = []
        history_chars = min(4000, prompt_tokens // 4) * self.chars_per_token
        while sum(len(m["content"]) for m in history) < history_chars:
            history.append({"role": "user", "content": pick.choice(QUESTIONS)})
            history.append({"role": "assistant", "content": " ".join(pick.choice(SENTENCES) for _ in range(4))})
        budget = (
            prompt_tokens * self.chars_per_token - len(system) - len(question) - sum(len(m["content"]) for m in history)
        )
        sources, number = [], 1
        while budget > 0:
            text = self.chunk(450)[: int(budget) + 1]
            sources.append(f"[{number}] {pick.choice(TITLES)}\n{text}")
            budget -= len(sources[-1]) + 2
            number += 1
        return [
            {"role": "system", "content": system + "\n\n".join(sources)},
            *history,
            {"role": "user", "content": question},
        ]


def estimate_tokens(text: str, chars_per_token: float) -> int:
    return max(1, math.ceil(len(text) / chars_per_token))


# --------------------------------------------------------------------------------------------------------------------
# Gateway client
# --------------------------------------------------------------------------------------------------------------------


class RunAborted(Exception):
    """A configuration or residency fault: measuring on would record the wrong thing."""


@dataclass
class Response:
    status: int
    headers: dict[str, str]
    body: bytes
    headers_s: float  # request start → response headers
    total_s: float  # request start → body read
    first_event_s: float | None = None  # streaming: first data event with a choice
    first_token_s: float | None = None  # streaming: first non-empty content delta
    usage: dict | None = None
    content: str = ""


@dataclass
class Gateway:
    base_url: str
    key: str
    ceiling: str
    run_id: str
    timeout_s: float
    ca_file: str | None
    observed: dict[str, dict[str, int]] = field(default_factory=dict)
    _lock: threading.Lock = field(default_factory=threading.Lock)
    _local: threading.local = field(default_factory=threading.local)

    def __post_init__(self) -> None:
        url = urllib.parse.urlsplit(self.base_url.rstrip("/").removesuffix("/v1"))
        if url.scheme not in ("http", "https") or not url.hostname:
            raise SystemExit(f"--base-url must be http(s)://host[:port][/path], got '{self.base_url}'")
        self._url = url
        self._path = url.path.rstrip("/") + "/v1"

    def _connection(self) -> http.client.HTTPConnection:
        connection = getattr(self._local, "connection", None)
        if connection is None:
            if self._url.scheme == "https":
                context = ssl.create_default_context(cafile=self.ca_file)
                connection = http.client.HTTPSConnection(
                    self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S, context=context
                )
            else:
                connection = http.client.HTTPConnection(self._url.hostname, self._url.port, timeout=CONNECT_TIMEOUT_S)
            self._local.connection = connection
        if connection.sock is None:
            # Connect (and TLS) outside the timed request, as the services' pooled connections; then the read timeout.
            connection.connect()
            connection.sock.settimeout(self.timeout_s)
        return connection

    def _reset(self) -> None:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            connection.close()
        self._local.connection = None

    def headers(self, step: str) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self.key}",
            "Content-Type": "application/json",
            "x-ume-max-residency": self.ceiling,
            "x-ume-run-id": self.run_id,
            "x-ume-step": step,
        }

    def post(self, path: str, payload: dict, step: str, stream: bool = False) -> Response:
        body = json.dumps(payload, ensure_ascii=False).encode()
        for attempt in (1, 2):  # one reconnect when a kept-alive connection was closed by the server
            connection = self._connection()
            start = time.perf_counter()
            try:
                connection.request("POST", self._path + path, body=body, headers=self.headers(step))
                raw = connection.getresponse()
                headers_s = time.perf_counter() - start
                headers = {k.lower(): v for k, v in raw.getheaders()}
                if stream and raw.status == 200:
                    response = self._read_stream(raw, start, headers_s, headers)
                else:
                    data = raw.read()
                    response = Response(raw.status, headers, data, headers_s, time.perf_counter() - start)
                break
            except (OSError, http.client.HTTPException) as exc:
                self._reset()  # a connection left mid-request cannot be reused
                stale = isinstance(exc, (http.client.RemoteDisconnected, BrokenPipeError, ConnectionResetError))
                if attempt == 2 or not stale:
                    raise
        self._check(response, path)
        return response

    @staticmethod
    def _read_stream(raw: http.client.HTTPResponse, start: float, headers_s: float, headers: dict) -> Response:
        response = Response(raw.status, headers, b"", headers_s, 0.0)
        content = []
        while line := raw.readline():
            if not line.startswith(b"data:"):
                continue
            data = line[5:].strip()
            if data == b"[DONE]":
                break
            event = json.loads(data)
            if event.get("usage"):
                response.usage = event["usage"]
            for choice in event.get("choices") or []:
                if response.first_event_s is None:
                    response.first_event_s = time.perf_counter() - start
                text = (choice.get("delta") or {}).get("content")
                if text:
                    if response.first_token_s is None:
                        response.first_token_s = time.perf_counter() - start
                    content.append(text)
        raw.read()  # drain to keep the connection reusable
        response.total_s = time.perf_counter() - start
        response.content = "".join(content)
        return response

    def _check(self, response: Response, path: str) -> None:
        if 200 <= response.status < 300:
            residency = response.headers.get("x-ume-residency")
            with self._lock:
                for name in ("x-ume-residency", "x-ume-provider", "x-ume-model"):
                    counts = self.observed.setdefault(name, {})
                    value = response.headers.get(name, "(missing)")
                    counts[value] = counts.get(value, 0) + 1
            allowed = {c.lower() for c in CEILINGS[: CEILINGS.index(self.ceiling) + 1]}
            if residency is None or residency.strip().lower() not in allowed:
                raise RunAborted(
                    f"{path} answered with x-ume-residency '{residency or '(missing)'}' above the ceiling "
                    f"{self.ceiling}: this is not a measurement of capacity within that ceiling."
                )
        elif response.status in (400, 401, 403, 404):
            raise RunAborted(
                f"{path} answered {response.status}: {error_text(response)} (check the key file, "
                f"--ceiling and the model name)."
            )

    def embed(self, model: str, inputs: list[str], step: str) -> tuple[Response, int | None]:
        """One /v1/embeddings call (encoding_format float, as the worker and the query path). Returns the response and
        the provider's prompt_tokens. Only the vector count and usage are parsed, so a large batch's body does not hold
        the GIL in this client long enough to delay a concurrent query measurement."""
        response = self.post("/embeddings", {"model": model, "input": inputs, "encoding_format": "float"}, step)
        if response.status != 200:
            return response, None
        vectors = len(EMBEDDING_KEY.findall(response.body))
        if vectors != len(inputs):
            raise RunAborted(f"/v1/embeddings returned {vectors} vectors for {len(inputs)} inputs.")
        usage = usage_from_tail(response.body)
        return response, usage.get("prompt_tokens") if usage else None

    def chat(
        self, model: str, messages: list[dict], step: str, max_tokens: int, temperature: float, stream: bool
    ) -> Response:
        payload = {
            "model": model,
            "messages": messages,
            "max_tokens": max_tokens,
            "temperature": temperature,
            "stream": stream,
        }
        if stream:
            payload["stream_options"] = {"include_usage": True}
        response = self.post("/chat/completions", payload, step, stream=stream)
        if response.status == 200 and not stream:
            parsed = json.loads(response.body)
            response.usage = parsed.get("usage")
            response.content = ((parsed.get("choices") or [{}])[0].get("message") or {}).get("content") or ""
        return response


EMBEDDING_KEY = re.compile(rb'"embedding"\s*:')
USAGE_KEY = re.compile(rb'"usage"\s*:\s*')


def usage_from_tail(body: bytes) -> dict | None:
    position = body.rfind(b'"usage"')
    if position < 0:
        return None
    match = USAGE_KEY.match(body, position)
    if not match:
        return None
    try:
        value, _ = json.JSONDecoder().raw_decode(body[match.end() :].decode())
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def error_text(response: Response) -> str:
    try:
        error = json.loads(response.body).get("error") or {}
        return f"{error.get('code')}: {error.get('message')}"
    except (ValueError, AttributeError):
        return response.body[:200].decode(errors="replace")


def retry_after(response: Response) -> float:
    try:
        return min(10.0, max(0.5, float(response.headers.get("retry-after", "1"))))
    except ValueError:
        return 1.0


# --------------------------------------------------------------------------------------------------------------------
# Load loops and statistics
# --------------------------------------------------------------------------------------------------------------------


@dataclass
class Sample:
    start: float
    end: float
    latency_ms: float
    status: int
    inputs: int = 0
    tokens: int = 0
    tokens_from_usage: bool = False
    extra: dict = field(default_factory=dict)


class Recorder:
    def __init__(self) -> None:
        self.samples: list[Sample] = []  # successful requests started in the measurement window (latency)
        self.completions: list[Sample] = []  # every successful request, warm-up included (throughput)
        self.errors: dict[str, int] = {}
        self.lock = threading.Lock()
        self.failure: BaseException | None = None

    def add(self, sample: Sample, measured: bool) -> None:
        with self.lock:
            self.completions.append(sample)
            if measured:
                self.samples.append(sample)

    def error(self, key: str) -> None:
        with self.lock:
            self.errors[key] = self.errors.get(key, 0) + 1


def run_loop(
    concurrency: int,
    warmup_s: float,
    duration_s: float,
    call,
    recorder: Recorder,
    stop: threading.Event | None = None,
    interval_s: float = 0.0,
) -> tuple[float, float]:
    """Closed loop: <concurrency> threads call <call>() back to back (plus <interval_s> think time) until <duration_s>
    after the warm-up. Latency samples are the requests started in the window (the first request of each thread is
    always warm-up); requests still running at the end are completed. Returns the window (start, end)."""
    stop = stop or threading.Event()
    t_measure = time.perf_counter() + warmup_s
    t_end = t_measure + duration_s

    def worker() -> None:
        first, failures = True, 0
        while not stop.is_set() and recorder.failure is None:
            now = time.perf_counter()
            if now >= t_end and not first:
                return
            try:
                sample = call()
                failures = 0
            except RunAborted as exc:
                recorder.failure = exc
                stop.set()
                return
            except (OSError, http.client.HTTPException) as exc:
                recorder.error(type(exc).__name__)
                failures += 1
                if failures >= 5:  # five transport failures in a row: the gateway is not reachable
                    recorder.failure = RunAborted(f"five requests in a row failed, the last with {exc!r}")
                    stop.set()
                    return
                time.sleep(0.5)
                continue
            if sample.status != 200:
                recorder.error(str(sample.status))
                if sample.status in (429, 503):
                    time.sleep(sample.extra.pop("retry_after", 1.0))
            else:
                recorder.add(sample, measured=not first and sample.start >= t_measure)
            first = False
            if interval_s:
                time.sleep(interval_s)

    threads = [threading.Thread(target=worker, daemon=True) for _ in range(concurrency)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    if recorder.failure:
        raise recorder.failure
    return t_measure, t_end


def percentile(values: list[float], p: float) -> float | None:
    """Linear interpolation between closest ranks (as numpy's default)."""
    if not values:
        return None
    ordered = sorted(values)
    rank = (len(ordered) - 1) * p / 100
    low, high = math.floor(rank), math.ceil(rank)
    return ordered[low] + (ordered[high] - ordered[low]) * (rank - low)


def latency_stats(values: list[float]) -> dict:
    return {
        "n": len(values),
        "p50": rnd(percentile(values, 50)),
        "p95": rnd(percentile(values, 95)),
        "p99": rnd(percentile(values, 99)),
        "max": rnd(max(values, default=None)),
        "mean": rnd(sum(values) / len(values) if values else None),
    }


def rnd(value: float | None, digits: int = 1) -> float | None:
    return None if value is None else round(value, digits)


def share_over(values: list[float], limit: float) -> float | None:
    return round(sum(v > limit for v in values) / len(values), 4) if values else None


def fmt(value: float | None, unit: str = "") -> str:
    if value is None:
        return "n/a"
    return f"{value:,.0f}{unit}" if abs(value) >= 100 else f"{value:,.1f}{unit}"


def p99_note(n: int) -> str:
    return "" if n >= 100 else f" (n={n} < 100: p99 is close to the max)"


# --------------------------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------------------------


def embed_call(gateway: Gateway, args, corpus: Corpus, batch: int, step: str):
    def call() -> Sample:
        inputs = [corpus.chunk(args.chunk_tokens) for _ in range(batch)]
        start = time.perf_counter()
        response, tokens = gateway.embed(args.embedding_model, inputs, step)
        end = time.perf_counter()
        sample = Sample(
            start,
            end,
            response.total_s * 1000,
            response.status,
            batch,
            tokens or sum(estimate_tokens(t, args.chars_per_token) for t in inputs),
            tokens is not None,
        )
        if response.status in (429, 503):
            sample.extra["retry_after"] = retry_after(response)
        return sample

    return call


def throughput(completions: list[Sample], window: tuple[float, float]) -> dict:
    """Work completed inside the window while the load runs; partial requests at both edges cancel out on average."""
    samples = [s for s in completions if window[0] <= s.end <= window[1]]
    elapsed = max(1e-9, window[1] - window[0])
    inputs = sum(s.inputs for s in samples)
    tokens = sum(s.tokens for s in samples)
    return {
        "requests": len(samples),
        "elapsed_s": round(elapsed, 2),
        "inputs_per_s": rnd(inputs / elapsed, 2),
        "tokens_per_s": rnd(tokens / elapsed),
        "tokens_from_usage": all(s.tokens_from_usage for s in samples) if samples else None,
        "mean_tokens_per_input": rnd(tokens / inputs if inputs else None),
    }


def cmd_embed_throughput(gateway: Gateway, args, corpus: Corpus) -> tuple[dict, list[str]]:
    rows: list[dict] = []
    lines = [
        f"Embedding throughput, model {args.embedding_model}, chunks ≈ {args.chunk_tokens} tokens",
        f"  estimate: {EMBED_ESTIMATE}; pilot needs {PILOT_TOKENS_PER_S:,} (engine §18 R1), "
        f"10x bulk {BULK_TOKENS_PER_S:,} (perf §2.1)",
    ]
    for concurrency in args.concurrency:
        for batch in args.batch_sizes:
            recorder = Recorder()
            window = run_loop(
                concurrency, args.warmup, args.duration, embed_call(gateway, args, corpus, batch, "embed"), recorder
            )
            row = {
                "batch_size": batch,
                "concurrency": concurrency,
                **throughput(recorder.completions, window),
                "request_latency_ms": latency_stats([s.latency_ms for s in recorder.samples]),
                "errors": recorder.errors,
            }
            notes = []
            if row["requests"] < 10:
                notes.append(f"only {row['requests']} requests completed in the window: raise --duration")
            if batch * args.chunk_tokens > WORKER_REQUEST_TOKEN_CAP:
                notes.append(
                    f"{batch} x {args.chunk_tokens} tokens exceeds the worker's {WORKER_REQUEST_TOKEN_CAP:,}"
                    " tokens per request (design-processing §11); measured as asked by to-verify #125"
                )
            row["notes"] = notes
            rows.append(row)
            tokens_per_s = row["tokens_per_s"] or 0
            lines.append(
                f"  batch {batch:>3} x conc {concurrency}: {fmt(row['inputs_per_s'])} inputs/s, "
                f"{fmt(tokens_per_s)} tokens/s{'' if row['tokens_from_usage'] else ' (tokens estimated)'}, "
                f"request p95 {fmt(row['request_latency_ms']['p95'], ' ms')}, ≈ {fmt(tokens_per_s)} pages/h at 3,600 "
                f"tokens/page; umea.se onboarding {fmt(UMEA_SE_TOKENS / tokens_per_s / 3600 if tokens_per_s else None)}"
                f" h (est. 4.6 h at 10k tokens/s); errors {row['errors'] or 0}"
                + "".join(f"\n      note: {note}" for note in notes)
            )
    best = max(rows, key=lambda r: r["tokens_per_s"] or 0)
    checks = [
        check(
            "embedding tokens/s (best batch x concurrency) ≥ pilot need",
            best["tokens_per_s"],
            PILOT_TOKENS_PER_S,
            ">=",
            "#98; a shortfall goes to register M2",
        )
    ]
    return {
        "rows": rows,
        "best": {k: best[k] for k in ("batch_size", "concurrency", "tokens_per_s")},
        "checks": checks,
    }, lines


def cmd_query_under_load(gateway: Gateway, args, corpus: Corpus) -> tuple[dict, list[str]]:
    phases: list[dict] = []
    lines = [
        f"Query-embedding latency under ingestion load, model {args.embedding_model}, query ≈ 30 tokens",
        f"  estimate: miss ≤ {QUERY_BUDGET_MS:.0f} ms (knowledge §11, assumes a GPU); behind 64 x 450-token "
        f"batches ≈ 1-3 s on an L4 (perf §3.9); attempt timeout {QUERY_ATTEMPT_TIMEOUT_MS:,.0f} ms (knowledge §5.3)",
    ]

    def query_call() -> Sample:
        text = corpus.query()
        start = time.perf_counter()
        response, tokens = gateway.embed(args.embedding_model, [text], "query-embed")
        sample = Sample(
            start,
            time.perf_counter(),
            response.total_s * 1000,
            response.status,
            1,
            tokens or estimate_tokens(text, args.chars_per_token),
            tokens is not None,
        )
        if response.status in (429, 503):
            sample.extra["retry_after"] = retry_after(response)
        return sample

    for batch in [0, *args.load_batch_sizes]:
        stop, load = threading.Event(), Recorder()
        loader = None
        if batch:
            loader = threading.Thread(
                target=background_load,
                daemon=True,
                args=(args.load_concurrency, embed_call(gateway, args, corpus, batch, "embed"), load, stop),
            )
            loader.start()
        foreground = Recorder()
        try:
            window = run_loop(
                args.query_concurrency,
                args.warmup,
                args.duration,
                query_call,
                foreground,
                stop,
                interval_s=args.query_interval_ms / 1000,
            )
        finally:
            stop.set()
            if loader:
                loader.join()
        if load.failure:
            raise load.failure
        latencies = [s.latency_ms for s in foreground.samples]
        background = [s for s in load.completions if window[0] <= s.end <= window[1]]
        phase = {
            "load": f"batch {batch} x {args.load_concurrency}" if batch else "idle",
            "load_batch_size": batch,
            "query_latency_ms": latency_stats(latencies),
            "share_over_budget": share_over(latencies, QUERY_BUDGET_MS),
            "share_over_attempt_timeout": share_over(latencies, QUERY_ATTEMPT_TIMEOUT_MS),
            "query_errors": foreground.errors,
            "load_throughput": throughput(background, window) if batch else None,
            "load_request_latency_ms": latency_stats([s.latency_ms for s in background]) if batch else None,
            "load_errors": load.errors,
        }
        phases.append(phase)
        stats = phase["query_latency_ms"]
        load_text = (
            f"; load {fmt(phase['load_throughput']['tokens_per_s'])} tokens/s, load request p95 "
            f"{fmt(phase['load_request_latency_ms']['p95'], ' ms')}"
            if batch
            else ""
        )
        lines.append(
            f"  {phase['load']:<16} query p50 {fmt(stats['p50'], ' ms')}, p95 {fmt(stats['p95'], ' ms')}, "
            f"p99 {fmt(stats['p99'], ' ms')}{p99_note(stats['n'])}; > {QUERY_BUDGET_MS:.0f} ms "
            f"{fmt_share(phase['share_over_budget'])}, > {QUERY_ATTEMPT_TIMEOUT_MS:,.0f} ms "
            f"{fmt_share(phase['share_over_attempt_timeout'])}{load_text}; errors {foreground.errors or 0}"
        )
    idle_p95 = phases[0]["query_latency_ms"]["p95"]
    checks = [check("query-embedding p95, idle", idle_p95, QUERY_BUDGET_MS, "<=", "#99; knowledge §11 budget")]
    for p in phases[1:]:
        p95 = p["query_latency_ms"]["p95"]
        checks.append(check(f"query-embedding p95, {p['load']}", p95, QUERY_BUDGET_MS, "<=", "#125; knowledge §11"))
        if idle_p95:
            checks.append(
                check(
                    f"query-embedding p95, {p['load']}, vs 2 x idle",
                    p95,
                    2 * idle_p95,
                    "<=",
                    "#125; above it the perf §6 query-lane trigger fires",
                )
            )
    return {"phases": phases, "checks": checks}, lines


def background_load(concurrency: int, call, recorder: Recorder, stop: threading.Event) -> None:
    """The ingestion load of query-under-load: runs until <stop>; a fault is left in <recorder> (and sets <stop>)."""
    with contextlib.suppress(RunAborted):
        run_loop(concurrency, 0.0, 86_400.0, call, recorder, stop)


def fmt_share(share: float | None) -> str:
    return "n/a" if share is None else f"{share * 100:.1f} %"


def cmd_condense(gateway: Gateway, args, corpus: Corpus) -> tuple[dict, list[str]]:
    def call() -> Sample:
        start = time.perf_counter()
        response = gateway.chat(args.condense_model, corpus.condense_messages(), "condense", 96, 0.0, stream=False)
        sample = Sample(start, time.perf_counter(), response.total_s * 1000, response.status)
        if response.status == 200:
            text = response.content.strip()
            sample.extra["accepted"] = 2 <= len(text) <= 400 and "\n" not in text  # chat §5 acceptance (part)
            sample.extra["completion_tokens"] = (response.usage or {}).get("completion_tokens")
        elif response.status in (429, 503):
            sample.extra["retry_after"] = retry_after(response)
        return sample

    recorder = Recorder()
    run_loop(args.concurrency[0], args.warmup, args.duration, call, recorder)
    latencies = [s.latency_ms for s in recorder.samples]
    completion = [s.extra["completion_tokens"] for s in recorder.samples if s.extra.get("completion_tokens")]
    result = {
        "model": args.condense_model,
        "concurrency": args.concurrency[0],
        "latency_ms": latency_stats(latencies),
        "share_over_timeout": share_over(latencies, CONDENSE_TIMEOUT_MS),
        "accepted_share": round(sum(s.extra["accepted"] for s in recorder.samples) / len(latencies), 4)
        if latencies
        else None,
        "mean_completion_tokens": rnd(sum(completion) / len(completion) if completion else None),
        "errors": recorder.errors,
    }
    stats = result["latency_ms"]
    lines = [
        f"Condense latency, model {args.condense_model}, non-streaming, max_tokens 96, temperature 0, "
        f"concurrency {args.concurrency[0]}",
        f"  estimate: p95 ≤ {CONDENSE_TARGET_MS:.0f} ms, cut at {CONDENSE_TIMEOUT_MS:.0f} ms (chat §16.1, §5)",
        f"  measured: p50 {fmt(stats['p50'], ' ms')}, p95 {fmt(stats['p95'], ' ms')}, p99 {fmt(stats['p99'], ' ms')}"
        f"{p99_note(stats['n'])}; > {CONDENSE_TIMEOUT_MS:.0f} ms {fmt_share(result['share_over_timeout'])}; "
        f"output accepted {fmt_share(result['accepted_share'])}; mean completion tokens "
        f"{fmt(result['mean_completion_tokens'])}; errors {recorder.errors or 0}",
    ]
    result["checks"] = [
        check("condense p95 within the timeout", stats["p95"], CONDENSE_TIMEOUT_MS, "<=", "#104"),
        check("condense p95 within the budget", stats["p95"], CONDENSE_TARGET_MS, "<=", "chat §16.1 target, advisory"),
    ]
    return result, lines


def cmd_answer_ttft(gateway: Gateway, args, corpus: Corpus) -> tuple[dict, list[str]]:
    models: list[dict] = []
    checks: list[dict] = []
    lines = [
        f"Answer model TTFT, streamed, prompt ≈ {args.prompt_tokens:,} tokens + max_tokens {args.max_tokens} "
        f"(ContextTokens 16,384 - answer reserve 1,024, chat §6.3), temperature 0.2",
        f"  estimate: TTFT p99 < {HEADERS_TIMEOUT_S:.0f} s headers timeout (chat §8.3, to-verify #149); "
        f"gateway adds ≈ 1.3 ms (chat §16.1)",
    ]
    for model in args.answer_models:
        calibration = calibrate(gateway, args, corpus, model)

        def call(model: str = model) -> Sample:
            start = time.perf_counter()
            response = gateway.chat(
                model, corpus.answer_messages(args.prompt_tokens), "answer", args.max_tokens, 0.2, stream=True
            )
            sample = Sample(start, time.perf_counter(), response.total_s * 1000, response.status)
            if response.status == 200:
                usage = response.usage or {}
                sample.extra = {
                    "headers_ms": response.headers_s * 1000,
                    "first_event_ms": (response.first_event_s or response.total_s) * 1000,
                    "ttft_ms": (response.first_token_s or response.total_s) * 1000,
                    "no_content": response.first_token_s is None,
                    "prompt_tokens": usage.get("prompt_tokens"),
                    "completion_tokens": usage.get("completion_tokens"),
                }
            elif response.status in (429, 503):
                sample.extra["retry_after"] = retry_after(response)
            return sample

        recorder = Recorder()
        run_loop(args.concurrency[0], args.warmup, args.duration, call, recorder)
        samples = recorder.samples
        ttft = [s.extra["ttft_ms"] for s in samples]
        decode = [
            s.extra["completion_tokens"] / ((s.latency_ms - s.extra["ttft_ms"]) / 1000)
            for s in samples
            if s.extra["completion_tokens"] and s.latency_ms > s.extra["ttft_ms"]
        ]
        prefill = [
            s.extra["prompt_tokens"] / (s.extra["ttft_ms"] / 1000)
            for s in samples
            if s.extra["prompt_tokens"] and s.extra["ttft_ms"] > 0
        ]
        prompt = [s.extra["prompt_tokens"] for s in samples if s.extra["prompt_tokens"]]
        row = {
            "model": model,
            "concurrency": args.concurrency[0],
            "calibration": calibration,
            "prompt_tokens_mean": rnd(sum(prompt) / len(prompt) if prompt else None),
            "headers_ms": latency_stats([s.extra["headers_ms"] for s in samples]),
            "first_event_ms": latency_stats([s.extra["first_event_ms"] for s in samples]),
            "ttft_ms": latency_stats(ttft),
            "total_ms": latency_stats([s.latency_ms for s in samples]),
            "share_ttft_over_headers_timeout": share_over(ttft, HEADERS_TIMEOUT_S * 1000),
            "streams_without_content": sum(s.extra["no_content"] for s in samples),
            "prefill_tokens_per_s_mean": rnd(sum(prefill) / len(prefill) if prefill else None),
            "decode_tokens_per_s_mean": rnd(sum(decode) / len(decode) if decode else None),
            "errors": recorder.errors,
        }
        models.append(row)
        stats = row["ttft_ms"]
        lines.append(
            f"  {model}: prompt {fmt(row['prompt_tokens_mean'])} tokens; TTFT p50 {fmt(stats['p50'], ' ms')}, "
            f"p95 {fmt(stats['p95'], ' ms')}, p99 {fmt(stats['p99'], ' ms')}{p99_note(stats['n'])} "
            f"(limit {HEADERS_TIMEOUT_S * 1000:,.0f} ms); headers p99 {fmt(row['headers_ms']['p99'], ' ms')}; "
            f"total p50 {fmt(row['total_ms']['p50'], ' ms')}, p95 {fmt(row['total_ms']['p95'], ' ms')}; "
            f"prefill ≈ {fmt(row['prefill_tokens_per_s_mean'])} tokens/s, decode ≈ "
            f"{fmt(row['decode_tokens_per_s_mean'])} tokens/s per stream; errors {recorder.errors or 0}"
            + (
                f"\n      note: {row['streams_without_content']} streams sent no content (TTFT = total time)"
                if row["streams_without_content"]
                else ""
            )
        )
        checks.append(check(f"answer TTFT p99, {model}", stats["p99"], HEADERS_TIMEOUT_S * 1000, "<", "#149"))
    return {"models": models, "checks": checks}, lines


def calibrate(gateway: Gateway, args, corpus: Corpus, model: str) -> dict:
    """Fits the characters per token to the model's tokenizer with up to three streamed requests, so the prompt is
    --prompt-tokens as the model counts them (usage.prompt_tokens); without usage the estimate stays."""
    rounds = []
    for _ in range(3):
        messages = corpus.answer_messages(args.prompt_tokens)
        chars = sum(len(m["content"]) for m in messages)
        response = gateway.chat(model, messages, "answer", 16, 0.2, stream=True)
        if response.status != 200:
            raise RunAborted(f"calibration request for {model} answered {response.status}: {error_text(response)}")
        tokens = (response.usage or {}).get("prompt_tokens")
        rounds.append({"chars": chars, "prompt_tokens": tokens})
        if not tokens:
            break
        corpus.chars_per_token = chars / tokens
        if abs(tokens - args.prompt_tokens) <= args.prompt_tokens * 0.02:
            break
    return {
        "rounds": rounds,
        "chars_per_token": round(corpus.chars_per_token, 3),
        "from_usage": bool(rounds and rounds[-1]["prompt_tokens"]),
    }


def check(name: str, measured: float | None, limit: float, op: str, ref: str) -> dict:
    ok = None if measured is None else {"<=": measured <= limit, "<": measured < limit, ">=": measured >= limit}[op]
    return {"check": name, "measured": measured, "limit": limit, "op": op, "pass": ok, "ref": ref}


VERDICT = {True: "pass", False: "FAIL", None: "no data"}
COMMANDS = {
    "embed-throughput": cmd_embed_throughput,
    "query-under-load": cmd_query_under_load,
    "condense": cmd_condense,
    "answer-ttft": cmd_answer_ttft,
}


# --------------------------------------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------------------------------------


def int_list(value: str) -> list[int]:
    return [int(v) for v in value.split(",") if v.strip()]


def str_list(value: str) -> list[str]:
    return [v.strip() for v in value.split(",") if v.strip()]


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0], formatter_class=argparse.RawTextHelpFormatter
    )
    parser.add_argument("command", choices=[*COMMANDS, "all"])
    parser.add_argument("--base-url", required=True, help="gateway base URL, as Gateway:BaseUrl (without /v1)")
    parser.add_argument(
        "--key-file",
        required=True,
        type=Path,
        help="file holding the gateway key of the ceiling's level (ume-sk-…); never printed",
    )
    parser.add_argument(
        "--ceiling",
        default="OnPrem",
        choices=CEILINGS,
        help="x-ume-max-residency sent on every call (default OnPrem: on-prem capacity)",
    )
    parser.add_argument("--embedding-model", default="bge-m3")
    parser.add_argument("--condense-model", default="ume/small-onprem", help="Settings.Chat.CondenseModel")
    parser.add_argument(
        "--answer-models",
        type=str_list,
        default=["ume/chat-onprem"],
        help="comma-separated; the slowest pilot answer model decides #149",
    )
    parser.add_argument("--duration", type=float, help="measured seconds per step (default per command)")
    parser.add_argument("--warmup", type=float, help="warm-up seconds per step, discarded (default per command)")
    parser.add_argument(
        "--concurrency",
        type=int_list,
        help="client concurrency; embed-throughput takes a list (default 1,2,4), others the first",
    )
    parser.add_argument("--batch-sizes", type=int_list, default=[1, 8, 16, 32, 64], help="embed-throughput")
    parser.add_argument("--chunk-tokens", type=int, default=450, help="tokens per chunk text (engine §18)")
    parser.add_argument("--load-batch-sizes", type=int_list, default=[32, 64], help="query-under-load background")
    parser.add_argument(
        "--load-concurrency",
        type=int,
        default=2,
        help="query-under-load: concurrent background batch requests (Worker:Concurrency:embed)",
    )
    parser.add_argument("--query-concurrency", type=int, default=1, help="query-under-load: foreground streams")
    parser.add_argument("--query-interval-ms", type=float, default=200, help="query-under-load: pause between queries")
    parser.add_argument("--prompt-tokens", type=int, default=15_360, help="answer-ttft prompt size (16,384 - 1,024)")
    parser.add_argument("--max-tokens", type=int, default=1024, help="answer-ttft max_tokens (answer reserve)")
    parser.add_argument(
        "--chars-per-token",
        type=float,
        default=4.0,
        help="estimate when the gateway reports no usage (the gateway's own estimate is 4)",
    )
    parser.add_argument("--request-timeout", type=float, default=300, help="socket timeout per request, seconds")
    parser.add_argument("--ca-file", help="CA bundle for an https gateway with a private CA")
    parser.add_argument("--out-dir", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--seed", type=int, default=22)
    parser.add_argument(
        "--smoke",
        action="store_true",
        help="mark the run as a smoke test of the script (fake gateway): not a capacity result",
    )
    parser.add_argument("--label", default="", help="free text stored with the result (hardware, gateway version …)")
    return parser.parse_args(argv)


DEFAULTS = {  # duration, warm-up (seconds), concurrency
    "embed-throughput": (60, 10, [1, 2, 4]),
    "query-under-load": (120, 15, [1]),
    "condense": (120, 10, [1]),
    "answer-ttft": (600, 30, [1]),
}


def run(command: str, args: argparse.Namespace, key: str) -> Path:
    duration, warmup, concurrency = DEFAULTS[command]
    step_args = argparse.Namespace(**vars(args))
    step_args.duration = args.duration if args.duration is not None else duration
    step_args.warmup = args.warmup if args.warmup is not None else warmup
    step_args.concurrency = args.concurrency or concurrency
    started = datetime.now(UTC)
    run_id = f"capacity-{started:%Y%m%dT%H%M%SZ}-{secrets.token_hex(3)}"
    gateway = Gateway(args.base_url, key, args.ceiling, run_id, args.request_timeout, args.ca_file)
    corpus = Corpus(args.seed, args.chars_per_token)
    print(
        f"{command}: run {run_id}, {args.base_url}, ceiling {args.ceiling}, duration {step_args.duration} s, "
        f"warm-up {step_args.warmup} s{' (SMOKE TEST)' if args.smoke else ''}",
        file=sys.stderr,
        flush=True,
    )
    result, lines = COMMANDS[command](gateway, step_args, corpus)
    finished = datetime.now(UTC)
    parameters = {k: (str(v) if isinstance(v, Path) else v) for k, v in vars(step_args).items() if k != "key_file"}
    document = {
        "tool": "scripts/capacity/capacity.py",
        "command": command,
        "run_id": run_id,
        "smoke_test": args.smoke,
        "label": args.label,
        "started_at": started.isoformat(),
        "finished_at": finished.isoformat(),
        "gateway": {"base_url": args.base_url, "ceiling_sent": args.ceiling, "observed": gateway.observed},
        "parameters": parameters,
        "result": result,
    }
    header = [
        f"{'SMOKE TEST (fake gateway): these numbers test the script, not capacity. ' if args.smoke else ''}"
        f"{command} {run_id} ({started:%Y-%m-%d %H:%M}-{finished:%H:%M} UTC){' - ' + args.label if args.label else ''}",
        f"  gateway {args.base_url}, x-ume-max-residency {args.ceiling} sent on every call; answered from "
        f"{gateway.observed.get('x-ume-residency', {})}, providers {gateway.observed.get('x-ume-provider', {})}",
    ]
    verdicts = [
        f"  check: {c['check']}: {fmt(c['measured'])} {c['op']} {fmt(c['limit'])} → {VERDICT[c['pass']]} ({c['ref']})"
        for c in result["checks"]
    ]
    summary = "\n".join(header + lines + verdicts) + "\n"
    args.out_dir.mkdir(parents=True, exist_ok=True)
    path = args.out_dir / f"{started:%Y%m%dT%H%M%SZ}-{command}.json"
    path.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    path.with_suffix(".txt").write_text(summary, encoding="utf-8")
    print(summary, flush=True)
    print(f"  → {path}", file=sys.stderr)
    return path


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    key = args.key_file.read_text(encoding="utf-8").strip()
    if not key:
        raise SystemExit(f"{args.key_file} is empty")
    commands = list(COMMANDS) if args.command == "all" else [args.command]
    try:
        for command in commands:
            run(command, args, key)
    except RunAborted as exc:
        print(f"ABORTED: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
