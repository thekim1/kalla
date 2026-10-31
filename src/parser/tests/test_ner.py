"""`/v1/ner`'s label mapping, span merging and the KB model on Swedish fixtures (parser design §7.8; processing
design §10, §17; to-verify #79).

The model tests need the baked model, so they run in the image (or with `PARSER_MODELS_DIR` pointing at a download)
and are skipped elsewhere.
"""

import pytest

from ume_parser import ner
from ume_parser.settings import Settings

PERSONS = ["PER", "PRS"]
MODEL = Settings().models_dir / ner.MODEL_DIR

# Text, and the names in it that are persons (as substrings, in order).
CASES = [
    ("Anna-Karin Lindqvist och Per Östberg deltog på mötet.", ["Anna-Karin Lindqvist", "Per Östberg"]),
    ("Handläggare är Maria Nilsson, telefon 090-16 10 00.", ["Maria Nilsson"]),
    ("Annas kollega Göran Andersson svarade på frågan.", ["Annas", "Göran Andersson"]),
    ("😀 Kontakta Lisa Ek på kontoret.", ["Lisa Ek"]),
    # Combining marks before the name (decomposed å and ö; the worker sends NFC, offsets are code points anyway).
    ("Protokollet fra\u030an mo\u0308tet skrevs av Maria Nilsson.", ["Maria Nilsson"]),
    ("Kommunfullmäktige beslutade om budgeten för 2027.", []),
]


def test_labels_map_to_person_or_ambiguous():
    assert [ner.map_label(label, PERSONS) for label in ("PER", "PRS", "ORG/PRS", "LOC/PRS", "LOC", "O")] == [
        "person",
        "person",
        "person_ambiguous",
        "person_ambiguous",
        None,
        None,
    ]


def test_merge_joins_hyphenated_parts_and_drops_window_overlaps():
    text = "Anna-Karin Ek"
    raw = [
        {"entity_group": "PER", "start": 0, "end": 4, "score": 0.9},
        {"entity_group": "PER", "start": 5, "end": 13, "score": 0.8},
        {"entity_group": "PER", "start": 5, "end": 10, "score": 0.95},  # the same name again from the next window
        {"entity_group": "LOC", "start": 11, "end": 13, "score": 0.99},
    ]
    assert ner.merge(text, raw, PERSONS) == [ner.Entity(0, 13, "person", 0.8)]


needs_model = pytest.mark.skipif(not (MODEL / "model.safetensors").is_file(), reason="needs the baked NER model")


@pytest.fixture(scope="module", params=[False, True], ids=["fp32", "int8"])
def recogniser(request):
    return ner.Recogniser(MODEL, threads=2, quantize=request.param, person_labels=PERSONS)


@needs_model
def test_self_test_passes(recogniser):
    assert ner.self_test(recogniser) == []


@needs_model
@pytest.mark.parametrize(("text", "names"), CASES, ids=[c[0][:24] for c in CASES])
def test_persons_are_found_with_code_point_offsets(recogniser, text, names):
    found = [text[e.start : e.end] for e in recogniser.find(text) if e.label == "person"]
    assert found == names


@needs_model
def test_texts_longer_than_the_model_window_are_read_whole(recogniser):
    filler = " ".join(["Kommunen har beslutat om nya regler för bygglov i området."] * 60)
    text = f"{filler} Kontakta Lisa Ek på kontoret."
    found = [(text[e.start : e.end], e.label) for e in recogniser.find(text)]
    assert ("Lisa Ek", "person") in found


@needs_model
def test_recognise_filters_by_label_and_score(recogniser):
    job = {
        "texts": [{"id": "t1", "text": CASES[0][0]}, {"id": "t2", "text": ""}],
        "labels": ["person"],
        "min_score": 0.5,
    }
    out = ner.recognise(recogniser, job)
    assert [(e["text_id"], e["label"]) for e in out["entities"]] == [("t1", "person"), ("t1", "person")]
    assert all("text" not in e for e in out["entities"])  # spans only, never the name
    assert ner.recognise(recogniser, {**job, "min_score": 1.0})["entities"] == []
