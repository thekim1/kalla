# Parser sidecar: detailed design

Detail step 5 of the Ume.Rag design. Details [`plan.md`](plan.md) (binding, incl. its answered items 3–5) within
the limits set by [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §2.4, §5.3, §6 and §9,
[`../platform/data-model.md`](../platform/data-model.md) §2.2 and §9, and [`../ingestion-worker/plan.md`](../ingestion-worker/plan.md)
§4–§8. Nothing is implemented. Where this document changes another one, the change is listed in §17; where it needed
the user, §18.

Docling API details were **checked in review against the source of `docling-slim` 2.145.0** (the `docling` wheel is a
meta package over it), downloaded for inspection only; citations of the form `docling/…:line` refer to that version.
Details still unchecked are marked *to verify* and collected in §19. Other citations `path:line` are in
`~/git/rag-platforms`.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Routing | Worker keeps HTML, plain text, Markdown, CSV and clean PDF text-layer pages (PdfPig). Everything else goes to the parser. CSV and XLSX produce **table blocks**, not prose (§3) |
| 2 | Per-page signals | New cheap **`POST /v1/inspect`** (no models, separate lane, never queued behind conversions) gives page count and per-page text-layer facts; the same detector scores parser output (§6) |
| 3 | Legacy and ODF | **Docling's own backends**: `.doc`/`.rtf`/`.xls`/`.ppt` are converted to OOXML by Docling's hardened LibreOffice wrapper, pointed at **our** `soffice` wrapper script (rlimits, locked-down profile template); `.odt`/`.ods`/`.odp` read natively by Docling's OpenDocument backend, no LibreOffice (§4) |
| 4 | Email | Body and headers by **Docling's email backend** (`.eml` stdlib, `.msg` via `python-oxmsg`, MIT); Docling does not return attachment contents, so our splitter extracts them (stdlib `email` / `python-oxmsg`) as **raw binary parts**; worker scans each and creates **child documents** (§5). `extract-msg` (GPL) not used |
| 5 | Archives | `.zip` and other archives refused in P1 (`unsupported_format`, reason `archive`); macro-enabled OOXML refused in P1 (§18 Q2) |
| 6 | Contract | Request `multipart/form-data` (file + options JSON); response `multipart/mixed`: `result.json` (our normalised blocks, tables, pages, signals), `docling.json.gz` (lossless `DoclingDocument`), then attachments and picture crops (§7) |
| 7 | Stored outputs | Worker stores `result.json` **and** `docling.json.gz` as the parse output; re-chunking reads `result.json` only and never calls the parser (§7.6) |
| 8 | Page ranges | PDF and multi-frame TIFF only; ≤ 20 pages, worker sizes ranges adaptively from returned timings; heading paths and cross-page tables are stitched **by the worker** from blocks with levels and bounding boxes (§8) |
| 9 | OCR | Modes per request (`auto`/`force`/`off`); the worker groups pages with equal strategy into ranges. Tesseract (`swe`+`eng`) and EasyOCR (`sv`+`en`) both baked; P1 bake-off decides (§9) |
| 10 | Runtime | One uvicorn process (API only) + an **own supervisor** with N pre-warmed conversion processes, one inspect process and (step 6c) one NER process `ner-0`; SIGKILL on deadline or disconnect; recycle after 50 jobs or RSS growth; no `uvicorn --workers`, no `--limit-concurrency` (§10) |
| 11 | Limits | 60 MB body, ≤ 20 pages per request, 120 s deadline **kept**; adds 2 000 pages per document, per-format bomb limits, tmpfs 1 GB (§11) |
| 12 | Image | `python:3.12-slim-trixie` (was bookworm: newer LibreOffice and Tesseract), uv lock with hashes, CPU torch, apt from a dated `snapshot.debian.org`, models ≈ 0.5 GB, target ≤ 3.5 GB (§12) |

## 2. Responsibilities and boundaries

Unchanged from the plan: one document per request, stateless, no fetch, no storage, no chunking, no strategy
decisions. Additions made here:

- The parser **does** split containers it understands (email) and returns the parts; it never recurses into them
  (the worker does, with a depth limit), so every part passes ClamAV and the worker's routing.
- The parser **does** compute quality signals (§6) because they need the PDF internals and the OCR output it holds.
- The parser **does** find person names for *Detect personal data* (`/v1/ner`, §7.8, added in step 6c): the Swedish
  NER model needs Python; counting and flagging stay in the worker.
- The parser **does not** compute heading paths, merge tables across pages, or infer heading levels from numbering
  ("Kapitel 3", "§ 4"): those depend on stitching and on parse profiles, which live in the worker (worker plan §6,
  RAGFlow lesson `platforms/ragflow.md` §7.4).

## 3. Format routing

| Format (sniffed) | Handled by | Path | Output shape | Rangeable |
|---|---|---|---|---|
| HTML (crawl, URL list, upload) | worker | AngleSharp + SmartReader (worker §4) | blocks | – |
| HTML, main-content extraction failed the Quality check | parser | Docling HTML backend (`fallback` node option) | blocks | no |
| Plain text, Markdown | worker | charset detection (BOM, UTF-8, Windows-1252 fallback); Markdown headings via Markdig | blocks | – |
| CSV / TSV | worker | delimiter sniffing, header-row detection; one **table block** | table | – |
| PDF, page with clean text layer (per `/v1/inspect`) | worker | PdfPig fast path | blocks per page | – |
| PDF, other pages | parser | Docling standard PDF pipeline | blocks, tables | **yes** |
| DOCX, PPTX, XLSX | parser | Docling declarative backends (no layout model) | blocks / tables | no |
| .doc, .rtf → DOCX; .xls → XLSX; .ppt → PPTX | parser | Docling's legacy path: LibreOffice → OOXML, then the OOXML backend (§4) | as OOXML | no |
| .odt, .ods, .odp | parser | Docling's OpenDocument backend (native, no LibreOffice) | as OOXML | no |
| JPG, PNG, TIFF | parser | Docling image input, OCR always | blocks | TIFF frames |
| .eml, .msg | parser | Docling email backend (body) + own attachment splitter (§5) | body blocks + attachments | no |
| zip, 7z, rar, tar, gz; .docm/.xlsm/.pptm; executables; unknown | – | refused: `unsupported_format` with a reason | – | – |

Reasoning:

- **Text, Markdown, CSV stay in .NET**: trivial to parse, no native code, no reason to spend a parser slot. CSV becomes
  one table block with a detected header row, so the worker's chunker treats it exactly like an XLSX sheet.
- **Spreadsheets are tables, not prose.** XLSX sheets come back as table blocks with cell structure (§7.4). The
  worker's chunker (step 6) should emit **row-group chunks with the header row repeated** (e.g. 20 rows per chunk,
  each row serialised as `Kolumn: värde; …`), never a flattened pipe table split mid-row. Flagged for the worker
  (§17).
- **PdfPig stays in the worker** (decided in worker §4) but only for pages `/v1/inspect` marked clean, so the worker
  needs no garble detector of its own. Accepted risk: the worker parses untrusted PDFs in managed code; mitigated by
  a 30 s timeout per document and the page cap, and it never renders.
- **The parser re-sniffs every input** (magic bytes, OOXML `[Content_Types].xml`, OLE root entries) and trusts its
  own result; the worker's declared media type is only compared and a mismatch is a warning, not an error.

Known P1 gap: scanned pages pasted as pictures into DOCX/PPTX are not OCR'd (Docling's declarative backends do not
OCR pictures, *to verify*). The parser reports `picture_area_ratio` per page so the Quality check can flag such
documents; an OCR-forced route (DOCX → PDF via LibreOffice → OCR) is a P2 node.

## 4. LibreOffice conversion

Docling 2.145 reads `.doc`, `.rtf`, `.xls` and `.ppt` itself by first converting them to OOXML with LibreOffice
(`docling/backend/msword_backend.py:683-684`, `:809-812`; `mspowerpoint_backend.py:370`; `msexcel_backend.py:786`),
through a hardened wrapper (`docling/backend/docx/drawingml/utils.py`): a throwaway `-env:UserInstallation` profile
per call (`:232-244`), flags `--headless --norestore --nologo --nolockcheck --nodefault` (`:54-60`), a new session so
the whole process group is killed on timeout (`:167-…`), default timeout 120 s (`:265`), and the binary taken from
`DOCLING_LIBREOFFICE_CMD` or `soffice` on the path (`:204`). OpenDocument (`.odt`/`.ods`/`.odp`) has a native backend
(`opendocument_backend.py`) and needs no LibreOffice. We use these paths instead of our own conversion step, and add
what Docling's wrapper does not do:

