"""Checks a pull request's changed paths against .github/stream-ownership.txt (plans/p0/README.md §2).

Usage: python3 scripts/check-ownership.py <branch> [<path> ...]
       git diff --name-only origin/main...HEAD | python3 scripts/check-ownership.py <branch>

A branch a/* may change paths owned by stream a or shared; b/* likewise for stream b. Any other branch (the main
session's) is not checked. The first matching rule wins; a path no rule matches fails. Every pattern is anchored at the
repository root: * stays inside one path segment, ** crosses segments, and a trailing /** matches everything below.
"""

import re
import sys
from pathlib import Path

MAP = Path(__file__).resolve().parent.parent / ".github" / "stream-ownership.txt"


def to_regex(glob: str) -> re.Pattern[str]:
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("".join(out) + r"\Z")


def load_rules() -> list[tuple[str, re.Pattern[str], str]]:
    rules = []
    for number, line in enumerate(MAP.read_text(encoding="utf-8").splitlines(), 1):
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) != 2 or parts[1] not in ("a", "b", "shared", "main"):
            sys.exit(f"{MAP.name}:{number}: expected '<glob> <a|b|shared|main>', got '{line}'")
        rules.append((parts[0], to_regex(parts[0].lstrip("/")), parts[1]))
    return rules


def owner(path: str, rules: list[tuple[str, re.Pattern[str], str]]) -> tuple[str, str] | None:
    for glob, pattern, who in rules:
        if pattern.match(path):
            return glob, who
    return None


def main() -> int:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    branch = sys.argv[1]
    stream = branch.split("/", 1)[0] if "/" in branch else ""
    if stream not in ("a", "b"):
        print(f"Branch '{branch}' is not a stream branch (a/* or b/*): ownership not checked.")
        return 0

    paths = sys.argv[2:] or [line.strip() for line in sys.stdin if line.strip()]
    rules = load_rules()
    failures = []
    for path in paths:
        match = owner(path, rules)
        if match is None:
            failures.append(f"{path}: no rule in .github/stream-ownership.txt (ask the main session to add one)")
        elif match[1] not in (stream, "shared"):
            failures.append(f"{path}: owned by '{match[1]}' (rule '{match[0]}'), not stream {stream}")

    for failure in failures:
        print(f"::error::{failure}")
    if failures:
        print(f"{len(failures)} of {len(paths)} changed paths are outside stream {stream}'s paths. "
              "Open an issue labelled for the owner instead (AGENTS.md, 'Owned paths').")
        return 1
    print(f"All {len(paths)} changed paths belong to stream {stream} or are shared.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
