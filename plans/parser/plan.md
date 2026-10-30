# Parser sidecar: plan

**Detailed design: [`design.md`](design.md)** (step 5); where they differ, the design wins. Rewritten to the
simplification pass ([`../simplification-pass.md`](../simplification-pass.md)); removed and deferred items in §6.

A small, stateless Python service that converts hard formats (PDF, Office, scanned documents) into structured
Markdown. It exists because Python's document tools (Docling) are far ahead of .NET's for this job; everything else
stays in .NET. The worker parses no binary format itself: every PDF comes here.

## 1. Scope

**Does:** convert one document per request; a text-layer fast path for clean PDF pages (pypdfium2); OCR (Tesseract
`swe`); table structure; page numbers and heading levels (paths are computed by the worker); cheap per-page signals
(`/v1/inspect`); names for personal-data detection (`/v1/ner`, called only when gated).

**Does not:** fetch anything, store anything, chunk, embed, or decide strategy (the worker's auto-parse decides and
passes options).

## 2. API

| Method | Path                            | Purpose                                                            |
| ------ | ------------------------------- | ------------------------------------------------------------------ |
| `POST` | `/v1/convert`                   | Multipart file + options → `multipart/mixed` (result JSON, DoclingDocument) |
| `POST` | `/v1/inspect`                   | Cheap page count and per-page text-layer signals (no models)       |
| `POST` | `/v1/ner`                       | Person names in text units (JSON in/out; design §7.8)              |
| `GET`  | `/health/live`, `/health/ready` | Probes                                                             |
| `GET`  | `/v1/info`                      | Versions, build hash (recorded in run items), capacity (`convert_slots`), limits |

Options (design §7.2): `request_id`, `file_ext`, `media_type`, `pages` (`{from, to}`), `deadline_ms`, `pipeline`
(`text_layer` / `docling`, PDF), `ocr` (`mode` `auto` / `force` / `off`, `languages`), `tables` (`fast` / `accurate`
/ `off`), `image_scale` (for re-render), `outputs` (`docling_json`, `markdown`).

Response: `multipart/mixed` with `result.json` (blocks with their own Markdown, heading levels, pages, tables with
cells, per-page quality signals, timings, build) and `docling.json.gz` (lossless `DoclingDocument`). Versioned contract
(`/v1`), tested by contract tests from the .NET side.

## 3. Design

- FastAPI + Docling; OCR with **Tesseract `swe` only**, always set explicitly, never Docling's auto choice (design §9).
- All models baked into the image at build time: no downloads at runtime (on-prem hosts without internet).
- CPU-only by default; a GPU image variant later if volumes need it.
- Concurrency: an own supervisor with pre-warmed conversion slots, one inspect process and one NER process (not
  uvicorn workers), no queue (`503 busy` when full); request deadline and max file size; the worker's `parse` job
  class reads the capacity from `/v1/info`, so the sidecar is never overloaded (RAGFlow's top complaint is stuck
  parsing; design §10).
- Long documents are converted in **page ranges** (e.g. 20 pages per request) chosen by the worker, so no request runs
  for minutes, a failure retries only its range, and per-page strategies fit naturally.
- Runs in the **ingestion resource zone** with hard CPU limits (at most half the host's cores on a shared pilot host);
  it is the first container to move to its own host (platform plan §2, review R1).
- Parse output is cached by (content hash, parser settings hash, parser build), so identical content is parsed once
  however many sources hold it (simplification pass §2).
- Hardened container: non-root, read-only root file system with a tmpfs for scratch, no network egress, internal
  network only, memory and CPU limits, temp files removed after each request.

## 4. Milestones

| Phase | Deliverable                                                                                                                                                 |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P0    | Image builds with models baked in; health; contract skeleton                                                                                                |
| P1    | `/v1/convert` and `/v1/inspect` for the P1 formats (answered 4) incl. the PDF text-layer fast path; `/v1/ner` (KB BERT NER, one process); OCR auto/force/off with Tesseract `swe` and the 20-page å/ä/ö acceptance check; table modes; page ranges; quality signals; contract tests; timings on a sample of Umeå documents (design §16) |
| P2    | Legacy Office/RTF (LibreOffice) and email with child documents (Z1); picture crops with a describe-pictures node; OCR of pictures inside DOCX/PPTX; archives and macro-enabled formats (Z1); fuzzing; GPU variant if needed |

## 5. Open questions

1. ~~OCR engine choice for Swedish~~ **Closed** (simplification pass §11): Tesseract `swe`; P1 acceptance check å/ä/ö
   recall ≥ 95 % on a 20-page Umeå sample, EasyOCR reconsidered only if it fails (design §9.3).
2. Licences of the bundled layout/OCR models (Docling itself is MIT): verify before the pilot.
3. ~~Volume of scanned documents~~ **Answered**: few (under 500) in the pilot, so CPU-only OCR within the parser's
   share of the pilot host is enough; revisit sizing when archives are onboarded.

Question 2 is a P1 verification task, not a decision.

Answered (2026-10-29, step 5 questions; restated 2026-10-30, simplification pass):

4. **Pilot formats** beyond PDF, DOCX, PPTX, XLSX, HTML: images (JPG/PNG/TIFF, OCR), OpenDocument (.odt/.ods/.odp,
   Docling's native backend), and plain text/Markdown/CSV. Legacy Office (.doc/.xls/.ppt), RTF and email (.eml/.msg
   with attachments) move to P2 and are refused as `format_deferred` in P1 unless the pilot source inventory taken
   at P1 start shows them common.
5. **Pictures inside documents**: P1 keeps captions and OCR'd text only; picture crops are dropped until a
   describe-pictures node exists (the contract can add them as an optional field then).

## 6. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md) §1, §9: LibreOffice (legacy Office, RTF) and email with
attachments → P2 (Z1); picture crops dropped; EasyOCR and the OCR bake-off dropped; the worker's PdfPig fast path and
worker/parser page stitching removed (fast path in the parser); separate NER lanes and the `ner` job class removed;
parser fuzzing → P2. Details in design §20.