| Topic | Decision |
|---|---|
| Target | OOXML, as Docling does: keeps heading styles and real tables for the declarative backends (fast, exact levels) |
| Our wrapper | `DOCLING_LIBREOFFICE_CMD=/opt/ume-parser/bin/soffice-sandboxed`: a small script that copies the baked profile template over the throwaway profile Docling created (taken from the `-env:UserInstallation` argument), sets `SAL_USE_VCLPLUGIN=svp` and `HOME` to the job directory, applies rlimits (`RLIMIT_AS` 1.5 GB, `RLIMIT_CPU` 90 s, `RLIMIT_FSIZE` 512 MB, `RLIMIT_NOFILE` 256, `oom_score_adj=1000`) and `exec`s `soffice` |
| Profile template | baked at `/opt/lo-profile-template` (`registrymodifications.xcu`): macro security *very high*, macro execution disabled, link update *never* for Writer and Calc, no auto-recovery, no update check (keys *to verify*) |
| Timeout | Docling's backend timeout set to 60 s where its options allow (*to verify*; otherwise its 120 s default, inside our request deadline and slot kill) → `office_conversion_failed`, `retry: none` |
| Macros, links | Never executed / never updated. No network, so remote links fail closed. **Local file links** (a field or OLE link to `file:///run/certs/…` or `/etc/passwd`) are the real risk: a fixture asserts nothing from the container's file system reaches the output (§15.3). If that test cannot be made to pass, LibreOffice moves to a second container without certificates (decided fallback) |
| Fallback | none in P1: a legacy file Docling cannot convert is `office_conversion_failed`; a PDF route is the P2 OCR-forced node (§3) |
| Fonts | `fonts-liberation2`, `fonts-dejavu-core`, `fonts-crosextra-carlito`, `fonts-crosextra-caladea` |
| Version | Recorded in `/v1/info` and in each response's `build` hash |
| Image size | `libreoffice-writer-nogui`, `-calc-nogui`, `-impress-nogui` with `--no-install-recommends` (no Java): ≈ 400–500 MB (*to verify*) |

## 5. Email

### 5.1 Parsing

| Part | How |
|---|---|
| Body and headers | Docling's `EmailDocumentBackend` (`InputFormat.EMAIL`, extensions `eml`, `msg`: `docling/datamodel/base_models.py:182`): `.eml` with the stdlib `email` package; `.msg` projected onto RFC 822 with `python-oxmsg` (MIT, built on `olefile`; `docling/backend/email_backend.py:39-47`, `:155-222`); HTML bodies go through Docling's HTML backend. `list_attachments` stays off (it only lists names, `backend_options.py:702-714`) |
| Attachments | Docling never returns attachment contents. Our splitter reads the same input: `.eml` with stdlib `email` (`policy.default`), `.msg` with `python-oxmsg` (`Message.attachments`). Every leaf part with `Content-Disposition: attachment` or a file name that is not the body; inline images referenced from the HTML body and smaller than 50 KB are dropped (logos, signatures); embedded messages are returned as `message/rfc822` bytes; reference attachments are listed with `skipped: "reference"` (exact `python-oxmsg` support for embedded messages *to verify*) |

`extract-msg` is not used: its PyPI metadata says GPL (checked in review), which would put a copyleft dependency in a
distributed image. `python-oxmsg` is already Docling's choice, so there is no second `.msg` reader in the image.

### 5.2 Response

- `blocks`: an `email-header` block (subject, date; see §18 Q3 for sender and recipients) followed by the body blocks.
- `email`: `{subject, date, from, to[], cc[], message_id, in_reply_to}` as metadata. The worker decides what enters
  chunk text and runs *Detect personal data* on all of it.
- `attachments[]`: `{index, name, media_type_declared, media_type_sniffed, bytes, sha256, part, kind: "file" |
  "message", skipped?}`; `part` is the `Content-ID` of a binary part in the multipart response.
- **Names are data only**: Unicode NFC, path components and control characters stripped, `..` removed, 200 characters
  max, empty → `attachment-<index>`. The parser writes attachment bytes to `/tmp/jobs/<id>/att/<index>` and never
  uses the name as a path; names are never logged.

### 5.3 Limits and recursion

| Limit | Value | On breach |
|---|---|---|
| Attachments per message | 100 | `limit_exceeded` (`limit: "attachments"`) |
| Sum of attachment bytes | 1.5 × input size, ≤ 90 MB | `limit_exceeded` |
| MIME nesting depth inside one message | 10 | `limit_exceeded` |
| Embedded messages rebuilt per `.msg` | 20 | rest listed with `skipped: "limit"` |
| **Document depth** (email → attached email → its attachments) | 2, enforced by the worker | child at depth 3 recorded as run-item outcome `skipped: depth` |

### 5.4 Child documents (worker, flagged for data-model)

The worker ClamAV-scans each attachment part as its own blob, then creates a **child document** of the same
classification and pushes it through the same pipeline (routing, parse, PII detection, chunking). The document store
had no parent link (`data-model.md` §2.2 `Documents`); added there in the step 5 review:

| Change | Detail |
|---|---|
| `Documents.ParentDocumentId uuid NULL` | FK (`ParentDocumentId`, `Classification`) → `Documents` **Cascade**: purging the parent purges its children |
| `Documents.AttachmentOrdinal smallint NULL`, `Documents.Depth smallint NOT NULL DEFAULT 0` | order within the parent; depth check |
| `IdentityKind` value `attachment` | `CanonicalIdentity` = `<parent canonical identity>#att/<ordinal>/<sha256>`; identical attachments in two emails are two documents (same rule as data-model §12.13) |
| `DocumentSources` | the worker writes the parent's `SourceId`s for each child in the same transaction, with the attachment's sanitised `name` as `DisplayName` of every mirrored row (never `Documents.Title`), so collection mapping, shared chunk sets and the deletion guard work unchanged; a re-parse of the parent soft-deletes children whose hash disappeared (connectors design §10) |
| Citations | Knowledge API shows "Bilaga *name* i *subject*" through `ParentDocumentId`: *name* = the child's `DisplayName`, *subject* = the parent's `Documents.Title`, which for an email is `email.subject` (knowledge design §10.3) |

## 6. Quality signals and `/v1/inspect`

### 6.1 What the worker's heuristics need (processing design §3.1)

| Decision | Signal | Source |
|---|---|---|
| Clean text layer → PdfPig | `text_chars`, `garbled_score`, `image_coverage` | inspect |
| Empty or scanned → OCR `auto` | `text_chars` ≈ 0, `image_coverage` high | inspect |
| Garbled layer → OCR `force` | `garbled_score` high, `bad_chars` | inspect |
| Nothing found → re-render | after convert: `chars` ≈ 0 while `image_coverage` high | convert |
| Table-dense → Docling tables `accurate` | `path_objects` (ruling lines), later `table_area_ratio` | inspect, convert |
| Quality check (processing design §8) | `chars`, `garbled_score`, `ocr_confidence`, `empty`, `table_area_ratio`, `picture_area_ratio` | convert |

Proposed starting thresholds, for the worker to tune on the fixture corpus in step 6: clean = `text_chars ≥ 200`
(or `≥ 50` with `image_coverage < 0.3`) and `garbled_score < 0.2`; force OCR at `garbled_score ≥ 0.4`; re-render once
at `image_scale` 3.0 when a converted page has `chars < 20` and `image_coverage ≥ 0.5`; table-dense at
`path_objects ≥ 40` per page.

### 6.2 `POST /v1/inspect`

Same request shape as convert (file + small options), no models, served by a separate inspect process (§10) so it
never waits behind a conversion. Deadline 10 s, ≤ 2 000 pages.

