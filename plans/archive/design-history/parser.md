# parser: history moved out of the design

The process sections of [`../../parser/design.md`](../../parser/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Routing | Worker parses only HTML, plain text, Markdown and CSV. **Every PDF** and every other binary format goes to the parser. CSV and XLSX produce **table blocks**, not prose (§3) |
| 2 | Per-page signals | Cheap **`POST /v1/inspect`** (no models, separate lane, never queued behind conversions) gives page count and per-page text-layer facts; the same detector scores parser output (§6) |
| 3 | PDF paths | Per page, chosen by the worker from inspect signals: **text-layer fast path** (pypdfium2, no models) or Docling layout, with OCR `off`/`auto`/`force` (§3, §7.2) |
| 4 | ODF | `.odt`/`.ods`/`.odp` read natively by Docling's OpenDocument backend, no LibreOffice (§3). Legacy Office, RTF and email: P2 (§4, §5) |
| 5 | Archives | `.zip` and other archives refused in P1 (`unsupported_format`, reason `archive`); macro-enabled OOXML refused in P1 (§18 Q2) |
| 6 | Contract | Request `multipart/form-data` (file + options JSON); response `multipart/mixed`: `result.json` (our normalised blocks, tables, pages, signals) and `docling.json.gz` (lossless `DoclingDocument`) (§7) |
| 7 | Stored outputs | Worker stores `result.json` **and** `docling.json.gz` as the parse output, cached by (content hash, parser settings hash, parser build); re-chunking reads `result.json` only and never calls the parser (§7.6) |
| 8 | Page ranges | PDF and multi-frame TIFF only; ≤ 20 pages, the worker sizes ranges (fixed sizes per strategy in P1, adaptive from returned timings in P2) and joins them in page order (§8) |
| 9 | OCR | **Tesseract `swe` only**, set explicitly, never Docling's auto choice; modes per request (`auto`/`force`/`off`); P1 acceptance check: å/ä/ö recall ≥ 95 % on a 20-page Umeå sample (§9) |
| 10 | Runtime | One uvicorn process (API only) + an **own supervisor** with N pre-warmed conversion processes, one inspect process and one NER process `ner-0`; SIGKILL on deadline or disconnect; recycle after 50 jobs or RSS growth; no `uvicorn --workers`, no `--limit-concurrency` (§10) |
| 11 | Limits | 60 MB body, ≤ 20 pages per request, 120 s deadline **kept**; adds 2 000 pages per document, per-format bomb limits, tmpfs 1 GB (§11) |
| 12 | Image | `python:3.12-slim-trixie`, uv lock with hashes, CPU torch, apt from a dated `snapshot.debian.org`, Tesseract `swe`, Docling and NER models; **no LibreOffice, no EasyOCR**; target ≤ 3.5 GB, 2.88 GB measured (§12) |

## 16. Milestones

| Phase | Deliverable |
|---|---|
| **P0** | Image with Docling, Tesseract `swe` and models baked; size measured; supervisor with one slot and warm-up self-test; `/health/*`, `/v1/info` with `build_hash`; the five convert/inspect/info/error schemas in `contracts/parser/v1/`; `/v1/convert` for PDF (the text-layer fast path and Docling with OCR `auto`/`force`/`off`, table modes, page ranges with `partial`, per-page signals with the mojibake part of the garble detector, the error codes, the disconnect kill; other formats refused); `/v1/inspect` answered `501 not_implemented`; `test_offline.py` green with `--network none` |
| **P1** | `/v1/inspect`; text-layer fast path; `/v1/ner` with `ner-0` and its two schemas (§7.8); all P1 formats (§3), legacy Office, RTF and email refused as `format_deferred`; OCR `auto`/`force`/`off`; tables `fast`/`accurate`; **page ranges** with `partial`; per-page signals and garble detector; every error code; limits and bomb checks; RSS watchdog, recycling, disconnect kill; OTel and metrics; fixture corpus and goldens; contract tests; OCR acceptance check (§9.3); licence inventory (postponed P2); `bench.py` report on the test host |
| **P2** | Legacy Office and RTF from crawled sources, email with child documents (Z1, §4, §5); picture crops with a describe-pictures node (worker → gateway, residency rules); OCR for pictures in DOCX/PPTX (route via PDF); PDF embedded files as child documents; decide archives and macro-enabled formats; fuzzing (§15.4); cross-page table merge and page-end hyphenation if the evaluation shows a need; adaptive range sizes (§8.1). A GPU image is a capacity trigger, not a P2 item (§12) |

Page ranges and table modes are P1: the 20-page request limit of repo-and-deployment §5.3 requires ranges for any long
PDF, and table modes are one option.

## 17. Changes to other documents

What this design needs from other documents; each of them states it.

| Document | Change |
|---|---|
| `archive/area-plans/parser.md` | §2: `/v1/inspect`; options as in §7.2; response `multipart/mixed` with `result.json` and `docling.json.gz`. §3: supervisor + slots. §4 milestones per §16 |
| `archive/area-plans/ingestion-worker.md` | parse concurrency from `/v1/info` `capacity.convert_slots`; `busy`/`not_ready`/`duplicate_in_flight` requeue without an attempt; error mapping §7.5; *Plain text/Markdown* and *CSV table* worker-side; spreadsheet chunking as row groups with repeated headers; signals and thresholds §6.1; range planning §8.1. no PdfPig: every PDF to the parser, fast path = `pipeline: text_layer`; joining per §8.2 (no cross-page table merge or hyphenation join in P1); upload types without legacy Office, RTF and email |
| `ingestion-worker/design-processing.md` | §3.1 strategy `pdfpig` → `text-layer` (a parser request with `pipeline: text_layer`); PdfPig fallback and the 30 s budget removed; §3.2 legacy, RTF and email rows to P2; §3.7 `pdf-fast-path` node removed; §4.1 stitching → joining per §8.2; NER called inline when gated (§7.8), no `ner` class; `level_source: font_size` |
| `ingestion-worker/design-engine.md` | no `ner` job class (`capacity.ner_slots` removed from `/v1/info`) |
| `platform/data-model.md` | `RunItems.ParserBuildHash bytea`; `content."ParserBuilds"` (`BuildHash` PK, `Info jsonb`, `FirstSeenAt`). parse output keyed by (content hash, parser settings hash, parser build) under a content-addressed prefix per level, not under `documents/{documentId}/nodes/…`; no `pictures/` blobs; no `ParentDocumentId`/`AttachmentOrdinal`/`Depth`/`attachment` in P1 |
| `platform/repo-and-deployment.md` | §2.4 module list (`supervisor.py`, `slot.py`, `inspect.py`, `textlayer.py`, `signals.py`, `limits.py`, `sniff.py`), scripts (`bench.py`, `ocr_check.py`, `update_goldens.py`), `LICENSES.md`; §5.1 parser base `python:3.12-slim-trixie`, tmpfs 1 GB; §5.3 `tini` entrypoint, no `--workers`/`--limit-concurrency`, limits §11.1; §6.2 `PARSER_SLOT_MAX_RSS_MB`; §9 metrics §13.2. packages without `easyocr` and `python-oxmsg`; apt without LibreOffice and its fonts; no `soffice-sandboxed`, `office.py`, `mail/`, `build_lo_profile.py`, `ocr_bakeoff.py`; size ≤ 3.5 GB; env without `PARSER_OCR_DEFAULT` and `NER_LANES`; `parser-robustness.yml` P2 |
| `identity/design.md` §10 / repo-and-deployment §9 | security event `5104 rag.parser.limit_exceeded` (worker logs it) |
| `archive/passes/performance-pass.md` §3.8 | parser hosts run a `parse` worker only (no `ner` class, no `NER_LANES` 2–6 sizing); the parser now also takes clean PDF pages (fast path) |
| `postponed-decisions.md` | V1: `docling-slim` (2.145 reviewed), Tesseract, pypdfium2, uv image digest, snapshot date. Z1: archives and macro-enabled formats. P1 (OCR engine) closed: Tesseract `swe` with the §9.3 check; Z1 joined by legacy Office, RTF and email; P2 licence list without LibreOffice, EasyOCR, `python-oxmsg` |
| `archive/decisions-log.md` | "Parser detail (step 5)" row: routing (every PDF to the parser, fast path), inspect, multipart contract, stored DoclingDocument, supervisor model, Tesseract `swe` set explicitly |

## 18. Answered questions (2026-10-29)

1. **`.msg` library**: Docling 2.145 reads `.msg` with `python-oxmsg` (MIT); `extract-msg` (GPL) is not used. Applies
   when email arrives in P2.
2. **Archives and macro-enabled Office in the pilot**: refused in P1 (`unsupported_format`), decided in P2
   (postponed item Z1, now joined by legacy Office, RTF and email).
3. **Email senders and recipients**: subject and date in chunk text; From/To/Cc only as metadata (not searchable,
   not shown in citations). Applies when email arrives in P2.

## 20. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md):

| Item | Where it went |
|---|---|
| Legacy `.doc`/`.xls`/`.ppt` and RTF via LibreOffice (§4: `soffice-sandboxed` wrapper, profile template, rlimits, local-file-link fixture, fonts, `office_conversion_failed`, `parser.office` span and metric, LibreOffice warm-up) | P2 (Z1), refused as `format_deferred` in P1 unless the pilot inventory shows them common (§1) |
| Email `.eml`/`.msg` (§5: email backend, attachment splitter, attachment limits, `email_header` block, `email` and `attachments[]` fields, `att-<n>` parts, child documents, `python-oxmsg`) | P2 (Z1), as above (§1) |
| Picture crops (`pictures` option, `pic-<n>` parts, `pictures/{n}.png` blobs, `crop` field) | Dropped until a describe-pictures node exists (§1) |
| EasyOCR, engine option `ocr.engine`, `PARSER_OCR_DEFAULT`, the OCR bake-off (§9.3 old), `eng` OCR language | Dropped: Tesseract `swe` only with a 20-page acceptance check; EasyOCR reconsidered only if it fails (§1) |
| PdfPig fast path in the worker and worker/parser page stitching (cross-page table merge, page-end hyphenation join) | Removed: every PDF to the parser, fast path via pypdfium2 in the parser (§9); table merge and hyphenation join P2 if needed |
| NER lanes (`ner-0 … L-1`, `NER_LANES`, dedicated-host sizing 2–6, `capacity.ner_slots`, the worker's `ner` job class) | Removed: one `ner-0`, called only when gated from the processing job (§1) |
| Parse output keyed per document (`documents/{documentId}/nodes/docling-parse/{cacheKey}/`) | Replaced by the content-addressed cache (content hash, parser settings hash, parser build) (§2) |
| Parser fuzzing (Atheris, mutation run, `parser-robustness.yml`) | P2 (§1) |
| Docling HTML backend as a fallback for failed main-content extraction (`html_base_url` option) | Removed: HTML is extracted in the worker only, with the site profile or a preset (processing design §3.7) |

## 21. Deferred or removed (weakness review)

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Range size adapted from measured ms per page (§8) | P2; fixed sizes per strategy from the worker's configuration in P1 |
