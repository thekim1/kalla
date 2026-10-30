# Ingestion worker, part C: processing nodes, personal data, embedding, evaluation and the publish gate

Detail step 6c of the Ume.Rag design. Details [`plan.md`](plan.md) §4–§7 (binding, incl. answered items 1–21) and
specifies the internals of the nodes [`design-engine.md`](design-engine.md) (6a) marks "6c" in its catalogue
(`design-engine.md:249-270`). It consumes, without redesigning: the port types (6a §2.2), the node interface and
results (6a §5, §7.3), segments and job classes (6a §7.4, §9.1), cache classes (6a §8), preview (6a §11), the publish
flow with `IPublishGate` and shadow sets (6a §12.1), the index writer (6a §14) and the AIMD limiter (6a §18); variants,
`ContentHashVersion`, noise rules and attachments from [`design-connectors.md`](design-connectors.md) (6b §8.4, §10,
§12); the parser contract, signals, ranges and lanes from [`../parser/design.md`](../parser/design.md) §3, §6–§10; the
tables of [`../platform/data-model.md`](../platform/data-model.md) §2.2 and the hybrid query §3.3. Nothing is
implemented. Changes needed elsewhere: §19; questions: §20; unverified facts: §21.

Library and model facts checked for this step (downloaded into the scratchpad, read only): `Microsoft.ML.Tokenizers`
2.0.1 (NuGet, MIT; contains `SentencePieceTokenizer` with `SentencePieceUnigramModel` and precompiled-charsmap
normalisation), BGE-M3's `sentencepiece.bpe.model` (Hugging Face `BAAI/bge-m3` revision `5617a9f`, MIT: despite the
file name its `TrainerSpec.model_type` is **1 = UNIGRAM**, 250,000 pieces, normaliser `nmt_nfkc`), and
`KBLab/bert-base-swedish-cased-ner` (revision `f3c83af`, 2026-10-15, MIT, `BertForTokenClassification`, 512
positions, `do_lower_case: false`, `strip_accents: false`; `config.json` labels are **`PER`** plus mixed labels
`ORG/PRS`, `PRS/WRK`, `LOC/PRS`, while the model card's example prints `PRS`; IO tags without `B-`/`I-`; reported
F1 0.922 on SUC 3.0). `transformers` (Apache-2.0) is already in the parser image through `docling-ibm-models` 4.0.4
(`transformers>=4.42,<6`).

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Per-page strategy | Six ordered rules on the `/v1/inspect` signals with parser §6.1's starting thresholds; post-conversion checks re-run garbled pages with forced OCR and empty scanned pages once at scale 3.0 (§3.1) |
| 2 | Profiles | P1: **built-in** profiles only (`default`, `regulations`, `protokoll`), versioned with the node; `ParseObservations` recorded from day one so P2 learning starts with data (§3.5) |
| 3 | Single-page failures | A page that fails alone after halving is a **missing page** (`degraded`) while ≤ 10 % of pages; above that the document fails (refines 6a §7.3) (§3.3) |
| 4 | Headings | Layout headings re-levelled by Swedish numbering patterns (`1.2`, `Kapitel 3`, `3 kap.`, `§ 4`, `Bilaga 2`); deeper than `maxLevel` becomes bold body text (§5) |
| 5 | Tokenizer | `Microsoft.ML.Tokenizers` 2.0.1 `SentencePieceTokenizer` over BGE-M3's own SentencePiece model, embedded in the worker; counts must equal the Hugging Face tokenizer on a fixture corpus (§6.1) |
| 6 | Chunk budget | 400 tokens **including** the heading-path prefix; overlap 60 tokens (15 %) of whole trailing sentences, within a section only; tables as `Kolumn: värde; …` row groups without overlap (§6) |
| 7 | `TextHash` | SHA-256 of the exact embedded text (prefix + body, NFC); the `EmbeddingCache` key and the cross-version chunk identity `(DocumentId, TextHash)` (§6.5) |
| 8 | Personal data | Patterns in .NET (personnummer and samordningsnummer with date and Luhn checks, phone, email); names via a new parser endpoint **`POST /v1/ner`** on its own lane; counts per type only; flag rule per type in `Settings` (§9, §10) |
| 9 | NER job class | *Detect personal data* runs in a new `ner` job class whose concurrency is the NER lane's capacity, like `parse` for conversions (§9.6) |
| 10 | NER down | Fail closed for `public` only: patterns still run, names are `pending`, the document counts as flagged for public collections until NER has run (§9.6) |
| 11 | Re-flag | A cleared document is re-flagged when a flagging type's count rises above its count at clearance; values are never stored (§9.5) |
| 12 | Embed | Platform profile only; ≤ 64 inputs and ≤ 16k tokens per request; cache by (level, profile, `TextHash`); a canary embedding detects a silently changed gateway model (§11) |
| 13 | Noise-only changes | **No new chunk set**: when the boundary output hash equals the current set's, the set is re-keyed to the new `ContentHash` in one row update (§12) |
| 14 | Evaluation target | Questions point at **documents**, not chunks; metrics recall@10 and MRR@10 with Wilson and bootstrap intervals (§13) |
| 15 | Retrieval in evaluation | A shared `Ume.Rag.Retrieval` library runs the knowledge design §6 statements and the .NET fusion and diversity for both the Knowledge API and the evaluator; scope is collections or an explicit chunk-set list (§13.5) |
| 16 | Shadow sets | **Agree with 6a**, refined: the gate builds a **sub-corpus** (sampled questions' documents + distractors) under the candidate as shadow sets and compares it with the same documents' current sets (§14.1) |
| 17 | Gate rule | Fail when recall@10 drops > 3 points **and** ≥ 3 questions net are lost; a drop on the 50-sample escalates to the full set before failing (§14.2) |
| 18 | First publish | A new collection is `building` until its full generated set reaches recall@10 ≥ 0.70 on the real index, or a platform admin overrides (§14.3) |

## 2. *Detect*

`detect` (inline, intermediate cache) adds facts to `RawContent`; it never changes the bytes.

| Fact | How | Used by |
|---|---|---|
| `mediaType`, `mediaTypeDeclared`, `mediaTypeMismatch` | magic bytes (PDF, ZIP+`[Content_Types].xml`, OLE, PNG/JPEG/TIFF, RFC 822 headers), then declared type; parser re-sniffs anyway (parser §3) | *Switch* `mediaType` |
| `language` | HTML `lang` attribute; text/Markdown/HTML body: own stop-word detector (200 Swedish and 200 English function words, ratio on the first 5,000 characters → `sv`/`en`/`und`); parser-bound types get it from *Clean* after parsing | *Switch* `language`, `Documents.Language` |
| `inspect` (PDF, TIFF, Office, email; `inspect: true`) | parser `/v1/inspect` (parser §6.2, own lane, 10 s): `page_count`, `rangeable`, `encrypted`, `pages[]` signals, `refusal` | *Auto-parse* plan (§3) |
| `scanned` | share of inspected pages that are not "clean" (§3.1 rule 5) ≥ 0.5 | *Switch* `scanned` |
| `pageCount`, `bytes` | inspect / blob | cost estimates (6a §5.1), limits |

`refusal` from inspect (`encrypted` with `user_password`, `too_many_pages` > 2,000, `unsupported_format`) ends the
document at once with the matching outcome (6a §10.2), without spending a parse slot. Inspect errors are mapped like
convert errors (parser §7.5); inspect `busy` requeues without an attempt.

## 3. *Auto-parse*

### 3.1 Per-page strategy

Evaluated per PDF page (and TIFF frame) on the inspect signals (parser §6.2), first matching rule wins:

| # | Condition (inspect page) | Strategy | Why |
|---|---|---|---|
| 1 | `text_chars < 50`, `image_coverage < 0.1`, `path_objects < 200` | `blank` (no request; marker kept) | empty page |
| 2 | `text_chars < 50`, `path_objects ≥ 200` | `docling-ocr-force` | text drawn as outlines or vector art |
| 3 | `garbled_score ≥ 0.4` or `bad_chars / text_chars ≥ 0.1` | `docling-ocr-force` | garbled layer (parser §6.1) |
| 4 | clean: (`text_chars ≥ 200` or (`≥ 50` and `image_coverage < 0.3`)) and `garbled_score < 0.2`, and `path_objects ≥ 40` | `docling-tables` (OCR off, tables `accurate`) | table-dense |
| 5 | clean as in 4, `path_objects < 40` | `pdfpig` (worker fast path) | clean text layer |
| 6 | otherwise (few characters with images, or garble 0.2–0.4) | `docling-ocr` (OCR `auto`: bitmaps only) | scanned or mixed |

After conversion, per page (parser §6.4 signals):

- **Re-render** once: `chars < 20` and `image_coverage ≥ 0.5` → `docling-ocr-force@3` (`image_scale` 3.0), the
  RAGFlow zoom retry (`../../platforms/ragflow.md:84-87`) bounded to one step.
- **Garble after conversion**: a `docling-ocr` page with `garbled_score ≥ 0.4` → `docling-ocr-force` once.
- **PdfPig fallback**: an exception or the 30 s per-document budget (parser §3) moves the remaining `pdfpig` pages to
  `docling-text` (OCR off, tables per node param).

Node params override the rules: `ocr: force` maps every parser-bound page to `docling-ocr-force` and disables
`pdfpig`; `ocr: off` turns rules 2, 3 and 6 into `docling-text` and blank results into `degraded`. `tables` sets the
table mode for every Docling page except rule 4 (always `accurate`). Thresholds live in the node's code with its
version (changing them is a `typeVersion` bump, 6a §5.1); P1 tunes them once on the fixture corpus (§17).

### 3.2 Other inputs

| Input | Request | Notes |
|---|---|---|
| DOCX, PPTX, XLSX, legacy, ODF, RTF | one `/v1/convert`, no ranges (parser §8.1) | `timeout` → `failed` (cannot be split) |
| JPG, PNG | one request, OCR `auto` | – |
| Multi-frame TIFF | frames as pages, rules 1–6 with `docling-ocr` as the floor | – |
| `.eml`, `.msg` | one request; attachments returned become child documents (6b §10) | – |
| HTML that failed *HTML main content* (template fallback) | parser HTML backend | `fallback` port otherwise |
| anything the parser cannot take (inspect `unsupported_format`) | – | leaves through `fallback` (6a §7.2) or ends `unsupported` |

### 3.3 Range planning, sizing, failures

1. Consecutive pages with the same Docling strategy form a run; each run is cut into ranges (parser §8.1). `pdfpig`
   and `blank` pages never go to the parser.
2. First range size: 20 (`docling-text`, `docling-tables`), 8 (`docling-ocr`, `-force`), 4 (`@3`). After each range:
   `msPerPage = EWMA(α 0.5)` of `timings.docling_ms / pages` per strategy, and
   `next = clamp(⌊0.5 × 110,000 / msPerPage⌋, 1, 20)` (half the 110 s request deadline).
3. Ranges run sequentially in the document's `parse` job; completed ranges are written as `ranges/{from}-{to}`
   (6a §8) so a crash, slice yield (6a §9.5) or retry resumes at the next range.
4. `status: partial` keeps `pages_completed` and requests the rest. `timeout`, `worker_crashed`, `resource_limit`
   halve the range; a **single page** that still fails is recorded as a missing page. Missing pages ≤ 10 % of the
   document (and ≤ 20 pages) → outcome `degraded` with the page list; more → `failed`, reason `parser.<code>`.
   This refines 6a §7.3 ("single page → fatal"), §19.
5. `ocr_failed` / `conversion_failed`: transient once for the range, then as 4.

### 3.4 `Strategy` string (`RunItems.Strategy`, ≤ 500 characters)

```
route=document;parse=auto(profile=regulations@1)[pdfpig:1-38,docling-tables:39-40,docling-ocr:41-44,blank:45;rerender:43;missing:44]
```

Grammar: `parse=<node>(profile=<name>@<v>)[<strategy>:<page ranges>,…;rerender:<pages>;missing:<pages>]`, pages
compressed to ranges, `…` when truncated. Non-paged inputs: `parse=auto[docling-docx]`, `[docling-email]`,
`[docling-image]`. The same string is the fact `parse.strategy` and `Documents.ParseStrategy`.

### 3.5 Profiles (P1 built-in) and observations

A profile is data: `Match` → strategy overrides (`ocr`, `tables`, `image_scale`) and structure hints (heading
patterns, furniture patterns) consumed by *Clean* and *Split by headings* through the fact `parse.profile`.

| Profile | Match | Effect |
|---|---|---|
| `default` | always (last) | none |
| `regulations` | title or first 2,000 characters match `(?i)\b(föreskrift(er)?\|reglemente\|taxa\|ordningsföreskrifter\|författningssamling)\b` | heading patterns kap./§/Bilaga (§5.2), `maxLevel` 4 |
| `protokoll` | title or first page matches `(?i)\b(protokoll\|sammanträdesprotokoll\|kallelse)\b` | each `§ N` item is a level-1 section; signature blocks (`Justerare`, `Justerandes signatur`, `Utdragsbestyrkande`) are furniture |

- P1 profiles are seeded rows (`ParseProfiles.Origin = builtin`, §19), read-only, versioned with the *Auto-parse*
  node: changing one is a node version bump, so caches and chunk-set keys stay correct. Strategy-level matches run
  before the plan (domain, path glob, media type); text-level matches run on the stitched output.
- **Observations are recorded in P1**: one `ParseObservations` row per non-preview document-stage commit:
  `Domain`, `PathPattern` (path with digits and the last segment replaced by `*`), `DocumentType`, `Strategy`
  (summary: share of pages per strategy), `Score` (§8), `Metrics` (quality facts). Retention 180 days (platform
  plan §8). P2 learning (plan §6.4) reads them; P1 only shows them on the document page.

### 3.6 Try-and-score hook (P2)

P1 ships the two seams so P2 adds a fan-out without changing the node contract:

```csharp
public interface IParsePlanner   // P1: rules of §3.1; P2: + learned profiles
{ PagePlan Plan(DocumentFacts facts, InspectResult inspect, ParseProfile profile, AutoParseParams p); }

public interface IParseScorer    // P1: used for observations and Quality check; P2: picks the winner
{ double Score(ParsedDocument parsed, QualityFacts facts); }
```

P2: for uncertain documents (rule-6 pages with garble 0.2–0.4, or `Score < 0.6`), *Auto-parse* runs up to three
plans on ≤ 5 sample pages each, keeps the best by `IParseScorer` (optionally a small on-prem judge, plan §6.3) and
records all candidates as observations.

### 3.7 Worker-side parse nodes

| Node | Internals |
|---|---|
| `html-main-content` | AngleSharp DOM (already built by the root for links, 6b §12.2) → SmartReader article → blocks: `h1`–`h6` (`level_source: html`), `p`, `li` (with nesting depth), `table` (cells, `th`/`thead` as header rows, spans expanded), `pre`/`code`, `figcaption`. Title: `og:title`, else `<title>` with a site suffix after ` - ` or a vertical bar removed when it repeats across the host, else first `h1`. `cleanupRules: learned` applies the host's learned CSS selectors before SmartReader (none exist in P1: no-op) |
| `pdf-fast-path` | PdfPig on the `pdfpig` pages only: `NearestNeighbourWordExtractor` → `DocstrumBoundingBoxes` → `UnsupervisedReadingOrderDetector` (*to verify*); a line with font size ≥ 1.25 × the page's median body size, ≤ 2 lines and ≤ 150 characters is a `heading` (`level_source: layout`, level by size rank); output in the `result.json` block shape with page and bbox, so stitching treats both paths alike |
| `plain-text` | charset (BOM, strict UTF-8, Windows-1252 fallback, parser §3); Markdown via Markdig (`level_source: markdown`), plain text as paragraphs split on blank lines |
| `csv-table` | delimiter sniffed among `;`, `,`, tab, `\|` on the first 50 lines; header row = first row all non-numeric and distinct; one table block; 200,000-cell cap as parser §7.4 |

## 4. Stitching and *Clean*

### 4.1 Stitching (parser §8.2, in *Auto-parse* before its output is stored)

Implemented exactly as parser §8.2 (order, heading paths after stitching, cross-page table merge with the 15 %
top/bottom bands and equal column counts, page-end hyphenation, partial ranges). Additions:

- PdfPig and Docling pages are merged in page order; a table that ends a `pdfpig` page and continues on a
  `docling-tables` page is merged only when both sides have cell structure (PdfPig pages have none, so never).
- The stitched `result.json` (parser §7.6) is the *Auto-parse* output; *Clean* never sees ranges.

### 4.2 *Clean* (`clean`, inline, intermediate)

Applied in this order to body blocks; every rule counts its effect into facts (`clean.furnitureDropped`, …).

| Rule | Detail |
|---|---|
| Unicode | NFC; NFKC only for the ligatures U+FB00–FB06; soft hyphen U+00AD removed; NBSP, thin and narrow NBSP → space; zero-width characters removed; quotes kept |
| Furniture (`dropFurniture`) | drop `layer: furniture`, `page_header`, `page_footer`; for `pdfpig` pages (no layer): a line in the top or bottom 8 % of the page whose digit-normalised text recurs on ≥ 50 % of pages (min 3) is furniture; blocks matching `^(sida\s*)?\d+(\s*(av\|/)\s*\d+)?$` are page numbers; profile furniture patterns (§3.5) |
| Table of contents | ≥ 4 consecutive lines matching `^.{3,120}?[ .·…]{3,}\s*\d{1,4}$` → dropped (they duplicate headings and pollute retrieval) |
| Dehyphenation (`dehyphenate`) | inside a block: `\p{L}{2,}-\n\p{Ll}` → join without hyphen, **except** when the next word is `och`, `eller`, `samt`, `till`, `respektive` (suspended compound: "kommun- och regionförbund"), or the part before the hyphen is all capitals or digits ("EU-\nmedel" → "EU-medel"); line breaks inside paragraphs → spaces |
| Whitespace | runs of spaces collapsed; more than two newlines → two |
| Minimum block | drop non-heading, non-list blocks with `< minBlockChars` (default 3) letters or digits after the rules above |
| Swedish specifics | `§4` → `§ 4`; `kap.` and numbering untouched (§5 needs them); no stemming or case change |
| Language | `language` fact from the cleaned text (§2) if not set |

Learned per-site cleanup rules (plan §6.4) are P2 and apply in `html-main-content`, not here.

## 5. *Split by headings* (`split-headings`, inline, intermediate)

### 5.1 Section model

```csharp
public sealed record Section(int Ordinal, int Level, IReadOnlyList<string> HeadingPath, string? Heading,
    IReadOnlyList<BlockRef> Blocks, int? PageStart, int? PageEnd);
```

The document title (`title` block or `Documents.Title`) is level 0 and the first element of every heading path unless
the first heading repeats it. A heading opens a section at its level and closes every open section at that level or
deeper. Blocks before the first heading belong to the level-0 section. A heading immediately followed by a deeper
heading opens a section with no blocks; it only contributes to paths.

### 5.2 Re-levelling (`relevelLayoutHeadings`)

`style`, `html` and `markdown` levels are trusted. `layout` levels (PDF; parser §7.3 calls them unreliable) are
recomputed from the heading text, first match wins:

| Pattern (after trimming) | Level |
|---|---|
| `^(Kapitel\|KAPITEL)\s+\d+` or `^\d+\s+kap\.` | 1 |
| `^Bilaga(\s+\d+)?\b` | 1 (resets the path below the title) |
| `^(?<n>\d{1,2}(\.\d{1,2}){0,4})\.?\s+\p{Lu}` | number of components of `n` (`1` → 1, `1.2` → 2, `1.2.3` → 3) |
| `^§\s*\d+\s*[a-z]?\b` | current chapter level + 1 (`protokoll`: 1) |
| `^[IVX]{1,5}\.\s` or `^[A-ZÅÄÖ]\.\s` | 1 |
| unnumbered | previous numbered heading's level + 1; at document start 1 |

Plausibility: a "heading" longer than 200 characters, or ending with `.` and longer than 12 words, is demoted to a
paragraph; a numbered heading whose number goes backwards within the same parent (`3.1` after `3.4`) keeps the layout
level (a list item misread as heading). Levels deeper than `maxLevel` (default 4) are not sections: the heading becomes
a bold paragraph (`**…**`) in its parent. The RAGFlow title chunker with regex level sets is the model
(`../../platforms/ragflow.md:183-185`), with Swedish patterns as its report proposes (`:249-253`).

### 5.3 Long sections and documents without headings

Section size is unbounded here; the chunker (§6) packs long sections into several chunks with the same path, so
*Split* never cuts mid-paragraph. A document without headings is one level-0 section (path = title). Parent-child
(P2, only if evaluation shows that query-time section expansion from neighbouring chunks, knowledge design §9, is
insufficient) cuts sections above 1,500 tokens into parent windows at block boundaries.

## 6. *Chunk* (`chunk`, inline, intermediate; answered 8)

### 6.1 Tokenizer

- `Microsoft.ML.Tokenizers` 2.0.1 (MIT, stable; the KM report already chose it for counting,
  `../../platforms/kernel-memory-and-dotnet.md:183`), `SentencePieceTokenizer.Create(stream, addBeginOfSentence:
  false, addEndOfSentence: false)` over BGE-M3's `sentencepiece.bpe.model` (5.07 MB, MIT), embedded as a resource in
  `Ume.Rag.Worker` with its SHA-256 checked at startup. The model is Unigram with `nmt_nfkc` (checked, see
  preamble); 2.0.1 contains the Unigram model and precompiled-charsmap normaliser. Counting needs no ids, so the
  fairseq id offset of XLM-R does not matter; the gateway adds `<s>`/`</s>` (2 tokens).
- Parity test: 1,000 Swedish and English passages from the fixture corpus, counts equal to Hugging Face
  `XLMRobertaTokenizerFast` for `BAAI/bge-m3` (golden file generated once in Python). A mismatch fails the build;
  if parity cannot be reached, the fallback is a 5 % safety margin on the budget (P1 decision at implementation).
- One shared instance (thread-safe for encoding, *to verify*); `CountTokens` and `GetIndexByTokenCount` on spans.

### 6.2 Packing

```
budget      = maxTokens (400)                         counted on  prefix + "\n\n" + body
prefix      = HeadingPath joined with " › "           ≤ 60 tokens: keep root and the last two levels, "…" between
overlap     = ⌊overlapPercent × maxTokens⌋ = 60       whole trailing sentences of the previous chunk, same section only
units       = sentences (paragraphs), items (lists), rows (tables), blocks (code)
```

1. Per section, split paragraphs into sentences with a Swedish splitter: boundary after `.`, `!`, `?`, `:` + space +
   upper-case letter or digit, except after the abbreviation list (`t.ex.`, `bl.a.`, `m.m.`, `s.k.`, `kap.`, `nr.`,
   `ca.`, `dvs.`, `o.s.v.`, `fr.o.m.`, `t.o.m.`, `jfr.`, `resp.`, `enl.`, single capitals as initials, `§`).
2. Greedily add units while `tokens(prefix + body) ≤ budget`. A unit larger than the remaining budget starts a new
   chunk; a unit larger than a whole budget is cut at the last whitespace before the limit
   (`GetIndexByTokenCount`).
3. A new chunk of the same section starts with the overlap: trailing sentences of the previous chunk up to 60
   tokens (one sentence over 60 tokens: its last 60 tokens at a word boundary). No overlap across sections, tables
   or code.
4. **Tail balance**: a section's last chunk below 25 % of the budget is merged into the previous one if the result
   is ≤ 115 % of the budget, else the last two chunks are re-split evenly at a sentence boundary.
5. **Short sections**: consecutive sibling sections each below 50 body tokens are packed together under their common
   parent path (their own headings become bold lines in the body), so "Kontakt" or "Öppettider" stubs do not become
   one-line chunks; never across a level-1 boundary.

Onyx embeds a title prefix inside the budget with no overlap (`../../platforms/onyx.md:173-176`); Eneo uses 200
tokens with no structure (`../../platforms/eneo.md:105`); Open WebUI loses heading metadata
(`../../platforms/open-webui.md:176-177`). Here the path is both in the embedded text and in `HeadingPath`.

### 6.3 Tables (`tableRowGroups`)

- From `tables[].cells` (source of truth, parser §7.3), spans expanded (a row-spanning value repeated in each row).
  Header = the `header_rows` rows joined per column with ` / `; no header row → a two-column table is read as
  key–value (`nyckel: värde`), wider tables get `Kolumn 1…n`.
- Each row is one line `Kolumn: värde; Kolumn: värde`, empty cells skipped. A row group packs rows up to the budget
  (cap 20 rows, parser §3) with the line `Tabell: <caption>` after the prefix when a caption exists. Header names are
  repeated in every row by construction; no overlap.
- A row longer than the budget is split into column groups, each repeating the first column (`Avgift: …; …`).
- CSV and XLSX sheets are tables (parser §3); sheet name joins the heading path. Tables above 2,000 rows are indexed
  but flagged `quality.largeTable` (data, not knowledge, parser §7.4).

### 6.4 Lists, code, other blocks

| Block | Rule |
|---|---|
| List | a list stays with its lead-in sentence when they fit; splits happen between items, nested items stay with their parent item; overlap = the lead-in sentence (≤ 60 tokens) |
| Code, formula | kept whole up to the budget, else split at blank lines; fenced in `Text`; no overlap |
| Picture | only its caption (P1), as a sentence in the flow |
| Email header | subject and date as the first line of the first chunk (parser answered Q3); From/To/Cc never in text |

### 6.5 Chunk rows written

| Column | Value |
|---|---|
| `Kind` | `child` for flat chunks (`ParentId` null), so the `SearchVector` and HNSW conditions apply (data-model `:154`, §3.2) |
| `Ordinal` | 0-based order within the chunk set |
| `HeadingPath` | full path joined with ` › ` (untruncated; the prefix may be truncated) |
| `Text` | body without the prefix (shown as context and citation snippet) |
| `OverlapChars` | number of leading characters of `Text` repeated from the previous chunk (§6.2 rule 3, list lead-in); 0 for the first chunk of a section, tables and code; section expansion strips them (knowledge design §9.3) |
| `TextHash` | `SHA-256(UTF-8(NFC(prefix + "\n\n" + Text)))` = exactly the embedded string |
| `TokenCount` | tokens of the embedded string |
| `Page`, `PageEnd` | first and last page of the chunk's blocks (`PageEnd` new, §19); null for HTML/DOCX |
| `Tags`, `DocumentType` | from *Metadata from path* (§7); `DocumentType` default from media type: `page`, `pdf`, `word`, `presentation`, `spreadsheet`, `text`, `email`, `image` |
| `SourceModifiedAt` | document's `SourceModifiedAt` (or the path date, §7) |

**Ids.** `Chunks.Id` stays a v7 id per row (the index writer's transaction makes retries safe). Anything that must
survive re-chunking (evaluation provenance, retrieval statistics, feedback) uses `(DocumentId, TextHash)`, the key
retrieval already deduplicates on (knowledge design §8).

## 7. *Metadata from path* (`metadata-from-path`, inline, no cache)

```json
{ "rules": [ { "match": "/bygga-bo-och-miljo/bygglov/**", "on": "path",
               "tags": ["bygglov"], "documentType": "page" },
             { "match": "^/kommun-och-politik/protokoll/(?<y>\\d{4})/", "on": "path-regex",
               "tags": ["protokoll"], "date": "${y}-01-01" } ],
  "segmentsAsTags": 0 }
```

- `on`: `path` (glob on the URL path, or the upload's display name and folder), `path-regex`, `host`, `title-regex`,
  `mediaType`. Every matching rule adds its tags; the first match sets `documentType` and `date`. `segmentsAsTags: n`
  adds the first n path segments as tags (default 0).
- Tags: lower case, NFC, `[\p{L}\p{N}-]{1,50}`, ≤ 20 per chunk. `date` overrides `SourceModifiedAt` (CMS
  `Last-Modified` is often the render time). Child documents inherit the parent's tags plus `bilaga`.
- Cheap, so not cached (6a §8). Invalid regex is a validation error at save (JSON Schema `format: regex`, 6a §4).

## 8. *Quality check* (`quality-check`, inline, no cache)

| Metric | On | Definition | Default fail threshold |
|---|---|---|---|
| `chars` | Parsed | body characters after *Clean* | `< minChars` (200 in the web template, 6a `:677`; node default 50) |
| `charsPerPage` | Parsed (paged) | `chars / non-blank pages` | `< 100` |
| `garble` | Parsed | page-weighted mean of `garbled_score` (parser pages; HTML/text: letter ratio < 0.5 → 1) | `> maxGarble` (0.3) |
| `emptyPageRatio` | Parsed | pages with `chars == 0` and not `blank` ÷ pages | `> 0.3` |
| `ocrConfidence` | Parsed | mean of page `ocr_confidence.p10` over OCR pages (null if unavailable, parser §6.4) | `< minOcrConfidence` (0.6), skipped when null |
| `missingPages` | Parsed | §3.3 | any → `degraded` fact, not a fail |
| `emptyChunkRatio` | Chunks | chunks with < 20 body tokens ÷ chunks | `> maxEmptyChunkRatio` (0.2) |
| `duplicateChunkRatio` | Chunks | chunks sharing a `TextHash` within the set ÷ chunks | `> 0.3` |

`Score` (observations, P2 try-and-score) =
`min(1, chars/(1500 × pages)) × (1 − garble) × coalesce(ocrConfidence, 1) × (1 − emptyPageRatio)`, in [0, 1].
Every metric is written as a fact (`quality.*`) whether it passes or not, and shown in the run item.

## 9. *Detect personal data* (`detect-personal-data`, locked, `ner` class, intermediate)

Runs on the cleaned `ParsedDocument` (blocks, table cells, email subject), outputs its input unchanged plus the
fact `pii.counts`; the engine writes the counts and flag in the document-stage commit (6a `:272`). Text is NFC with
NBSP and soft hyphens already normalised (§4.2), which matters for phone numbers and names.

### 9.1 Patterns (.NET, `Ume.Rag.Worker/Pii`)

| Type | Candidate (regex, with `\b`-style digit boundaries: no digit or `-` directly before/after) | Validation |
|---|---|---|
| `personnummer` | `(?<c>18\|19\|20)?(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?<sep>[-+]?)(?<nnn>\d{3})(?<k>\d)` | `mm` 01–12; `dd` valid for the month and year (leap years with the century: given, else the latest century with date ≤ today, minus 100 years when `sep = +`); **Luhn** over the 10 digits `yymmddnnn k`; 10-digit form **without** separator only with a keyword (`personnummer`, `pers.nr`, `pnr`, `personnr`, `födelsenummer`) within 30 characters before it |
| `samordningsnummer` | same | as above with `dd` 61–91 (day + 60) |
| `email` | `[\p{L}\p{N}._%+-]{1,64}@[\p{L}\p{N}.-]{1,253}\.\p{L}{2,24}`; obfuscated `namn[at]domän.se`, `namn (at) domän.se`, `snabel-a` | domain has a dot and a valid TLD shape |
| `phone` | `(?:\+46\|0046\|0)[\s-]?\(?0?\)?(?<area>[1-9]\d{0,3})[\s-]?\d{2,3}(?:[\s-]?\d{2,3}){1,3}` and `\+\d{1,3}[\s\d-]{6,16}` | national significant digits 7–9 (after the leading 0); mobile `7[02369]` with 9 digits; not a personnummer match; not preceded by `bankgiro`, `plusgiro`, `bg`, `pg`, `org.nr`, `OCR`, `fakturanr` within 20 characters |

### 9.2 False positives

| Look-alike | Rule |
|---|---|
| Organisationsnummer (`NNNNNN-NNNN`, Luhn-valid by design) | digits 3–4 ≥ 20 → organisation, never personnummer (a sole trader's number is a personnummer and is counted, correctly) |
| OCR payment references, invoice and case numbers (often Luhn-valid) | negative keywords within 20 characters: `OCR`, `bankgiro`, `plusgiro`, `faktura`, `fakturanr`, `ärendenr`, `dnr`, `diarienr`, `diarienummer`, `kundnr`, `org.nr`, `organisationsnummer` |
| Mobile numbers without separators (`0701234567` is a valid date + 10 % Luhn chance) | unseparated 10-digit personnummer require a keyword (§9.1) |
| Postal codes, amounts, years | phone requires ≥ 7 significant digits after a trunk prefix; `903 25 Umeå`, `1 234 567 kr` never match |
| Dates `2024-05-12` | not a personnummer shape (dashes inside the date) |
| Skatteverket test numbers | detected like real ones (fixtures rely on it) |
| Healthcare reserve numbers | not standardised; not detected (documented gap) |

### 9.3 Names (parser NER, §10)

Text units sent: sentences of body blocks, table rows serialised as in §6.3 (context for names in lists), the email
subject. **Prefilter** (cost, §16): a unit is sent only if it contains a capitalised word that is not its first word,
or a first word found in a first-name gazetteer (SCB name statistics, names with ≥ 50 bearers, *to verify* licence);
ALL-CAPS units are sent as they are (the cased model misses some; measured). A worker-side LRU (per level, 100,000
entries, memory only) caches results by `SHA-256(model build ‖ unit)`, so repeated contact blocks across a site are
analysed once. Entities with label `person` and `score ≥ Settings.NerMinScore` (0.80) count; distinct names are
counted after NFC, case folding and joining hyphenated parts.

### 9.4 Counts and the flag

`Documents.PiiCounts` (values never stored, never logged, never in traces):

```json
{ "personnummer": 2, "samordningsnummer": 0, "email": 14, "emailExternal": 1, "phone": 9, "name": 23,
  "ner": "done", "rules": 1, "model": "kb-bert-ner@f3c83af" }
```

`emailExternal` = addresses outside `Settings.PiiOwnDomains` (e.g. `umea.se`). `ner` is `done`, `pending` (§9.6) or
`off` (`ner: false`, platform admins only). Spans live only in memory during the node and in preview blobs (24 h),
where the document page can show highlights to an editor deciding on a clearance.

**Flag rule** (`Settings.PiiFlagRules`, per type `{ "flag": "always" | "threshold" | "never", "min": n }`).
Proposed default (open question §20.1): personnummer and samordningsnummer `always` (≥ 1); `emailExternal`
`threshold` 1; `name`, `phone`, `email` `threshold` together: ≥ 5 distinct names **and** ≥ 5 phone/email values
(list-like documents: member lists, attendance, case lists). A document is flagged when any rule fires;
`ner = pending` counts as flagged for public collections (§9.6).

If two pipelines with different (platform-admin-set) detection params process the same document, `PiiCounts` is the
per-type maximum over its current document-stage outputs; the locked template params make this the exception.

### 9.5 Flags, `public` collections, clearing, re-flagging

- **Blocking** is the index writer's membership rule (6a `:771-773`): `CollectionIds` never contains a `public`
  collection while `PiiFlaggedAt` is set and `PiiClearedAt` is null. Chunk sets are still built, so clearing is a
  membership change, never re-processing.
- **New flag in a document-stage commit**: `PiiFlaggedAt = now()`, `PiiClearedAt = null`; existing sets of the
  document are recomputed in the same transaction (≤ 1,000 chunks) or by a `recompute-membership` command with
  priority 10, so a public collection stops serving the old content of a newly flagged document at once. Run items
  get reason `pii.flagged` (outcome stays `added`/`updated`: the document is indexed for its other collections).
- **`clear-pii-flag` command** (6a §9.1): issued by `rag-department-admin` of a department with a live reference
  to the document, or `rag-platform-admin`; reason required (≥ 10 characters); admin audit 6001 `pii.clear`. The
  worker sets `PiiClearedAt`, `PiiClearedBy`, `PiiClearedCounts = PiiCounts` (new, §19), then
  `RecomputeMembershipAsync` and `EnsureCurrent`.
- **Re-flag** on new content: if any type that can flag has a count above its `PiiClearedCounts` value, the
  clearance ends (`PiiClearedAt = null`) and the document is flagged again; equal or lower counts keep the
  clearance. Accepted gap: one personnummer replaced by another keeps the count; keyed fingerprints are a P3 option
  (§18).
- **Event 5103 `rag.pii.flagged`** (Information; SIEM rule only for documents with a `public` reference and a
  personnummer) on every transition to flagged: `DocumentId`, `Classification`, `Types` (types that fired),
  `Counts` (numbers only), `SourceId`, `RunId`, `Reflag` (bool). Never values or text.
- Child documents are detected and flagged on their own; a flagged attachment does not flag its parent.

### 9.6 Job class and fail-closed behaviour

- The node declares job class **`ner`** (new, §19): concurrency = `/v1/info` `capacity.ner_slots` × 2 (requests are
  short; the parser answers `busy` when its small queue is full). A web page becomes `fetch` → `ner` (PII, split,
  chunk) → `embed`. With `ner: false` the node is `inline`.
- NER `busy`/`not_ready` requeue without an attempt (6a §7.3). If the NER lane is not ready for more than 5 minutes
  (`/v1/info`), the node completes with patterns only and `ner: pending`: the document is indexed for non-public
  collections and kept out of public ones; the nightly drift sweep (6a §2.3) re-enqueues `pending` documents from
  this node with `force` once the lane is ready. Internal content is not held hostage to the NER lane; public content
  never goes out unchecked.

### 9.7 Masking (P2)

`pii-mask` is a separate P2 node after detection in the document stage: replaces spans with `[personnummer]`,
`[telefon]`, `[e-post]`, `[namn]` in block text; it re-runs the patterns on its output and writes the **residual**
counts. Pattern types masked to zero no longer flag; masked names still need a clearance unless a platform setting
accepts NER masking (its recall is below 1). Masking never runs by default.

## 10. Parser change: `POST /v1/ner` (answered 6, 18)

### 10.1 Contract (`contracts/parser/v1/ner-request.schema.json`, `ner-result.schema.json`)

Request `application/json` (no file; text only):

```json
{ "request_id": "0193…-n2",
  "texts": [ { "id": "u17", "text": "Kontakta handläggare Anna-Karin Öberg på bygglovsenheten." } ],
  "labels": ["person"], "min_score": 0.5 }
```

Response:

```json
{ "contract": "parser/v1", "request_id": "0193…-n2",
  "model": { "name": "KBLab/bert-base-swedish-cased-ner", "revision": "f3c83af8196b…", "quantized": true },
  "results": [ { "id": "u17", "entities": [ { "start": 20, "end": 36, "label": "person", "score": 0.97 } ] } ],
  "timings": { "total_ms": 41, "windows": 1, "wait_ms": 0 }, "build": { "build_hash": "sha256:…" } }
```

- Offsets are **Unicode code points** of the text as sent (Python string indices); the .NET client converts with
  `Rune` enumeration (surrogate pairs!). Entity text is never echoed back, logged or traced.
- Limits: ≤ 64 texts, each ≤ 4,000 characters, ≤ 100,000 characters per request; deadline 15 s. Errors as parser §7.5:
  400 `invalid_request`, 413 `too_large`, 503 `busy`/`not_ready` with `Retry-After`, 500 `worker_crashed`.
- **Label mapping** in the parser, from the model's `config.json` (checked): `PER` → `person` (and `PRS` if a revision
  renames it); the mixed labels `ORG/PRS`, `PRS/WRK`, `LOC/PRS` → `person_ambiguous`, returned only when requested
  and not counted in P1 (measured on fixtures); everything else dropped. Configurable via `NER_PERSON_LABELS`.

### 10.2 Inference

- `BertTokenizerFast` with `do_lower_case=False`, `strip_accents=False` set **explicitly** (the repository's
  `tokenizer_config.json` has no `tokenizer_class`; transformers 5 defaults are *to verify*), `return_offsets_mapping`,
  overflowing windows of 512 tokens with stride 64; for a token seen in two windows the prediction from the window
  where it is farther from the edge wins. Short texts are packed into a batch (≤ 16 sequences, dynamic padding).
- Aggregation without `pipeline()` (its API moves between versions): word label = label of the word's first
  word-piece; consecutive words with the same label form one entity (IO tags: two adjacent names merge; acceptable
  for counts); score = mean probability of the label over those first pieces.
- `torch.ao.quantization.quantize_dynamic` on `Linear` layers at load (`NER_QUANTIZE=1`), measured against fp32 on the
  fixtures before it is the default; `torch.set_num_threads(NER_THREADS)`.

### 10.3 Swedish pitfalls (fixtures in §17)

| Pitfall | Handling |
|---|---|
| Decomposed å/ä/ö from PDFs (a + U+030A) | NFC in the worker before sending (§4.2) |
| Genitive `Annas`, hyphenated `Anna-Karin`, particles `von`, `af`, `de` | word-level aggregation keeps them in one entity; distinct counting folds the genitive `s` |
| Surnames that are nouns (`Berg`, `Ek`, `Lilja`, `Strand`, `Björn`) | context-dependent; measured, not patched |
| Names in places and streets (`Gustav Adolfs torg`, `Rådhusesplanaden`) | model gives `LOC` or `LOC/PRS`; mixed labels not counted (§10.1) |
| ALL-CAPS headings, names in tables without context | lower recall; rows carry their column names (§9.3) |
| OCR noise (`rn` → `m`) | lower recall on scanned pages; reported per strategy |

### 10.4 Lane, image, licence

- Process model (parser §10.1): a third spawned process kind, `ner-0`, model loaded and warm, self-test on a fixed
  Swedish sentence at start; recycled after 5,000 requests or RSS growth > 40 %; `RLIMIT_AS` not used (torch). The
  API admits at most `NER_QUEUE` (4) waiting requests per lane process, else `503 busy`; `/health/ready` does not
  depend on it, `/v1/info` reports `capacity.ner_slots`, `ner_ready` and the model block.
- CPU: `NER_THREADS` (default 2) on the parser's 4 CPUs. During OCR batches the convert slot and the NER lane
  oversubscribe; acceptable at night, decided by the R1 test (§16).
- Image: `config.json`, `vocab.txt`, `tokenizer_config.json`, `special_tokens_map.json`, `model.safetensors`
  (≈ 499 MB, fp32) from the pinned revision with SHA-256 in `download_models.py`; `/opt/models/kb-ner`;
  `HF_HUB_OFFLINE` already set (parser §11). Image size target rises from ≤ 3.5 GB to ≤ 4.0 GB until the OCR bake-off
  loser is removed (parser §16 P2).
- Licence inventory (parser §12): model MIT (KBLab model card and Hugging Face metadata, checked); `transformers`,
  `tokenizers`, `safetensors` Apache-2.0 (already present via `docling-ibm-models`); training data SUC 3.0 (terms for
  derived weights *to verify*). KBLab marks the model experimental (answered 18): P1 measures it (§17).

## 11. *Embed* (`embed`, locked profile, `embed` class)

| Topic | Rule |
|---|---|
| Profile | `platform-default` → the `EmbeddingProfiles` row with `IsDefault` (`bge-m3`, 1024, cosine, `MaxInputTokens` 8192; data-model `:108`); other profiles only by platform admins (R3, `../../02-architecture-review.md:45-59`) |
| Input | the embedded string of §6.5 (prefix + body); no instruction prefix (BGE-M3 dense retrieval uses none, *to verify*); queries are embedded raw by the Knowledge API with the same NFC normalisation |
| Cache | per document: one `SELECT … WHERE "Classification" = @l AND "EmbeddingProfileId" = @p AND "TextHash" = ANY(@h)`; misses deduplicated, embedded, inserted `ON CONFLICT DO NOTHING` in their own short transaction (6a §7.6); `LastUsedAt` touched at most once a day per row; unused 30 days → deleted (platform plan §8). Dify's cached embedding is the precedent (`../../platforms/dify.md:218`) |
| Batching | ≤ `batchSize` (64) inputs and ≤ 16,384 tokens per request (`TokenCount` sum; gateway limit *to verify*), all misses of one document, then the next document's in the same `embed` job only when it serves several documents |
| Request | `POST /v1/embeddings` `{model: "bge-m3", input: [...], encoding_format: "float"}`; headers by the engine (6a §7.7): `x-ume-on-behalf-of` = owner team of the triggering source's collection, `x-ume-max-residency: OnPrem` always, `x-ume-run-id`, `x-ume-step: embed` |
| Response checks | count equals inputs; each vector 1024 finite floats (else fatal `embed.bad_vector`); `x-ume-residency` ≤ `OnPrem` (else fatal, 5003); stored as returned (cosine is scale-invariant) |
| Canary | at startup and hourly the worker embeds a fixed Swedish canary text and compares it with `EmbeddingProfiles.CanaryEmbedding` (new, §19): cosine < 0.999 → the `embed` class stops on this instance, health `unhealthy`, alert: the gateway alias now serves another model, which would silently mix vector spaces |
| Concurrency | AIMD of 6a §18 (`:862-865`): baseline = lowest per-minute p95 of the last 24 h (floor 200 ms); above 2× baseline halve, else +1 per minute up to `WorkerConcurrency.embed` |
| Failures | 6a §7.3 table; a 400 for one input fails the document (`embed.input_rejected`, a bug: 400 tokens ≪ 8192); partial results of a failed batch are discarded and the batch retried whole |

**Profile migration (P3 outline).** New profile row `building`; `rag_ensure_profile_index` (data-model §3.2); a
`reprocess` per affected source builds chunk sets keyed by the new profile id from cached boundary outputs (re-chunk is
free, embedding is the cost) as shadow sets; the gate (§14.5) compares both profiles on the full index; the swap sets
`Collections.EmbeddingProfileId` and recomputes membership per collection; old sets kept 14 days; the Knowledge API
embeds queries per profile and fuses with RRF while both exist (knowledge plan §3).

## 12. Noise-only content changes (6b §12.2 open item)

**Decision: a noise-only change creates no new chunk set.**

A new `ContentHash` whose parsed output is unchanged (6b counts it as `rag.connector.change.noise`) would otherwise
create a new chunk set: identical chunks rewritten with binary COPY, as many HNSW and GIN inserts, the old set
soft-deleted, for nothing; a noisy site would churn its whole index weekly.

1. `ChunkSets.InputHash` (new, §19) = `OutputHash` of the boundary output the set was built from (6a §8).
2. In the fenced document-stage commit, for every live reference whose current set has the same `PipelineHash`,
   profile and an `InputHash` equal to the new boundary `OutputHash`, the set is **re-keyed**:
   `UPDATE content."ChunkSets" SET "ContentHash" = @new WHERE "Id" = @set` (one row; `Chunks` carry no content
   hash). No collection-stage job is enqueued; the run item is `unchanged`, reason `change.noise`.
3. This only works if `OutputHash` covers content, not fetch circumstances: the hash of a `ParsedDocument` covers
   blocks, tables, pages with signals, title and language, and **excludes** volatile facts (`ETag`, fetch time,
   timings, parser build). Rule added to 6a §8 (§19).
4. `SourceModifiedAt` of the chunks is not touched: noise is not a modification. The PII node's cache key is the same
   input hash, so detection is a cache hit and cannot re-flag.
5. The same rule serves non-HTML files whose bytes change without content change (re-exported PDF): they are re-parsed
   (raw-byte hash) but not re-chunked or re-embedded.

## 13. Evaluation sets

### 13.1 Generation (`eval` job `generate-set`, `top-up-set`)

| Step | Rule |
|---|---|
| When | after a collection's first sync runs finish; weekly top-up check (scheduler, 6a §9.6); on demand from the admin |
| Size (answered 20) | target `N = min(100, 3 × D, eligible chunks)` for `D` live documents; over-generate 1.6 × N to survive filtering |
| Eligible chunks | current sets of the collection; body ≥ 50 tokens; not from PII-flagged documents (keeps personal data out of stored questions); not `quality.largeTable` rows beyond the first group |
| Stratification | by document: round-robin over documents in seeded random order, ≤ `⌈N/D⌉ + 1` per document; by document type: proportional to chunk share, each type with ≥ 5 % of chunks gets ≥ 2; by position: long documents (> 10 chunks) draw from first, middle and last third; by kind: table row groups and lists at least their share, min(5, available) |
| Seed | `SHA-256(setId ‖ round)`; the sample is reproducible |
| Model | `Settings.EvaluationModel` (on-prem chat model in the gateway catalogue, §20.3) with the collection level's ceiling (`public` none, `internal` `Eu`, `sensitive` `OnPrem`; platform plan §6); `x-ume-step: eval-generate`; temperature 0.3; JSON output |

Prompt outline (Swedish, versioned as `PromptVersion`):

```
System: Du skriver testfrågor för en sökfunktion hos Umeå kommun.
Användare: Dokument: <titel>. Avsnitt: <rubrikväg>. Text: <chunktext>.
Skriv en fråga som en invånare eller medarbetare skulle ställa och som texten besvarar.
Regler: fristående (inga "texten", "dokumentet", "ovan"), 6–25 ord, på svenska, citera inte texten ordagrant,
inga personuppgifter. Svara med JSON: {"fraga": "...", "svar": "...", "typ": "fakta|procedur|definition|tabell"}.
```

Questions are Swedish whatever the document language (BGE-M3 is cross-lingual). **Filtering** (order: cheap first):

1. Rules: Swedish (§2 detector), 6–25 words, ends with `?`, no deictic phrases (`texten`, `dokumentet`, `ovan`,
   `nedan`, `detta avsnitt`), no pattern hit from §9.1.
2. **Answer leakage**: the normalised expected answer is not a substring of the question; share of the question's
   content words (stop words removed) found verbatim in the chunk ≤ 0.8 (a near-copy is a trivial keyword match).
3. Judge (`Settings.EvaluationJudgeModel`, default the same model, temperature 0, `x-ume-step: eval-judge`): rates
   *answerable from the text*, *understandable without the text*, *specific to this content* on 1–5; keep only all ≥ 4.
4. Near-duplicates: BGE-M3 cosine ≥ 0.92 between kept questions → keep one (embeddings go to `EmbeddingCache`).
5. Never filter on whether retrieval finds the document: that would bias the set towards the current pipeline.

Expected documents: the source chunk's document plus every live document of the collection with a chunk of the same
`TextHash` (duplicated pages). Stored: `Question`, `ExpectedAnswer`, `ExpectedDocumentIds`, `SourceChunkId`
(provenance only), `SourceContentHash` (the document's content hash at generation), `Origin` (model, prompt version,
seed, judge scores), `Strata` (document type, kind) (§19).

**Cost** per kept question ≈ 1.6 × (≈ 800 tokens in, 80 out) + judge ≈ 600 in, 20 out ≈ 2,000 tokens: a full set
≈ 200,000 on-prem tokens, charged to the collection's owner team; shown before generation.

### 13.2 Why documents, not chunks

- Chunk ids and boundaries change with every pipeline version; a chunk-keyed set would be invalidated by exactly the
  change the gate must judge, and would penalise a better chunker for cutting differently.
- Users and citations work per document (knowledge plan §2); "the right document is in the top 10" is what an
  assistant needs to answer.
- Duplicates across documents are expressible (`ExpectedDocumentIds` is an array).
- Cost: long documents make document recall easier. Mitigation: per-question `ExpectedRank` and MRR show position;
  PDFs also report whether the hit chunk's page span covers the source page (diagnostic, not gated).

### 13.3 Regeneration as content changes

- A question becomes **stale** when an expected document is deleted, or its content hash differs from
  `SourceContentHash` and a judge call on that document's best-matching chunk (retrieval restricted to that document)
  no longer rates it answerable. Only changed documents are re-checked (cheap).
- Weekly top-up: retire stale questions (`RetiredAt`), regenerate to the target size with a new round seed; also when
  `D` grew by ≥ 25 % since the last round. Retired questions keep their results for history.
- A gate uses the set as it was when the gate started; top-ups never run while a gate for the collection is open.

### 13.4 Human and skill layers

- **P1**: editors add questions with expected documents and edit or retire generated ones (`edit-eval-question`
  command, 6a §9.1); `Layer = human`. Human questions are always part of a gate sample (all of them, up to 50 more).
- **P2**: questions rewritten from flagged chat feedback (plan §7: only the rewritten, personal-data-free question
  is kept, with the collection's classification); **skill tests** (`SkillVersions.TestQuestions`, tools plan
  `../tools/plan.md:33`) as `Layer = skill`, run on skill publish (`skill-publish` trigger) and in scheduled runs.
- **P3**: answer faithfulness by an on-prem judge for assistants (`EvaluationResults.Faithfulness`). Results are
  reported per layer; gates decide on the combined sample.

### 13.5 Running retrieval as the Knowledge API does

- New shared project **`Ume.Rag.Retrieval`** (used by `Ume.Rag.Api`, the worker and the admin test panel): runs the
  keyword and vector candidate statements (knowledge design §6.2) with the collection's `RetrievalSettings` (weights,
  `candidates`, `efSearch`) and profile literal, then fuses in .NET (RRF) and applies the same dedup by
  `(DocumentId, TextHash)`, variant collapse and per-document cap (default 3) as the API (knowledge design §6.5, §8);
  `k = 10` (knowledge answered 1); mode hybrid; no filters; `expand=none`; never degrades to keyword-only.
- Scope: `Collections(ids)` (the API's `CollectionIds && $scope`) or `ChunkSets(ids)` (`c."ChunkSetId" = ANY($scope)`,
  same statements otherwise) for gates (§14.1). `ChunkSets` scopes always use the **exact** vector variant; `Collections`
  scopes follow the API's exact/HNSW rule (≤ 10,000 chunks exact, knowledge design §6.4). Shadow chunks have `Chunks.DeletedAt` null and `CollectionIds = '{}'`, so they
  are invisible to the API and visible to a chunk-set scope.
- Query embedding through the same embedding client and normalisation as the API, cached in `EmbeddingCache` by
  the question's hash (stable across runs).
- Runs in the worker on the ingestion pool (R1: the data plane never pays for evaluation).
- Contract test: evaluator and `POST /v1/retrieve` return the same ids in the same order. Questions are standalone,
  so assistant query steps (condense) are not exercised (P3 assistant layer).

### 13.6 Metrics

Per question: `ExpectedRank` = rank of the first result whose `DocumentId` ∈ `ExpectedDocumentIds` (null if not in
the top 10). `recall@10` = share with a rank; `MRR@10` = mean of `1/rank` (0 without). Intervals: **Wilson** 95 % for
recall, **bootstrap** (2,000 resamples, seeded) for MRR. Paired comparisons (gates): wins, losses, net, exact
**McNemar** p on discordant questions, and a paired-bootstrap interval of the difference. With retrieval deterministic
for a fixed index, a re-run of the same subject reproduces every number exactly (test, §17).

## 14. The gate (`IPublishGate`)

```csharp
public interface IPublishGate
{
    Task<GatePlan>   PlanAsync(GateSubject subject, CancellationToken ct);   // collections, sample, sub-corpus, forecast
    Task<GateResult> RunAsync(GateSubject subject, GatePlan plan, CancellationToken ct);
}
public sealed record GateSubject(GateKind Kind, Guid SubjectId, Guid? CandidateVersionId, JsonElement? CandidateSettings);
public enum GateKind { PipelinePublish, CollectionPublish, RetrievalChange, EmbeddingChange, ProfileAutoApply, SkillPublish }
```

### 14.1 Pipeline publish: sample, sub-corpus, shadow sets

**Agree with 6a §12.1** that the candidate's chunk sets are built for real as shadow sets and reused on publish; the
refinement is *which* sets. Building shadow sets only for the documents behind 50 sampled questions and searching the
live collection would mix candidate chunks of a few documents with current chunks of all others: a candidate with
smaller chunks would rank differently against old neighbours for reasons unrelated to quality. Both arms must search
the same kind of corpus.

1. **Affected collections**: collections of sources using the pipeline; for a platform template, the 10 largest
   (by chunks) plus every collection whose owner asks for it.
2. **Sample** per collection: all human questions (≤ 50), then generated questions whose expected documents are
   referenced by an affected source, stratified by document type and kind, up to 50 in total; up to 10 others as
   controls. Fewer than 10 affected questions → result `not-measurable`, shown, does not block.
3. **Sub-corpus**: the sample's expected documents plus distractors drawn from the collection (stratified by document
   type, seeded) up to `min(D, 300)` documents, capped so the candidate's forecast stays ≤ 600 parser pages (distractors
   removed first).
