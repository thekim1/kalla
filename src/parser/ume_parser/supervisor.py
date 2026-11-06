"""The supervisor: owns the parser's worker processes, their warm-up, watchdog, recycling and respawn (design §10).

It runs inside the API process on the asyncio loop and never imports Docling or torch. Workers are grouped in lanes,
each a list of `multiprocessing` spawn processes with a pipe:

| Lane | Processes | Entry point | Killed after | Recycled after |
|---|---|---|---|---|
| `convert` | `PARSER_WORKERS` | `slot.run` | deadline + `KILL_GRACE_S` | `PARSER_RECYCLE_JOBS` jobs, RSS growth |
| `inspect` | 1 | `inspect.run` | the 10 s inspect deadline | `INSPECT_RECYCLE_JOBS` jobs |
| `ner` | 1, none with `NER_ENABLED=false` | `ner.run` | the request deadline | `NER_RECYCLE_REQUESTS` or RSS growth |

A monitor task polls every process each `POLL_INTERVAL_S`: it records the warm-up report and the warm RSS, respawns a
process that died, failed its self-test or did not warm up in time, and runs the **RSS watchdog**: a convert slot
(with its children, Tesseract included) above `PARSER_SLOT_MAX_RSS_MB` is killed, and the request it was serving
answers `resource_limit`. An idle process due for recycling (job count, or RSS grown beyond its warm RSS) is stopped
and started again at once, without the respawn back-off. Readiness follows §10.2 on the convert lane only: ready once
every slot passed its self-test at start, then while at least one is warm or cold.

**Cold processes** (resource review 2026-11-06): a process of a lane with an idle stop (`PARSER_IDLE_STOP_S`,
`NER_IDLE_STOP_S`; 0 = never) that served no request for that long is stopped and left `cold`: the monitor does not
respawn it. A request that finds no free warm process wakes one cold process of its lane (spawned at once, without the
back-off) and is answered `not_ready` (or `busy` while another process of the lane is warm); the caller retries after
`Retry-After`. `ner-0` with an idle stop starts cold, so NER loads on the first `/v1/ner`; the convert slots start
warm, so the start-up self-test and readiness stay as they are.

A request takes a free warm process of its lane at once or is refused with `busy` (or `not_ready`): there is no
queue (§10.1), except that up to `NER_QUEUE` NER requests may wait for `ner-0`. While a process works, the request's
task owns its pipe: it kills the process when the deadline passes (`timeout`), when the client disconnects, or finds
it dead (`worker_crashed`, or `resource_limit` when the watchdog killed it); the monitor then respawns it.
"""

import asyncio
import contextlib
import enum
import logging
import multiprocessing
import shutil
import time
from collections import Counter
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from multiprocessing.connection import Connection
from multiprocessing.process import BaseProcess
from typing import Any

import psutil

from ume_parser import inspect, ner, slot
from ume_parser.errors import ParserError
from ume_parser.settings import NerSettings, Settings

log = logging.getLogger(__name__)

POLL_INTERVAL_S = 0.5
STOP_GRACE_S = 5.0
RESPAWN_BACKOFF_S = (1.0, 5.0, 30.0, 120.0)
JOB_POLL_S = 0.05
DISCONNECT_POLL_S = 1.0
KILL_GRACE_S = 10.0  # a convert slot is killed this long after the request's deadline (§10.3)
BUSY_RETRY_AFTER_S = 2
NOT_READY_RETRY_AFTER_S = 5
INSPECT_RECYCLE_JOBS = 200
INSPECT_WARMUP_TIMEOUT_S = 60.0
NER_RSS_GROWTH = 0.4
NER_WARMUP_TIMEOUT_S = 300.0
MB = 1024 * 1024

CONVERT, INSPECT, NER = "convert", "inspect", "ner"


class ClientDisconnectedError(Exception):
    """The client closed the connection while its job ran; the process was killed."""


class SlotState(enum.StrEnum):
    STARTING = "starting"
    READY = "ready"
    DOWN = "down"
    COLD = "cold"  # stopped while idle; started again by the next request of its lane


@dataclass(frozen=True)
class Lane:
    name: str
    size: int
    target: Callable[..., None]
    threads: int
    warmup_timeout_s: float
    max_jobs: int
    kill_grace_s: float
    max_rss_bytes: int | None = None
    rss_growth: float | None = None
    queue: int = 0
    idle_stop_s: float = 0.0  # 0: never stopped when idle
    starts_cold: bool = False