| Field | PDF | TIFF | Image | OOXML / ODF / legacy | Email |
|---|---|---|---|---|---|
| `format`, `media_type_sniffed` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `page_count` | pages | frames | 1 | slides (PPTX), sheets (XLSX), null (DOCX, legacy) | null |
| `rangeable` | true | true | false | false | false |
| `encrypted` | `none` / `owner_only` (opens, accepted) / `user_password` (refused) | – | – | OOXML encryption package → `user_password` | – |
| `has_javascript`, `has_embedded_files`, `has_xfa` | ✓ (reported, never executed or extracted) | – | – | – | – |
| `pages[]` | per page below | per frame: size, DPI | size | – | – |
| `refusal` | the error code convert would give, if any | | | | |

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

The same function scores text-layer text (inspect) and converted page text (convert), so the worker compares like
with like. Test vectors in `tests/fixtures/parser/garbled/*.json` (text, expected range).

### 6.4 Per-page signals in a convert response

`page`, `label` (slide number, sheet name, or null), `width`, `height`, `chars` (body text), `furniture_chars`
(headers/footers), `garbled_score`, `text_layer_chars` (PDF), `image_coverage`, `ocr_applied` (none / bitmap /
full-page), `ocr_confidence` (mean and p10 of word confidences, null if the engine exposes none: *to verify*
whether Docling keeps OCR cell confidence on the converted page), `table_count`, `table_area_ratio`,
`picture_area_ratio`, `empty` (`chars == 0`), `ms` (time spent on the page, where Docling reports it).

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
  "ocr": { "mode": "auto", "languages": ["swe", "eng"], "engine": "default" },
  "tables": { "mode": "accurate" },      // fast | accurate | off
  "image_scale": 2.0,                    // 1.0–4.0, page render scale for layout/OCR
  "pictures": { "crops": false, "max_count": 50, "min_area_ratio": 0.05,
                "format": "png", "max_edge_px": 2048, "pages": null },
  "html_base_url": null,                 // HTML fallback only; never fetched
  "outputs": { "docling_json": true, "markdown": false }
}
```

`engine`: `default` (the P1 bake-off winner, §9), `tesseract`, `easyocr`. Unknown properties are rejected (400), so
drift shows at once.

### 7.3 Response

`200 OK`, `Content-Type: multipart/mixed; boundary=…`, parts in this order:

| Part | `Content-Type` | `Content-ID` | Present |
|---|---|---|---|
| 1 | `application/json` (`convert-result.schema.json`) | `result` | always |
| 2 | `application/vnd.docling.document+json`, `Content-Encoding: gzip` | `docling` | `outputs.docling_json` and not email/HTML-only |
| 3… | the attachment's sniffed type | `att-<index>` | email |
| … | `image/png` or `image/webp` | `pic-<n>` | `pictures.crops` |

Binary parts avoid base64 (33 % larger, held in memory twice) and let the worker stream each part straight into an
encrypted blob. The .NET client reads them with `MultipartReader` (`Microsoft.AspNetCore.WebUtilities`).

`result.json` (abridged):

```json
{
  "contract": "parser/v1",
  "request_id": "0193…-r3",
  "status": "success",                        // success | partial (deadline hit, see pages_completed)
  "input": { "format": "pdf", "media_type_sniffed": "application/pdf", "sha256": "…", "bytes": 4183022,
             "page_count": 112, "pages_requested": { "from": 41, "to": 60 },
             "pages_completed": { "from": 41, "to": 60 }, "converted_via": null },
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
                  "ocr_chars": 0, "crop": null } ],
  "attachments": [],
  "email": null,
  "warnings": [ { "code": "ocr_low_confidence", "page": 47 } ],
  "timings": { "total_ms": 38110, "wait_ms": 4, "office_ms": 0, "docling_ms": 37200, "signals_ms": 310 },
  "build": { "parser": "1.0.0", "docling": "2.x.y", "build_hash": "sha256:…" }
}
```

- **Block kinds**: `title`, `heading`, `paragraph`, `list_item`, `table`, `picture`, `caption`, `code`, `formula`,
  `footnote`, `page_header`, `page_footer`, `email_header`. `layer` is `body` or `furniture` (headers, footers, page
  numbers: Docling's content layer, *to verify*); the worker drops furniture from chunks by default.
- **`level_source`**: `style` (DOCX/ODF/RTF via styles), `layout` (PDF: the layout model detects section headers but
  its levels are unreliable, *to verify*), `html`, `markdown`. The worker's *Split by headings* node may re-level
  `layout` headings by numbering or a parse profile.
- **No character offsets**: each block carries its own Markdown (`md`), so there is no UTF-16 versus code-point
  question. The document Markdown is defined as the blocks' `md` joined by blank lines, with a line
  `<!-- page: N -->` before the first block of each page; the parser includes it only when `outputs.markdown` is set
  (preview, debugging). Pages without blocks still get a marker, unlike Docling's own page-break placeholder (Open
  WebUI notes that blank pages leave no break: `open-webui/backend/open_webui/retrieval/loaders/main.py:312-325`).
- **`ref`** is the JSON pointer into the `DoclingDocument` (`#/texts/12`), so a later consumer of the lossless JSON
  can join it to our blocks. Docling's JSON keys `texts`, `tables`, `pictures`, `key_value_items`, `form_items`,
  `pages`, `prov[].page_no` and `content_layer` are used by both Open WebUI (`main.py:312-325`) and RAGFlow
  (`ragflow/internal/parser/parser/pdf_parser_docling.go:273-300`), so they are stable across recent versions.
- **Pages**: absolute page numbers of the original file in every case (Docling keeps original numbering under a page
  range, *to verify*; otherwise the parser offsets). DOCX has `page` null; PPTX uses the slide number; XLSX uses the
  sheet index with `label` = sheet name.
- `tables[].cells` is the source of truth; `md` of a table block is a convenience rendering (pipe tables lose spans).

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
| 415 | `unsupported_format` | `reason`: `archive`, `macro_enabled`, `executable`, `unknown` | `none` | Fatal, outcome `unsupported` |
| 422 | `encrypted` | user password required (PDF or OOXML) | `none` | Fatal, outcome `encrypted` |
| 422 | `corrupt` | cannot be opened by the format's library | `none` | Fatal |
| 422 | `too_many_pages` | document > 2 000 pages, or range > 20 | `none` / `smaller_range` | Fatal / re-plan |
| 422 | `limit_exceeded` | bomb limits (§11), `limit` names which | `none` | Fatal, security event `5104` |
| 422 | `office_conversion_failed` | LibreOffice failed or hung | `none` | Fatal |
| 422 | `ocr_failed` | OCR engine error | `same` (once) | Transient once, then Fatal |
| 422 | `conversion_failed` | Docling raised | `same` (once) | Transient once, then Fatal |
| 422 | `timeout` | deadline hit with nothing usable | `smaller_range` | re-plan with half the range; single page → Fatal |
| 200 | `status: partial` | deadline hit after some pages | – | keep completed pages, request the rest |
| 500 | `worker_crashed` | conversion process died (signal) | `smaller_range` | as `timeout` |
| 507 | `resource_limit` | RSS watchdog killed the conversion | `smaller_range` | as `timeout` |
| 503 | `busy` / `not_ready` | no warm slot (`Retry-After`) | `later` | requeue after `Retry-After`, no attempt counted |

`5104 rag.parser.limit_exceeded` is a new security event in the worker's range (identity design §10 numbering), logged
by the worker with document id and limit name only.

### 7.6 What the worker stores

The parse output (data-model §2.2 `NodeOutputs` with `IsParseOutput`, kept while the document lives) becomes a set of
blobs under `documents/{documentId}/nodes/docling-parse/{cacheKey}/`:

| Blob | Content | Why |
|---|---|---|
| `result.json` | stitched `result.json` (all ranges, plus fast-path pages from PdfPig in the same shape) | input of chunking; **re-chunking never calls the parser** |
| `docling/{from}-{to}.json.gz` | one `DoclingDocument` per range (not merged) | lossless form for future chunkers or a P2 re-read; no re-parse after a chunker change |
| `pictures/{n}.png` | crops, only when requested (P2 describe-pictures) | – |
| `ranges/{from}-{to}.result.json` | per-range results, intermediate (30 days) | a failed range retries alone |

Size: a `DoclingDocument` is several times the Markdown (bounding boxes per item); gzip brings it near the Markdown
size. Acceptable at pilot scale; if not, drop it for `public` web PDFs first. `NodeOutputs.BlobPath` becomes a prefix
for multi-blob outputs (§17).

