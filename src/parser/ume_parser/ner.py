"""`ner-0`: person names for *Detect personal data* (parser design §7.8, processing design §9.3, §10).

One spawned process holds `KBLab/bert-base-swedish-cased-ner` warm (`NER_THREADS` torch threads, int8 dynamic
quantisation of the linear layers with `NER_QUANTIZE`). It loads the model from the baked models directory, checks a
fixed Swedish sentence, and then answers `("ner", job)` with code-point spans per text unit.

The model's labels have no B-/I- prefixes (`PER`, `ORG/PRS`, …): the token-classification pipeline aggregates word
pieces to words ("first" strategy) and joins neighbouring words of one label, so "Anna-Karin Lindqvist" is one
entity. Texts longer than the model's 512 tokens are read in overlapping windows (`stride`). `NER_PERSON_LABELS`
(default `PER,PRS`) are `person`; a mixed label containing one of them (`ORG/PRS`, `PRS/WRK`, `LOC/PRS`) is
`person_ambiguous`, returned only when the request asks for it. Entity text is never returned or logged.
"""

import logging
import time
import warnings
from dataclasses import dataclass
from multiprocessing.connection import Connection
from pathlib import Path
from typing import Any

from ume_parser import slot
from ume_parser.settings import NerSettings

log = logging.getLogger(__name__)

MODEL_DIR = "kb-ner"
MAX_TOKENS = 512
STRIDE = 128
_HYPHENS = {"", "-", "\N{HYPHEN}", "\N{NON-BREAKING HYPHEN}", "\N{EN DASH}"}
SELF_TEST_TEXT = "Anna Lindqvist arbetar på Umeå kommun sedan 2019."
SELF_TEST_PERSON = (0, 14)


@dataclass
class Entity:
    start: int
    end: int
    label: str
    score: float


def map_label(raw: str, person_labels: list[str]) -> str | None:
    """The contract label of a model label: `person`, `person_ambiguous`, or None (not a person)."""
    if raw in person_labels:
        return "person"
    if any(part in person_labels for part in raw.split("/")):
        return "person_ambiguous"
    return None


def merge(text: str, raw: list[dict[str, Any]], person_labels: list[str]) -> list[Entity]:
    """Pipeline output (sorted by start) as entities; neighbours of one label separated by nothing or a hyphen are
    one entity, scored by the lower score."""
    out: list[Entity] = []
    for r in sorted(raw, key=lambda r: int(r["start"])):
        label = map_label(r["entity_group"], person_labels)
        if label is None:
            continue
        start, end, score = int(r["start"]), int(r["end"]), float(r["score"])
        previous = out[-1] if out else None
        if previous is not None and previous.label == label and text[previous.end : start] in _HYPHENS:
            previous.end, previous.score = max(previous.end, end), min(previous.score, score)
        elif previous is not None and start < previous.end:
            continue  # the same span found again in an overlapping window
        else:
            out.append(Entity(start, end, label, score))
    return out


class Recogniser:
    def __init__(self, model_dir: Path, threads: int, quantize: bool, person_labels: list[str]) -> None:
        import torch
        from transformers import AutoModelForTokenClassification, AutoTokenizer, pipeline

        torch.set_num_threads(threads)
        tokenizer = AutoTokenizer.from_pretrained(model_dir, model_max_length=MAX_TOKENS)
        model = AutoModelForTokenClassification.from_pretrained(model_dir)
        model.eval()
        if quantize:
            with warnings.catch_warnings():
                # torch.ao.quantization and quantised tensors are deprecated in torch 2.14, not removed.
                warnings.simplefilter("ignore", DeprecationWarning)
                warnings.simplefilter("ignore", UserWarning)
                model = torch.ao.quantization.quantize_dynamic(model, {torch.nn.Linear}, dtype=torch.qint8)
        self.person_labels = person_labels
        self._pipeline = pipeline(
            "token-classification", model=model, tokenizer=tokenizer, aggregation_strategy="first", device="cpu"
        )

    def find(self, text: str) -> list[Entity]:
        if not text.strip():
            return []
        return merge(text, self._pipeline(text, stride=STRIDE), self.person_labels)


def recognise(recogniser: Recogniser, job: dict[str, Any]) -> dict[str, Any]:
    """`job`: `texts` [{id, text}], `labels`, `min_score`; returns the entities and the model time."""
    started = time.monotonic()
    wanted = set(job["labels"])
    entities = []
    for unit in job["texts"]:
        for e in recogniser.find(unit["text"]):
            if e.label in wanted and e.score >= job["min_score"]:
                entities.append(
                    {
                        "text_id": unit["id"],
                        "start": e.start,
                        "end": e.end,
                        "label": e.label,
                        "score": round(e.score, 4),
                    }
                )
    return {"entities": entities, "ner_ms": round((time.monotonic() - started) * 1000)}


def self_test(recogniser: Recogniser) -> list[str]:
    persons = [(e.start, e.end) for e in recogniser.find(SELF_TEST_TEXT) if e.label == "person"]
    return [] if persons == [SELF_TEST_PERSON] else [f"self-test: persons {persons}, expected [{SELF_TEST_PERSON}]"]


def run(slot_id: int, models_dir: str, threads: int, conn: Connection) -> None:
    """`ner-0`."""
    settings = NerSettings()
    slot.prepare(f"ner-{slot_id}", threads)
    started = time.monotonic()
    try:
        recogniser = Recogniser(Path(models_dir) / MODEL_DIR, threads, settings.quantize, settings.person_label_list)
        problems = self_test(recogniser)
    except Exception as exc:
        log.error("ner-%d: warm-up raised %s", slot_id, type(exc).__name__)
        log.debug("ner warm-up error", exc_info=True)
        problems = [f"raised {type(exc).__name__}"]
    report = {"warmup_ms": round((time.monotonic() - started) * 1000), "problems": problems}
    conn.send(("failed" if problems else "ready", report))
    if not problems:
        slot.serve(conn, {"ner": lambda job: recognise(recogniser, job)})
