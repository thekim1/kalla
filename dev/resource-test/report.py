"""Writes the resource test's report (dev/resource-test/README.md). Standard library only.

Usage: python3 -I report.py OUTDIR

Reads OUTDIR/samples.tsv (from sample.py) and OUTDIR/facts.txt (`key<TAB>value` lines from run.sh) and writes
OUTDIR/report.md: max memory and mean/max cores per component and phase, then the run's facts.
"""

import collections
import statistics
import sys
from pathlib import Path

PHASES = ["idle", "ingest", "load-1", "load-8", "parser"]


def main() -> None:
    out = Path(sys.argv[1])
    rows = collections.defaultdict(lambda: collections.defaultdict(list))
    for line in (out / "samples.tsv").read_text(encoding="utf-8").splitlines():
        phase, _, component, mb, cores = line.split("\t")
        rows[component][phase].append((float(mb), float(cores)))
    phases = [p for p in PHASES if any(p in r for r in rows.values())]
    facts = [line.split("\t", 1) for line in (out / "facts.txt").read_text(encoding="utf-8").splitlines() if "\t" in line]

    lines = ["# Resource test report", ""]
    lines += [f"- **{k}:** {v}" for k, v in facts]
    lines += ["", "## Memory: max MB per phase", "", "| Component | " + " | ".join(phases) + " |",
              "|---|" + "---:|" * len(phases)]
    for component in sorted(rows):
        cells = [f"{max(m for m, _ in rows[component][p]):.0f}" if rows[component][p] else "–" for p in phases]
        lines.append(f"| {component} | " + " | ".join(cells) + " |")
    lines += ["", "## CPU: mean / max cores per phase", "", "| Component | " + " | ".join(phases) + " |",
              "|---|" + "---:|" * len(phases)]
    for component in sorted(rows):
        cells = []
        for p in phases:
            values = [c for _, c in rows[component][p]]
            cells.append(f"{statistics.fmean(values):.2f} / {max(values):.2f}" if values else "–")
        lines.append(f"| {component} | " + " | ".join(cells) + " |")
    lines += ["", "Total of the max memory per phase (all components): " + ", ".join(
        f"{p} {sum(max((m for m, _ in rows[c][p]), default=0) for c in rows) / 1024:.1f} GB" for p in phases), ""]
    (out / "report.md").write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