**Parser upgrades do not re-parse.** The cache key stays `inputHash ‖ nodeType ‖ nodeVersion ‖ paramsHash`
(data-model §9); the parser's `build_hash` is recorded per run item. A deliberate re-parse is a node-version bump or a
`reprocess` run, so "each canonical document is parsed once" holds across upgrades.

### 7.7 Idempotency and versioning

Conversion is a pure function of (bytes, options, build): retries are always safe. `request_id` only correlates logs
and rejects a concurrent duplicate (a worker whose lease expired while the first is still converting). `/v1` changes
are additive only (new optional fields, new enum values the worker treats as unknown-safe); a breaking change is
`/v2`, served alongside `/v1` for one release.

### 7.8 `POST /v1/ner` (added in step 6c)

Names for *Detect personal data* (worker plan answered 6, 18), specified in
[`../ingestion-worker/design-processing.md`](../ingestion-worker/design-processing.md) §10: JSON request with text
blocks, JSON response with code-point spans and labels; schemas `ner-request.schema.json`, `ner-result.schema.json`.
Model `KBLab/bert-base-swedish-cased-ner` (MIT), pinned revision `f3c83af8196b812028fc08e1971a2d289d0a3f1d`. Its
`config.json` labels (checked in review): `PER` for persons, plus mixed labels `ORG/PRS`, `PRS/WRK`, `LOC/PRS` and
others; `NER_PERSON_LABELS` defaults to `PER,PRS`; mixed labels are returned only on request and not counted in P1.
Served by its own warm process **`ner-0`** (own lane, never behind conversions; `NER_THREADS` 2, `NER_QUANTIZE`,
`NER_QUEUE` 4, recycle after 5 000 requests); `/v1/info` adds `capacity.ner_slots`, `ner_ready` and the model block
inside `build_hash`; readiness does not depend on NER.

## 8. Page ranges and stitching

### 8.1 Planning

1. Worker calls `/v1/inspect` (cheap) → page count, `rangeable`, per-page signals.
2. Auto-parse assigns a strategy per page (§6.1); PdfPig pages are taken locally.
3. Consecutive pages with the **same** parser strategy (OCR mode, table mode, scale) form runs; each run is cut into
   ranges of at most 20 pages. Range size starts at 20 (no OCR) or 8 (OCR) and adapts: after each range the worker
   sets the next size so that `measured ms/page × size ≤ 50 % of the deadline`.
4. Ranges of one document run sequentially in its `parse` job (the lease heartbeat covers it); different documents
   use the parallel slots. One parse job per document keeps run records and caching simple.

Non-rangeable formats (DOCX, PPTX, XLSX, legacy, email, single images) are one request; the declarative backends
and LibreOffice are fast enough (§14) that 120 s is ample for the pilot's documents. A DOCX that times out is a
`timeout` with `retry: none` (cannot be split) and is recorded as a failure.

### 8.2 Stitching (worker)

| Problem | Rule |
|---|---|
| Order | blocks sorted by (page, Docling reading order within the range) |
| Heading paths | computed after stitching from the ordered heading blocks and their levels, never per range, so a range that starts mid-chapter gets the right path |
| Tables across pages (also within one range: Docling does not merge them, *to verify*) | merge table B into A when A is the last body block of page p and ends within 15 % of the bottom, B is the first body block of page p+1 and starts within 15 % of the top, the column counts match, and B's header row is absent or equal to A's (then dropped) |
| Hyphenation at page ends | join `ord-\n` + lowercase continuation across the page boundary (`kommun-` / `fullmäktige`), also between ranges |
| Furniture | repeated `page_header`/`page_footer` text dropped |
| Partial ranges | `pages_completed` merged; missing pages requested again; still missing after the retry → outcome `degraded` with the page list |

## 9. OCR

### 9.1 Modes

| Mode | Docling setting (names checked in 2.145) | Use |
|---|---|---|
| `off` | `do_ocr=False` | clean text layer that still needs layout or tables |
| `auto` | `do_ocr=True`, `force_full_page_ocr=False`: OCR only bitmap regions (Docling's area threshold option, name *to verify*) | scanned or mixed pages |
| `force` | `do_ocr=True`, `force_full_page_ocr=True`: ignore the text layer | garbled text layer |

Images (JPG/PNG/TIFF) are always OCR'd (`off` is treated as `auto`, with a warning). Languages: `swe` + `eng` by
default (Tesseract codes; mapped to `sv`, `en` for EasyOCR). Strategy per page is achieved through ranges (§8.1),
because Docling's OCR options apply to a whole conversion.

### 9.2 Engines baked

| Engine | Docling options class (exists in 2.145) | Models | Notes |
|---|---|---|---|
| Tesseract 5 (CLI) | `TesseractCliOcrOptions(lang=["swe","eng"], tesseract_cmd="tesseract")` | Debian `tesseract-ocr-swe`, `-eng` (`tessdata_fast` or `best`, *to verify*); ≈ 10–30 MB | CLI avoids building `tesserocr` (uv `no-build = true`) |
| EasyOCR | `EasyOcrOptions(lang=["sv","en"], model_storage_directory=/opt/models/easyocr, download_enabled=False)` | CRAFT detector + Latin recogniser, ≈ 100 MB | torch-based |

Docling 2.145's default is `OcrAutoOptions` (`docling/datamodel/pipeline_options.py:332-345`, `:2204-2212`): it
picks the first available engine in the order OcrMac, Nemotron (CUDA), **RapidOCR (onnxruntime)**, EasyOCR, RapidOCR
(torch), and **never Tesseract**. The parser therefore **always sets the engine explicitly** and never uses the auto
option, so the engine cannot change silently with what happens to be installed. "Docling's default" in postponed
item P1 is read as EasyOCR; RapidOCR joins the bake-off only if a Latin model can be baked (*to verify*).

### 9.3 Bake-off (postponed item P1, during P1)

| Item | Design |
|---|---|
| Corpus | 60 scanned pages, public or synthetic only in the repo (§15.3), plus up to 40 real internal pages kept on the test host: forms, protokoll, typewritten older documents, fax-quality, phone photos of notices, tables, stamps and handwriting margins. Ground truth typed by two people for 40 pages |
| Metrics | CER and WER (normalised whitespace); **å/ä/ö recall** (share of ground-truth å, ä, ö recognised as the same letter); ms/page and peak RSS on 4 CPUs; confidence usefulness (Spearman ρ between page `ocr_confidence` and page CER); end-to-end: recall@10 of 20 questions over the OCR'd pages with BGE-M3 |
| Runs | each engine × `image_scale` {2.0, 3.0} × Tesseract `fast`/`best` data if both are available; script `src/parser/scripts/ocr_bakeoff.py`, output a Markdown report |
| Decision rule | Discard any engine with å/ä/ö recall < 95 %. Among the rest pick the lowest CER, unless another is within 1 CER point and ≥ 30 % faster. Confidence with ρ < 0.3 is reported as unusable: the Quality check then relies on the garble score |
| After | `ocr.engine=default` maps to the winner via `PARSER_OCR_DEFAULT`; the loser stays baked through P1 (re-test on real archives), removed in P2 to save image size; postponed item P1 struck |

## 10. Runtime

### 10.1 Process model

```
tini (PID 1)
└─ python -m ume_parser            supervisor + uvicorn (1 process, asyncio, TLS); no Docling import
   ├─ convert-slot-0 … N-1         spawned processes: Docling pipelines warm, PARSER_THREADS torch/OMP threads
   │   └─ soffice (per job)        own process group, rlimits (§4)
   ├─ inspect-0                    spawned process: pypdfium2 only, RLIMIT_AS 1 GB
   └─ ner-0 … L-1                  spawned processes (L = NER_LANES): KB BERT NER warm, NER_THREADS threads each (§7.8, step 6c)
```

- **Why not `uvicorn --workers N`** (repo-and-deployment §5.3): each uvicorn worker would run Docling in the
  request process, a native hang cannot be cancelled from Python, and a crash kills the request handler. **Why not
  `--limit-concurrency N`**: it counts every connection, so `/health/ready` would be refused while all slots are busy
  and the probe would mark a working container unhealthy. Both removed (§17).
- **Why own supervisor** (≈ 300 lines, `multiprocessing` spawn + pipes): `ProcessPoolExecutor` breaks the whole pool
  when one worker is killed; `pebble` is LGPL. The supervisor owns slot state, deadlines, kills and respawns.
- Admission: a request takes a free warm slot or gets `503 busy` with `Retry-After` at once. The parser has **no
  queue**: the worker's `parse` concurrency equals the capacity, which it reads from `/v1/info` `capacity.convert_slots`
  at start and every minute (so a change of `PARSER_WORKERS` needs no worker config).
- Slots: `PARSER_WORKERS` × `PARSER_THREADS` = the container's `cpus` (16 GB host 1 × 4; 24 GB host 2 × 2,
  repo-and-deployment §6.2). Inspect lane: one process, recycled after 200 jobs. NER lanes: `NER_LANES` processes
  (default 1; dedicated parser hosts 2–6, [`../performance-pass.md`](../performance-pass.md) §3.8), each with a
  self-test on a fixed Swedish sentence at start, at most `NER_QUEUE` (4) waiting requests else `503 busy`, recycled
  after 5 000 requests or RSS growth > 40 %; readiness does not depend on them (`ner_ready` in `/v1/info`).
