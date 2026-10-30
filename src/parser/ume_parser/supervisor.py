"""The supervisor: owns the conversion slots, their warm-up and their respawn (parser design §10).

It runs inside the API process on the asyncio loop and never imports Docling. Each slot is a `multiprocessing`
spawn process with a pipe (`ume_parser.slot.run`). A monitor task polls every slot: it records the warm-up report,
respawns a slot that died, failed its self-test or did not warm up within `warmup_timeout_s`, and keeps the
readiness rule of §10.2: ready once every slot has passed its self-test at start, then while at least one is warm.
"""

import asyncio
import contextlib
import enum
import logging
import multiprocessing
import shutil
import time
from dataclasses import dataclass, field
from multiprocessing.connection import Connection
from multiprocessing.process import BaseProcess

from ume_parser import slot
from ume_parser.settings import Settings

log = logging.getLogger(__name__)

POLL_INTERVAL_S = 0.5
STOP_GRACE_S = 5.0
RESPAWN_BACKOFF_S = (1.0, 5.0, 30.0, 120.0)


class SlotState(enum.StrEnum):
    STARTING = "starting"
    READY = "ready"
    DOWN = "down"


@dataclass
class Slot:
    id: int
    state: SlotState = SlotState.DOWN
    process: BaseProcess | None = None
    conn: Connection | None = None
    started_at: float = 0.0
    respawn_at: float = 0.0
    failures: int = 0
    warmup_ms: int | None = None
    problems: list[str] = field(default_factory=list)


class Supervisor:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._context = multiprocessing.get_context("spawn")
        self._slots = [Slot(i) for i in range(settings.workers)]
        self._all_warmed_once = False
        self._task: asyncio.Task[None] | None = None

    @property
    def convert_slots(self) -> int:
        return len(self._slots)

    @property
    def slots_ready(self) -> int:
        return sum(1 for s in self._slots if s.state is SlotState.READY)

    @property
    def live(self) -> bool:
        return self._task is not None and not self._task.done()

    @property
    def ready(self) -> bool:
        return self.live and self._all_warmed_once and self.slots_ready >= 1

    async def start(self) -> None:
        """Wipes the job directory, spawns every slot and starts the monitor (§10.2 step 1)."""
        jobs_dir = self._settings.jobs_dir
        shutil.rmtree(jobs_dir, ignore_errors=True)
        jobs_dir.mkdir(parents=True, exist_ok=True)
        for s in self._slots:
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
            target=slot.run,
            args=(s.id, str(self._settings.models_dir), self._settings.threads, child),
            name=f"convert-slot-{s.id}",
            daemon=True,
        )
        process.start()
        child.close()
        s.process, s.conn, s.state = process, parent, SlotState.STARTING
        s.started_at, s.warmup_ms, s.problems = time.monotonic(), None, []
        log.info("slot %d: spawned pid %d", s.id, process.pid)

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

    def _down(self, s: Slot, reason: str) -> None:
        if s.process is not None and s.process.is_alive():
            s.process.kill()
        self._terminate(s)
        delay = RESPAWN_BACKOFF_S[min(s.failures, len(RESPAWN_BACKOFF_S) - 1)]
        s.failures += 1
        s.respawn_at = time.monotonic() + delay
        log.warning("slot %d: down (%s), respawn in %.0f s", s.id, reason, delay)

    def _receive(self, s: Slot, conn: Connection) -> None:
        try:
            kind, report = conn.recv()
        except EOFError:
            return
        s.warmup_ms, s.problems = report["warmup_ms"], report["problems"]
        if kind == "ready":
            s.state, s.failures = SlotState.READY, 0
            self._all_warmed_once = self._all_warmed_once or all(x.state is SlotState.READY for x in self._slots)
            log.info("slot %d: warm in %d ms", s.id, s.warmup_ms)
        else:
            self._down(s, "self-test failed: " + "; ".join(s.problems))

    def _check(self, s: Slot, now: float) -> None:
        process, conn = s.process, s.conn
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
        elif s.state is SlotState.STARTING and now - s.started_at > self._settings.warmup_timeout_s:
            self._down(s, "warm-up timed out")

    async def _monitor(self) -> None:
        while True:
            now = time.monotonic()
            for s in self._slots:
                self._check(s, now)
            await asyncio.sleep(POLL_INTERVAL_S)
