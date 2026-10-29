# Parser sidecar: plan

A small, stateless Python service that converts hard formats (PDF, Office, scanned documents) into structured
Markdown. It exists because Python's document tools (Docling) are far ahead of .NET's for this job; everything else
stays in .NET.

## 1. Scope

**Does:** convert one document per request; OCR (Swedish); table structure; page numbers and heading paths.

**Does not:** fetch anything, store anything, chunk, embed, or decide strategy (the worker's auto-parse decides and
passes options).

## 2. API

| Method | Path                            | Purpose                                                            |
| ------ | ------------------------------- | ------------------------------------------------------------------ |
| `POST` | `/v1/convert`                   | Multipart file + options → parsed document                         |
| `GET`  | `/health/live`, `/health/ready` | Probes                                                             |
| `GET`  | `/v1/info`                      | Versions of Docling, models and OCR engine (recorded in run items) |

Options: `ocr` (`auto` / `force` / `off`), `ocr_lang` (`swe`, `eng`), `tables` (`fast` / `accurate`), `pages`
(range), `image_scale` (for re-render).

Response: Markdown with page markers, a structure list (headings with levels, page per block, tables), quality
signals (characters per page, OCR confidence, pages with no text), timings and versions. Versioned contract
(`/v1`), tested by contract tests from the .NET side.

## 3. Design

- FastAPI + Docling; OCR engine with Swedish models (Tesseract `swe` or Docling's default engine, choose by test).
- All models baked into the image at build time: no downloads at runtime (on-prem hosts without internet).
- CPU-only by default; a GPU image variant later if volumes need it.
- Concurrency limited per container (one conversion per worker process, configurable), request timeout and max file
  size; the worker's `parse` job class matches this limit so the sidecar is never overloaded (RAGFlow's top
  complaint is stuck parsing).
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
| P1    | `/v1/convert` for PDF, DOCX, PPTX, XLSX, HTML fallback; OCR auto/force with Swedish; quality signals; contract tests; timings on a sample of Umeå documents |
| P2    | Table modes, re-render scale, page ranges for per-page strategies; GPU variant if needed                                                                    |

## 5. Open questions

1. OCR engine choice for Swedish (test Tesseract `swe` vs Docling's default on real scanned municipal documents).
2. Licences of the bundled layout/OCR models (Docling itself is MIT): verify before the pilot.
3. ~~Volume of scanned documents~~ **Answered**: few (under 500) in the pilot, so CPU-only OCR within the parser's
   share of the pilot host is enough; revisit sizing when archives are onboarded.

Questions 1 and 2 are P1 test and verification tasks, not decisions.