4. **Baseline arm**: the current chunk sets of the sub-corpus documents. **Candidate arm**: their shadow sets for the
   candidate version, built by normal `parse`/`ner`/`embed` jobs with `Priority` 5 and the gate's
   `EvaluationRuns.Id` in `Jobs.RunIds`; shadow executions write node outputs and shadow sets only, **never**
   `Documents` facts, PII counts or child documents (like preview, but with cache writes), and are cancelled by new
   content like any execution. The finalizer (6a §10.4) enqueues the measuring `eval` job when no job of the gate is
   open.
5. **Measure**: both arms with `ChunkSets` scope (§13.5) on the same questions; both arms are searched **exactly**
   (no ANN noise, knowledge design §6.4), so they measure chunking only. Absolute numbers from a sub-corpus are optimistic and are shown
   only as context; the floor uses the full index (§14.3).

On publish, `EnsureCurrent` finds and revives the shadow sets (6a §14 rule 2); the rest of the collection is
processed by the normal `reprocess` run.

### 14.2 Decision rule and noise

```
Δ = recall@10(candidate) − recall@10(baseline)               on the 50-question sample, paired
pass                         if Δ ≥ −ThresholdPoints (3)
else escalate                run the remaining questions of the set (≤ 100) on the same sub-corpus
    pass (noise)             if Δ_full ≥ −3 points
    fail                     if Δ_full < −3 points and net losses ≥ 3 questions
    pass (not significant)   otherwise (only possible on sets < 100), shown as a warning
MRR drop > 0.05              warning only
```

