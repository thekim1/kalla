# Parser sidecar: plan

**Detailed design: [`design.md`](design.md)** (step 5); where they differ, the design wins.

A small, stateless Python service that converts hard formats (PDF, Office, scanned documents) into structured
Markdown. It exists because Python's document tools (Docling) are far ahead of .NET's for this job; everything else
stays in .NET.

## 1. Scope

**Does:** convert one document per request; OCR (Swedish); table structure; page numbers and heading levels (paths
are stitched by the worker); cheap per-page signals (`/v1/inspect`); names for personal-data detection (`/v1/ner`,
added in step 6c).

**Does not:** fetch anything, store anything, chunk, embed, or decide strategy (the worker's auto-parse decides and
passes options).

## 2. API

| Method | Path                            | Purpose                                                            |
| ------ | ------------------------------- | ------------------------------------------------------------------ |
| `POST` | `/v1/convert`                   | Multipart file + options → `multipart/mixed` (result JSON, DoclingDocument, attachments, crops) |
| `POST` | `/v1/inspect`                   | Cheap page count and per-page text-layer signals (no models)       |
| `POST` | `/v1/ner`                       | Person names in text units (JSON in/out, own lane; design §7.8)    |
| `GET`  | `/health/live`, `/health/ready` | Probes                                                             |
| `GET`  | `/v1/info`                      | Versions, build hash (recorded in run items), capacity (`convert_slots`, `ner_slots`), limits |

Options (design §7.2): `request_id`, `file_ext`, `media_type`, `pages` (`{from, to}`), `deadline_ms`, `ocr` (`mode`
`auto` / `force` / `off`, `languages`, `engine`), `tables` (`fast` / `accurate` / `off`), `image_scale` (for re-render),
`pictures` (crops on request), `outputs` (`docling_json`, `markdown`).

Response: `multipart/mixed` with `result.json` (blocks with their own Markdown, heading levels, pages, tables with
cells, per-page quality signals, timings, build), `docling.json.gz` (lossless `DoclingDocument`), binary email
attachments and picture crops. Versioned contract (`/v1`), tested by contract tests from the .NET side.

## 3. Design

- FastAPI + Docling; OCR engine with Swedish models (Tesseract `swe` CLI or EasyOCR, both baked, always set
  explicitly, never Docling's auto choice; the P1 bake-off decides, design §9).
- All models baked into the image at build time: no downloads at runtime (on-prem hosts without internet).
- CPU-only by default; a GPU image variant later if volumes need it.
- Concurrency: an own supervisor with pre-warmed conversion slots, one inspect process and one NER process (not
  uvicorn workers), no queue (`503 busy` when full); request deadline and max file size; the worker's `parse` and
  `ner` job classes read the capacity from `/v1/info`, so the sidecar is never overloaded (RAGFlow's top complaint is
  stuck parsing; design §10).
- Long documents are converted in **page ranges** (e.g. 20 pages per request) chosen by the worker, so no request runs
  for minutes, a failure retries only its range, and per-page strategies fit naturally.
- Runs in the **ingestion resource zone** with hard CPU limits (at most half the host's cores on a shared pilot host);
  it is the first container to move to its own host (platform plan §2, review R1).
- Parses each canonical document once for the whole platform (document store, review R2).
- Hardened container: non-root, read-only root file system with a tmpfs for scratch, no network egress, internal
  network only, memory and CPU limits, temp files removed after each request.

## 4. Milestones

| Phase | Deliverable                                                                                                                                                 |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P0    | Image builds with models baked in; health; contract skeleton                                                                                                |
| P1    | `/v1/convert` and `/v1/inspect` for all pilot formats (answered 4) incl. legacy/ODF and email attachments; `/v1/ner` (KB BERT NER, own lane); OCR auto/force/off with Swedish; table modes; page ranges; quality signals; contract tests; OCR bake-off; timings on a sample of Umeå documents (design §16) |
| P2    | Describe-pictures node on crops; OCR of pictures inside DOCX/PPTX; archives and macro-enabled formats (Z1); GPU variant if needed |

## 5. Open questions

1. OCR engine choice for Swedish (Tesseract `swe` vs EasyOCR on real scanned municipal documents; design §9.3,
   postponed item P1).
2. Licences of the bundled layout/OCR models (Docling itself is MIT): verify before the pilot.
3. ~~Volume of scanned documents~~ **Answered**: few (under 500) in the pilot, so CPU-only OCR within the parser's
   share of the pilot host is enough; revisit sizing when archives are onboarded.

Questions 1 and 2 are P1 test and verification tasks, not decisions.

Answered (2026-10-29, step 5 questions):

4. **Pilot formats** beyond PDF, DOCX, PPTX, XLSX, HTML: images (JPG/PNG/TIFF, OCR), legacy Office (.doc/.xls/.ppt),
   OpenDocument (.odt/.ods/.odp) and RTF (via LibreOffice headless inside the parser sandbox), email (.eml/.msg: body
   plus attachments as separate documents), and plain text/Markdown/CSV.
5. **Pictures inside documents**: P1 keeps captions and OCR'd text only; the contract can return picture crops on
   request so a P2 "describe pictures" node (worker → gateway, residency rules) needs no contract change.
