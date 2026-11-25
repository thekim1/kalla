#!/usr/bin/env python3
"""Unreleased change notes: one file per pull request in changes/, folded into CHANGELOG.md and the upgrade guide at a
release.

Every pull request that adds to CHANGELOG.md's or docs/upgrade-guide.md's *Unreleased* used to append at the same place,
so any two of them conflicted. Now a pull request adds its own files instead, named after its branch with "/" as "-":

  changes/<branch>.md           its CHANGELOG bullets, as they would stand under *Unreleased*
  changes/<branch>.upgrade.md   its upgrade guide bullets (only when an operator has something to know or do)

Each file holds one or more Markdown bullets ("- ..."), wrapped at 120 characters. At the release preparation the main
session runs `python3 scripts/unreleased.py fold`: it appends every note to the *Unreleased* section of its document in
the order the notes were added to git, deletes the files, and leaves the rest of the release preparation as before
(main-handbook "At each release").

Usage:
  python3 scripts/unreleased.py check   fail on a note that is not a bullet list or has an unknown name (the `changes, ownership, doc-refs` job)
  python3 scripts/unreleased.py show    print what fold would append, document by document
  python3 scripts/unreleased.py fold    append the notes and delete the files (git rm when tracked)
"""

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NOTES = ROOT / "changes"
TARGETS = {".md": ROOT / "CHANGELOG.md", ".upgrade.md": ROOT / "docs" / "upgrade-guide.md"}
HEADING = "## Unreleased"


def kind(path: Path) -> str | None:
    if path.name == "README.md":
        return None
    return ".upgrade.md" if path.name.endswith(".upgrade.md") else ".md" if path.suffix == ".md" else "?"


def added_at(path: Path) -> tuple[int, str]:
    """When the note was first committed (oldest first); an uncommitted note sorts last, by name."""
    out = subprocess.run(
        ["git", "log", "--diff-filter=A", "--format=%ct", "--", str(path.relative_to(ROOT))],
        cwd=ROOT, capture_output=True, text=True, check=False,
    ).stdout.split()
    return (int(out[-1]) if out else sys.maxsize, path.name)


def notes() -> list[Path]:
    return sorted((p for p in NOTES.glob("*") if p.is_file() and kind(p) is not None), key=added_at)


def problems() -> list[str]:
    found = []
    for p in notes():
        rel = p.relative_to(ROOT)
        if kind(p) == "?":
            found.append(f"{rel}: a note is <branch>.md or <branch>.upgrade.md")
            continue
        text = p.read_text(encoding="utf-8").strip("\n")
        if not text:
            found.append(f"{rel}: empty")
        elif not text.startswith("- "):
            found.append(f"{rel}: starts with a bullet (\"- \")")
        for n, line in enumerate(text.splitlines(), 1):
            if line.startswith("#"):
                found.append(f"{rel}:{n}: no headings in a note; the release preparation adds them")
            if len(line) > 120:
                found.append(f"{rel}:{n}: longer than 120 characters")
    return found


def by_target() -> dict[Path, list[Path]]:
    grouped: dict[Path, list[Path]] = {t: [] for t in TARGETS.values()}
    for p in notes():
        grouped[TARGETS[kind(p)]].append(p)
    return grouped


def fold_into(document: Path, files: list[Path]) -> None:
    lines = document.read_text(encoding="utf-8").splitlines()
    try:
        start = lines.index(HEADING)
    except ValueError:
        sys.exit(f"{document.relative_to(ROOT)}: no '{HEADING}' heading to fold into")
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    while end > start + 1 and not lines[end - 1].strip():
        end -= 1
    added = [line for f in files for line in f.read_text(encoding="utf-8").strip("\n").splitlines()]
    lines[end:end] = ([""] if end == start + 1 else []) + added
    document.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main(argv: list[str]) -> int:
    command = argv[1] if len(argv) > 1 else "check"
    found = problems()
    if found:
        print("\n".join(found))
        return 1
    if command == "check":
        print(f"{len(notes())} unreleased notes, all well-formed")
        return 0
    grouped = by_target()
    if command == "show":
        for document, files in grouped.items():
            print(f"=== {document.relative_to(ROOT)} ({len(files)} notes)")
            for f in files:
                print(f.read_text(encoding="utf-8").rstrip("\n"))
        return 0
    if command == "fold":
        for document, files in grouped.items():
            if files:
                fold_into(document, files)
        for f in notes():
            tracked = subprocess.run(["git", "ls-files", "--error-unmatch", str(f)], cwd=ROOT, capture_output=True)
            if tracked.returncode == 0:
                subprocess.run(["git", "rm", "-q", str(f)], cwd=ROOT, check=True)
            else:
                f.unlink()
        print("folded; review CHANGELOG.md and docs/upgrade-guide.md, then go on with the release preparation")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
