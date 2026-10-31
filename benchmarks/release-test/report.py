"""Markdown report of the R1 release test and the chat overhead run, from the result files run.sh writes.

Usage: python3 -I report.py RESULTS_DIR [--max-ratio 1.10]

Modelled on the gateway's benchmarks/compare/latency_report.py. R1 (repo-and-deployment §8.3) passes when chat p95 time
to first token in phase 2 (during the crawl and the OCR batch) is at most --max-ratio times phase 1 (idle), no response
was an error, Postgres refused no connection and the background load ran through the whole of phase 2. Exit 0 when it
passes (and always for the overhead run, which only reports, chat design §16.2 item 3), 1 when it fails, 2 when result
files are missing. A dry run (run.sh --dry-run, FakeGateway directly) evaluates the same rule on its numbers, but
reports the checks it cannot make as "not checked" instead of failing them: it tests the harness, it is not R1.
"""

import argparse
import json
import sys
from pathlib import Path

PHASES = {"r1": ["idle", "load"], "overhead": ["overhead"]}
TITLES = {"idle": "1 idle", "load": "2 crawl + OCR", "overhead": "overhead"}
# The gateway's published Eneo comparison (chat design §16.2 item 3; gateway docs/performance.md:298-311).
ENEO = "Eneo: 54–68 ms added latency and 78 Postgres statements per message (gateway docs/performance.md)."


def load(path: Path) -> dict | None:
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None


def fmt(value: float | None, digits: int = 0) -> str:
    return "–" if value is None else f"{value:.{digits}f}"


def good_streams(probe: dict, k6: dict) -> int:
    return probe["requests"] - probe["errors"] + k6["requests"] - k6["failedStreams"]


def errors_of(probe: dict, k6: dict) -> int:
    return probe["errors"] + k6["failedStreams"] + k6["droppedIterations"]


def cpu_per_message(cpu: dict | None, container: str, messages: int) -> str:
    if not cpu or container not in cpu.get("containers", {}) or not messages:
        return "–"
    return f"{cpu['containers'][container] / 1e6 / messages:.2f}"


def refusals_text(pg: dict | None) -> str:
    if not pg or not pg.get("checked"):
        return "not checked"
    return str(pg["refusals"])


