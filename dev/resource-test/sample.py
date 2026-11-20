"""Resource sampler for the local resource test (dev/resource-test/README.md). Standard library only.

Usage: python3 -I sample.py LABEL SECONDS OUTFILE [--interval 5]

Every interval it appends one row per component to OUTFILE (tab-separated: label, unix time, component, RSS MB,
cores used since the previous sample). The .NET services run on the host under the AppHost, so they are read from
/proc (VmRSS, utime + stime); the containers come from `docker stats` (memory usage, CPU %). Only the platform's
components are recorded: the AppHost's own processes and other containers are not.
"""

import argparse
import os
import re
import subprocess
import time
from pathlib import Path

SERVICE = re.compile(r"Ume\.Rag\.(Api|AdminApi|Worker|FakeGateway)(\.dll)?( |$)")
CONTAINER = re.compile(r"^(postgres|clamav|keycloak|fixture-site|renderer|parser|kalla-restest-parser)(-|$)")
TICKS = os.sysconf("SC_CLK_TCK")
UNITS = {"B": 1 / 2**20, "KiB": 1 / 1024, "MiB": 1, "GiB": 1024, "kB": 1 / 1000, "MB": 1, "GB": 1000}


def services() -> dict[str, tuple[float, float]]:
    """Name -> (RSS MB, CPU seconds) of the Källa processes on the host."""
    found: dict[str, tuple[float, float]] = {}
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            cmdline = (entry / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
            match = SERVICE.search(cmdline)
            if not match:
                continue
            status = (entry / "status").read_text()
            stat = (entry / "stat").read_text().rsplit(")", 1)[1].split()
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
        rss = next((int(line.split()[1]) / 1024 for line in status.splitlines() if line.startswith("VmRSS:")), 0.0)
        cpu = (int(stat[11]) + int(stat[12])) / TICKS  # utime, stime (fields 14 and 15 of /proc/<pid>/stat)
        name = match.group(1)
        prev = found.get(name, (0.0, 0.0))
        found[name] = (prev[0] + rss, prev[1] + cpu)
    return found


def containers() -> dict[str, tuple[float, float]]:
    """Name -> (memory MB, cores) of the platform's containers, from one `docker stats` call."""
    out = subprocess.run(
        ["docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}"],
        capture_output=True, text=True, check=False,
    ).stdout
    found = {}
    for line in out.splitlines():
        name, mem, cpu = line.split("\t")
        if not CONTAINER.match(name):
            continue
        value, unit = re.match(r"([\d.]+)\s*([A-Za-z]+)", mem.split("/")[0].strip()).groups()
        component = "parser" if "parser" in name else name.rsplit("-", 1)[0] if name.count("-") else name
        found[component] = (float(value) * UNITS.get(unit, 1), float(cpu.rstrip("%") or 0) / 100)
    return found


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("label")
    parser.add_argument("seconds", type=float)
    parser.add_argument("outfile", type=Path)
    parser.add_argument("--interval", type=float, default=5.0)
    args = parser.parse_args()

    end = time.monotonic() + args.seconds
    previous, previous_at = services(), time.monotonic()
    with args.outfile.open("a", encoding="utf-8") as out:
        while time.monotonic() < end:
            time.sleep(args.interval)
            now, now_at = services(), time.monotonic()
            elapsed = now_at - previous_at
            stamp = int(time.time())
            for name, (rss, cpu) in sorted(now.items()):
                cores = max(0.0, (cpu - previous.get(name, (0.0, cpu))[1]) / elapsed)
                out.write(f"{args.label}\t{stamp}\t{name}\t{rss:.0f}\t{cores:.3f}\n")
            for name, (mem, cores) in sorted(containers().items()):
                out.write(f"{args.label}\t{stamp}\t{name}\t{mem:.0f}\t{cores:.3f}\n")
            out.flush()
            previous, previous_at = now, now_at


if __name__ == "__main__":
    main()
