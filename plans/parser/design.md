# Parser sidecar: detailed design

Detail step 5 of the Ume.Rag design. Details [`plan.md`](plan.md) (binding, incl. its answered items 3–5) within
the limits set by [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §2.4, §5.3, §6 and §9,
[`../platform/data-model.md`](../platform/data-model.md) §2.2 and §9, and [`../ingestion-worker/plan.md`](../ingestion-worker/plan.md)
§4–§8. Rewritten to the simplification pass ([`../simplification-pass.md`](../simplification-pass.md) §1, §9, §13);
removed and deferred items are listed in §20. Nothing is implemented. Where this document changes another one, the
change is listed in §17; where it needed the user, §18.

Docling API details were **checked in review against the source of `docling-slim` 2.145.0** (the `docling` wheel is a
meta package over it), downloaded for inspection only; citations of the form `docling/…:line` refer to that version.
Details still unchecked are marked *to verify* and collected in §19. Other citations `path:line` are in
`~/git/rag-platforms`.

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
| 8 | Page ranges | PDF and multi-frame TIFF only; ≤ 20 pages, worker sizes ranges adaptively from returned timings and joins them in page order (§8) |
| 9 | OCR | **Tesseract `swe` only**, set explicitly, never Docling's auto choice; modes per request (`auto`/`force`/`off`); P1 acceptance check: å/ä/ö recall ≥ 95 % on a 20-page Umeå sample (§9) |
| 10 | Runtime | One uvicorn process (API only) + an **own supervisor** with N pre-warmed conversion processes, one inspect process and one NER process `ner-0`; SIGKILL on deadline or disconnect; recycle after 50 jobs or RSS growth; no `uvicorn --workers`, no `--limit-concurrency` (§10) |
| 11 | Limits | 60 MB body, ≤ 20 pages per request, 120 s deadline **kept**; adds 2 000 pages per document, per-format bomb limits, tmpfs 1 GB (§11) |
| 12 | Image | `python:3.12-slim-trixie`, uv lock with hashes, CPU torch, apt from a dated `snapshot.debian.org`, Tesseract `swe`, Docling and NER models; **no LibreOffice, no EasyOCR**; target ≤ 3.5 GB (§12) |

## 2. Responsibilities and boundaries

Unchanged from the plan: one document per request, stateless, no fetch, no storage, no chunking, no strategy
decisions. Additions made here:

- The parser **does** compute quality signals (§6) because they need the PDF internals and the OCR output it holds.
- The parser **does** find person names for *Detect personal data* (`/v1/ner`, §7.8): the Swedish NER model needs
  Python; counting, gating and flagging stay in the worker, which calls it only when the name check can change the
  flag, from its processing job (simplification pass §1).
- The parser **does not** compute heading paths or infer heading levels from numbering ("Kapitel 3", "§ 4"): those
  depend on joining ranges and on parse rules, which live in the worker (worker plan §6, RAGFlow lesson
  `platforms/ragflow.md` §7.4).

## 3. Format routing

| Format (sniffed) | Handled by | Path | Output shape | Rangeable |
|---|---|---|---|---|
| HTML (crawl, URL list, upload) | worker | AngleSharp + site profile (connectors design §5.6, processing design) | blocks | – |
| Plain text, Markdown | worker | charset detection (BOM, UTF-8, Windows-1252 fallback); Markdown headings via Markdig | blocks | – |
| CSV / TSV | worker | delimiter sniffing, header-row detection; one **table block** | table | – |
| PDF, page with clean text layer (per `/v1/inspect`) | parser | **text-layer fast path**: pypdfium2 text extraction, no models (`pipeline: text_layer`, §7.2) | blocks per page | **yes** |
| PDF, other pages | parser | Docling standard PDF pipeline, OCR per §9 | blocks, tables | **yes** |
| DOCX, PPTX, XLSX | parser | Docling declarative backends (no layout model) | blocks / tables | no |
| .odt, .ods, .odp | parser | Docling's OpenDocument backend (native, no LibreOffice) | as OOXML | no |
| JPG, PNG, TIFF | parser | Docling image input, OCR always | blocks | TIFF frames |
| .doc, .xls, .ppt, .rtf, .eml, .msg | – | P2 (Z1): refused `format_deferred` | – | – |
| zip, 7z, rar, tar, gz; .docm/.xlsm/.pptm; executables; unknown | – | refused: `unsupported_format` with a reason | – | – |

Reasoning:

- **Text, Markdown, CSV stay in .NET**: trivial to parse, no native code, no reason to spend a parser slot. CSV becomes
  one table block with a detected header row, so the worker's chunker treats it exactly like an XLSX sheet.
- **Spreadsheets are tables, not prose.** XLSX sheets come back as table blocks with cell structure (§7.4). The
  worker's chunker should emit **row-group chunks with the header row repeated** (e.g. 20 rows per chunk, each row
  serialised as `Kolumn: värde; …`), never a flattened pipe table split mid-row.
- **No untrusted binary parsing in the worker** (simplification pass §9): every PDF goes to the parser, also its clean
  pages, so the worker runs no PDF library. The fast path sits next to Docling inside the sandboxed parser and uses
  pypdfium2, which `/v1/inspect` already loads, so it adds no dependency. PDFs are the larger half of umea.se's
  sitemap (≈ 7,960 PDFs against ≈ 6,160 HTML pages, simplification pass §13.1), so this path carries much of the load.
- **The parser re-sniffs every input** (magic bytes, OOXML `[Content_Types].xml`, OLE root entries) and trusts its
  own result; the worker's declared media type is only compared and a mismatch is a warning, not an error. OLE
  inputs (legacy Office, `.msg`) are recognised only to refuse them as `format_deferred`.

**Text-layer fast path** (`pipeline: text_layer`): per page, pypdfium2's text page gives the characters in content
order; lines are grouped into paragraphs by vertical gap; a line whose font size is ≥ 1.25 × the page's median body
size, ≤ 2 lines and ≤ 150 characters becomes a `heading` (`level_source: font_size`, level by size rank, at most 3);
no table structure (table-dense pages take Docling per §6.1); no OCR, no rendering. Per-character font size through
pypdfium2's raw pdfium API and the reading order of two-column pages are *to verify* on the fixture corpus (§15.3); if
column order fails, inspect adds a column signal and such pages go to Docling.

Known P1 gap: scanned pages pasted as pictures into DOCX/PPTX are not OCR'd (Docling's declarative backends do not
OCR pictures, *to verify*). The parser reports `picture_area_ratio` per page so the quality facts can flag such
documents; an OCR-forced route (DOCX → PDF → OCR) needs LibreOffice and is P2.

## 4. LibreOffice conversion

Removed (simplification pass): legacy `.doc`/`.xls`/`.ppt` and RTF move to P2 (postponed Z1), and the P1 image has no
LibreOffice. The P2 starting point is Docling's own legacy → OOXML path with a sandboxed `soffice` wrapper (design in
this document's history, step 5 review), with a separate container as the fallback if local file links cannot be
shown harmless.

## 5. Email

