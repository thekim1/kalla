#!/usr/bin/env python3
"""Checks that design-section citations and relative Markdown links resolve.

A citation is a document name followed by a section number ("engine design §9.6", "data-model §2.1",
"`plans/platform/repo-and-deployment.md` §3"); it resolves when the document has a heading with that number, or a
numbered list item or table row under one ("data-model §12.3" is row 3 of §12).
A relative Markdown link resolves when its file exists and, with a #fragment, the file has a heading with that slug.
Two headings with the same section number in one document are a miss too.

Usage: python3 -I scripts/check-doc-refs.py (prints every miss, exits 1 if there is one)
"""
import os
import re
import subprocess
import sys
import unicodedata

ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()

ALIASES = {
    "engine design": "plans/ingestion-worker/design-engine.md",
    "engine": "plans/ingestion-worker/design-engine.md",
    "connectors design": "plans/ingestion-worker/design-connectors.md",
    "connectors": "plans/ingestion-worker/design-connectors.md",
    "processing design": "plans/ingestion-worker/design-processing.md",
    "processing": "plans/ingestion-worker/design-processing.md",
    "knowledge design": "plans/knowledge-api/design.md",
    "knowledge-api design": "plans/knowledge-api/design.md",
    "knowledge": "plans/knowledge-api/design.md",
    "chat design": "plans/chat-api/design.md",
    "chat-api design": "plans/chat-api/design.md",
    "chat": "plans/chat-api/design.md",
    "admin design": "plans/admin/design.md",
    "admin": "plans/admin/design.md",
    "identity design": "plans/identity/design.md",
    "identity": "plans/identity/design.md",
    "identity plan": "plans/identity/plan.md",
    "parser design": "plans/parser/design.md",
    "parser": "plans/parser/design.md",
    "tools design": "plans/tools/design.md",
    "tools": "plans/tools/design.md",
    "gateway design": "plans/gateway-changes/design.md",
    "gateway-changes design": "plans/gateway-changes/design.md",
    "gateway-changes": "plans/gateway-changes/design.md",
    "data-model": "plans/platform/data-model.md",
    "repo-and-deployment": "plans/platform/repo-and-deployment.md",
    "weakness review": "plans/weakness-review.md",
    "weakness-review": "plans/weakness-review.md",
    "simplification pass": "plans/simplification-pass.md",
    "simplification-pass": "plans/simplification-pass.md",
    "performance pass": "plans/performance-pass.md",
    "performance-pass": "plans/performance-pass.md",
    "consistency pass": "plans/consistency-pass.md",
    "general idea": "01-general-idea.md",
}

SCAN_DIRS = ["src", "tests", "plans", "docs", "scripts", "deploy", "contracts", ".github", "images"]
SCAN_EXT = {".cs", ".ts", ".tsx", ".vue", ".py", ".sh", ".ps1", ".yml", ".yaml", ".md", ".json", ".sql", ".props",
            ".csproj", ".toml", ".conf", ".txt", ".html"}
SKIP_PATHS = {".github/skills"}
SKIP_PARTS = {"node_modules", "bin", "obj", ".git", "dist", "gold-set", "fixtures"}

ALIAS_RE = "|".join(sorted((re.escape(a) for a in ALIASES), key=len, reverse=True))
NAMED = re.compile(r"(?<![\w-])(" + ALIAS_RE + r")(?:'s)?,? §\s?(\d+[a-z]?(?:\.\d+[a-z]?)*)", re.IGNORECASE)
PATHED = re.compile(r"`?((?:\.\./)*[\w./-]+\.md)`?(?:'s)?,? §\s?(\d+[a-z]?(?:\.\d+[a-z]?)*)")
LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")


def files():
    for name in os.listdir(ROOT):
        if name.endswith(".md"):
            yield name
    for d in SCAN_DIRS:
        for base, dirs, names in os.walk(os.path.join(ROOT, d)):
            dirs[:] = [x for x in dirs if x not in SKIP_PARTS and os.path.relpath(os.path.join(base, x), ROOT) not in SKIP_PATHS]
            for n in names:
                if os.path.splitext(n)[1] in SCAN_EXT:
                    yield os.path.relpath(os.path.join(base, n), ROOT)


_headings = {}