Why: on 50 paired questions one question is 2 points, so a 3-point threshold alone would block on a single lost
question. Requiring the drop to survive the full set and to be ≥ 3 net questions makes a fail mean "at least three
questions that worked now fail"; McNemar p and the difference interval are shown with every result. The threshold
and sample size come from `Collections.GateSettings`, defaulting to `Settings` (3 points, 50).

### 14.3 New collections and runs without a baseline (answered 21)

- A collection is created **`building`** (`Collections.PublishedAt` null, §19). Its sources sync normally; the
  Knowledge API leaves it out of every retrieval except the admin's test panel; assistants may link it in advance.
- When its first runs are finished the worker generates the set (§13.1) and runs a `collection-publish` evaluation
  on the **full collection** with `Collections` scope: the real index and the real query.
- `recall@10 ≥ Settings.GateFloor` (0.70) → the department admin may publish (`PublishedAt`, `PublishGateRunId`);
  below → blocked unless overridden (§14.4). Sets with < 20 questions pass on the point estimate with the warning
  "set too small"; the Wilson interval is shown.
- Afterwards every gate is relative (§14.2). A pipeline publish on a collection with no passed evaluation yet uses the
  floor on the sub-corpus candidate arm instead of the relative rule.

### 14.4 Overrides (answered 19)