Removed (simplification pass): `.eml`/`.msg` with attachments as child documents move to P2 (postponed Z1). The P2
starting point is Docling's email backend (`python-oxmsg`, MIT; `extract-msg` is GPL and stays unused) plus our own
attachment splitter (design in this document's history).

## 6. Quality signals and `/v1/inspect`

### 6.1 What the worker's heuristics need (processing design §3.1)

| Decision | Signal | Source |
|---|---|---|
| Clean text layer → text-layer fast path | `text_chars`, `garbled_score`, `image_coverage` | inspect |
| Empty or scanned → OCR `auto` | `text_chars` ≈ 0, `image_coverage` high | inspect |
| Garbled layer → OCR `force` | `garbled_score` high, `bad_chars` | inspect |
| Nothing found → re-render | after convert: `chars` ≈ 0 while `image_coverage` high | convert |
| Table-dense → Docling tables `accurate` | `path_objects` (ruling lines), later `table_area_ratio` | inspect, convert |
| Quality facts (processing design §8) | `chars`, `garbled_score`, `ocr_confidence`, `empty`, `table_area_ratio`, `picture_area_ratio` | convert |

Proposed starting thresholds, for the worker to tune on the fixture corpus: clean = `text_chars ≥ 200` (or `≥ 50`
with `image_coverage < 0.3`) and `garbled_score < 0.2`; force OCR at `garbled_score ≥ 0.4`; re-render once at
`image_scale` 3.0 when a converted page has `chars < 20` and `image_coverage ≥ 0.5`; table-dense at
`path_objects ≥ 40` per page.

### 6.2 `POST /v1/inspect`

Same request shape as convert (file + small options), no models, served by a separate inspect process (§10) so it
never waits behind a conversion. Deadline 10 s, ≤ 2 000 pages.

| Field | PDF | TIFF | Image | OOXML / ODF |
|---|---|---|---|---|
| `format`, `media_type_sniffed` | ✓ | ✓ | ✓ | ✓ |
| `page_count` | pages | frames | 1 | slides (PPTX), sheets (XLSX), null (DOCX, ODT) |
| `rangeable` | true | true | false | false |
| `encrypted` | `none` / `owner_only` (opens, accepted) / `user_password` (refused) | – | – | OOXML encryption package → `user_password` |
| `has_javascript`, `has_embedded_files`, `has_xfa` | ✓ (reported, never executed or extracted) | – | – | – |
| `pages[]` | per page below | per frame: size, DPI | size | – |
| `refusal` | the error code convert would give, if any | | | |

Per PDF page (pypdfium2, no rendering, typically < 20 ms/page): `width`, `height`, `rotation`, `text_chars`,
`bad_chars` (U+FFFD, private-use area, C0 controls, literal `(cid:N)`), `garbled_score`, `image_coverage` (union
area of image objects ÷ page area, clipped to 1), `path_objects`.

### 6.3 Garble detector (one implementation, in the parser)

Ported in spirit from RAGFlow (`ragflow/internal/deepdoc/parser/pdf/util/garbled.go:59-339`: `IsGarbledChar`,
`IsGarbledText`, `IsGarbledByFontEncoding`, `IsScanNoise`), adapted to Swedish. Score in [0, 1], max of:

1. bad-character ratio (above);
2. letter ratio among non-space characters below 0.5;
3. **Swedish/English plausibility**: share of tokens of length ≥ 3 that are implausible under a character-bigram model
   trained on Swedish and English public text (a ≈ 50 KB table in the image), so "kommunfullmäktige" scores clean and
   "ÃÂ¤kfm" does not; mojibake pairs (`Ã¤`, `Ã¶`, `Ã¥`) count double;
4. scan noise: average token length < 2 or > 25, or > 30 % single-character tokens.

The same function scores text-layer text (inspect, fast path) and converted page text (convert), so the worker
compares like with like. Test vectors in `tests/fixtures/parser/garbled/*.json` (text, expected range).

### 6.4 Per-page signals in a convert response

`page`, `label` (slide number, sheet name, or null), `width`, `height`, `chars` (body text), `furniture_chars`
(headers/footers), `garbled_score`, `text_layer_chars` (PDF), `image_coverage`, `ocr_applied` (none / bitmap /
full-page), `ocr_confidence` (mean and p10 of word confidences, null if not exposed: *to verify* whether Docling keeps
OCR cell confidence on the converted page), `table_count`, `table_area_ratio`, `picture_area_ratio`, `empty`
(`chars == 0`), `ms` (time spent on the page, where reported).

## 7. Contract (`contracts/parser/v1/`)

### 7.1 Endpoints

| Method | Path | Body | Purpose |
|---|---|---|---|
| `POST` | `/v1/convert` | `multipart/form-data`: `file` (bytes), `options` (JSON) | Convert one document or page range |
| `POST` | `/v1/inspect` | same, `options` optional | Cheap facts and per-page signals (§6.2) |
| `POST` | `/v1/ner` | `application/json` (text units, no file) | Person names with code-point spans (§7.8) |
| `GET` | `/v1/info` | – | Versions, build hash, capacity, limits, formats (§13) |
| `GET` | `/health/live`, `/health/ready` | – | Probes (§13) |

Multipart, not a raw stream with options in headers: options are a nested JSON object validated by one schema, and
`python-multipart` spools the file to the job directory without holding it in memory. The worker sends the
**extension only** (`file_ext`), never the file name.

### 7.2 Request options (`convert-request.schema.json`)

```json
{
  "request_id": "0193…-r3",              // job id + range ordinal: log correlation and duplicate detection
  "file_ext": ".pdf",
  "media_type": "application/pdf",       // declared; compared with the sniffed type
  "pages": { "from": 41, "to": 60 },     // 1-based, inclusive; null = whole document
  "deadline_ms": 110000,                 // ≤ 120000
  "pipeline": "docling",                 // PDF only: text_layer | docling
  "ocr": { "mode": "auto", "languages": ["swe"] },
  "tables": { "mode": "accurate" },      // fast | accurate | off
  "image_scale": 2.0,                    // 1.0–4.0, page render scale for layout/OCR
  "outputs": { "docling_json": true, "markdown": false }
}
```

`pipeline: text_layer` ignores `ocr`, `tables` and `image_scale` and returns no `docling.json.gz`. Unknown properties
are rejected (400), so drift shows at once; new options are added as optional fields (§7.7).

### 7.3 Response

`200 OK`, `Content-Type: multipart/mixed; boundary=…`, parts in this order:

| Part | `Content-Type` | `Content-ID` | Present |
|---|---|---|---|
| 1 | `application/json` (`convert-result.schema.json`) | `result` | always |
| 2 | `application/vnd.docling.document+json`, `Content-Encoding: gzip` | `docling` | `outputs.docling_json`, Docling path only |

The binary part avoids base64 (33 % larger, held in memory twice) and is streamed straight into a blob. The .NET
client reads it with `MultipartReader` (`Microsoft.AspNetCore.WebUtilities`).

`result.json` (abridged):

```json
{
  "contract": "parser/v1",
  "request_id": "0193…-r3",
  "status": "success",                        // success | partial (deadline hit, see pages_completed)
  "input": { "format": "pdf", "media_type_sniffed": "application/pdf", "sha256": "…", "bytes": 4183022,
             "page_count": 112, "pages_requested": { "from": 41, "to": 60 },
             "pages_completed": { "from": 41, "to": 60 }, "pipeline": "docling" },
  "pages": [ { "page": 41, "label": null, "width": 595.3, "height": 841.9, "signals": { … } } ],
  "blocks": [
    { "id": "b0", "kind": "heading", "level": 2, "level_source": "layout", "page": 41, "page_end": 41,
      "bbox": [56.7, 88.1, 410.2, 104.0], "layer": "body", "md": "## 4 Avgifter", "ref": "#/texts/12" },
    { "id": "b1", "kind": "table", "page": 41, "page_end": 41, "bbox": [ … ], "layer": "body",
      "md": "| Avgift | Belopp |\n|---|---|\n| … |", "table": "t0", "ref": "#/tables/0" }
  ],
  "tables": [ { "id": "t0", "rows": 12, "cols": 2, "header_rows": 1, "caption": "Tabell 3",
                "cells": [ { "r": 0, "c": 0, "rs": 1, "cs": 1, "header": true, "text": "Avgift" } ] } ],
  "pictures": [ { "id": "p0", "block": "b7", "page": 43, "bbox": [ … ], "caption": "Karta över …",
                  "ocr_chars": 0 } ],
  "warnings": [ { "code": "ocr_low_confidence", "page": 47 } ],
  "timings": { "total_ms": 38110, "wait_ms": 4, "docling_ms": 37200, "text_layer_ms": 0, "signals_ms": 310 },
  "build": { "parser": "1.0.0", "docling": "2.x.y", "build_hash": "sha256:…" }
}
```

- **Block kinds**: `title`, `heading`, `paragraph`, `list_item`, `table`, `picture`, `caption`, `code`, `formula`,
  `footnote`, `page_header`, `page_footer`. `layer` is `body` or `furniture` (headers, footers, page numbers:
  Docling's content layer, *to verify*); the worker drops furniture from chunks by default.
- **`level_source`**: `style` (DOCX/ODF via styles), `layout` (PDF: the layout model detects section headers but its
  levels are unreliable, *to verify*), `font_size` (text-layer fast path), `html`, `markdown`. The worker may re-level
  `layout` and `font_size` headings by numbering or a parse rule.
- **No character offsets**: each block carries its own Markdown (`md`), so there is no UTF-16 versus code-point
  question. The document Markdown is defined as the blocks' `md` joined by blank lines, with a line
  `<!-- page: N -->` before the first block of each page; the parser includes it only when `outputs.markdown` is set
  (preview, debugging). Pages without blocks still get a marker, unlike Docling's own page-break placeholder (Open
  WebUI notes that blank pages leave no break: `open-webui/backend/open_webui/retrieval/loaders/main.py:312-325`).
- **`ref`** is the JSON pointer into the `DoclingDocument` (`#/texts/12`; null on the fast path), so a later consumer
  of the lossless JSON can join it to our blocks. Docling's JSON keys `texts`, `tables`, `pictures`,
  `key_value_items`, `form_items`, `pages`, `prov[].page_no` and `content_layer` are used by both Open WebUI
  (`main.py:312-325`) and RAGFlow (`ragflow/internal/parser/parser/pdf_parser_docling.go:273-300`), so they are stable
  across recent versions.
- **Pages**: absolute page numbers of the original file in every case (Docling keeps original numbering under a page
  range, *to verify*; otherwise the parser offsets). DOCX has `page` null; PPTX uses the slide number; XLSX uses the
  sheet index with `label` = sheet name.
- `tables[].cells` is the source of truth; `md` of a table block is a convenience rendering (pipe tables lose spans).
- `pictures[]` carries captions and OCR'd character counts only; picture crops are not part of P1 (§20).

### 7.4 Spreadsheets

Hidden sheets are skipped; cell values are the cached results of formulas (openpyxl `data_only`, *to verify* what
Docling's Excel backend does; post-filter if needed); empty rows and columns at the edges are trimmed; several tables
on one sheet are separate table blocks when Docling detects them. Limit 200 000 non-empty cells per workbook: beyond
it the parser returns the first 200 000 with warning `truncated_cells` (a 300 000-row export is data, not knowledge).

### 7.5 Errors

Error responses are `application/json` (`error.schema.json`): `{ "error": { "code", "detail", "retry", "limit"?,
"pages_completed"? }, "request_id", "build" }`. `detail` is a fixed sentence per code, never a library message (those
can quote document content).

| HTTP | `code` | Meaning | `retry` | Worker maps to |
|---|---|---|---|---|
| 400 | `invalid_request` | options fail the schema | `none` | Fatal (bug) |
| 409 | `duplicate_in_flight` | same `request_id` already converting | `later` | requeue, no attempt counted |
| 413 | `too_large` | body > 60 MB | `none` | Fatal, outcome `too-large` |
| 415 | `format_deferred` | legacy Office, RTF, email: P2 formats | `none` | Fatal, outcome `unsupported`, reason `format_deferred` |
| 415 | `unsupported_format` | `reason`: `archive`, `macro_enabled`, `executable`, `unknown` | `none` | Fatal, outcome `unsupported` |
| 422 | `encrypted` | user password required (PDF or OOXML) | `none` | Fatal, outcome `encrypted` |
| 422 | `corrupt` | cannot be opened by the format's library | `none` | Fatal |
| 422 | `too_many_pages` | document > 2 000 pages, or range > 20 | `none` / `smaller_range` | Fatal / re-plan |
| 422 | `limit_exceeded` | bomb limits (§11), `limit` names which | `none` | Fatal, security event `5104` |
| 422 | `ocr_failed` | OCR engine error | `same` (once) | Transient once, then Fatal |
| 422 | `conversion_failed` | Docling or the fast path raised | `same` (once) | Transient once, then Fatal |
| 422 | `timeout` | deadline hit with nothing usable | `smaller_range` | re-plan with half the range; single page → Fatal |
| 200 | `status: partial` | deadline hit after some pages | – | keep completed pages, request the rest |
| 500 | `worker_crashed` | conversion process died (signal) | `smaller_range` | as `timeout` |
| 507 | `resource_limit` | RSS watchdog killed the conversion | `smaller_range` | as `timeout` |
| 503 | `busy` / `not_ready` | no warm slot (`Retry-After`) | `later` | requeue after `Retry-After`, no attempt counted |

`5104 rag.parser.limit_exceeded` is a security event in the worker's range (identity design §10 numbering), logged
by the worker with document id and limit name only.

### 7.6 What the worker stores

The parse output is cached by **(content hash, parser settings hash, parser build)** (simplification pass §2): the
settings hash covers the per-range plan (pipeline, OCR mode, table mode, scale per page range); the build is
`build_hash` (§13.1). The output is a set of write-once blobs under the cache key (blob layout in data-model §9):

| Blob | Content | Why |
|---|---|---|
| `result.json` | joined `result.json` of all ranges (§8.2) | input of chunking; **re-chunking never calls the parser** |
| `docling/{from}-{to}.json.gz` | one `DoclingDocument` per Docling range (not merged) | lossless form for future chunkers or a P2 re-read; no re-parse after a chunker change |
| `ranges/{from}-{to}.result.json` | per-range results, intermediate (30 days) | a failed range retries alone |

Size: a `DoclingDocument` is several times the Markdown (bounding boxes per item); gzip brings it near the Markdown
size. Acceptable at pilot scale; if not, drop it for `public` web PDFs first.

Because the key is content-addressed, two sources with the same file share one parse. **Parser upgrades do not
re-parse by themselves**: an unchanged document keeps its stored output; only a parse that is needed anyway (new
content, re-processing) runs on the new build, and its `build_hash` is recorded per run item.

### 7.7 Idempotency and versioning

Conversion is a pure function of (bytes, options, build): retries are always safe. `request_id` only correlates logs
and rejects a concurrent duplicate (a worker whose lease expired while the first is still converting). `/v1` changes
are additive only (new optional fields, new enum values the worker treats as unknown-safe); a breaking change is
`/v2`, served alongside `/v1` for one release.

### 7.8 `POST /v1/ner`

Names for *Detect personal data*, specified in
[`../ingestion-worker/design-processing.md`](../ingestion-worker/design-processing.md) §10: JSON request with text
blocks, JSON response with code-point spans and labels; schemas `ner-request.schema.json`, `ner-result.schema.json`.
Model `KBLab/bert-base-swedish-cased-ner` (MIT), pinned revision `f3c83af8196b812028fc08e1971a2d289d0a3f1d`. Its
`config.json` labels (checked in review): `PER` for persons, plus mixed labels `ORG/PRS`, `PRS/WRK`, `LOC/PRS` and
others; `NER_PERSON_LABELS` defaults to `PER,PRS`; mixed labels are returned only on request and not counted in P1.

The worker calls it **only when gated** (the document is in a source of a `public` collection, is not already flagged
by another rule, and has ≥ 5 contact values) and from its processing job, not from a job class of its own
(simplification pass §1). Served by one warm process **`ner-0`** (never behind conversions; `NER_THREADS` 2,
`NER_QUANTIZE`, at most `NER_QUEUE` (4) waiting requests else `503 busy`, recycled after 5 000 requests); `/v1/info`
adds `ner_ready` and the model block inside `build_hash`; readiness does not depend on NER.

## 8. Page ranges and joining

### 8.1 Planning

1. Worker calls `/v1/inspect` (cheap) → page count, `rangeable`, per-page signals.
2. Auto-parse assigns a strategy per page (§6.1): text-layer fast path, Docling with an OCR and table mode, or blank.
3. Consecutive pages with the **same** strategy (pipeline, OCR mode, table mode, scale) form runs; each run is cut
   into ranges of at most 20 pages. Range size starts at 20 (fast path or no OCR) or 8 (OCR) and, from P2, adapts: after each
   range the worker sets the next size so that `measured ms/page × size ≤ 50 % of the deadline`. In P1 (weakness
   review §1) the sizes are fixed per strategy in the worker's configuration, set from fixture measurements so that a
   range stays within 50 % of the deadline; a failing range is halved.
4. Ranges of one document run sequentially in its `parse` job (the lease heartbeat covers it); different documents
   use the parallel slots. One parse job per document keeps run records and caching simple.

Non-rangeable formats (DOCX, PPTX, XLSX, ODF, single images) are one request; the declarative backends are fast enough
(§14) that 120 s is ample for the pilot's documents. A DOCX that times out is a `timeout` with `retry: none` (cannot be
split) and is recorded as a failure.

### 8.2 Joining ranges (worker)

All pages now come from the parser in one shape, so joining is concatenation, not stitching of two parsers:

| Problem | Rule |
|---|---|
| Order | blocks sorted by (page, reading order within the range) |
| Heading paths | computed after joining from the ordered heading blocks and their levels, never per range, so a range that starts mid-chapter gets the right path |
| Furniture | repeated `page_header`/`page_footer` text dropped |
| Partial ranges | `pages_completed` merged; missing pages requested again; still missing after the retry → outcome `degraded` with the page list |

Merging tables across page breaks and joining hyphenation at page ends are not done in P1 (§20).

## 9. OCR

### 9.1 Modes

| Mode | Docling setting (names checked in 2.145) | Use |
|---|---|---|
| `off` | `do_ocr=False` | clean text layer that still needs layout or tables |
| `auto` | `do_ocr=True`, `force_full_page_ocr=False`: OCR only bitmap regions (Docling's area threshold option, name *to verify*) | scanned or mixed pages |
| `force` | `do_ocr=True`, `force_full_page_ocr=True`: ignore the text layer | garbled text layer |

Images (JPG/PNG/TIFF) are always OCR'd (`off` is treated as `auto`, with a warning). Language: `swe`. Strategy per
page is achieved through ranges (§8.1), because Docling's OCR options apply to a whole conversion.

### 9.2 Engine

| Engine | Docling options class (exists in 2.145) | Models | Notes |
|---|---|---|---|
| Tesseract 5 (CLI) | `TesseractCliOcrOptions(lang=["swe"], tesseract_cmd="tesseract")` | Debian `tesseract-ocr-swe` (`tessdata_fast` or `best`, *to verify*); ≈ 10–30 MB | CLI avoids building `tesserocr` (uv `no-build = true`) |

Docling 2.145's default is `OcrAutoOptions` (`docling/datamodel/pipeline_options.py:332-345`, `:2204-2212`): it
picks the first available engine in the order OcrMac, Nemotron (CUDA), RapidOCR (onnxruntime), EasyOCR, RapidOCR
(torch), and **never Tesseract**. The parser therefore **always sets the engine explicitly** and never uses the auto
option, so the engine cannot change silently with what happens to be installed.

### 9.3 Acceptance check (P1; postponed item P1 closed)

| Item | Design |
|---|---|
| Sample | 20 scanned Umeå pages: public or synthetic pages in the repo (§15.3) plus real internal pages kept on the test host (forms, protokoll, older typewritten documents, tables, stamps); ground truth typed once and checked by a second person |
| Measure | **å/ä/ö recall** (share of ground-truth å, ä, ö recognised as the same letter); CER and ms/page reported alongside; script `src/parser/scripts/ocr_check.py`, output a Markdown report |
| Pass | å/ä/ö recall ≥ 95 % |
| Fail | EasyOCR (or another engine) is reconsidered only then, as a P1 change with its own image and licence review |

## 10. Runtime

### 10.1 Process model

```
tini (PID 1)
└─ python -m ume_parser            supervisor + uvicorn (1 process, asyncio, TLS); no Docling import
   ├─ convert-slot-0 … N-1         spawned processes: Docling pipelines warm, PARSER_THREADS torch/OMP threads
   ├─ inspect-0                    spawned process: pypdfium2 only, RLIMIT_AS 1 GB
   └─ ner-0                        spawned process: KB BERT NER warm, NER_THREADS threads (§7.8)
```

- **Why not `uvicorn --workers N`** (repo-and-deployment §5.3): each uvicorn worker would run Docling in the
  request process, a native hang cannot be cancelled from Python, and a crash kills the request handler. **Why not
  `--limit-concurrency N`**: it counts every connection, so `/health/ready` would be refused while all slots are busy
  and the probe would mark a working container unhealthy. Both removed (§17).
- **Why own supervisor** (≈ 300 lines, `multiprocessing` spawn + pipes): `ProcessPoolExecutor` breaks the whole pool
  when one worker is killed; `pebble` is LGPL. The supervisor owns slot state, deadlines, kills and respawns.
- Admission: a request takes a free warm slot or gets `503 busy` with `Retry-After` at once. The parser has **no
  queue**: the worker's `parse` concurrency equals the capacity, which it reads from `/v1/info` `capacity.convert_slots`
  at start and every minute (so a change of `PARSER_WORKERS` needs no worker config). The text-layer fast path runs in
  a conversion slot like any other request (it is fast, so it frees the slot quickly).
- Slots: `PARSER_WORKERS` × `PARSER_THREADS` = the container's `cpus` (16 GB host 1 × 4; 24 GB host 2 × 2,
  repo-and-deployment §6.2). Inspect lane: one process, recycled after 200 jobs. NER: one process `ner-0` with a
  self-test on a fixed Swedish sentence at start, recycled after 5 000 requests or RSS growth > 40 %.
- Scale-out ([`../performance-pass.md`](../performance-pass.md) §3.8): a parser host runs its own `parse` worker on
  the host's internal `parser` network, so the parser keeps no other network. Only the optional multi-endpoint mode
  (`Parser:Endpoints`, cross-host parser traffic) adds `PARSER_TLS_CLIENT_CA` (uvicorn client-certificate
  verification, *to verify*); not used in the 3- and 5-host topologies.

### 10.2 Warm-up and readiness

1. Supervisor starts the API (live = true), wipes `/tmp/jobs`, spawns slots.
2. Each slot builds a `DocumentConverter` for the default option set (OCR `auto` with Tesseract `swe`, tables
   `accurate`, scale 2.0) from `/opt/models`, then converts an embedded 1-page self-test PDF (text, a scanned strip,
   a small table) through Docling and through the fast path, and checks the expected text.
3. Ready when **all** slots passed at start; afterwards ready while **at least one** slot is warm (a recycling slot
   reduces `slots_ready` in `/v1/info`; requests meanwhile may see `busy`).

Other option sets (OCR `force`, tables `fast`) are built lazily and kept in an LRU of three pipelines per slot
(Docling caches pipelines per options, *to verify*; each holds its models, hence the bound).

### 10.3 Limits per conversion, recycling, cleanup

| Mechanism | Value (16 GB host, 1 × 4) | Effect |
|---|---|---|
| Deadline | `min(deadline_ms, 120 s)`; Docling's own document timeout set to deadline − 10 s (*to verify* option name) for a graceful `partial` | then `SIGKILL` at deadline + 10 s → `timeout` |
| RSS watchdog | supervisor samples each slot every 500 ms (`psutil`); kill above `PARSER_SLOT_MAX_RSS_MB` (2 800; 2 × 2: 2 600) | `resource_limit`, respawn |
| `RLIMIT_AS` | not used for slots (torch and ONNX reserve large virtual ranges); used for inspect | – |
| OOM | slots `oom_score_adj=1000`, so the kernel picks them before the supervisor | supervisor survives, respawns |
| Recycle | after 50 conversions, or when idle RSS exceeds warm RSS + 40 % | respawn between jobs; reason in metrics |
| Job directory | `/tmp/jobs/<request_id>/` (input and output) | removed by the supervisor after the response is sent or the slot is killed; wiped at start |
| tmpfs | `/tmp` 1 GB (was 2 GB): tmpfs pages count against the container's memory limit | – |

### 10.4 Cancellation

The worker gives up by closing the connection (its HTTP timeout = deadline + 15 s, or a newer execution cancels the
job, worker §4). The API awaits the slot result while checking `request.is_disconnected()` every second; on
disconnect the supervisor kills the slot and respawns it. Killing costs a re-warm (≈ 10–30 s, *to measure*) but is
the only reliable stop for native code. No cancel endpoint is needed: worker and parser talk directly, without a
proxy that could hide a disconnect.

## 11. Security

Inputs arrive after ClamAV but are untrusted. The container is the outer sandbox: no network (`parser` network is
`internal`, the parser has no other), non-root 10001, read-only root, `cap_drop: [ALL]`, `no-new-privileges`,
`pids_limit` 256, memory and CPU limits (repo-and-deployment §6). Inside it:

| Threat | Control |
|---|---|
| Decompression bomb, OOXML/ODF zip | pre-check before any library opens it: entries ≤ 10 000, total uncompressed ≤ 512 MB, per-entry ratio ≤ 200, no absolute or `..` entry names, no nested zip except known embeddings (`embeddings/*.xlsx` etc., not opened in P1) → `limit_exceeded` |
| Decompression bomb, images | Pillow `Image.MAX_IMAGE_PIXELS` = 100 MP as a hard error (`DecompressionBombError`, not a warning); TIFF ≤ 2 000 frames; render size per PDF page ≤ 60 MP (scale reduced, warning) |
| PDF JavaScript, XFA, launch actions | never executed: pdfium in pypdfium2 is built without V8/XFA (*to verify*); reported by inspect only |
| PDF embedded files | not extracted in P1, reported (`has_embedded_files`) |
| Pathological PDF (deep object trees, huge content streams, endless xref loops) | deadline + kill, RSS watchdog, page cap 2 000; inspect lane has `RLIMIT_AS` and its own 10 s deadline; the fast path runs in a killable slot like Docling |
| XXE in OOXML/ODF | lxml ≥ 5 resolves internal entities only by default (*to verify*); `defusedxml` installed so openpyxl uses it (*to verify*); no network for external DTDs; XXE fixtures (§15.3) |
| Path traversal | zip entry names never used as paths; job directory per request |
| Resource exhaustion | slot count = capacity, no queue, per-request deadline, body limit 60 MB enforced while streaming (uvicorn `h11_max_incomplete_event_size` and a counting reader), multipart part count ≤ 2 |
| Runtime downloads | `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`, `HF_DATASETS_OFFLINE=1`, Docling `artifacts_path=/opt/models`, `TESSDATA_PREFIX` fixed, `TORCH_HOME`/`XDG_CACHE_HOME` under `/tmp/cache`; `test_offline.py` runs a conversion of every format in a container with `--network none` **and** with `socket.socket.connect` patched to raise, so a silent fallback download fails the test instead of passing on a cached file |
| Content in errors and logs | §7.5 fixed `detail`; §13 log rules |
| Parser compromise | blast radius: its own TLS key and the documents in flight. No database, blob or key access; the worker treats every response as untrusted data (schema validation, size caps per part: `result.json` ≤ 50 MB, the binary part ≤ 60 MB, total ≤ 150 MB) |

### 11.1 Limits (reconciled with repo-and-deployment §5.3)

| Limit | §5.3 | Here | Change |
|---|---|---|---|
| Request body | 60 MB | 60 MB (uploads ≤ 50 MB; crawler max fetch also 50 MB) | none |
| Pages per request | 20 | 20 (hard); worker sizes adaptively, 8 with OCR | none |
| Request deadline | 120 s | 120 s max, from `deadline_ms`; hard kill +10 s | none |
| Pages per document | – | 2 000 | new |
| Inspect deadline | – | 10 s | new |
| `/tmp` tmpfs | 2 GB | 1 GB | changed |
| Per format | – | §7.4 cells, §11 zip/pixels | new |

## 12. Image

| Topic | Decision |
|---|---|
| Base | `python:3.12-slim-trixie`, digest-pinned (Debian 13: newer Tesseract; bookworm is oldstable during the pilot). Python 3.12 (Docling supports 3.10–3.13, *to verify*) |
| Stages | 1: uv (pinned `ghcr.io/astral-sh/uv` digest) `uv sync --frozen --no-dev --no-install-project`; `scripts/download_models.py` with pinned Hugging Face revisions and SHA-256 per file into `/opt/models` (the only networked step). 2: runtime packages, copy venv and models; `USER 10001` |
| apt | from `snapshot.debian.org/archive/debian/<date>` and `…/debian-security/<date>` with the date ≥ 7 days before the build (same intent as `exclude-newer`; reproducible); bumping the date is the security update. Packages: `tini`, `tesseract-ocr`, `tesseract-ocr-swe` (`libglib2.0-0` comes with Tesseract; `libgl1` is not needed because `opencv-python-headless` is a direct dependency; P0, PR #9). **No LibreOffice, no fonts for it** |
| Python | uv lock with hashes, `no-build = true`, `exclude-newer` 7 days before lock (repo-and-deployment §2.4). `docling-slim` with only the extras needed (standard formats, Tesseract CLI), not the `docling` meta package's full set; **no EasyOCR, no `python-oxmsg`**. Adds: `pypdfium2` (also a Docling dependency), `defusedxml`, `psutil`, `pillow`. CPU wheels for `torch` and `torchvision` from the PyTorch CPU index |
| Docling extras | layout and TableFormer models only; picture classification, code/formula enrichment and picture description **off and not baked** |
| Entry | `ENTRYPOINT ["tini","--"]`, `CMD ["python","-m","ume_parser"]` (supervisor starts uvicorn programmatically on **8000**, TLS when `PARSER_TLS_CERT_FILE`/`PARSER_TLS_KEY_FILE` are set: off in dev, on in production Compose; decided 2026-10-30, PR #9) |
| Env | `PARSER_WORKERS`, `PARSER_THREADS`, `OMP_NUM_THREADS`, `PARSER_SLOT_MAX_RSS_MB`, `NER_THREADS` (2), `NER_QUANTIZE` (1), `NER_QUEUE` (4), `NER_PERSON_LABELS` (`PER,PRS`), offline variables (§11) |
| Size target | ≤ 3.5 GB: base ≈ 150 MB, torch CPU + torchvision ≈ 800 MB, other Python ≈ 500 MB, Docling models ≈ 400 MB, NER model ≈ 500 MB, Tesseract ≈ 50 MB. Measured in P0 |

Models baked (sizes approximate, *to verify* at P0):

| Model | Source | Size | Licence (postponed P2) |
|---|---|---|---|
| Docling layout model (current default, e.g. "heron") | Hugging Face `docling-project/…`, pinned revision | ≈ 150–200 MB | *to verify* (model card) |
| TableFormer fast + accurate | same repository family | ≈ 200 MB | *to verify* |
| KB BERT NER (`KBLab/bert-base-swedish-cased-ner`, rev `f3c83af`) | Hugging Face, pinned revision + SHA-256 | ≈ 499 MB fp32 | MIT (SUC 3.0 training data: terms *to verify*); uses `transformers`, `tokenizers`, `safetensors` (Apache-2.0, already present) |
| Tesseract `swe` | Debian package | ≈ 10–30 MB | Apache-2.0 |
| Garble bigram table | built in-repo from public Swedish/English text | ≈ 50 KB | own |

**Licence inventory** (postponed P2, before the pilot): `src/parser/LICENSES.md` replaces `MODELS.md`, generated at
build from `uv pip list` + package metadata, Debian `copyright` files of installed packages, and the model table above.
Known so far: Docling and its parsers MIT (`docling-parse` *to verify*), `docling-slim` and `docling-core` MIT (PyPI
metadata, checked in review), PyTorch BSD-3, pdfium BSD-3/Apache-2.0, Tesseract Apache-2.0, psutil BSD-3, tini MIT.

## 13. Health, info, observability

### 13.1 Endpoints

| Endpoint | Content |
|---|---|
| `/health/live` | 200 while the supervisor loop runs (no slot check) |
| `/health/ready` | 200 when the self-tests of §10.2 passed and ≥ 1 slot is warm; 503 otherwise. Never blocked by conversions |
| `/v1/info` | `parser` version and git revision; Python; `docling`, `docling-core`, `docling-parse`, `docling-ibm-models`, `torch`, `pypdfium2` (+ pdfium build), Tesseract version and tessdata languages; each model with repository, revision and SHA-256; the NER model block (repository, revision, SHA-256, labels, quantised); `capacity` (`convert_slots`, `slots_ready`, `inspect_slots`, `ner_ready`); `limits` (§11.1); supported and deferred formats; `build_hash` = SHA-256 of the canonical JSON of everything except capacity |

The worker records `build_hash` on every run item and stores the full info JSON once per hash (§17). The release smoke
test already calls `/v1/info` from the worker container (repo-and-deployment §8.1).

### 13.2 Traces, metrics, logs

- **Traces**: `opentelemetry-instrumentation-fastapi`, OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT` (same collector as the
  .NET services); the worker sends `traceparent`, so fetch → scan → inspect → parse → embed is one trace. Slots report
  timings back over the pipe and the supervisor records child spans (`parser.docling`, `parser.text_layer`,
  `parser.signals`) with explicit start and end times, so slot processes need no exporter. Span attributes: format,
  page count, range, pipeline, OCR mode, table mode, slot, outcome code, bytes. **Never** file names, titles or text.
- **Metrics** (meter `Ume.Rag.Parser`, names in the platform's `rag.` style):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.parser.duration` | histogram (s) | endpoint, format, pipeline, ocr mode, outcome |
| `rag.parser.duration_per_page` | histogram (s) | format, pipeline, ocr mode, table mode |
| `rag.parser.pages` | counter | format, pipeline, ocr applied |
| `rag.parser.failures` | counter | code |
| `rag.parser.inflight`, `rag.parser.slots_ready` | gauge | – |
| `rag.parser.busy_rejections` | counter | – |
| `rag.parser.recycles` | counter | reason (`max_jobs`, `rss_growth`, `rss_limit`, `deadline`, `disconnect`, `crash`) |
| `rag.parser.slot_rss` | gauge (bytes) | slot |

  The worker's existing `rag.parse.duration_per_page` (repo-and-deployment §9) stays as the end-to-end view. The
  *Ingestion* dashboard gets slot readiness, recycles by reason and failures by code.
- **Logs**: JSON to stdout, one line per request: `request_id`, trace id, endpoint, format, bytes, page count, range,
  options summary, timings, outcome code, slot, recycle reason. Docling receives the stream as
  `DocumentStream(name="<request_id><ext>")`, so its own log lines carry no real names. Library exception messages
  only at DEBUG (off in production); at INFO the exception type and our code. Docling and Tesseract stdout/stderr
  captured and reduced to line counts unless DEBUG.

## 14. Performance targets

Pilot host parser share: 4 CPUs (`cpus: 4.0`), 1 slot × 4 threads. Targets are p50 per page, upper bound p95; all
*to measure* in P1 (the numbers are planning estimates, not Docling benchmarks).

| Input | p50 | p95 | Notes |
|---|---|---|---|
| `/v1/inspect`, PDF | 0.02 s | 0.1 s | ≤ 2 s for 100 pages |
| PDF, text-layer fast path | 0.05 s | 0.2 s | most pages of native PDFs; *to measure* |
| PDF, text layer, layout + tables `fast` | 1.0 s | 2.5 s | pages the fast path cannot take |
| PDF, text layer, tables `accurate` | 1.5 s | 4 s | table-dense pages |
| PDF/image, OCR `auto`, Tesseract, scale 2.0 | 4 s | 8 s | scanned page |
| DOCX / PPTX / XLSX / ODF (declarative) | ≤ 5 s per document of 100 pages | 15 s | no models |
| Slot re-warm after a kill | ≤ 30 s | 60 s | |
| `/v1/ner`, per 512-token window (fp32 / int8) | *to measure* | – | only for gated documents (§7.8) |

**Throughput estimate**: the pilot's < 500 scanned documents at ≈ 6 pages each ≈ 3 000 pages × 4 s ≈ 3.3 h of parser
time: one night. Native PDFs now reach the parser too; their clean pages take the fast path. The initial load of
umea.se's ≈ 7,960 PDFs is the largest single parser job of the pilot: at an assumed 10 pages per PDF and 0.05 s per
fast-path page it is ≈ 1.1 h of slot time plus the Docling pages; the page count and the fast-path share come from
the pilot inventory (*to measure*).

**How P1 measures it**: `src/parser/scripts/bench.py` runs the fixture corpus plus a public sample of umea.se
documents against the image on the test host (8 vCPU / 16 GB, parser at `cpus: 4`), for 1 × 4 and 2 × 2 slot
layouts, and writes s/page by format, pipeline and OCR mode, peak RSS per slot, and documents per hour. The R1 release
test's OCR batch of 50 scanned PDFs (repo-and-deployment §8.3) adds the "while chat runs" number. Expectation to
confirm: 2 × 2 gives more pages per hour, 1 × 4 lower latency per document; the pilot default stays 1 × 4 on 16 GB.

## 15. Tests

### 15.1 Parser (pytest, `src/parser/tests/`)

| Kind | Content |
|---|---|
| Unit | options → Docling options mapping (engine always Tesseract, never `OcrAutoOptions`); sniffing (every format, mismatches, legacy/email refused as `format_deferred`); garble detector vectors; signals from synthetic page data; fast path (paragraph grouping, font-size headings, page markers); zip pre-check; limit enforcement; error mapping (each code once); Markdown join with page markers |
| Supervisor | with a fake converter: deadline kill, disconnect kill, crash (`os.abort`), RSS limit (allocating converter), recycle after N, `busy` when full, readiness during recycle, job directory always removed |
| Integration (`slow`) | real Docling and the fast path on `tests/fixtures/parser/*`: expected block kinds, heading levels, table shapes, page numbers, å/ä/ö preserved; fast path and Docling agree on the text of clean pages (similarity ≥ 0.98) |
| Offline | `test_offline.py` (§11) |
| NER | label mapping, limits, `busy`, code-point offsets with emoji and combining characters, windows over 512 tokens, quantised vs fp32 agreement (processing design §17) |
| Schema | `export_schema.py` writes `convert-request`, `convert-result`, `inspect-result`, `ner-request`, `ner-result`, `info`, `error` schemas from the pydantic models; CI fails on `git diff` in `contracts/` (repo-and-deployment §8.2) |

### 15.2 Contract tests (.NET, `tests/Ume.Rag.ContractTests`)

- Start the parser image (Testcontainers), convert every fixture with the worker's real client, read the multipart
  response, validate `result.json` against `contracts/parser/v1/convert-result.schema.json` (JsonSchema.Net) and
  deserialise into the client's records with `JsonUnmappedMemberHandling.Disallow` in tests (production tolerates
  unknown fields), so drift in either direction fails.
- Compare with golden files `tests/fixtures/parser/golden/*.json`, normalised: timings, versions and `build` removed;
  block kinds, levels, page numbers and table shapes exact; text by similarity ≥ 0.98 per block (Docling upgrades
  shift whitespace). Goldens are updated only by `scripts/update_goldens.py` in a PR that bumps Docling, with the diff
  reviewed.
- Error contract: one fixture per error code (encrypted, corrupt, zip, `.doc` `format_deferred`, too many pages via a 2 001-page
  generated PDF, bomb), asserting HTTP status, `code` and `retry`.

### 15.3 Fixture corpus (`tests/fixtures/parser/`)

Every file is **public or synthetic**, listed in `MANIFEST.csv` (path, origin URL or generator, licence, contains
personal data: must be `no`). The pre-commit hook and CI reject a fixture missing from the manifest and scan fixture
text for personnummer patterns other than Skatteverket's published test numbers.

| Group | Content |
|---|---|
| Municipal PDFs | published umea.se documents: kallelse, protokoll with § numbering, taxa with fee tables, detaljplan text pages, a two-column newsletter, a report with footnotes and a TOC; the PDFs of the ≈ 20-page umea.se regression corpus (connectors design §5.6) |
| Scanned | synthetic forms rendered with fake data, then "print-scanned" (noise, 1–3° skew, 200 dpi, JPEG artefacts); a typewriter-style page; a rotated page; a mixed PDF (text pages + scanned annex); the public part of the OCR acceptance sample (§9.3) |
| Swedish text | å/ä/ö/é in headings and tables; hyphenation across lines; a garbled-encoding PDF (font without ToUnicode); mojibake text |
| Tables | merged cells, multi-row headers, a table across a page break, an XLSX with two sheets (one hidden), formulas, a 250 000-cell sheet (truncation), CSV in Windows-1252 with `;` |
| Office | DOCX with heading styles, lists, tables; PPTX with notes; `.odt`/`.ods`/`.odp` from the same synthetic sources; one `.doc`, `.rtf` and `.msg` each (refused as `format_deferred`) |
| Malicious | zip bomb inside a DOCX; nested-zip "42"-style; PNG with 60 000 × 60 000 dimensions; PDF with `/OpenAction` JavaScript; PDF with an embedded file; PDF with a cyclic xref and deep object nesting; DOCX and ODT with an XXE entity to `file:///etc/passwd` (assert no `root:` in any output); macro-enabled `.docm` (refused) |

Real internal documents used for the OCR acceptance check and benchmarks stay on the test host in an access-limited,
encrypted directory (`/srv/ume-rag-bench/`), are never committed, and are reported as aggregate metrics only.

### 15.4 Robustness and fuzzing

P2 (simplification pass §1): Atheris harnesses for our own parsers (zip pre-check, sniffing, fast path) and a mutation
run against the whole container (each fixture mutated, sent through `/v1/inspect` and `/v1/convert`; every response
valid or a defined error, no request beyond deadline + 15 s, supervisor never restarts), in a `parser-robustness.yml`.
In P1 the malicious fixtures (§15.3) cover the known attack shapes; crash findings become fixtures.

## 16. Milestones

| Phase | Deliverable |
|---|---|
| **P0** | Image with Docling, Tesseract `swe` and models baked; size measured; supervisor with one slot and warm-up self-test; `/health/*`, `/v1/info` with `build_hash`; the five convert/inspect/info/error schemas in `contracts/parser/v1/`; `/v1/convert` for a plain PDF (enough for the contract job); `test_offline.py` green with `--network none` (P0 checklist #12, #13) |
| **P1** | `/v1/inspect`; text-layer fast path; `/v1/ner` with `ner-0` and its two schemas (§7.8); all P1 formats (§3), legacy Office, RTF and email refused as `format_deferred`; OCR `auto`/`force`/`off`; tables `fast`/`accurate`; **page ranges** with `partial`; per-page signals and garble detector; every error code; limits and bomb checks; RSS watchdog, recycling, disconnect kill; OTel and metrics; fixture corpus and goldens; contract tests; OCR acceptance check (§9.3); licence inventory (postponed P2); `bench.py` report on the test host |
| **P2** | Legacy Office and RTF via LibreOffice, email with child documents (Z1, §4, §5); picture crops with a describe-pictures node (worker → gateway, residency rules); OCR for pictures in DOCX/PPTX (route via PDF); PDF embedded files as child documents; decide archives and macro-enabled formats; fuzzing (§15.4); cross-page table merge and page-end hyphenation if the evaluation shows a need; GPU target if volumes need it |

Moved from the plan's P2 to P1: page ranges (the 20-page request limit of repo-and-deployment §5.3 already requires them for any long
PDF) and table modes (one option).

## 17. Changes to other documents

Step 5 rows were applied in the step 5 review commit (2026-10-29); they are restated here in their
simplification-pass form. Items marked **(SP)** are new with this rewrite and still to be applied by the owners.

| Document | Change |
|---|---|
| `parser/plan.md` | §2: `/v1/inspect`; options as in §7.2; response `multipart/mixed` with `result.json` and `docling.json.gz`. §3: supervisor + slots. §4 milestones per §16 (applied with this rewrite) |
| `ingestion-worker/plan.md` | parse concurrency from `/v1/info` `capacity.convert_slots`; `busy`/`not_ready`/`duplicate_in_flight` requeue without an attempt; error mapping §7.5; *Plain text/Markdown* and *CSV table* worker-side; spreadsheet chunking as row groups with repeated headers; signals and thresholds §6.1; range planning §8.1. **(SP)** no PdfPig: every PDF to the parser, fast path = `pipeline: text_layer`; joining per §8.2 (no cross-page table merge or hyphenation join in P1); upload types without legacy Office, RTF and email |
| `ingestion-worker/design-processing.md` **(SP)** | §3.1 strategy `pdfpig` → `text-layer` (a parser request with `pipeline: text_layer`); PdfPig fallback and the 30 s budget removed; §3.2 legacy, RTF and email rows to P2; §3.7 `pdf-fast-path` node removed; §4.1 stitching → joining per §8.2; NER called inline when gated (§7.8), no `ner` class; `level_source: font_size` |
| `ingestion-worker/design-engine.md` **(SP)** | no `ner` job class (`capacity.ner_slots` removed from `/v1/info`) |
| `platform/data-model.md` | `RunItems.ParserBuildHash bytea`; `content."ParserBuilds"` (`BuildHash` PK, `Info jsonb`, `FirstSeenAt`). **(SP)** parse output keyed by (content hash, parser settings hash, parser build) under a content-addressed prefix per level, not under `documents/{documentId}/nodes/…`; no `pictures/` blobs; no `ParentDocumentId`/`AttachmentOrdinal`/`Depth`/`attachment` in P1 |
| `platform/repo-and-deployment.md` | §2.4 module list (`supervisor.py`, `slot.py`, `inspect.py`, `textlayer.py`, `signals.py`, `limits.py`, `sniff.py`), scripts (`bench.py`, `ocr_check.py`, `update_goldens.py`), `LICENSES.md`; §5.1 parser base `python:3.12-slim-trixie`, tmpfs 1 GB; §5.3 `tini` entrypoint, no `--workers`/`--limit-concurrency`, limits §11.1; §6.2 `PARSER_SLOT_MAX_RSS_MB`; §9 metrics §13.2. **(SP)** packages without `easyocr` and `python-oxmsg`; apt without LibreOffice and its fonts; no `soffice-sandboxed`, `office.py`, `mail/`, `build_lo_profile.py`, `ocr_bakeoff.py`; size ≤ 3.5 GB; env without `PARSER_OCR_DEFAULT` and `NER_LANES`; `parser-robustness.yml` P2 |
| `identity/design.md` §10 / repo-and-deployment §9 | security event `5104 rag.parser.limit_exceeded` (worker logs it) |
| `performance-pass.md` §3.8 **(SP)** | parser hosts run a `parse` worker only (no `ner` class, no `NER_LANES` 2–6 sizing); the parser now also takes clean PDF pages (fast path) |
| `postponed-decisions.md` | V1: `docling-slim` (2.145 reviewed), Tesseract, pypdfium2, uv image digest, snapshot date. Z1: archives and macro-enabled formats. **(SP)** P1 (OCR engine) closed: Tesseract `swe` with the §9.3 check; Z1 joined by legacy Office, RTF and email; P2 licence list without LibreOffice, EasyOCR, `python-oxmsg` |
| `01-general-idea.md` §10 | "Parser detail (step 5)" row: routing (every PDF to the parser, fast path), inspect, multipart contract, stored DoclingDocument, supervisor model, Tesseract `swe` set explicitly |

## 18. Answered questions (2026-10-29)

1. **`.msg` library**: Docling 2.145 reads `.msg` with `python-oxmsg` (MIT); `extract-msg` (GPL) is not used. Applies
   when email arrives in P2.
2. **Archives and macro-enabled Office in the pilot**: refused in P1 (`unsupported_format`), decided in P2
   (postponed item Z1, now joined by legacy Office, RTF and email).
3. **Email senders and recipients**: subject and date in chunk text; From/To/Cc only as metadata (not searchable,
   not shown in citations). Applies when email arrives in P2.

## 19. To verify

Docling (checked in review against `docling-slim` 2.145.0: `InputFormat` incl. ODF; `PdfPipelineOptions` fields
`do_ocr`, `do_table_structure`, `images_scale`, `artifacts_path`, `document_timeout`, `force_full_page_ocr`;
`TableFormerMode`; `TesseractCliOcrOptions`; `OcrAutoOptions` as default; `convert(page_range=…)`). Still to verify
at P0 against the pinned version:

- Whether page numbers stay absolute under `page_range`; `max_num_pages`, `max_file_size`.
- The name of the bitmap-area threshold option (`bitmap_area_threshold` does not exist in 2.145); `do_cell_matching`.
- Whether Docling keeps OCR word confidence on converted pages, and how to read it after conversion.
- `DoclingDocument`: `content_layer` values (`body`, `furniture`), `SectionHeaderItem.level` reliability on PDFs,
  `export_to_markdown(page_no=…)` and `page_break_placeholder`, JSON pointer `self_ref`, table cell fields
  (`row_span`, `col_span`, `column_header`).
- Excel backend: `data_only` values, hidden sheets, several tables per sheet; PPTX slide = page; DOCX `page_no`.
- How Docling handles `.docm` and OLE inputs (must not reach a backend: our sniffing refuses them first).
- Pipeline caching per options in `DocumentConverter` and the memory cost of several cached pipelines.
- Current layout model name, Hugging Face repositories, revisions, sizes and **model licences**; TableFormer sizes.
- Supported Python versions of the pinned release.

Text-layer fast path (pypdfium2):

- Per-character font size through the raw pdfium API (`FPDFText_GetFontSize`) from pypdfium2; line and paragraph
  grouping from character boxes.
- Reading order of pdfium's text on two-column pages (the newsletter fixture); whether a column signal in inspect is
  needed.
- Fast path vs Docling text agreement on clean pages; p50/p95 per page (§14).

OCR and image:

- Debian `tesseract-ocr-swe` data variant (`fast` or `best`); whether `tesseract-ocr` pulls `tesseract-ocr-eng` as a
  dependency (installed but not used).
- The 20-page OCR acceptance sample: å/ä/ö recall ≥ 95 % with Tesseract `swe` (§9.3).
- Image size ≤ 3.5 GB without LibreOffice and EasyOCR (§12).

Libraries and licences:

- pypdfium2's pdfium build without V8/XFA; licence of the bundled pdfium.
- lxml ≥ 5 default `resolve_entities`; whether openpyxl, python-docx and python-pptx (used by Docling) resolve
  external entities; openpyxl's use of `defusedxml`.
- uvicorn: detection of client disconnect during a long await; `h11_max_incomplete_event_size` behaviour for bodies.
- `snapshot.debian.org` usability for `debian-security` in CI builds (rate limits).

Numbers:

- All performance targets in §14, warm-up and re-warm times, slot RSS at idle and peak; page count and fast-path share
  of umea.se's PDFs (pilot inventory).

## 20. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md):

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

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Range size adapted from measured ms per page (§8) | P2; fixed sizes per strategy from the worker's configuration in P1 |