- Scale-out ([`../performance-pass.md`](../performance-pass.md) §3.8): a parser host runs its own `parse,ner` worker on
  the host's internal `parser` network, so the parser keeps no other network. Only the optional multi-endpoint mode
  (`Parser:Endpoints`, cross-host parser traffic) adds `PARSER_TLS_CLIENT_CA` (uvicorn client-certificate
  verification, *to verify*); not used in the 3- and 5-host topologies.

### 10.2 Warm-up and readiness

1. Supervisor starts the API (live = true), wipes `/tmp/jobs`, spawns slots.
2. Each slot builds a `DocumentConverter` for the default option set (OCR `auto` with the default engine, tables
   `accurate`, scale 2.0) from `/opt/models`, then converts an embedded 1-page self-test PDF (text, a scanned strip,
   a small table) and checks the expected text.
3. Slot 0 also runs LibreOffice on an embedded 1-paragraph `.odt` (first start of LibreOffice is slow; this pays it).
4. Ready when **all** slots passed at start; afterwards ready while **at least one** slot is warm (a recycling slot
   reduces `slots_ready` in `/v1/info`; requests meanwhile may see `busy`).

Other option sets (OCR `force`, other engine, tables `fast`) are built lazily and kept in an LRU of three pipelines per
slot (Docling caches pipelines per options, *to verify*; each holds its models, hence the bound).

### 10.3 Limits per conversion, recycling, cleanup

| Mechanism | Value (16 GB host, 1 × 4) | Effect |
|---|---|---|
| Deadline | `min(deadline_ms, 120 s)`; Docling's own document timeout set to deadline − 10 s (*to verify* option name) for a graceful `partial` | then `SIGKILL` at deadline + 10 s → `timeout` |
| RSS watchdog | supervisor samples each slot every 500 ms (`psutil`); kill above `PARSER_SLOT_MAX_RSS_MB` (2 800; 2 × 2: 2 600) | `resource_limit`, respawn |
| `RLIMIT_AS` | not used for slots (torch and ONNX reserve large virtual ranges); used for LibreOffice and inspect | – |
| OOM | slots and LibreOffice `oom_score_adj=1000`, so the kernel picks them before the supervisor | supervisor survives, respawns |
| Recycle | after 50 conversions, or when idle RSS exceeds warm RSS + 40 % | respawn between jobs; reason in metrics |
| Job directory | `/tmp/jobs/<request_id>/` (input, LibreOffice profile and output, attachments, crops) | removed by the supervisor after the response is sent or the slot is killed; wiped at start |
| tmpfs | `/tmp` 1 GB (was 2 GB): tmpfs pages count against the container's memory limit | – |

### 10.4 Cancellation

The worker gives up by closing the connection (its HTTP timeout = deadline + 15 s, or a newer execution cancels the
job, worker §4). The API awaits the slot result while checking `request.is_disconnected()` every second; on
disconnect the supervisor kills the slot (and its LibreOffice group) and respawns it. Killing costs a re-warm
(≈ 10–30 s, *to measure*) but is the only reliable stop for native code. No cancel endpoint is needed: worker and
parser talk directly, without a proxy that could hide a disconnect.

## 11. Security

Inputs arrive after ClamAV but are untrusted. The container is the outer sandbox: no network (`parser` network is
`internal`, the parser has no other), non-root 10001, read-only root, `cap_drop: [ALL]`, `no-new-privileges`,
`pids_limit` 256, memory and CPU limits (repo-and-deployment §6). Inside it:

| Threat | Control |
|---|---|
| Decompression bomb, OOXML/ODF zip | pre-check before any library opens it: entries ≤ 10 000, total uncompressed ≤ 512 MB, per-entry ratio ≤ 200, no absolute or `..` entry names, no nested zip except known embeddings (`embeddings/*.xlsx` etc., not opened in P1) → `limit_exceeded` |
| Decompression bomb, images | Pillow `Image.MAX_IMAGE_PIXELS` = 100 MP as a hard error (`DecompressionBombError`, not a warning); TIFF ≤ 2 000 frames; render size per PDF page ≤ 60 MP (scale reduced, warning) |
| PDF JavaScript, XFA, launch actions | never executed: pdfium in pypdfium2 is built without V8/XFA (*to verify*); reported by inspect only |
| PDF embedded files | not extracted in P1, reported (`has_embedded_files`); P2 may return them like email attachments |
| Pathological PDF (deep object trees, huge content streams, endless xref loops) | deadline + kill, RSS watchdog, page cap 2 000; inspect lane has `RLIMIT_AS` and its own 10 s deadline |
| XXE in OOXML/ODF | lxml ≥ 5 resolves internal entities only by default (*to verify*); `defusedxml` installed so openpyxl uses it (*to verify*); no network for external DTDs; XXE fixtures (§15.3) |
| LibreOffice macros and links | §4: macros disabled, links never updated, fresh profile, rlimits, timeout; local-file-link fixture |
| Path traversal | attachment and zip entry names never used as paths (§5.2); job directory per request |
| Resource exhaustion | slot count = capacity, no queue, per-request deadline, body limit 60 MB enforced while streaming (uvicorn `h11_max_incomplete_event_size` and a counting reader), multipart part count ≤ 2 |
| Runtime downloads | `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`, `HF_DATASETS_OFFLINE=1`, Docling `artifacts_path=/opt/models`, EasyOCR `download_enabled=False`, `TESSDATA_PREFIX` fixed, `TORCH_HOME`/`XDG_CACHE_HOME` under `/tmp/cache`; `test_offline.py` runs a conversion of every format in a container with `--network none` **and** with `socket.socket.connect` patched to raise, so a silent fallback download fails the test instead of passing on a cached file |
| Content in errors and logs | §7.5 fixed `detail`; §13 log rules |
| Parser compromise | blast radius: its own TLS key and the documents in flight. No database, blob or key access; the worker treats every response as untrusted data (schema validation, size caps per part: `result.json` ≤ 50 MB, each binary part ≤ 60 MB, total ≤ 150 MB) |

### 11.1 Limits (reconciled with repo-and-deployment §5.3)

| Limit | §5.3 | Here | Change |
|---|---|---|---|
| Request body | 60 MB | 60 MB (uploads ≤ 50 MB; crawler max fetch also 50 MB) | none |
| Pages per request | 20 | 20 (hard); worker sizes adaptively, 8 with OCR | none |
| Request deadline | 120 s | 120 s max, from `deadline_ms`; hard kill +10 s | none |
| Pages per document | – | 2 000 | new |
| Inspect deadline | – | 10 s | new |
| `/tmp` tmpfs | 2 GB | 1 GB | changed |
| Per format | – | §5.3 email, §7.4 cells, §11 zip/pixels | new |

## 12. Image