Only `rag-platform-admin`, per gate run (never standing), reason ≥ 20 characters; stored on the run (`OverriddenBy`,
`OverrideReason`, `OverriddenAt`, §19), audited 6001 `gate.override`, shown on the pipeline or collection page next to
the failed result. Rollbacks are never blocked (6a §12.3), so they need no override.

### 14.5 Retrieval-setting and embedding-profile changes

| Change | Corpus | Questions | Arms |
|---|---|---|---|
| `Collections.RetrievalSettings` gated values: `vectorWeight`, `keywordWeight`, `candidates`, `efSearch`, `maxChunksPerDocument` (knowledge design §12.1; top-k is per request, not a collection setting) | full collection, `Collections` scope | full set (cheap: no rebuild) | current vs candidate settings |
| Embedding profile (P3) | full collection under each profile after the background re-embed | full set | old vs new profile |
| Coverage threshold (`coverageMaxDistance`) or `expandMaxTokens` only | none | – | not gated (does not change ranking) |

Same decision rule as §14.2 on the full set; the admin saves the settings only after a pass or an override.

### 14.6 Parse-profile auto-apply and rollback (P2)

Answered 1: a learned profile with confidence ≥ 0.9 over ≥ 20 documents goes `trial`: shadow sets for 10 % (≥ 5) of
its matching documents per affected collection, gate as §14.1 with those documents in the sub-corpus; pass → `active`
and matching documents re-processed; the next scheduled run (and one 24 h after applying) compares against the run
before; a drop beyond §14.2's rule rolls the profile back automatically (`rolled-back`, notification, audit) and
re-processes with the previous strategy. The kill switch in `Settings` stops all auto-apply. P2 must add the active
profile's fingerprint to the auto-parse cache key and to the chunk-set key, because a learned profile changes the
document stage without changing the pipeline hash (P1 built-ins avoid this by versioning with the node, §3.5).