def headings(path):
    if path not in _headings:
        numbers, slugs, counts, dupes, seen = set(), set(), {}, [], set()
        current = None
        try:
            with open(os.path.join(ROOT, path), encoding="utf-8") as f:
                fence = False
                for line in f:
                    if line.lstrip().startswith("```"):
                        fence = not fence
                    if fence:
                        continue
                    m = HEADING.match(line)
                    if not m:
                        item = re.match(r"^(?:(\d+)\.\s|\|\s*\**(\d+)\**\s*\|)", line)
                        if item and current:
                            numbers.add(f"{current}.{item.group(1) or item.group(2)}")
                        continue
                    text = m.group(2)
                    n = re.match(r"(?:§\s?)?(\d+[a-z]?(?:\.\d+[a-z]?)*)[.\s:)]", text + " ")
                    current = n.group(1).lower() if n else None
                    if n:
                        if current in seen:
                            dupes.append(current)
                        seen.add(current)
                        numbers.add(current)
                    s = slug(text)
                    k = counts.get(s, 0)
                    counts[s] = k + 1
                    slugs.add(s if k == 0 else f"{s}-{k}")
        except OSError:
            _headings[path] = None
            return None
        _headings[path] = (numbers, slugs, dupes)
    return _headings[path]


def slug(text):
    text = re.sub(r"`|\*\*|\*|_(?=\w)|(?<=\w)_", "", text)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = unicodedata.normalize("NFC", text).lower()
    text = "".join(c for c in text if c.isalnum() or c in " -_")
    return text.replace(" ", "-")


_md_index = None


def resolve_md(cited, src):
    global _md_index
    cand = os.path.normpath(os.path.join(os.path.dirname(src), cited))
    if os.path.isfile(os.path.join(ROOT, cand)):
        return cand
    if os.path.isfile(os.path.join(ROOT, cited)):
        return cited
    if _md_index is None:
        _md_index = [f for f in files() if f.endswith(".md")]
    tail = cited.lstrip("./")
    hits = [f for f in _md_index if f == tail or f.endswith("/" + tail)]
    hits = [h for h in hits if h.startswith("plans/") or "/" not in h] or hits
    return hits[0] if len(hits) == 1 else None


def check():
    misses = []
    for src in files():
        if src.endswith(".md"):
            h = headings(src)
            for d in (h[2] if h else []):
                misses.append(f"{src}: duplicate section number {d}")
    for src in files():
        try:
            with open(os.path.join(ROOT, src), encoding="utf-8") as f:
                lines = f.readlines()
        except (OSError, UnicodeDecodeError):
            continue
        for no, line in enumerate(lines, 1):
            for m in NAMED.finditer(line):
                target = ALIASES[m.group(1).lower()]
                h = headings(target)
                if h and m.group(2).lower() not in h[0]:
                    misses.append(f"{src}:{no}: section {m.group(1)} §{m.group(2)} -> {target}")
            for m in PATHED.finditer(line):
                target = resolve_md(m.group(1), src)
                if target is None:
                    continue
                h = headings(target)
                if h and m.group(2).lower() not in h[0]:
                    misses.append(f"{src}:{no}: section {m.group(1)} §{m.group(2)} -> {target}")
            if not src.endswith(".md"):
                continue
            for m in LINK.finditer(re.sub(r"`[^`]*`", "", line)):
                url = m.group(1)
                if re.match(r"^[a-z][a-z0-9+.-]*:", url, re.I) or url.startswith("<"):
                    continue
                path, _, frag = url.partition("#")
                target = os.path.normpath(os.path.join(os.path.dirname(src), path)) if path else src
                full = os.path.join(ROOT, target)
                if not os.path.exists(full):
                    misses.append(f"{src}:{no}: link {url} -> missing {target}")
                    continue
                if frag and target.endswith(".md") and os.path.isfile(full):
                    h = headings(target)
                    if h and frag.lower() not in h[1]:
                        misses.append(f"{src}:{no}: link {url} -> no heading #{frag} in {target}")
    return misses


def main():
    misses = sorted(set(check()))
    for m in misses:
        print(m)
    print(f"{len(misses)} misses")
    return 1 if misses else 0


if __name__ == "__main__":
    sys.exit(main())