def phase_table(folder: Path, phases: list[str], meta: dict) -> list[str]:
    app = meta.get("app_container") or "rag-api"
    lines = [
        "| Phase | Streams (probe + k6) | Streams/s | Errors | TTFT p50 | TTFT p95 | TTFT p99 | First event p95 "
        "| Gap p99 | Whole stream p50 (k6) | Whole p95 (k6) | Dropped (k6) | Late starts (probe) "
        f"| Postgres refusals | CPU ms/message ({app}) |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for phase in phases:
        probe, k6 = load(folder / f"{phase}.probe.json"), load(folder / f"{phase}.k6.json")
        pg, cpu = load(folder / f"{phase}.postgres.json"), load(folder / f"{phase}.cpu.json")
        t, e, g = probe["ttft_ms"], probe["first_event_ms"], probe["gap_ms"]
        whole = k6.get("whole_ms") or {}
        streams = probe["requests"] + k6["requests"]
        rate = probe["streams_per_s"] + k6["rps"]
        lines.append(
            f"| {TITLES[phase]} | {streams} | {rate:.2f} | {errors_of(probe, k6)} | {fmt(t['p50'])} | {fmt(t['p95'])} "
            f"| {fmt(t['p99'])} | {fmt(e['p95'])} | {fmt(g['p99'])} | {fmt(whole.get('med'))} "
            f"| {fmt(whole.get('p(95)'))} "
            f"| {k6['droppedIterations']} | {probe['late_starts']} | {refusals_text(pg)} "
            f"| {cpu_per_message(cpu, app, good_streams(probe, k6))} |"
        )
    return lines


def r1_checks(folder: Path, meta: dict, max_ratio: float) -> tuple[list[tuple[str, str, str, str]], bool]:
    dry = meta.get("dry_run", False)
    idle, busy = load(folder / "idle.probe.json"), load(folder / "load.probe.json")
    checks: list[tuple[str, str, str, str]] = []
    passed = True

    p1, p2 = idle["ttft_ms"]["p95"], busy["ttft_ms"]["p95"]
    if p1 and p2 is not None:
        ratio = p2 / p1
        ok = ratio <= max_ratio
        checks.append(
            (
                "Chat p95 TTFT, phase 2 / phase 1",
                f"{ratio:.3f} ({p2:.0f} / {p1:.0f} ms)",
                f"≤ {max_ratio:.2f}",
                "pass" if ok else "**FAIL**",
            )
        )
    else:
        ok = False
        checks.append(
            ("Chat p95 TTFT, phase 2 / phase 1", "no successful stream in a phase", f"≤ {max_ratio:.2f}", "**FAIL**")
        )
    passed &= ok

    errors = sum(errors_of(load(folder / f"{p}.probe.json"), load(folder / f"{p}.k6.json")) for p in ("idle", "load"))
    ok = errors == 0
    checks.append(
        (
            "Error responses (probe and k6, both phases; k6 dropped iterations count)",
            str(errors),
            "0",
            "pass" if ok else "**FAIL**",
        )
    )
    passed &= ok

    pgs = [load(folder / f"{p}.postgres.json") for p in ("idle", "load")]
    if all(pg and pg.get("checked") for pg in pgs):
        refusals = sum(pg["refusals"] for pg in pgs)
        ok = refusals == 0
        first = next((pg["first"] for pg in pgs if pg.get("first")), None)
        checks.append(
            (
                "Postgres connection refusals (postgres and service logs)",
                str(refusals) + (f": `{first.replace('|', '/')}`" if first else ""),
                "0",
                "pass" if ok else "**FAIL**",
            )
        )
        passed &= ok
    else:
        checks.append(
            ("Postgres connection refusals", "not checked (no --deploy-env)", "0", "not checked" if dry else "**FAIL**")
        )
        passed &= dry

    ingest = load(folder / "load.ingest.json")
    if ingest is None:
        checks.append(
            (
                "Background load (crawl + OCR batch) through phase 2",
                "none (no --ingest-cmd)",
                "whole phase",
                "not checked" if dry else "**FAIL**",
            )
        )
        passed &= dry
    elif ingest["finished_early"]:
        how = "failed" if ingest["exit"] else "finished"
        checks.append(
            (
                "Background load (crawl + OCR batch) through phase 2",
                f"{how} after {ingest['ran_s']:.0f} s (exit {ingest['exit']})",
                "whole phase",
                "**FAIL**",
            )
        )
        passed = False
    else:
        checks.append(
            (
                "Background load (crawl + OCR batch) through phase 2",
                "running at the end of phase 2",
                "whole phase",
                "pass",
            )
        )
    return checks, passed


def header(meta: dict) -> list[str]:
    fake = meta.get("fake_latency")
    lines = [
        f"- Target: `{meta['base_url']}`, model `{meta['model']}`, request `{meta['request']}`",
        f"- Load: {meta['rate']} streamed messages/s in total (k6 {meta['rate'] - meta['probe_rate']:g}/s, "
        f"stream probe {meta['probe_rate']:g}/s), {meta['duration_s']} s per phase, "
        f"after a {meta['warmup_s']} s warm-up",
        f"- Started {meta['started_at']}, revision `{meta.get('revision') or 'unknown'}`",
    ]
    if fake:
        lines.append(
            f"- FakeGateway latency profile: FirstTokenMs={fake['first_token_ms']}, "
            f"TokenIntervalMs={fake['token_interval_ms']}, JitterPercent={fake['jitter_percent']}, "
            f"AnswerWords={fake['answer_words']}"
        )
    if meta.get("ingest_cmd"):
        lines.append(f"- Background load (phase 2): `{meta['ingest_cmd']}`")
    return lines


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("folder", type=Path)
    parser.add_argument("--max-ratio", type=float, default=1.10)
    args = parser.parse_args(argv)
    meta = load(args.folder / "meta.json")
    if meta is None:
        print(f"No meta.json in {args.folder}.", file=sys.stderr)
        return 2
    phases = PHASES[meta["mode"]]
    expected = [f"{p}.{kind}.json" for p in phases for kind in ("probe", "k6")]
    missing = [name for name in expected if not (args.folder / name).exists()]
    if missing:
        print(f"Missing result files: {', '.join(missing)}.", file=sys.stderr)
        return 2

    dry = meta.get("dry_run", False)
    out: list[str] = []
    if meta["mode"] == "r1":
        out.append("# R1 release test" + (" (dry run, not R1)" if dry else "") + "\n")
    else:
        out.append("# Chat overhead run" + (" (dry run)" if dry else "") + "\n")
    if dry:
        out.append(
            "> **Dry run, not R1.** The load went straight to FakeGateway's `/v1/chat/completions`, not through the "
            "chat API, on whatever machine ran it, with no crawl or OCR batch unless one is listed below. It proves "
            "the harness and this report; R1 runs on the pilot-shaped test host (repo-and-deployment §8.3).\n"
        )
    out += header(meta)
    out.append("")
    out.append(
        "Client side, per streamed chat message, in ms. TTFT = time to the first event with answer text (stream "
        "probe); whole stream = until `data: [DONE]` (k6). Gap = time between consecutive answer events.\n"
    )
    out += phase_table(args.folder, phases, meta)
    out.append("")

    passed = True
    if meta["mode"] == "r1":
        checks, passed = r1_checks(args.folder, meta, args.max_ratio)
        out.append("## Pass rule\n")
        out.append("| Check | Measured | Limit | Result |")
        out.append("|---|---|---|---|")
        out += [f"| {c} | {m} | {lim} | {r} |" for c, m, lim, r in checks]
        out.append("")
        verdict = "PASS" if passed else "FAIL"
        note = " (dry run: the harness's verdict on FakeGateway, not R1)" if dry else ""
        out.append(f"**Result: {verdict}**{note}")
    else:
        probe = load(args.folder / "overhead.probe.json")
        fake = meta.get("fake_latency")
        if fake:
            base = fake["first_token_ms"]
            t = probe["ttft_ms"]
            added = ", ".join(f"{k} {fmt(t[k] - base if t[k] is not None else None)}" for k in ("p50", "p95", "p99"))
            out.append(f"TTFT minus FakeGateway's FirstTokenMs ({base} ms, ±{fake['jitter_percent']} %): {added} ms.\n")
        out.append(
            "`rag.chat.overhead` p50/p95/p99: read from the deployment's metrics (OTLP); this harness does not "
            "scrape it.\n"
        )
        out.append(ENEO)
    print("\n".join(out))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