### 14.7 Timing and cost

| Gate | Work | Typical time |
|---|---|---|
| Pipeline publish, collection stage only (e.g. chunk size) | 300 documents re-chunked (cache) + ≈ 1M embedding tokens + 2 × 100 queries | ≈ 3–5 min at the §18 embedding rate |
| Pipeline publish, parse changed | ≤ 600 parser pages + NER + embedding | ≤ 40 min with OCR (parser §14) |
| Retrieval change | 2 × 100 queries | < 1 min |
| Collection publish | set generation (≈ 200k on-prem LLM tokens) + 100 queries | model-bound, ≈ 10–20 min |

The forecast (6a §12.2 style) is shown before the gate starts; the `eval` class (concurrency 1) runs only planning and
measuring, while shadow builds use the normal classes at priority 5, so a gate does not wait behind a full crawl.

### 14.8 Scheduled runs

Weekly per published collection: full set, `Collections` scope, trigger `scheduled`; feeds the trend on the admin
evaluation page and `rag.evaluation.recall_at_10` (repo-and-deployment §9). Alert (admin overview) when recall@10 falls
below the floor or 5 points below the 4-week median. Onyx found an ingestion bug only through poor evaluation scores
(`../../platforms/onyx.md:322`); this makes that routine.

## 15. Observability