| Topic | Decision |
|---|---|
| Base | `python:3.12-slim-trixie`, digest-pinned. **Changed from bookworm**: Debian 13 carries LibreOffice and Tesseract releases about two years newer, and bookworm is oldstable during the pilot. Python 3.12 stays (Docling supports 3.10–3.13, *to verify*) |
| Stages | 1: uv (pinned `ghcr.io/astral-sh/uv` digest) `uv sync --frozen --no-dev --no-install-project`; `scripts/download_models.py` with pinned Hugging Face revisions and SHA-256 per file into `/opt/models` (the only networked step); `scripts/build_lo_profile.py` writes `/opt/lo-profile-template`. 2: runtime packages, copy venv, models, profile; `USER 10001` |
| apt | from `snapshot.debian.org/archive/debian/<date>` and `…/debian-security/<date>` with the date ≥ 7 days before the build (same intent as `exclude-newer`; reproducible); bumping the date is the security update. Packages: `tini`, `tesseract-ocr`, `tesseract-ocr-swe`, `tesseract-ocr-eng`, `libreoffice-writer-nogui`, `libreoffice-calc-nogui`, `libreoffice-impress-nogui`, the fonts in §4, `libgl1`, `libglib2.0-0` |
| Python | uv lock with hashes, `no-build = true`, `exclude-newer` 7 days before lock (repo-and-deployment §2.4). `docling-slim` with only the extras needed (standard formats, `feat-ocr-easyocr`, email), not the `docling` meta package's full set. Adds: `easyocr`, `pypdfium2` (also a Docling dependency), `python-oxmsg` (Docling's `.msg` reader), `defusedxml`, `psutil`, `pillow`. CPU wheels for `torch` and `torchvision` from the PyTorch CPU index |
| Docling extras | layout and TableFormer models only; picture classification, code/formula enrichment and picture description **off and not baked** |
| Entry | `ENTRYPOINT ["tini","--"]`, `CMD ["python","-m","ume_parser"]` (supervisor starts uvicorn programmatically on 8443 with the TLS files) |
| Env | `PARSER_WORKERS`, `PARSER_THREADS`, `OMP_NUM_THREADS`, `PARSER_OCR_DEFAULT`, `PARSER_SLOT_MAX_RSS_MB`, `NER_LANES` (1), `NER_THREADS` (2), `NER_QUANTIZE` (1), `NER_QUEUE` (4), `NER_PERSON_LABELS` (`PER,PRS`), offline variables (§11) |
| Size target | ≤ 4.0 GB with the NER model (≤ 3.5 GB before it; §5.1 says 2.5–3.5 GB): base ≈ 150 MB, torch CPU + torchvision ≈ 800 MB, other Python ≈ 500 MB, models ≈ 500 MB, LibreOffice + fonts ≈ 500 MB, Tesseract ≈ 50 MB. Measured in P0; above 3.5 GB, the first cut is the bake-off loser |

Models baked (sizes approximate, *to verify* at P0):

| Model | Source | Size | Licence (postponed P2) |
|---|---|---|---|
| Docling layout model (current default, e.g. "heron") | Hugging Face `docling-project/…`, pinned revision | ≈ 150–200 MB | *to verify* (model card) |
| TableFormer fast + accurate | same repository family | ≈ 200 MB | *to verify* |
| EasyOCR CRAFT detector + Latin recogniser | EasyOCR model release URLs, pinned SHA-256 | ≈ 100 MB | Apache-2.0 (*to verify* for weights) |
| KB BERT NER (`KBLab/bert-base-swedish-cased-ner`, rev `f3c83af`) | Hugging Face, pinned revision + SHA-256 | ≈ 499 MB fp32 | MIT (SUC 3.0 training data: terms *to verify*); uses `transformers`, `tokenizers`, `safetensors` (Apache-2.0, already present) |
| Tesseract `swe`, `eng` | Debian packages | ≈ 10–30 MB | Apache-2.0 |
| Garble bigram table | built in-repo from public Swedish/English text | ≈ 50 KB | own |

**Licence inventory** (postponed P2, before the pilot): `src/parser/LICENSES.md` replaces `MODELS.md`, generated at
build from `uv pip list` + package metadata, Debian `copyright` files of installed packages, and the model table above.
Known so far: Docling and its parsers MIT (`docling-parse` *to verify*), PyTorch BSD-3, pdfium BSD-3/Apache-2.0,
LibreOffice MPL-2.0, Tesseract Apache-2.0, EasyOCR Apache-2.0, olefile BSD-2, psutil BSD-3, tini MIT, Liberation and
Carlito/Caladea fonts OFL-1.1, DejaVu fonts Bitstream Vera licence; `python-oxmsg` MIT; `docling-slim` and
`docling-core` MIT and EasyOCR Apache-2.0 (PyPI metadata, checked in review); **`extract-msg` GPL, not used**.

## 13. Health, info, observability

### 13.1 Endpoints

| Endpoint | Content |
|---|---|
| `/health/live` | 200 while the supervisor loop runs (no slot check) |
| `/health/ready` | 200 when the self-tests of §10.2 passed and ≥ 1 slot is warm; 503 otherwise. Never blocked by conversions |
| `/v1/info` | `parser` version and git revision; Python; `docling`, `docling-core`, `docling-parse`, `docling-ibm-models`, `torch`, `pypdfium2` (+ pdfium build), `easyocr`, Tesseract version and tessdata languages; LibreOffice version; each model with repository, revision and SHA-256; `ocr_default`; the NER model block (repository, revision, SHA-256, labels, quantised); `capacity` (`convert_slots`, `slots_ready`, `inspect_slots`, `ner_slots` = `NER_LANES`, `ner_ready`); `limits` (§11.1); supported formats; `build_hash` = SHA-256 of the canonical JSON of everything except capacity |

The worker records `build_hash` on every run item and stores the full info JSON once per hash (§17). The release smoke
test already calls `/v1/info` from the worker container (repo-and-deployment §8.1).

### 13.2 Traces, metrics, logs

- **Traces**: `opentelemetry-instrumentation-fastapi`, OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT` (same collector as the
  .NET services); the worker sends `traceparent`, so fetch → scan → inspect → parse → embed is one trace. Slots report
  timings back over the pipe and the supervisor records child spans (`parser.office`, `parser.docling`,
  `parser.signals`) with explicit start and end times, so slot processes need no exporter. Span attributes: format,
  page count, range, OCR mode and engine, table mode, slot, outcome code, bytes. **Never** file names, titles,
  attachment names, email headers or text.
- **Metrics** (meter `Ume.Rag.Parser`, names in the platform's `rag.` style):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.parser.duration` | histogram (s) | endpoint, format, ocr mode, engine, outcome |
| `rag.parser.duration_per_page` | histogram (s) | format, ocr mode, engine, table mode |
| `rag.parser.pages` | counter | format, ocr applied |
| `rag.parser.failures` | counter | code |
| `rag.parser.inflight`, `rag.parser.slots_ready` | gauge | – |
| `rag.parser.busy_rejections` | counter | – |
| `rag.parser.recycles` | counter | reason (`max_jobs`, `rss_growth`, `rss_limit`, `deadline`, `disconnect`, `crash`) |
| `rag.parser.slot_rss` | gauge (bytes) | slot |
| `rag.parser.office.duration` | histogram (s) | source format, outcome |

  The worker's existing `rag.parse.duration_per_page` (repo-and-deployment §9) stays as the end-to-end view including
  PdfPig pages. The *Ingestion* dashboard gets slot readiness, recycles by reason and failures by code.
- **Logs**: JSON to stdout, one line per request: `request_id`, trace id, endpoint, format, bytes, page count, range,
  options summary, timings, outcome code, slot, recycle reason. Docling receives the stream as
  `DocumentStream(name="<request_id><ext>")`, so its own log lines carry no real names. Library exception messages
  only at DEBUG (off in production); at INFO the exception type and our code. Docling, EasyOCR and LibreOffice
  stdout/stderr captured and reduced to line counts unless DEBUG.

## 14. Performance targets

Pilot host parser share: 4 CPUs (`cpus: 4.0`), 1 slot × 4 threads. Targets are p50 per page, upper bound p95; all
*to measure* in P1 (the numbers are planning estimates, not Docling benchmarks).

