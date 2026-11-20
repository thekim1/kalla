"""Build the character-bigram table of the garble detector (parser design §6.3, criterion 3).

Counts letter bigrams, with word boundaries, over the word types of two public sources in this repository, so the
table needs no download and changes only when this script is rerun:

- Swedish: the stems of `src/Kalla.Retrieval/Text/sv_SE/sv_SE.dic` (Den stora svenska ordlistan, LGPL-3.0; the
  table holds counts derived from it, not the list);
- English: the words of the repository's own English design documents (`plans/**/*.md`, `01-general-idea.md`).

Each word type counts once, so a word repeated in the documents does not outweigh the dictionary. The output is
`ume_parser/garble_bigrams.json`: the alphabet and the count of every bigram seen at least once (`^` starts a word,
`$` ends it). `signals.garbled_score` calls a bigram rare when its count is below `signals.RARE_BIGRAM_COUNT`.

    uv run python -m scripts.build_bigrams
"""

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent.parent
DICTIONARY = REPO / "src" / "Kalla.Retrieval" / "Text" / "sv_SE" / "sv_SE.dic"
ENGLISH = (REPO / "01-general-idea.md", REPO / "plans")
OUT = Path(__file__).resolve().parent.parent / "ume_parser" / "garble_bigrams.json"
ALPHABET = "abcdefghijklmnopqrstuvwxyzåäöéüàèáíóúñçæøß"
_WORD = re.compile(r"[^\W\d_]+")
_ENGLISH_WORD = re.compile(r"(?<![\w`/.#-])[A-Za-z]{2,}(?![\w`/.#-])")
_CODE = re.compile(r"`[^`\n]*`|```.*?```", re.DOTALL)


def swedish_words(path: Path) -> set[str]:
    words: set[str] = set()
    lines = path.read_text(encoding="utf-8").splitlines()[1:]  # the first line is the entry count
    for line in lines:
        stem = line.split("/", 1)[0].strip().lower()
        words.update(w for w in _WORD.findall(stem) if len(w) >= 2)
    return words


def english_words(roots: tuple[Path, ...]) -> set[str]:
    files = sorted(p for root in roots for p in ([root] if root.is_file() else root.rglob("*.md")))
    words: set[str] = set()
    for path in files:
        text = _CODE.sub(" ", path.read_text(encoding="utf-8"))
        words.update(w.lower() for w in _ENGLISH_WORD.findall(text))
    return words


def bigrams(words: set[str]) -> Counter[str]:
    counts: Counter[str] = Counter()
    for word in sorted(words):
        if not all(ch in ALPHABET for ch in word):
            continue
        padded = f"^{word}$"
        counts.update(padded[i : i + 2] for i in range(len(padded) - 1))
    return counts


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=OUT)
    args = parser.parse_args()
    swedish, english = swedish_words(DICTIONARY), english_words(ENGLISH)
    counts = bigrams(swedish | english)
    table = {
        "alphabet": ALPHABET,
        "sources": {"swedish_words": len(swedish), "english_words": len(english)},
        "bigrams": dict(sorted(counts.items())),
    }
    args.out.write_text(json.dumps(table, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"{args.out.name}: {len(counts)} bigrams from {len(swedish)} Swedish and {len(english)} English words")
    return 0


if __name__ == "__main__":
    sys.exit(main())