Meter `Ume.Rag` (6a §17), low-cardinality dimensions only:

| Metric | Type | Dimensions |
|---|---|---|
| `rag.parse.pages` | counter | strategy (`pdfpig`, `docling-text`, `docling-tables`, `docling-ocr`, `docling-ocr-force`, `rerender`, `blank`, `missing`) |
| `rag.parse.duration_per_page` (existing) | histogram | strategy |
| `rag.parse.range_size` | histogram | strategy |
| `rag.clean.dropped_blocks` | counter | rule |
| `rag.chunk.count`, `rag.chunk.tokens` | histogram | document type |
| `rag.pii.documents` | counter | outcome (`clean`, `flagged`, `reflagged`, `ner_pending`) |
| `rag.pii.ner.duration`, `rag.pii.ner.units`, `rag.pii.ner.cache` | histogram, counter, counter | –, `sent`/`prefiltered`, `hit`/`miss` |
| `rag.embed.cache` | counter | `hit`/`miss` |
| `rag.embed.canary_similarity` | gauge | profile |
| `rag.chunkset.rekeyed` | counter | – (noise re-keys, §12) |
| `rag.evaluation.recall_at_10` (existing), `rag.evaluation.gate` | gauge, counter | collection; kind, decision |
| `rag.evaluation.llm_tokens` | counter | step (`generate`, `judge`) |

