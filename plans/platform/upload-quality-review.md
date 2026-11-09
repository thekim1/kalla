# Upload quality review (2026-11-09)

The question (user, 2026-11-09): **is the RAG pipeline good enough for any file a user uploads, and does it follow
best practice?** This is the main session's answer, checked against the designs (parser §3, processing §5–6 and §11,
knowledge §1), not against every line of code. It is an assessment, not a binding design: what it proposes becomes
binding only through a Decisions row and the design for its area.

## Verdict

Uploads that are mostly text are handled well. That covers most of them: PDF, Word, PowerPoint, Excel, OpenDocument,
Markdown, text, HTML, and since #526 the older `.doc`, `.xls`, `.ppt` and RTF. Content that lives in **pictures**,
**large spreadsheets** or **e-mail** is not handled well yet.

**Answer quality on uploads has not been measured.** The only retrieval numbers so far come from the crawled `turkos`
collection. The pilot gold set and the support-assistant proof of concept
([`../cases/support-assistant/07-rollout.md`](../cases/support-assistant/07-rollout.md) §0b) are what will tell. Both
wait on DoIT's documents and tickets.

## What already follows best practice

| Area | What we do | Where |
|---|---|---|
| Parsing | The structure is kept. Office files are read through Docling's backends from their styles, so headings and tables survive. PDFs are handled per page: the text layer when it is clean, Docling layout and Tesseract `swe` OCR when it isn't. Formats are sniffed, never trusted from the extension. | parser §3, §9; processing §3 |
| Sections | Splitting follows the headings. Unreliable PDF heading levels are recomputed from numbering (`1.2`, `Kapitel`, `§`, `Bilaga`), with plausibility checks. | processing §5 |
| Chunks | 400 tokens with a 60-token overlap of whole sentences, never cut mid-sentence, and balanced at the tail. Short sibling sections ("Kontakt", "Öppettider") are merged. Every chunk starts with its breadcrumb and heading path, which is embedded and searched with it. | processing §6.2 |
| Tables | Row groups with the header repeated in each row (`Kolumn: värde; …`). Wide rows are split into column groups, and spreadsheet sheets become tables. | processing §6.3 |
| Retrieval | Hybrid: keyword search with a Swedish decompounder plus dense vectors, merged with RRF. Dedup and at most 3 chunks per document. A hit is expanded with its neighbouring chunks. Citations carry the page range and the heading path. | knowledge §1, §6, §8, §9 |
| Evaluation | Evaluation sets are generated per collection, the evaluator runs the same retrieval as the API, a report comes with every publish, and a nightly run follows. Most platforms (Eneo, Open WebUI) have none of this. | processing §13–14 |
| Safety | A malware scan, personal-data flags (with a hold for `public`), and upload limits (50 MB per file, 2,000 pages). | connectors §9, §11; processing §9 |

## Gaps, by how much they hurt uploads

| # | Gap | Effect | Direction | Today |
|---|---|---|---|---|
| U1 | **Pictures are dropped**; only their caption stays (processing §6.4) | Screenshots in IT instructions, flowcharts, slides that are mostly pictures, and scanned diagrams lose their content. This matters directly for the DoIT support case | A stage that describes pictures: crops from Docling, a vision model through the gateway at the collection's residency ceiling, and the description as text in the flow | P2 (parser §16) |
| U2 | **E-mail (`.eml`, `.msg`) and archives (`.zip`) are refused** | Support teams often keep knowledge in saved e-mails | The e-mail backend with attachments as child documents (parser §5, knowledge §10.3). Archives are still to be decided | P2 (Z1) |
| U3 | **Spreadsheets give rows, not calculations** | "What does X cost?" works. "How many…" and "the total of…" don't, and sheets above 2,000 rows are only flagged (`quality.largeTable`) | A tool that queries the table (SQL over a sheet) instead of retrieval | not planned |
| U4 | **Rerank is off by default** (knowledge §1 row 19) | Common practice reranks the top 20–50 candidates. Ours is built and set per assistant, but off | Switch it on when the gold set shows a gain; `GatewayReranker` with the Foundry/on-prem model | built; waits on the gold set |
| U5 | **Documents without headings get thin context** | A chunk from a flat PDF or a text file carries only the title as its prefix | Trial *contextual retrieval*: a small model writes one sentence of context per chunk at ingestion. It costs one model call per chunk, so measure it on the evaluation set before adopting it | not planned |
| U6 | **Versions and near-duplicates** | "Policy 2024.pdf" and "Policy 2025.pdf" uploaded side by side are both used in answers. Dedup only catches identical text (`DocumentId`, `TextHash`), and age decay is off for uploads | A near-duplicate warning when a file is uploaded (similar name or text to an existing one, with "Replace instead?"); optional age decay for upload sources | not planned |
| U7 | **OCR is Tesseract `swe` only** | Printed Swedish scans are fine. English or mixed scans are weaker, and handwriting is not read | Add `eng` as a second language when the OCR check shows a need; handwriting is out of scope | parser §9 |

## Proposed order

1. **Measure first.** Run the support-assistant proof of concept on real DoIT documents and tickets as soon as they
   arrive. Without that, "good enough" is a guess.
2. **U1, picture descriptions.** This is the biggest content loss for IT documentation. It would bring a P2 item
   forward, which needs a Decisions row.
3. **U4, rerank on** for assistants where the evaluation shows a gain. This is a settings change.
4. **U2, `.msg` and `.eml`.**
5. **U5, a contextual-header trial** on the evaluation set, adopted only if the gain pays for the model calls.
6. **U6, a near-duplicate warning** at upload (admin UI §5.5.7, Files tab).

U3 and U7 wait until real use shows a need.

## Related

- Upload knowledge bases in the admin UI: admin design §5.5.7 (#532). The worker records an upload's outcome: #533.
- Legacy Office and RTF through LibreOffice: Decisions "Legacy Office and RTF uploads", parser §4 (#526).