@dataclass
class Slot:
    lane: Lane
    id: int
    state: SlotState = SlotState.DOWN
    process: BaseProcess | None = None
    conn: Connection | None = None
    started_at: float = 0.0
    respawn_at: float = 0.0
    failures: int = 0
    warmup_ms: int | None = None
    problems: list[str] = field(default_factory=list)
    busy: bool = False
    jobs: int = 0
    rss: int = 0
    warm_rss: int | None = None
    recycle: str | None = None  # the reason, once the process is due for recycling
    killed_for: str | None = None  # set by the watchdog when it kills a busy process
    used_at: float = 0.0  # the last time it became warm or finished a request
    woken: bool = False  # spawned from cold by a request, not yet warm

    @property
    def name(self) -> str:
        return f"{self.lane.name}-{self.id}"


def _rss(process: BaseProcess) -> int:
    """Resident memory of a worker process and its children (Tesseract runs as a child of a slot).

    A child that has forked but not yet exec'd shares the worker's pages and reports the worker's whole RSS, so only
    children running another program count (measured: a slot of 1.8 GB read as 3.7 GB while Docling started
    Tesseract).
    """
    try:
        root = psutil.Process(process.pid)
        own = root.exe()
        total = root.memory_info().rss
        for child in root.children(recursive=True):
            with contextlib.suppress(psutil.Error):
                if child.exe() != own:
                    total += child.memory_info().rss
        return total
    except (psutil.Error, ValueError):
        return 0