Traces: node spans (6a §17) gain `strategy`, `ranges`, `pii.types_fired`, `ner.units`, `embed.cache_hits`; never
text, names or evaluation questions (content: shown only to roles that may see the collection, never logged).

## 16. Performance (R1: 10,000 pages/hour)

Per typical web page (≈ 600 words, ≈ 8 chunks), planning numbers *to measure* in the R1 test:

| Node | Worker CPU | Other resource | Note |
|---|---|---|---|
| *Detect* | ≈ 1 ms | – | HTML: no inspect call |
| *HTML main content*, *Clean* | in 6a's 80 ms | – | – |
| *Detect personal data*, patterns | ≈ 2 ms | – | compiled regex, source-generated |
| *Detect personal data*, NER | ≈ 2 ms (client) | **≈ 0.3–1.0 s of a 2-thread NER lane** after the prefilter and cache | **revised from 6a §18's 100 ms**: BERT-base costs ≈ 0.17 GFLOP per token; ≈ 300–600 tokens sent per page; quantisation and the cache are the levers. At 2.8 pages/s this needs 1–3 lanes' worth of CPU: the likely R1 bottleneck (§20.2) |
| *Split*, *Chunk* | ≈ 5–10 ms | – | tokenizer dominates; `CountTokens` on spans, no allocations per token |
| *Metadata from path*, *Quality check* | < 1 ms | – | – |
| *Embed* | ≈ 2 ms | ≈ 3,600 tokens on the gateway's BGE-M3 | the other bottleneck to verify (6a §23) |

Parser-bound documents add inspect (≈ 20 ms per PDF) and conversion (parser §14); evaluation uses the ingestion pool.

## 17. Tests

| Kind | Project / location | Content |
|---|---|---|
| Golden per node and version | `Ume.Rag.Worker.Tests`, `tests/fixtures/processing/` | input → expected output for `detect`, `auto-parse` planning (signals → plan), `html-main-content`, `pdf-fast-path`, `plain-text`, `csv-table`, `clean`, `split-headings`, `chunk`, `metadata-from-path`, `quality-check`, `detect-personal-data` (with a fake NER client); a changed output without a version bump fails (6a §19) |
| Tokenizer parity | same | 1,000 passages, counts equal to the Hugging Face tokenizer (§6.1) |
| Swedish fixture corpus | `tests/fixtures/processing/` (public or synthetic, `MANIFEST.csv` as parser §15.3) | umea.se pages with deep navigation and contact blocks; a protokoll with `§` items and justering block; föreskrifter with `kap.`/`§`/`Bilaga`; a taxa with fee tables across pages; a report with TOC, footnotes and `1.2.3` numbering; hyphenation and suspended compounds; a PDF whose layout headings have wrong levels; a page with only headings; a 300-row XLSX; a two-column newsletter |
| Planner | `Ume.Rag.Worker.Tests` | every §3.1 rule at and around its thresholds; range sizing with fake timings; halving and missing-page outcomes; `Strategy` string grammar and truncation |
| PII precision/recall | `tests/fixtures/pii/` (synthetic only) | generated Swedish texts with Skatteverket test personnummer and samordningsnummer, organisationsnummer, OCR references, bankgiro, case numbers, phone numbers in every §9.1 format, obfuscated emails, names from SCB statistics in running text, tables, all caps, genitive and hyphenated forms; **targets**: personnummer/samordningsnummer precision and recall ≥ 0.99, email ≥ 0.99, phone ≥ 0.95, names (NER) recall ≥ 0.85 and precision ≥ 0.80, measured, reported per release; the NER model is accepted for P1 only if it reaches them (answered 18) |
| NER endpoint | parser pytest + `Ume.Rag.ContractTests` | schema, limits, `busy`, offsets with emoji and combining characters (code points vs UTF-16), label mapping, windows over 512 tokens with a name on the boundary, quantised vs fp32 agreement |
| Flag lifecycle | `Ume.Rag.IntegrationTests` | flag → public collection loses the document in the same commit; clear → membership restored without re-processing; re-flag on higher count; NER down → `pending`, internal indexed, public excluded, sweep completes it; 5103 emitted once per transition |
| Noise | `Ume.Rag.IntegrationTests` | noise-only HTML change → set re-keyed, zero chunk inserts, `EnsureCurrent` satisfied; real change → new set |
| Evaluation reproducibility | `Ume.Rag.IntegrationTests` with FakeGateway (deterministic embeddings and LLM answers) | same seed → same sample, questions and metrics; a re-run of a gate gives identical results; evaluator vs `/v1/retrieve` identical order; gate decisions for crafted win/loss patterns (§14.2 table); shadow executions leave `Documents` untouched |

## 18. Milestones