| Input | p50 | p95 | Notes |
|---|---|---|---|
| `/v1/inspect`, PDF | 0.02 s | 0.1 s | ≤ 2 s for 100 pages |
| PDF, text layer, layout + tables `fast` | 1.0 s | 2.5 s | only pages PdfPig cannot take |
| PDF, text layer, tables `accurate` | 1.5 s | 4 s | table-dense pages |
| PDF/image, OCR `auto`, Tesseract, scale 2.0 | 4 s | 8 s | scanned page |
| same with EasyOCR | 8 s | 15 s | bake-off decides |
| DOCX / PPTX / XLSX (declarative) | ≤ 5 s per document of 100 pages | 15 s | no models |
| Legacy/ODF/RTF | LibreOffice ≤ 10 s + DOCX path | 30 s | first conversion after a recycle slower |
| Email (without attachments) | ≤ 2 s per message | 5 s | |
| Slot re-warm after a kill | ≤ 30 s | 60 s | |
| `/v1/ner`, per 512-token window (fp32 / int8) | *to measure* | – | ≈ 0.3–1.0 s of lane time per typical web page after the worker's prefilter (processing design §16) |

**Throughput estimate**: the pilot's < 500 scanned documents at ≈ 6 pages each ≈ 3 000 pages × 4 s ≈ 3.3 h of parser
time with Tesseract (≈ 7 h with EasyOCR): one night. Native PDFs are mostly PdfPig in the worker; the parser sees
only their table-dense or garbled pages.

**How P1 measures it**: `src/parser/scripts/bench.py` runs the fixture corpus plus a public sample of umea.se
documents against the image on the test host (8 vCPU / 16 GB, parser at `cpus: 4`), for 1 × 4 and 2 × 2 slot
layouts, and writes s/page by format, OCR mode and engine, peak RSS per slot, and documents per hour. The R1 release
test's OCR batch of 50 scanned PDFs (repo-and-deployment §8.3) adds the "while chat runs" number. Expectation to
confirm: 2 × 2 gives more pages per hour, 1 × 4 lower latency per document; the pilot default stays 1 × 4 on 16 GB.

## 15. Tests

### 15.1 Parser (pytest, `src/parser/tests/`)

| Kind | Content |
|---|---|
| Unit | options → Docling options mapping (engine always explicit, never `OcrAutoOptions`); sniffing (every format, mismatches); garble detector vectors; signals from synthetic page data; attachment splitter (.eml multipart variants, encodings, RFC 2231 names, inline images; `.msg` attach methods and embedded messages via `python-oxmsg`); `soffice-sandboxed` wrapper (profile copied, rlimits set); name sanitiser; zip pre-check; limit enforcement; error mapping (each code once); Markdown join with page markers |
| Supervisor | with a fake converter: deadline kill, disconnect kill, crash (`os.abort`), RSS limit (allocating converter), recycle after N, `busy` when full, readiness during recycle, job directory always removed |
| Integration (`slow`) | real Docling and LibreOffice on `tests/fixtures/parser/*`: expected block kinds, heading levels, table shapes, page numbers, å/ä/ö preserved |
| Offline | `test_offline.py` (§11) |
| NER | label mapping, limits, `busy`, code-point offsets with emoji and combining characters, windows over 512 tokens, quantised vs fp32 agreement (processing design §17) |
| Schema | `export_schema.py` writes `convert-request`, `convert-result`, `inspect-result`, `ner-request`, `ner-result`, `info`, `error` schemas from the pydantic models; CI fails on `git diff` in `contracts/` (repo-and-deployment §8.2) |

### 15.2 Contract tests (.NET, `tests/Ume.Rag.ContractTests`)

- Start the parser image (Testcontainers), convert every fixture with the worker's real client, read the multipart
  response, validate `result.json` against `contracts/parser/v1/convert-result.schema.json` (JsonSchema.Net) and
  deserialise into the client's records with `JsonUnmappedMemberHandling.Disallow` in tests (production tolerates
  unknown fields), so drift in either direction fails.
- Compare with golden files `tests/fixtures/parser/golden/*.json`, normalised: timings, versions and `build` removed;
  block kinds, levels, page numbers, table shapes and attachment lists exact; text by similarity ≥ 0.98 per block
  (Docling upgrades shift whitespace). Goldens are updated only by `scripts/update_goldens.py` in a PR that bumps
  Docling, with the diff reviewed.
- Error contract: one fixture per error code (encrypted, corrupt, zip, too many pages via a 2 001-page generated
  PDF, bomb), asserting HTTP status, `code` and `retry`.

### 15.3 Fixture corpus (`tests/fixtures/parser/`)

Every file is **public or synthetic**, listed in `MANIFEST.csv` (path, origin URL or generator, licence, contains
personal data: must be `no`). The pre-commit hook and CI reject a fixture missing from the manifest and scan fixture
text for personnummer patterns other than Skatteverket's published test numbers.

| Group | Content |
|---|---|
| Municipal PDFs | published umea.se documents: kallelse, protokoll with § numbering, taxa with fee tables, detaljplan text pages, a two-column newsletter, a report with footnotes and a TOC |
| Scanned | synthetic forms rendered with fake data, then "print-scanned" (noise, 1–3° skew, 200 dpi, JPEG artefacts); a typewriter-style page; a rotated page; a mixed PDF (text pages + scanned annex) |
| Swedish text | å/ä/ö/é in headings and tables; hyphenation across lines and pages (`kommun-` / `fullmäktige`); a garbled-encoding PDF (font without ToUnicode); mojibake text |
| Tables | merged cells, multi-row headers, a table across a page break, an XLSX with two sheets (one hidden), formulas, a 250 000-cell sheet (truncation), CSV in Windows-1252 with `;` |
| Office | DOCX with heading styles, lists, tables; PPTX with notes; legacy `.doc`/`.xls`/`.ppt` and `.odt`/`.ods`/`.odp`/`.rtf` produced from the same synthetic sources by LibreOffice (so outputs can be compared) |
| Email | `.eml` with HTML body, PDF and DOCX attachments, an attached `.eml` with its own attachment, an inline logo, a zip attachment; `.msg` with the same content, created once in Outlook by the team from synthetic content (provenance in the manifest) |
| Malicious | zip bomb inside a DOCX; nested-zip "42"-style; PNG with 60 000 × 60 000 dimensions; PDF with `/OpenAction` JavaScript; PDF with an embedded file; PDF with a cyclic xref and deep object nesting; DOCX and ODT with an XXE entity to `file:///etc/passwd`; ODT and DOC with a link field to `file:///run/certs/parser.key` (assert no `BEGIN` or `root:` in any output); macro-enabled `.docm` (refused); attachment named `../../etc/cron.d/x` |

Real internal documents used for the OCR bake-off and benchmarks stay on the test host in an access-limited,
encrypted directory (`/srv/ume-rag-bench/`), are never committed, and are reported as aggregate metrics only.

### 15.4 Robustness and fuzzing

- **Atheris** (Apache-2.0) harnesses for our own parsers: attachment splitter, zip pre-check, sniffing,
  name sanitiser; nightly for 10 minutes each in a new `parser-robustness.yml` (weekly in P1, nightly once stable).
- **Mutation run** against the whole container: each fixture mutated 200 times (byte flips, truncation, chunk
  duplication) and sent through `/v1/inspect` and `/v1/convert`. Pass: every response is valid schema or a defined
  error; no request exceeds deadline + 15 s; the supervisor never restarts; RSS returns to baseline after recycling.
- Crash findings become fixtures.

## 16. Milestones

| Phase | Deliverable |
|---|---|
| **P0** | Image with Docling, both OCR engines, LibreOffice and models baked; size measured; supervisor with one slot and warm-up self-test; `/health/*`, `/v1/info` with `build_hash`; the five convert/inspect/info/error schemas in `contracts/parser/v1/`; `/v1/convert` for a plain PDF (enough for the contract job); `test_offline.py` green with `--network none` (P0 checklist #12, #13) |
| **P1** | `/v1/inspect`; `/v1/ner` with `ner-0` and its two schemas (§7.8); all pilot formats (§3) incl. LibreOffice and email with attachments; OCR `auto`/`force`/`off`; tables `fast`/`accurate`; **page ranges** with `partial`; per-page signals and garble detector; picture crops on request (contract and implementation; no consumer yet); every error code; limits and bomb checks; RSS watchdog, recycling, disconnect kill; OTel and metrics; fixture corpus and goldens; contract tests; OCR bake-off decided (postponed P1); licence inventory (postponed P2); `bench.py` report on the test host |
| **P2** | Remove the bake-off loser; describe-pictures node consumes crops (worker → gateway, residency rules); OCR for pictures in DOCX/PPTX (route via PDF); PDF embedded files as child documents; decide archives and macro-enabled formats; nightly fuzzing; GPU target if volumes need it |

Moved from the plan's P2 to P1: page ranges (the 20-page request limit in §5.3 already requires them for any long
PDF) and table modes (one option). Flagged in §17.