class Supervisor:
    def __init__(
        self,
        settings: Settings,
        ner_settings: NerSettings | None = None,
        targets: dict[str, Callable[..., None]] | None = None,
    ) -> None:
        """`targets` replaces a lane's entry point; tests pass fake workers with the same pipe protocol."""
        self._settings = settings
        ner_settings = ner_settings or NerSettings()
        entry = {CONVERT: slot.run, INSPECT: inspect.run, NER: ner.run, **(targets or {})}
        self.lanes = {
            CONVERT: Lane(
                CONVERT,
                settings.workers,
                entry[CONVERT],
                settings.threads,
                settings.warmup_timeout_s,
                settings.recycle_jobs,
                KILL_GRACE_S,
                max_rss_bytes=settings.slot_max_rss_mb * MB,
                rss_growth=settings.recycle_rss_growth,
                idle_stop_s=settings.idle_stop_s,
            ),
            INSPECT: Lane(INSPECT, 1, entry[INSPECT], 1, INSPECT_WARMUP_TIMEOUT_S, INSPECT_RECYCLE_JOBS, 0.0),
            NER: Lane(
                NER,
                1 if ner_settings.enabled else 0,
                entry[NER],
                ner_settings.threads,
                NER_WARMUP_TIMEOUT_S,
                ner_settings.recycle_requests,
                0.0,
                rss_growth=NER_RSS_GROWTH,
                queue=ner_settings.queue,
                idle_stop_s=ner_settings.idle_stop_s,
                starts_cold=ner_settings.idle_stop_s > 0,
            ),
        }
        self._context = multiprocessing.get_context("spawn")
        self._slots = [Slot(lane, i) for lane in self.lanes.values() for i in range(lane.size)]
        self._all_warmed_once = False
        self._task: asyncio.Task[None] | None = None
        self._in_flight: set[tuple[str, str]] = set()
        self._waiting: Counter[str] = Counter()
        self.recycles: Counter[str] = Counter()

    def slots(self, lane: str) -> list[Slot]:
        return [s for s in self._slots if s.lane.name == lane]

    def _ready_in(self, lane: str) -> int:
        return sum(1 for s in self.slots(lane) if s.state is SlotState.READY)

    @property
    def convert_slots(self) -> int:
        return self.lanes[CONVERT].size

    @property
    def slots_ready(self) -> int:
        return self._ready_in(CONVERT)

    @property
    def inspect_slots(self) -> int:
        return self.lanes[INSPECT].size

    @property
    def ner_ready(self) -> bool:
        return self._ready_in(NER) > 0

    @property
    def live(self) -> bool:
        return self._task is not None and not self._task.done()

    @property
    def slots_cold(self) -> int:
        return sum(1 for s in self.slots(CONVERT) if s.state is SlotState.COLD or s.woken)

    @property
    def ready(self) -> bool:
        """§10.2 on the convert lane; a cold slot (or one warming after a wake) counts, since a request starts it."""
        return self.live and self._all_warmed_once and self.slots_ready + self.slots_cold >= 1

    def check_capacity(self, lane: str = CONVERT) -> None:
        """Refuses at once when no process of `lane` could take a request (`not_ready`, `busy`), before its body is
        read. Without a free warm process it wakes a cold one of the lane first."""
        slots = self.slots(lane)
        if not self.live or (lane == CONVERT and not self.ready):
            raise ParserError("not_ready", retry_after_s=NOT_READY_RETRY_AFTER_S)
        if any(s.state is SlotState.READY and not s.busy for s in slots):
            return
        cold = next((s for s in slots if s.state is SlotState.COLD), None)
        if cold is not None:
            self._wake(cold)
        if any(s.state is SlotState.READY for s in slots):
            raise ParserError("busy", retry_after_s=BUSY_RETRY_AFTER_S)
        raise ParserError("not_ready", retry_after_s=NOT_READY_RETRY_AFTER_S)

    def reserve(self, request_id: str, lane: str = CONVERT) -> Slot:
        """Takes a free warm process of `lane` for `request_id`, or refuses at once (`not_ready`, `busy`,
        `duplicate_in_flight`)."""
        if (lane, request_id) in self._in_flight:
            raise ParserError("duplicate_in_flight")
        self.check_capacity(lane)
        for s in self.slots(lane):
            if s.state is SlotState.READY and not s.busy:
                s.busy = True
                self._in_flight.add((lane, request_id))
                return s
        raise ParserError("busy", retry_after_s=BUSY_RETRY_AFTER_S)

    async def acquire(self, request_id: str, lane: str, wait_s: float) -> Slot:
        """`reserve`; when the lane is busy and fewer than its `queue` requests wait, waits up to `wait_s` first."""
        try:
            return self.reserve(request_id, lane)
        except ParserError as exc:
            if exc.code != "busy" or self._waiting[lane] >= self.lanes[lane].queue:
                raise
        self._waiting[lane] += 1
        try:
            give_up = time.monotonic() + wait_s
            while True:
                await asyncio.sleep(JOB_POLL_S)
                try:
                    return self.reserve(request_id, lane)
                except ParserError as exc:
                    if exc.code != "busy" or time.monotonic() >= give_up:
                        raise
        finally:
            self._waiting[lane] -= 1

    def release(self, s: Slot, request_id: str) -> None:
        s.busy = False
        s.used_at = time.monotonic()
        self._in_flight.discard((s.lane.name, request_id))
        s.jobs += 1
        if s.jobs >= s.lane.max_jobs and s.recycle is None:
            s.recycle = "max_jobs"

    async def run(
        self,
        s: Slot,
        kind: str,
        job: dict[str, Any],
        deadline_s: float,
        is_disconnected: Callable[[], Awaitable[bool]],
    ) -> dict[str, Any]:
        """Runs `job` on the reserved process `s` and returns its result; raises `ParserError` or
        `ClientDisconnectedError`."""
        process, conn = s.process, s.conn
        if process is None or conn is None or s.state is not SlotState.READY:
            raise ParserError("busy", retry_after_s=BUSY_RETRY_AFTER_S)
        s.killed_for = None
        conn.send((kind, job))
        now = time.monotonic()
        kill_at, check_at = now + deadline_s + s.lane.kill_grace_s, now + DISCONNECT_POLL_S
        while True:
            if conn.poll():
                try:
                    result_kind, payload = conn.recv()
                except EOFError:
                    raise self._died(s) from None
                if result_kind == "result":
                    return payload
                raise ParserError.from_wire(payload)
            if not process.is_alive():
                raise self._died(s)
            now = time.monotonic()
            if now >= kill_at:
                self._down(s, "deadline")
                raise ParserError("timeout")
            if now >= check_at:
                if await is_disconnected():
                    self._down(s, "disconnect")
                    raise ClientDisconnectedError
                check_at = now + DISCONNECT_POLL_S
            await asyncio.sleep(JOB_POLL_S)

    def _died(self, s: Slot) -> ParserError:
        if s.killed_for == "rss_limit":
            self._down(s, "rss_limit", failure=False)
            return ParserError("resource_limit")
        exitcode = s.process.exitcode if s.process is not None else None
        self._down(s, f"crashed with code {exitcode}")
        return ParserError("worker_crashed")

    async def start(self) -> None:
        """Wipes the job directory, spawns every process and starts the monitor (§10.2 step 1)."""
        jobs_dir = self._settings.jobs_dir
        shutil.rmtree(jobs_dir, ignore_errors=True)
        jobs_dir.mkdir(parents=True, exist_ok=True)
        for s in self._slots:
            if s.lane.starts_cold:
                s.state = SlotState.COLD
            else:
                self._spawn(s)
        self._task = asyncio.create_task(self._monitor(), name="parser-supervisor")

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
        await asyncio.gather(*(asyncio.to_thread(self._terminate, s) for s in self._slots))

    def _spawn(self, s: Slot) -> None:
        parent, child = self._context.Pipe(duplex=True)
        process = self._context.Process(
            target=s.lane.target,
            args=(s.id, str(self._settings.models_dir), s.lane.threads, child),
            name=s.name,
            daemon=True,
        )
        process.start()
        child.close()
        s.process, s.conn, s.state = process, parent, SlotState.STARTING
        s.started_at, s.warmup_ms, s.problems = time.monotonic(), None, []
        s.jobs, s.rss, s.warm_rss, s.recycle, s.killed_for = 0, 0, None, None, None
        log.info("%s: spawned pid %d", s.name, process.pid)

    def _terminate(self, s: Slot) -> None:
        if s.conn is not None:
            with contextlib.suppress(OSError):
                s.conn.send("stop")
            s.conn.close()
        if s.process is not None:
            s.process.join(STOP_GRACE_S)
            if s.process.is_alive():
                s.process.kill()
                s.process.join()
            s.process.close()
        s.process, s.conn, s.state = None, None, SlotState.DOWN

    def _down(self, s: Slot, reason: str, *, failure: bool = True) -> None:
        """Kills and closes the process. A failure is respawned after a back-off that grows with consecutive failures;
        a watchdog kill at once."""
        if s.process is not None and s.process.is_alive():
            s.process.kill()
        self._terminate(s)
        s.woken = False
        delay = 0.0
        if failure:
            delay = RESPAWN_BACKOFF_S[min(s.failures, len(RESPAWN_BACKOFF_S) - 1)]
            s.failures += 1
        else:
            self.recycles[reason] += 1
        s.respawn_at = time.monotonic() + delay
        log.warning("%s: down (%s), respawn in %.0f s", s.name, reason, delay)

    def _wake(self, s: Slot) -> None:
        """Starts a cold process for a request; it counts as startable for readiness until warm or down."""
        log.info("%s: waking (cold)", s.name)
        s.woken = True
        self._spawn(s)

    def _sleep(self, s: Slot, now: float) -> None:
        """Stops an idle process and leaves it cold; the monitor does not respawn it."""
        log.info("%s: stopping after %.0f s idle at %d MB", s.name, now - s.used_at, s.rss // MB)
        self._terminate(s)
        s.state = SlotState.COLD

    def _recycle(self, s: Slot, reason: str) -> None:
        """Stops an idle process and starts it again at once."""
        log.info("%s: recycling (%s) after %d jobs at %d MB", s.name, reason, s.jobs, s.rss // MB)
        self.recycles[reason] += 1
        self._terminate(s)
        self._spawn(s)

    def _receive(self, s: Slot, conn: Connection) -> None:
        try:
            kind, report = conn.recv()
        except EOFError:
            return
        s.warmup_ms, s.problems = report["warmup_ms"], report["problems"]
        if kind == "ready":
            s.state, s.failures, s.woken = SlotState.READY, 0, False
            s.used_at = time.monotonic()
            s.warm_rss = _rss(s.process) if s.process is not None else None
            if s.lane.name == CONVERT:
                self._all_warmed_once = self._all_warmed_once or all(
                    x.state is SlotState.READY for x in self.slots(CONVERT)
                )
            log.info("%s: warm in %d ms at %d MB", s.name, s.warmup_ms, (s.warm_rss or 0) // MB)
        else:
            self._down(s, "self-test failed: " + "; ".join(s.problems))

    def _watch(self, s: Slot) -> None:
        """Samples a live process's RSS; the watchdog kills it above its lane's limit."""
        if s.process is None:
            return
        s.rss = _rss(s.process)
        limit = s.lane.max_rss_bytes
        if limit is None or s.rss <= limit:
            return
        log.warning("%s: RSS %d MB over the limit of %d MB", s.name, s.rss // MB, limit // MB)
        if s.busy:
            s.killed_for = "rss_limit"  # the request's task answers resource_limit and takes it down
            with contextlib.suppress(OSError, ValueError):
                s.process.kill()
        else:
            self._down(s, "rss_limit", failure=False)

    def _check(self, s: Slot, now: float) -> None:
        self._watch(s)
        if s.busy:
            return  # the request's task owns the pipe while the process works
        process, conn = s.process, s.conn
        if s.state is SlotState.COLD:
            return
        if process is None or conn is None:
            if now >= s.respawn_at:
                self._spawn(s)
            return
        if conn.poll():
            self._receive(s, conn)
            if s.state is SlotState.DOWN:
                return
        if not process.is_alive():
            self._down(s, f"exited with code {process.exitcode}")
        elif s.state is SlotState.STARTING and now - s.started_at > s.lane.warmup_timeout_s:
            self._down(s, "warm-up timed out")
        elif s.state is SlotState.READY:
            if s.recycle is not None:
                self._recycle(s, s.recycle)
            elif s.lane.idle_stop_s > 0 and now - s.used_at >= s.lane.idle_stop_s:
                self._sleep(s, now)
            elif s.lane.rss_growth is not None and s.warm_rss and s.rss > s.warm_rss * (1 + s.lane.rss_growth):
                self._recycle(s, "rss_growth")

    async def _monitor(self) -> None:
        while True:
            now = time.monotonic()
            for s in self._slots:
                self._check(s, now)
            await asyncio.sleep(POLL_INTERVAL_S)