| Phase | Deliverable |
|---|---|
| P1 | *Detect* with inspect; *Auto-parse* rules, ranges, stitching, re-render, built-in profiles, observations; worker parse nodes; *Clean*; *Split by headings* with re-levelling; *Chunk* with the BGE-M3 tokenizer, row groups; *Metadata from path*; *Quality check*; *Detect personal data* with patterns, `/v1/ner`, `ner` class, flag rules, clear command, re-flag, 5103; *Embed* with cache and canary; noise re-key; generated sets with filtering; human questions; `Ume.Rag.Retrieval`; gates for pipeline publish, collection publish and retrieval settings; overrides; scheduled runs; tests of §17 |
| P2 | try-and-score; learned profiles and cleanup rules with auto-apply and rollback; `pii-mask`; feedback-derived questions; skill test layer; parent-child chunks; `halfvec` evaluation (data-model §12.16); ONNX NER if the R1 data asks |
| P3 | embedding-profile migration tooling and gate; answer-faithfulness judge; keyed PII fingerprints if count-based re-flagging proves too weak |

## 19. Changes to other documents

Applied in the step 6c review commit (2026-10-29), except the admin plan items (step 10); engine, connectors and
knowledge documents got pointer notes or answered-item updates.

| Document | Change |
|---|---|
| `parser/design.md` §7.1, new §7.8 | endpoint `POST /v1/ner` (JSON request/response, limits, errors, label mapping, code-point offsets) per §10.1–§10.2; schemas `contracts/parser/v1/ner-request.schema.json`, `ner-result.schema.json` |
| `parser/design.md` §10.1, §13.1 | process tree gains `ner-0` (own lane, warm, self-test, recycle after 5,000 requests); `/v1/info` `capacity.ner_slots`, `ner_ready`, NER model block (repository, revision, SHA-256, labels, quantised) inside `build_hash`; readiness independent of NER; env `NER_THREADS` (2), `NER_QUANTIZE` (1), `NER_QUEUE` (4), `NER_PERSON_LABELS` (`PER,PRS`) |
| `parser/design.md` §12 | model table: `KBLab/bert-base-swedish-cased-ner` revision `f3c83af8196b812028fc08e1971a2d289d0a3f1d`, ≈ 499 MB fp32 safetensors, MIT; licence inventory adds it plus `transformers`, `tokenizers`, `safetensors` (Apache-2.0, already pulled in by `docling-ibm-models`) and the SUC 3.0 note; size target ≤ 4.0 GB until the bake-off loser is removed |
| `parser/design.md` §14, §15, §16 | performance row for NER (per 512-token window, to measure); NER tests (§17); P1 milestone adds `/v1/ner` |
| `ingestion-worker/plan.md` answered 18 | label is **`PER`** in the model's `config.json` (the card's example says `PRS`); mixed `ORG/PRS`, `PRS/WRK`, `LOC/PRS` not counted in P1 |
| `ingestion-worker/plan.md` §4, §6, §7 | flag rule per type (§9.4, after §20.1); NER fail-closed for public only; observations recorded in P1; built-in profiles; gate rule with escalation; collection `building` state; noise re-key |
| `ingestion-worker/design-engine.md` §6, §7.4, §9.1 | new job class **`ner`** (concurrency from `capacity.ner_slots`); `detect-personal-data` class `ner` (inline when `ner: false`); a web page is `fetch` → `ner` → `embed` |
| `ingestion-worker/design-engine.md` §7.3 | a single page failing after halving is a missing page; `degraded` up to 10 % (≤ 20 pages), else `failed` |
| `ingestion-worker/design-engine.md` §8 | `OutputHash` of `ParsedDocument` excludes volatile facts (ETag, fetch time, timings, parser build) |
| `ingestion-worker/design-engine.md` §12.1, §14 | gate builds a sub-corpus (§14.1); shadow executions never write `Documents` facts, PII or children; gate jobs at `Priority` 5 carrying the `EvaluationRuns.Id` in `RunIds`; finalizer enqueues the measuring job; index writer re-key path for noise (§12) |
| `ingestion-worker/design-engine.md` §18 | NER lane ≈ 0.3–1.0 s per page (not 100 ms), likely bottleneck |
| `ingestion-worker/design-connectors.md` §12.2 | open item decided: noise-only changes re-key the chunk set; run item `unchanged`, reason `change.noise` |
| `platform/data-model.md` §2.2 `Documents` | `PiiClearedCounts jsonb NULL`; `PiiCounts` shape of §9.4 (incl. `ner`, `rules`, `model`) |
| `platform/data-model.md` §2.2 `ChunkSets`, `Chunks` | `ChunkSets.InputHash bytea NOT NULL`; `Chunks.PageEnd int NULL`; flat chunks are `Kind = 'child'`; confirm an index on `Chunks.ChunkSetId` (FK cascade and chunk-set scope need it) |
| `platform/data-model.md` §2.2 profiles | `ParseProfiles.Origin` adds `builtin`; `EmbeddingProfiles.CanaryText text`, `CanaryEmbedding vector` |
| `platform/data-model.md` §2.2 evaluation | `EvaluationQuestions`: `SourceContentHash bytea`, `Origin jsonb`, `Strata jsonb`, `State` (`active`/`stale`/`retired`); `EvaluationRuns`: `Kind` (`baseline`/`candidate`/`single`), `Scope` (`collection`/`sub-corpus`), `CorpusDocuments int`, `QuestionCount`, `RecallCiLow/High`, `MrrCiLow/High`, `Wins`, `Losses`, `McNemarP`, `Decision` (`pass`/`pass-noise`/`pass-not-significant`/`fail`/`not-measurable`), `OverriddenBy`, `OverrideReason`, `OverriddenAt`, `PromptVersion`, `Seed`; `Trigger` adds `collection-publish` |
| `platform/data-model.md` §2.1 | `Collections.PublishedAt timestamptz NULL`, `PublishGateRunId uuid NULL`; `Settings`: `PiiFlagRules jsonb`, `PiiOwnDomains text[]`, `NerMinScore` 0.80, `EvaluationModel`, `EvaluationJudgeModel`, `GateFloor` 0.70, `GateSampleSize` 50, `EvaluationMaxQuestions` 100 |
| `platform/data-model.md` §4.1 | `Jobs.Class` CHECK adds `ner` |
| `platform/repo-and-deployment.md` §1, §2 | new project `src/Ume.Rag.Retrieval` (Api and Worker); worker resource `bge-m3/sentencepiece.bpe.model` (revision `5617a9f`, SHA-256 checked); package `Microsoft.ML.Tokenizers` 2.0.1; `Ume.Rag.Worker/Pii`; fixtures `tests/fixtures/processing/`, `tests/fixtures/pii/` |
| `platform/repo-and-deployment.md` §9 | metrics of §15 |
| `identity/design.md` §10 | 5103 attributes `DocumentId`, `Classification`, `Types`, `Counts`, `SourceId`, `RunId`, `Reflag`; SIEM rule for public references with personnummer; audit actions `pii.clear`, `gate.override` |
| `knowledge-api/plan.md` (step 7) | uses `Ume.Rag.Retrieval`; excludes `building` collections except the admin test panel; queries embedded without instruction prefix, NFC; note the conflict between its answered 3 (parent-child default) and ingestion answered 8 (flat in P1) |
| `admin/plan.md` (step 10) | PII list with highlight preview and clear (reason); collection publish with floor result; gate page with arms, wins/losses, intervals, escalation and override; evaluation page: layers, stale questions, generation cost |
| `platform/plan.md` §9 | evaluation targets documents; collection go-live gated by the floor |
| `01-general-idea.md` §10 | after review, a "Processing (step 6c)" row: per-page rules, built-in profiles, BGE-M3 tokenizer in .NET, `/v1/ner` lane and `ner` class, flag rules, noise re-key, sub-corpus gate with escalation, collection go-live floor |

## 20. Answered questions (2026-10-29)

1. **Flag rules**: option (a): personnummer, samordningsnummer and external email always; names, phones and
   own-domain emails only together in list-like documents (§9.4).
2. **NER capacity**: option (a): index first, names checked behind it, documents out of `public` collections until
   then (§9.6).
3. **Evaluation models**: default the gateway's on-prem general chat model for both (settings).
4. **Collection go-live**: `building` until the floor or an override (§14.3).
5. **Chunking conflict** with knowledge answered 3 (raised in review): flat chunks with section expansion at query time
   in the knowledge API.

## 21. To verify

- `Microsoft.ML.Tokenizers` 2.0.1: count parity with Hugging Face for BGE-M3 (Unigram with `nmt_nfkc` precompiled
  charsmap), whitespace and dummy-prefix options to match XLM-R, thread safety of one shared instance, speed on
  5,000-character pages.
- KB NER: actual labels returned for persons with the pinned revision (`PER` in `config.json`, `PRS` in the card);
  transformers 5 tokenizer loading without `tokenizer_class` (`do_lower_case` honoured); quality on our fixtures;
  CPU per 512-token window fp32 vs dynamic int8 on the test host; SUC 3.0 terms for derived weights.
- Gateway embeddings: maximum inputs and tokens per request for the on-prem BGE-M3 provider; whether BGE-M3 is
  served without instruction prefixes; throughput (6a §23).
- PdfPig 0.1.x: `NearestNeighbourWordExtractor`, `DocstrumBoundingBoxes`, `UnsupervisedReadingOrderDetector` names and
  behaviour on two-column municipal PDFs; per-letter font size access.
- SmartReader output for umea.se templates (headings kept, contact boxes dropped or kept).
- Skatteverket's published test personnummer and samordningsnummer (format, open-data licence) and SCB name
  statistics (licence) for fixtures and the gazetteer.
- Docling `timings` granularity for per-page milliseconds (range sizing falls back to `docling_ms / pages`).
- Postgres: chunk-set scope with `ChunkSetId = ANY(…)` uses a bitmap scan and exact ordering (no HNSW) for a
  2,000-chunk sub-corpus; the index on `Chunks.ChunkSetId` is the unique (`ChunkSetId`, `Ordinal`) index (knowledge
  design §18).
- On-prem LLM through the gateway: JSON output mode, and that judge scores at temperature 0 are stable enough for
  reproducible filtering.
- All planning numbers of §14.7 and §16, measured in the R1 test.