## 17. Changes to other documents

All applied in the step 5 review commit (2026-10-29).

| Document | Change |
|---|---|
| `parser/plan.md` | §2: add `POST /v1/inspect`; options as in §7.2 (`pages` object, `pictures`, `outputs`, `deadline_ms`, `request_id`); response is `multipart/mixed` with `result.json`, `docling.json.gz`, binary attachments and crops. §3: process model = supervisor + slots (not uvicorn workers). §4: P1 adds page ranges, table modes, inspect, email, LibreOffice formats, images; P2 per §16 |
| `ingestion-worker/plan.md` | §2/§4: parse concurrency read from `/v1/info` `capacity.convert_slots`; `busy`/`not_ready`/`duplicate_in_flight` requeue without counting an attempt; error mapping §7.5. §4 node catalogue P1: *Plain text/Markdown* and *CSV table* nodes (worker-side), *Inspect* used by Detect/Auto-parse, email child documents with depth ≤ 2; spreadsheet chunking as row groups with repeated headers. §6: signals and starting thresholds from §6.1; range planning and adaptive sizing (§8.1); stitching rules (§8.2). §11.3: upload types add `.doc`/`.xls`/`.ppt`, `.rtf`, `.jpg`/`.png`/`.tif(f)`, `.eml`/`.msg`; zip and macro-enabled formats refused |
| `platform/data-model.md` | §2.2 `Documents`: `ParentDocumentId` (FK with `Classification`, Cascade), `AttachmentOrdinal`, `Depth`, `IdentityKind` `attachment`; children mirror the parent's `DocumentSources`. `NodeOutputs.BlobPath` may be a prefix (multi-blob outputs). `RunItems.ParserBuildHash bytea`; new main-tier table `content."ParserBuilds"` (`BuildHash` PK, `Info jsonb`, `FirstSeenAt`). §9 blob layout: `documents/{documentId}/nodes/docling-parse/{cacheKey}/result.json`, `docling/{from}-{to}.json.gz`, `pictures/{n}.png`, `ranges/…` (intermediate, 30 days); child documents' raw blobs under their own `documents/{childId}/raw/` |
| `platform/repo-and-deployment.md` | §2.4: module list (`supervisor.py`, `slot.py`, `inspect.py`, `office.py`, `mail/`, `signals.py`, `limits.py`, `sniff.py`), packages (`docling-slim` with selected extras, `easyocr`, `pypdfium2`, `python-oxmsg`, `defusedxml`, `psutil`, `pillow`), the `soffice-sandboxed` wrapper (`bin/`), scripts (`bench.py`, `ocr_bakeoff.py`, `update_goldens.py`, `build_lo_profile.py`), `LICENSES.md` replaces `MODELS.md`. §5.1: parser base `python:3.12-slim-trixie`, tmpfs `/tmp` 1 GB, size ≤ 3.5 GB incl. LibreOffice. §5.3: apt from `snapshot.debian.org` (7-day date), packages per §12, `tini` entrypoint, `CMD python -m ume_parser`; **remove `--workers` and `--limit-concurrency`** (the latter would fail health probes under load); limits table §11.1 (adds 2 000 pages/document, inspect 10 s). §6.2: parser memory limits unchanged; add env `PARSER_SLOT_MAX_RSS_MB`. §8.2: parser job also runs supervisor tests; new `parser-robustness.yml`; contract job validates schemas and error fixtures. §9: parser metrics `rag.parser.*` (§13.2). `contracts/parser/v1/` file list: `convert-request`, `convert-result`, `inspect-result`, `info`, `error` |
| `identity/design.md` §10 / repo-and-deployment §9 | security event `5104 rag.parser.limit_exceeded` (worker logs it) |
| `postponed-decisions.md` | P1: candidates Tesseract CLI vs EasyOCR (RapidOCR only with a Latin model), bake-off design and rule in §9.3. P2: add LibreOffice, fonts, EasyOCR weights, `python-oxmsg`, `docling-parse`; note `extract-msg` rejected (GPL). V1: add `docling-slim` (2.145 reviewed), its parts, EasyOCR, Tesseract, LibreOffice (trixie), pypdfium2, python-oxmsg, uv image digest, snapshot date. New row **Z1**: archives (.zip) and macro-enabled Office formats, refused in P1, decide in P2 |
| `01-general-idea.md` §10 | after review, a "Parser detail (step 5)" row: routing, inspect, Docling's native legacy/ODF/email backends with our sandboxed `soffice` wrapper, attachment splitter, multipart contract, stored DoclingDocument, supervisor model, explicit OCR engine |

## 18. Answered questions (2026-10-29)

1. **`.msg` library**: resolved in review: Docling 2.145 reads `.msg` with `python-oxmsg` (MIT) and the splitter
   reuses it; `extract-msg` (GPL) is not used.
2. **Archives and macro-enabled Office in the pilot**: refused in P1 (`unsupported_format`), decided in P2
   (postponed item Z1).
3. **Email senders and recipients**: subject and date in chunk text; From/To/Cc only as metadata (not searchable,
   not shown in citations).

## 19. To verify

Docling (checked in review against `docling-slim` 2.145.0: `InputFormat` incl. `DOC`, `XLS`, `PPT`, `RTF`, ODF and
`EMAIL`; the legacy → OOXML LibreOffice path; the email backend and `python-oxmsg`; `PdfPipelineOptions` fields
`do_ocr`, `do_table_structure`, `images_scale`, `generate_picture_images`, `artifacts_path`, `document_timeout`,
`force_full_page_ocr`; `TableFormerMode`; `TesseractCliOcrOptions`, `EasyOcrOptions`, `RapidOcrOptions`,
`OcrAutoOptions` as default; `convert(page_range=…)`). Still to verify at P0 against the pinned version:

- Whether page numbers stay absolute under `page_range`; `max_num_pages`, `max_file_size`.
- The name of the bitmap-area threshold option (`bitmap_area_threshold` does not exist in 2.145); `do_cell_matching`.
- Whether the legacy LibreOffice timeout can be set through backend options; how Docling handles `.docm`.
- Whether Docling keeps OCR word confidence on converted pages, and how to read it after conversion.
- `DoclingDocument`: `content_layer` values (`body`, `furniture`), `SectionHeaderItem.level` reliability on PDFs,
  `export_to_markdown(page_no=…)` and `page_break_placeholder`, JSON pointer `self_ref`, table cell fields
  (`row_span`, `col_span`, `column_header`), whether tables split across pages are ever merged.
- Excel backend: `data_only` values, hidden sheets, several tables per sheet; PPTX slide = page; DOCX `page_no`.
- `python-oxmsg` support for embedded messages and reference attachments.
- Pipeline caching per options in `DocumentConverter` and the memory cost of several cached pipelines.
- Current layout model name, Hugging Face repositories, revisions, sizes and **model licences**; TableFormer sizes.
- Supported Python versions of the pinned release.

LibreOffice:

- `-nogui` package names and installed size on trixie; filter names for `--convert-to` (DOCX/XLSX/PPTX).
- Registry keys for macro security level, disabling macro execution, link update mode (Writer and Calc), and update
  check, in `registrymodifications.xcu`.
- That a document field or OLE link to a local file cannot leak content with links set to "never update"
  (the fixture in §15.3 is the proof).
- Behaviour under `RLIMIT_AS` 1.5 GB for large spreadsheets.

Libraries and licences:

- `olefile` licence (BSD-2, via `python-oxmsg`).
- pypdfium2's pdfium build without V8/XFA; licence of the bundled pdfium.
- lxml ≥ 5 default `resolve_entities`; whether openpyxl, python-docx and python-pptx (used by Docling) resolve
  external entities; openpyxl's use of `defusedxml`.
- EasyOCR Swedish (`sv`) support in the Latin model and its weights' licence.
- Debian `tesseract-ocr-swe` data variant (`fast` or `best`).
- uvicorn: detection of client disconnect during a long await; `h11_max_incomplete_event_size` behaviour for bodies.
- `snapshot.debian.org` usability for `debian-security` in CI builds (rate limits).

Numbers:

- All performance targets in §14, warm-up and re-warm times, slot RSS at idle and peak, image size (§12).
