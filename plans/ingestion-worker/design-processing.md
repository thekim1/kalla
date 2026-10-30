# Ingestion worker, part C: processing stages, personal data, embedding and evaluation

Detail step 6c of the Ume.Rag design. Details [`plan.md`](plan.md) §4–§7 (binding, incl. answered items 1–26) and
specifies the internals of the stages [`design-engine.md`](design-engine.md) (6a) marks "6c" in its stage table
(`design-engine.md` §6). It consumes, without redesigning: the stage values (6a §2.2), the stage interface and results
(6a §5, §7.3), segments and job classes (6a §7.4, §9.1), cache classes (6a §8), preview (6a §11), the publish flow
(6a §12), the index writer (6a §14) and the AIMD limiter (6a §18); site profiles (learning, versions, identity rule,
`lastmod` reliability), `ContentHashVersion` and noise rules from [`design-connectors.md`](design-connectors.md); the
parser contract, signals, ranges and the text-layer fast path from [`../parser/design.md`](../parser/design.md) §3,
§6–§10; the tables of [`../platform/data-model.md`](../platform/data-model.md) §2.2 and the hybrid query §3.3.
Rewritten to the decided form of [`../simplification-pass.md`](../simplification-pass.md) (2026-10-30). Nothing is
implemented. Changes needed elsewhere: §19; questions: §20; unverified facts: §21; removed mechanisms: §22.

Library and model facts checked for this step (downloaded into the scratchpad, read only): `Microsoft.ML.Tokenizers`
2.0.1 (NuGet, MIT; contains `SentencePieceTokenizer` with `SentencePieceUnigramModel` and precompiled-charsmap
normalisation), BGE-M3's `sentencepiece.bpe.model` (Hugging Face `BAAI/bge-m3` revision `5617a9f`, MIT: despite the
file name its `TrainerSpec.model_type` is **1 = UNIGRAM**, 250,000 pieces, normaliser `nmt_nfkc`), and
`KBLab/bert-base-swedish-cased-ner` (revision `f3c83af`, 2026-10-15, MIT, `BertForTokenClassification`, 512
positions, `do_lower_case: false`, `strip_accents: false`; `config.json` labels are **`PER`** plus mixed labels
`ORG/PRS`, `PRS/WRK`, `LOC/PRS`, while the model card's example prints `PRS`; IO tags without `B-`/`I-`; reported
F1 0.922 on SUC 3.0).

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Routing | Per document by detected media type: HTML, text/Markdown and CSV in the worker; PDF, Office, ODF and images to the parser. The worker parses **no untrusted binary format** (§3) |
| 2 | Per-page strategy | Six ordered rules on the `/v1/inspect` signals; clean text-layer pages use the parser's **text-layer fast path** (pypdfium2); post-conversion checks re-run garbled pages with forced OCR and empty scanned pages once at scale 3.0 (§3.1) |
| 3 | Parse rules | Built-in rules (`default`, `regulations`, `protokoll`) plus platform-admin parse rules, versioned; `ParseObservations` recorded; no auto-apply learning of parse strategies (§3.5) |
| 4 | HTML | Extraction set by the host's **site profile** (main root, boilerplate, breadcrumb, date pattern, metadata, link lists for discovery only), versioned by 6b (manual in P1, learned from P2, weakness review §1); built-in presets (Sitevision first) when no profile exists (§3.7) |
| 5 | Single-page failures | A page that fails alone after halving is a **missing page** (`degraded`) while ≤ 10 % of pages; above that the document fails (§3.3) |
| 6 | Headings | Layout headings re-levelled by Swedish numbering patterns (`1.2`, `Kapitel 3`, `3 kap.`, `§ 4`, `Bilaga 2`); deeper than `maxLevel` becomes bold body text (§5) |
| 7 | Tokenizer | `Microsoft.ML.Tokenizers` 2.0.1 `SentencePieceTokenizer` over BGE-M3's own SentencePiece model, embedded in the worker; counts must equal the Hugging Face tokenizer on a fixture corpus (§6.1) |
| 8 | Chunk budget | 400 tokens **including** the prefix (breadcrumb › heading path); overlap 60 tokens (15 %) of whole trailing sentences, within a section only; tables as `Kolumn: värde; …` row groups without overlap (§6) |
| 9 | `TextHash`, `SearchText` | `TextHash` = SHA-256 of the exact embedded text (prefix + body, NFC); `SearchText` = text plus the parts of its compounds from the .NET `SwedishDecompounder` (§6.5) |
| 10 | Personal data | Runs on the chunk set after chunking; patterns in .NET for every set; names (NER, `POST /v1/ner`) **only when they can change the flag**; counts and flag live **on the chunk set** (§9) |
| 11 | NER down | Only sets that need the name check wait: `pending`, held out of the `public` collection until checked (§9.6) |
| 12 | Re-flag | A cleared set's clearance carries to its successor unless a flagging type's count rises above its count at clearance; values are never stored (§9.5) |
| 13 | Embed | Platform profile only; ≤ 64 inputs and ≤ 16k tokens per request; cache by (level, profile, `TextHash`); a canary embedding detects a silently changed gateway model (§11) |
| 14 | Noise-only changes | **No new chunk set**: when the chunk-stage input hash equals the live set's, the set is re-keyed in one row update (§12) |
| 15 | Evaluation | **Advisory harness**: questions point at **documents**; recall@10 and MRR@10 with counts on the full collection after each publish and nightly, compared with the previous run; never blocks (§13, §14). Reports lead with the `human` gold-set results; generated questions are a regression signal only (weakness review §2, §13.4) |
| 16 | Retrieval in evaluation | A shared `Ume.Rag.Retrieval` library runs the Knowledge API's statements, fusion and diversity for both the API and the evaluator (§13.5) |

## 2. *Detect*

`detect` (inline, intermediate cache) adds facts to `RawContent`; it never changes the bytes.

| Fact | How | Used by |
|---|---|---|
| `mediaType`, `mediaTypeDeclared`, `mediaTypeMismatch` | magic bytes (PDF, ZIP+`[Content_Types].xml`, PNG/JPEG/TIFF), then declared type; the parser re-sniffs anyway (parser §3) | route (§3.2) |
| `language` | HTML `lang` attribute; text/Markdown/HTML body: own stop-word detector (200 Swedish and 200 English function words, ratio on the first 5,000 characters → `sv`/`en`/`und`); parser-bound types get it from *Clean* after parsing | `Documents.Language` |
| `inspect` (PDF, TIFF, Office, ODF) | parser `/v1/inspect` (parser §6.2, own lane, 10 s): `page_count`, `rangeable`, `encrypted`, `pages[]` signals, `refusal` | parse plan (§3) |
| `scanned` | share of inspected pages that are not "clean" (§3.1 rule 5) ≥ 0.5 | facts, cost estimates |
| `pageCount`, `bytes` | inspect / blob | cost estimates (6a §5.1), limits |

`refusal` from inspect (`encrypted` with `user_password`, `too_many_pages` > 2,000, `unsupported_format`,
`format_deferred`) ends the
document at once with the matching outcome (6a §10.2), without spending a parse slot. Inspect errors are mapped like
convert errors (parser §7.5); inspect `busy` requeues without an attempt.

## 3. *Parse* (routing and auto-parse rules)

### 3.1 Per-page strategy

Evaluated per PDF page (and TIFF frame) on the inspect signals (parser §6.2), first matching rule wins:

| # | Condition (inspect page) | Strategy | Why |
|---|---|---|---|
| 1 | `text_chars < 50`, `image_coverage < 0.1`, `path_objects < 200` | `blank` (no request; marker kept) | empty page |
| 2 | `text_chars < 50`, `path_objects ≥ 200` | `docling-ocr-force` | text drawn as outlines or vector art |
| 3 | `garbled_score ≥ 0.4` or `bad_chars / text_chars ≥ 0.1` | `docling-ocr-force` | garbled layer (parser §6.1) |
| 4 | clean: (`text_chars ≥ 200` or (`≥ 50` and `image_coverage < 0.3`)) and `garbled_score < 0.2`, and `path_objects ≥ 40` | `docling-tables` (OCR off, tables `accurate`) | table-dense |
| 5 | clean as in 4, `path_objects < 40` | `text-layer` (parser fast path, pypdfium2) | clean text layer |
| 6 | otherwise (few characters with images, or garble 0.2–0.4) | `docling-ocr` (OCR `auto`: bitmaps only) | scanned or mixed |

As built in P0 (parser §3; P0 #13, PR #16; settled at the integration sync point): the parser checks rule 5 again
before its fast path, and if any page of a `text-layer` request fails it, converts the **whole request** with Docling
and OCR and adds the warning `text_layer_rejected`; so the worker groups only rule-5 pages into `text-layer` requests.
Layout headings on PDFs always need the re-levelling of rule 6 (§5): Heron gives every heading, the title included,
level 1. `/v1/inspect` answers `501 not_implemented` in P0; the rules above run from P1.

After conversion, per page (parser §6.4 signals):

- **Re-render** once: `chars < 20` and `image_coverage ≥ 0.5` → `docling-ocr-force@3` (`image_scale` 3.0), the
  RAGFlow zoom retry (`../../platforms/ragflow.md:84-87`) bounded to one step.
- **Garble after conversion**: a `docling-ocr` or `text-layer` page with `garbled_score ≥ 0.4` → `docling-ocr-force`
  once.

Template parameters override the rules: `ocr: force` maps every page to `docling-ocr-force`; `ocr: off` turns rules
2, 3 and 6 into `docling-text` and blank results into `degraded`. `tables` sets the table mode for every Docling page
except rule 4 (always `accurate`). Thresholds live in the stage's code with its version (changing them is a stage
version bump, 6a §5.1); P1 tunes them once on the fixture corpus (§17). OCR is Tesseract `swe` in the parser
(simplification pass §1).

### 3.2 Routes

| Input | Route | Notes |
|---|---|---|
| HTML (`text/html`, `application/xhtml+xml`) | worker: *HTML extraction* (§3.7) | site profile or preset |
| `text/plain`, `text/markdown` | worker (§3.7) | – |
| `text/csv` | worker (§3.7) | one table |
| PDF | parser, ranges per §3.3 | all PDFs, including clean text layers |
| DOCX, PPTX, XLSX, ODT, ODS, ODP | parser, one `/v1/convert`, no ranges (parser §8.1) | `timeout` → `failed` (cannot be split) |
| JPG, PNG | parser, one request, OCR `auto` | – |
| Multi-frame TIFF | parser, frames as pages, rules 1–6 with `docling-ocr` as the floor | – |
| Legacy `.doc`/`.xls`/`.ppt`, RTF, `.eml`/`.msg` | – (P2) | outcome `unsupported`, reason `format_deferred` |
| anything else (archives, macro-enabled Office, executables, unknown) | – | outcome `unsupported` (`unsupported_format`) |

### 3.3 Range planning, sizing, failures

1. Consecutive pages with the same strategy form a run; each run is cut into ranges (parser §8.1). `blank` pages never
   go to the parser.
2. First range size: 20 (`text-layer`, `docling-text`, `docling-tables`), 8 (`docling-ocr`, `-force`), 4 (`@3`).
   **P1** (weakness review §1): these sizes are fixed per strategy from configuration (`Worker:Parse:RangePages:<strategy>`)
   and only halved on failure (item 4). **P2**: after each range `msPerPage = EWMA(α 0.5)` of `timings.docling_ms / pages` per strategy, and
   `next = clamp(⌊0.5 × 110,000 / msPerPage⌋, 1, 20)` (half the 110 s request deadline).
3. Ranges run sequentially in the document's `parse` job; completed ranges are written as `ranges/{from}-{to}`
   (6a §8) so a crash, slice yield (6a §9.5) or retry resumes at the next range.
4. `status: partial` keeps `pages_completed` and requests the rest. `timeout`, `worker_crashed`, `resource_limit`
   halve the range; a **single page** that still fails is recorded as a missing page. Missing pages ≤ 10 % of the
   document (and ≤ 20 pages) → outcome `degraded` with the page list; more → `failed`, reason `parser.<code>`.
5. `ocr_failed` / `conversion_failed`: transient once for the range, then as 4.

### 3.4 `Strategy` string (`RunItems.Strategy`, ≤ 500 characters)

```
parse=auto(rule=regulations@1)[text-layer:1-38,docling-tables:39-40,docling-ocr:41-44,blank:45;rerender:43;missing:44]
parse=html(profile=www.umea.se@3)
```

Grammar: `parse=<route>(rule=<name>@<v> | profile=<host>@<v>)[<strategy>:<page ranges>,…;rerender:<pages>;missing:<pages>]`,
pages compressed to ranges, `…` when truncated. Non-paged inputs: `parse=auto[docling-docx]`, `[docling-image]`. The
same string is the fact `parse.strategy` and `Documents.ParseStrategy`.

### 3.5 Parse rules and observations

A parse rule is data: `Match` → strategy overrides (`ocr`, `tables`, `image_scale`) and structure hints (heading
patterns, furniture patterns) consumed by *Clean* and *Split by headings* through the fact `parse.rule`.

| Rule | Match | Effect |
|---|---|---|
| `default` | always (last) | none |
| `regulations` | title or first 2,000 characters match `(?i)\b(föreskrift(er)?\|reglemente\|taxa\|ordningsföreskrifter\|författningssamling)\b` | heading patterns kap./§/Bilaga (§5.2), `maxLevel` 4 |
| `protokoll` | title or first page matches `(?i)\b(protokoll\|sammanträdesprotokoll\|kallelse)\b` | each `§ N` item is a level-1 section; signature blocks (`Justerare`, `Justerandes signatur`, `Utdragsbestyrkande`) are furniture |

- Built-in rules are seeded rows (`ParseProfiles.Origin = builtin`, §19), read-only, versioned with the stage.
  **Admin-made rules** (`Origin = admin`, platform admins, generic schema-driven form in P1) take the same shape (domain,
  path glob, media type, text pattern → overrides and hints); each save is a new rule version, audited. The matched
  rule's id and version are part of the parse stage's settings (6a §3.3), so a rule change re-keys only the documents
  it matches, which the next run or a `reprocess` brings up to date. Strategy-level matches run before the plan;
  text-level matches run on the joined output.
- **Observations**: one `ParseObservations` row per non-preview parse: `Domain`, `PathPattern` (path with digits and
  the last segment replaced by `*`), `DocumentType`, `Strategy` (share of pages per strategy), `Score` (§8),
  `Metrics` (quality facts). Retention 180 days (platform plan §8). P1 shows them on the document page; from P2 they
  feed **rule proposals** shown to platform admins, applied only after approval.

### 3.6 Scorer seam

```csharp
public interface IParsePlanner   // rules of §3.1 with the matched parse rule
{ PagePlan Plan(DocumentFacts facts, InspectResult inspect, ParseRule rule, ParseParams p); }

public interface IParseScorer    // observations (§3.5) and P2 rule proposals
{ double Score(ParsedDocument parsed, QualityFacts facts); }
```

### 3.7 Worker-side parsers

The worker parses only HTML, text, Markdown and CSV (simplification pass §9).

| Parser | Internals |
|---|---|
| *HTML extraction* | AngleSharp DOM (already built by the root for links, 6b §12.2), then the host's **site profile** (6b; built-in preset when the host has none; uploaded HTML uses presets only): (1) **main root** by the profile's selector (`main` for Sitevision), fallback landmark (`main`, `article`, `role=main`) then SmartReader; (2) **strip** the profile's boilerplate selectors and repeated phrases inside the root (share widgets, short-URL lines, cookie banners); (3) **link-list blocks** marked by the profile ("Undersidor") are kept for discovery only, never as text; (4) blocks: `h1`–`h6` (`level_source: html`), `p`, `li` (with nesting depth), `table` (cells, `th`/`thead` as header rows, spans expanded), `pre`/`code`, `figcaption`; (5) facts: **breadcrumb** from the profile's selector, else `aria-label` *Brödsmulor*/*breadcrumb*, else `BreadcrumbList`, with screen-reader-only text (`sr-only` and similar classes, `aria-hidden` excluded, visually hidden helper spans) removed, the current page dropped when it repeats the title; **visible date** by the profile's date pattern (e.g. `Sidan uppdaterades (?<d>\d{4}-\d{2}-\d{2})`); **description** (`meta description`, else `og:description`) and **keywords** (`meta keywords`, split on commas); title: `og:title`, else `<title>` with a repeated site suffix after ` - ` or a vertical bar removed, else first `h1`; the site-profile version used. Contact blocks ("Kontakt") stay ordinary sections: answers to "vem kontaktar jag?" need them; personal-data rules apply (§9) |
| `plain-text` | charset (BOM, strict UTF-8, Windows-1252 fallback, parser §3); Markdown via Markdig (`level_source: markdown`), plain text as paragraphs split on blank lines |
| `csv-table` | delimiter sniffed among `;`, `,`, tab, `\|` on the first 50 lines; header row = first row all non-numeric and distinct; one table block; 200,000-cell cap as parser §7.4 |

The **site-profile check** (P2, weakness review §1; 6b: a re-learned profile is applied automatically only when, on the sample, no page loses
more than 10 % of its main-text words, none ends up empty and the boilerplate share does not rise) runs this same
extraction in preview mode on both profile versions; its procedure and thresholds are 6b's.

## 4. Joining and *Clean*

### 4.1 Joining (parser §8.2, in *Parse* before its output is stored)

Ranges of one document are all parser outputs (no worker-extracted pages are merged in) and are joined exactly as
parser §8.2. The joined `result.json` (parser §7.6) is the *Parse* output; *Clean* never sees ranges.

### 4.2 *Clean* (`clean`, inline, intermediate)

Applied in this order to body blocks; every rule counts its effect into facts (`clean.furnitureDropped`, …).

| Rule | Detail |
|---|---|
| Unicode | NFC; NFKC only for the ligatures U+FB00–FB06; soft hyphen U+00AD removed; NBSP, thin and narrow NBSP → space; zero-width characters removed; quotes kept |
| Furniture | drop `layer: furniture`, `page_header`, `page_footer`; for pages without layer labels (`text-layer` pages, *to verify* what the fast path returns): a line in the top or bottom 8 % of the page whose digit-normalised text recurs on ≥ 50 % of pages (min 3) is furniture; blocks matching `^(sida\s*)?\d+(\s*(av\|/)\s*\d+)?$` are page numbers; parse-rule furniture patterns (§3.5) |
| Table of contents | ≥ 4 consecutive lines matching `^.{3,120}?[ .·…]{3,}\s*\d{1,4}$` → dropped (they duplicate headings and pollute retrieval) |
| Dehyphenation | inside a block: `\p{L}{2,}-\n\p{Ll}` → join without hyphen, **except** when the next word is `och`, `eller`, `samt`, `till`, `respektive` (suspended compound: "kommun- och regionförbund"), or the part before the hyphen is all capitals or digits ("EU-\nmedel" → "EU-medel"); line breaks inside paragraphs → spaces |
| Whitespace | runs of spaces collapsed; more than two newlines → two |
| Minimum block | drop non-heading, non-list blocks with fewer than 3 letters or digits after the rules above |
| Swedish specifics | `§4` → `§ 4`; `kap.` and numbering untouched (§5 needs them); no stemming or case change |
| Language | `language` fact from the cleaned text (§2) if not set |

HTML boilerplate is removed by the site profile in extraction (§3.7), not here.

## 5. *Split by headings* (part of `chunk`)

### 5.1 Section model

```csharp
public sealed record Section(int Ordinal, int Level, IReadOnlyList<string> HeadingPath, string? Heading,
    IReadOnlyList<BlockRef> Blocks, int? PageStart, int? PageEnd);
```

The document title (`title` block or `Documents.Title`) is level 0 and the first element of every heading path unless
the first heading repeats it. A heading opens a section at its level and closes every open section at that level or
deeper. Blocks before the first heading belong to the level-0 section. A heading immediately followed by a deeper
heading opens a section with no blocks; it only contributes to paths.

### 5.2 Re-levelling

`style`, `html` and `markdown` levels are trusted. `layout` levels (PDF Docling pages; parser §7.3 calls them
unreliable) and `font_size` levels (text-layer fast path) are recomputed from the heading text, first match wins:

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
level (a list item misread as heading). Levels deeper than `maxLevel` (4) are not sections: the heading becomes a bold
paragraph (`**…**`) in its parent. The RAGFlow title chunker with regex level sets is the model
(`../../platforms/ragflow.md:183-185`), with Swedish patterns as its report proposes (`:249-253`).

### 5.3 Long sections and documents without headings

Section size is unbounded here; the chunker (§6) packs long sections into several chunks with the same path, so
splitting never cuts mid-paragraph. A document without headings is one level-0 section (path = title). Parent-child
(P2, only if evaluation shows that query-time section expansion from neighbouring chunks, knowledge design §9, is
insufficient) cuts sections above 1,500 tokens into parent windows at block boundaries.

## 6. *Chunk* (`chunk`, inline, intermediate; answered 8)

### 6.1 Tokenizer

- `Microsoft.ML.Tokenizers` 2.0.1 (MIT, stable; the KM report already chose it for counting,
  `../../platforms/kernel-memory-and-dotnet.md:183`), `SentencePieceTokenizer.Create(stream, addBeginOfSentence:
  false, addEndOfSentence: false)` over BGE-M3's `sentencepiece.bpe.model` (5.07 MB, MIT), embedded as a resource in
  `Ume.Rag.Worker` with its SHA-256 checked at startup. The model is Unigram with `nmt_nfkc` (checked, see preamble);
  2.0.1 contains the Unigram model and precompiled-charsmap normaliser. Counting needs no ids, so the fairseq id
  offset of XLM-R does not matter; the gateway adds `<s>`/`</s>` (2 tokens).
- Parity test: 1,000 Swedish and English passages from the fixture corpus, counts equal to Hugging Face
  `XLMRobertaTokenizerFast` for `BAAI/bge-m3` (golden file generated once in Python). A mismatch fails the build; if
  parity cannot be reached, the fallback is a 5 % safety margin on the budget (P1 decision at implementation).
- One shared instance (thread-safe for encoding, *to verify*); `CountTokens` and `GetIndexByTokenCount` on spans.

### 6.2 Packing

```
budget      = chunkTokens (400)                       counted on  prefix + "\n\n" + body
prefix      = Breadcrumb ++ HeadingPath, joined " › "  ≤ 60 tokens: keep the first breadcrumb element and the last
                                                       two heading levels, "…" between
overlap     = ⌊overlapPercent × chunkTokens⌋ = 60     whole trailing sentences of the previous chunk, same section only
units       = sentences (paragraphs), items (lists), rows (tables), blocks (code)
```

The breadcrumb (§3.7) goes in front of the heading path; an element that repeats the next one (breadcrumb's last
level equal to the title) is dropped. Document date and type stay metadata, never in the prefix.

1. Per section, split paragraphs into sentences with a Swedish splitter: boundary after `.`, `!`, `?`, `:` + space +
   upper-case letter or digit, except after the abbreviation list (`t.ex.`, `bl.a.`, `m.m.`, `s.k.`, `kap.`, `nr.`,
   `ca.`, `dvs.`, `o.s.v.`, `fr.o.m.`, `t.o.m.`, `jfr.`, `resp.`, `enl.`, single capitals as initials, `§`).
2. Greedily add units while `tokens(prefix + body) ≤ budget`. A unit larger than the remaining budget starts a new
   chunk; a unit larger than a whole budget is cut at the last whitespace before the limit (`GetIndexByTokenCount`).
3. A new chunk of the same section starts with the overlap: trailing sentences of the previous chunk up to 60 tokens
   (one sentence over 60 tokens: its last 60 tokens at a word boundary). No overlap across sections, tables or code.
4. **Tail balance**: a section's last chunk below 25 % of the budget is merged into the previous one if the result is
   ≤ 115 % of the budget, else the last two chunks are re-split evenly at a sentence boundary.
5. **Short sections**: consecutive sibling sections each below 50 body tokens are packed together under their common
   parent path (their own headings become bold lines in the body), so "Kontakt" or "Öppettider" stubs do not become
   one-line chunks; never across a level-1 boundary.

Onyx embeds a title prefix inside the budget with no overlap (`../../platforms/onyx.md:173-176`); Eneo uses 200
tokens with no structure (`../../platforms/eneo.md:105`); Open WebUI loses heading metadata
(`../../platforms/open-webui.md:176-177`). Here the path is both in the embedded text and in `HeadingPath`.

### 6.3 Tables (row groups)

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
| Picture | only its caption, as a sentence in the flow (picture crops dropped until a describe-pictures stage exists) |

### 6.5 Chunk rows written

| Column | Value |
|---|---|
| `Kind` | `child` for flat chunks (`ParentId` null), so the `SearchVector` and HNSW conditions apply (data-model §2.2, §3.2) |
| `CollectionId` | the source's collection (scalar) |
| `Ordinal` | 0-based order within the chunk set |
| `HeadingPath` | full heading path joined with ` › ` (untruncated; the prefix may be truncated; breadcrumb not included) |
| `Text` | body without the prefix (shown as context and citation snippet) |
| `SearchText` | `Text` followed by the parts of the compounds found in the prefix and `Text` by `SwedishDecompounder` (data-model §3; `bygglovsansökan` → `bygglov ansökan`); the generated `SearchVector` uses the built-in `swedish` configuration over it |
| `OverlapChars` | number of leading characters of `Text` repeated from the previous chunk (§6.2 rule 3, list lead-in); 0 for the first chunk of a section, tables and code; section expansion strips them (knowledge design §9.3) |
| `TextHash` | `SHA-256(UTF-8(NFC(prefix + "\n\n" + Text)))` = exactly the embedded string |
| `TokenCount` | tokens of the embedded string |
| `Page`, `PageEnd` | first and last page of the chunk's blocks; null for HTML/DOCX |
| `Tags`, `DocumentType` | from *Metadata* (§7); `DocumentType` default from media type: `page`, `pdf`, `word`, `presentation`, `spreadsheet`, `text`, `image` |
| `SourceModifiedAt` | the document's date (§7) |

**Ids.** `Chunks.Id` stays a v7 id per row. Anything that must survive re-chunking (evaluation provenance, retrieval
statistics, feedback) uses `(DocumentId, TextHash)`, the key retrieval already deduplicates on (knowledge design §8).
Upload replacements keep the document id (6b), so this survives file revisions too.

## 7. *Metadata* (part of `chunk`)

```json
{ "pathRules": [ { "match": "/bygga-bo-och-miljo/bygglov/**", "on": "path",
                   "tags": ["bygglov"], "documentType": "page" },
                 { "match": "^/kommun-och-politik/protokoll/(?<y>\\d{4})/", "on": "path-regex",
                   "tags": ["protokoll"], "date": "${y}-01-01" } ] }
```

- `on`: `path` (glob on the URL path, or the upload's display name), `path-regex`, `host`, `title-regex`, `mediaType`.
  Every matching rule adds its tags; the first match sets `documentType` and `date`.
- Tags: lower case, NFC, `[\p{L}\p{N}-]{1,50}`, ≤ 20 per chunk. Invalid regex is a validation error at save (JSON
  Schema `format: regex`, 6a §4).
- **Date** (`SourceModifiedAt`), first available: the visible date from the site profile's date pattern (§3.7); a
  path-rule `date`; the sitemap `lastmod` when 6b marks it reliable for the host; the document's own metadata (PDF/Office
  properties); HTTP `Last-Modified`. CMS `Last-Modified` is often the render time, so it comes last.
- **Description and keywords** from the page (§3.7) are stored as document metadata (`Documents.Description`,
  `Documents.Keywords`, §19), shown with citations and available as filters; they are not chunk text.

## 8. Quality facts

Computed in *Parse*, *Clean* and *Chunk*; written as facts (`quality.*`) and shown in the run item. They never route a
document (there are no branches) and only missing pages make it `degraded`.

| Metric | On | Definition | Note above |
|---|---|---|---|
| `chars` | Parsed | body characters after *Clean* | `< 200` → info note `quality.short` in the crawl report (never `degraded`; rendering is P2) |
| `charsPerPage` | Parsed (paged) | `chars / non-blank pages` | `< 100` |
| `garble` | Parsed | page-weighted mean of `garbled_score` (parser pages; HTML/text: letter ratio < 0.5 → 1) | `> 0.3` |
| `emptyPageRatio` | Parsed | pages with `chars == 0` and not `blank` ÷ pages | `> 0.3` |
| `ocrConfidence` | Parsed | mean of page `ocr_confidence.p10` over OCR pages (null if unavailable, parser §6.4) | `< 0.6`, skipped when null |
| `missingPages` | Parsed | §3.3 | any → `degraded` |
| `emptyChunkRatio` | Chunks | chunks with < 20 body tokens ÷ chunks | `> 0.2` |
| `duplicateChunkRatio` | Chunks | chunks sharing a `TextHash` within the set ÷ chunks | `> 0.3` |
| `boilerplateShare`, `emptyRoot` | HTML | stripped ÷ root text; root selector matched nothing | feed 6b's re-learning trigger (P2); reported in the run from P1 |

`Score` (observations) = `min(1, chars/(1500 × pages)) × (1 − garble) × coalesce(ocrConfidence, 1) × (1 − emptyPageRatio)`,
in [0, 1].

## 9. *Personal data* (`personal-data`, inline, intermediate)

Runs on the chunk set after chunking, over chunk bodies with their `OverlapChars` stripped (so repeated text is counted
once) plus table rows as in §6.3; outputs the chunks unchanged plus the fact `pii.counts`. The index writer stores the
counts and flag **on the chunk set** (6a §14). Every execution that produces a set, preview and re-processing included,
produces its state. Text is NFC with NBSP and soft hyphens already normalised (§4.2), which matters for phone numbers
and names.

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

### 9.3 Names (gated, parser `/v1/ner`, §10)

Names are checked **only when they can change the flag** (simplification pass §1), i.e. all of:

1. the source's collection is `public` (only `public` collections are blocked by a flag);
2. no other rule already flags the set (§9.4);
3. the set has ≥ 5 contact values (phone and own-domain email, distinct), so the list-like rule could fire.

Otherwise `ner` is `skipped` and names count 0. Text units sent: sentences of chunk bodies and table rows (context for
names in lists). **Prefilter**: a unit is sent only if it contains a capitalised word that is not its first word, or a
first word found in a first-name gazetteer (SCB name statistics, names with ≥ 50 bearers, *to verify* licence); ALL-CAPS
units are sent as they are. Entities with label `person` and `score ≥ Settings.NerMinScore` (0.80) count; distinct
names are counted after NFC, case folding and joining hyphenated parts.

### 9.4 Counts and the flag

`ChunkSets.PiiCounts` (values never stored, never logged, never in traces):

```json
{ "personnummer": 2, "samordningsnummer": 0, "email": 14, "emailExternal": 1, "phone": 9, "name": 23,
  "ner": "done", "rules": 1, "model": "kb-bert-ner@f3c83af" }
```

`emailExternal` = addresses outside `Settings.PiiOwnDomains` (e.g. `umea.se`). `ner` is `done`, `skipped` (not needed,
§9.3) or `pending` (§9.6). Spans live only in memory during the stage and in preview blobs (24 h), where the document
page can show highlights to an editor deciding on a clearance.

**Flag rule** (`Settings.PiiFlagRules`, per type `{ "flag": "always" | "threshold" | "never", "min": n }`; answered
§20.1): personnummer and samordningsnummer `always` (≥ 1); `emailExternal` `threshold` 1; `name`, `phone`, `email`
`threshold` together: ≥ 5 distinct names **and** ≥ 5 phone/email values (list-like documents: member lists,
attendance, case lists). A set is flagged when any rule fires; `ner = pending` counts as flagged for the `public`
collection (§9.6). The umea.se example page's contact block (3 contact values) is below the name-check threshold.

### 9.5 Flags, `public` collections, clearing, re-flagging

- **Blocking**: a flagged or `pending` set in a `public` collection is swapped in as **`held`** (6a §14 rule 3): the
  document's old content stops being served at once and the new content (its chunks keep `DeletedAt`) is not served
  until the document is cleared. In other collections the flag is shown but nothing is blocked. Clearing releases the
  held set (state `live`, `DeletedAt` cleared on its chunks, 6a §14), never re-processing.
- **New flag**: `PiiFlaggedAt = now()` on the set; run items get reason `pii.flagged` (outcome stays
  `added`/`updated`).
- **`clear-pii-flag` command** (6a §9.1): a person's decision on the **document**, issued by `rag-department-admin`
  of the source's department, or `rag-platform-admin`; reason required (≥ 10 characters); admin audit 6001
  `pii.clear`. The worker sets `Documents.PiiClearedAt`, `PiiClearedBy` and `PiiClearedCounts` (= the held set's
  `PiiCounts`) and releases the document's `held` set. Flag and counts stay on `ChunkSets`.
- **Carry-over and re-flag**: when a new set of a cleared document is swapped in (new content, new pipeline version),
  it is covered by the clearance if no type that can flag has a count above the document's `PiiClearedCounts` value;
  otherwise the new set is flagged again (`Reflag`) and held. Accepted gap: one personnummer replaced by another keeps the count; keyed fingerprints
  are a P3 option (§18).
- **Event 5103 `rag.pii.flagged`** (Information; SIEM rule only for sets in a `public` collection with a personnummer)
  on every transition to flagged: `DocumentId`, `ChunkSetId`, `Classification`, `Types` (types that fired), `Counts`
  (numbers only), `SourceId`, `RunId`, `Reflag` (bool). Never values or text.

### 9.6 NER availability (fail-closed for `public` only)

- The `/v1/ner` call runs inside the job that reaches `personal-data` (6a §7.4); there is no separate job class or
  lane sizing. NER `busy`/`not_ready` requeue that job without an attempt; it resumes from the cached chunk output.
- If NER is not ready for more than 5 minutes (`/v1/info`), the stage completes with patterns only and `ner: pending`:
  the set is swapped in `held` (it can only be pending in a `public` collection, §9.3); the nightly drift sweep (6a
  §2.3) re-runs `personal-data` for `pending` sets once NER is ready. Sets in other collections never wait for NER.

### 9.7 Masking (P2)

`pii-mask` is a P2 stage after detection: replaces spans with `[personnummer]`, `[telefon]`, `[e-post]`, `[namn]` in
chunk text; it re-runs the patterns on its output and writes the **residual** counts. Pattern types masked to zero no
longer flag; masked names still need a clearance unless a platform setting accepts NER masking (its recall is below 1).
Masking never runs by default.

## 10. Parser endpoint `POST /v1/ner` (answered 6, 18)

The contract is parser design §7.8 (request `texts[]` with ids, `labels`, `min_score`; response entities with
`start`/`end`/`label`/`score`, model block, timings; limits ≤ 64 texts, each ≤ 4,000 characters, ≤ 100,000 characters
per request, deadline 15 s; errors as parser §7.5). Worker-side rules:

- Offsets are **Unicode code points** of the text as sent; the .NET client converts with `Rune` enumeration
  (surrogate pairs). Entity text is never echoed back, logged or traced.
- Only `person` counts. The parser maps the model's `PER` (and `PRS` if a revision renames it) to `person`; mixed labels
  `ORG/PRS`, `PRS/WRK`, `LOC/PRS` → `person_ambiguous`, not counted in P1 (measured on fixtures).
- Units are batched up to the limits; a set needing more requests sends them sequentially in the same job.
- Model, process and licence are the parser's (parser design §10.1, §12): `KBLab/bert-base-swedish-cased-ner` at the
  pinned revision in one warm `ner-0` process; model MIT, SUC 3.0 terms for derived weights *to verify* (§21).
  KBLab marks the model experimental (answered 18): P1 measures it (§17).

Swedish pitfalls (fixtures in §17):

| Pitfall | Handling |
|---|---|
| Decomposed å/ä/ö from PDFs (a + U+030A) | NFC in the worker before sending (§4.2) |
| Genitive `Annas`, hyphenated `Anna-Karin`, particles `von`, `af`, `de` | word-level aggregation in the parser keeps them in one entity; distinct counting folds the genitive `s` |
| Surnames that are nouns (`Berg`, `Ek`, `Lilja`, `Strand`, `Björn`) | context-dependent; measured, not patched |
| Names in places and streets (`Gustav Adolfs torg`, `Rådhusesplanaden`) | model gives `LOC` or `LOC/PRS`; mixed labels not counted |
| ALL-CAPS headings, names in tables without context | lower recall; rows carry their column names (§6.3) |
| OCR noise (`rn` → `m`) | lower recall on scanned pages; reported per strategy |

## 11. *Embed* (`embed`, `embed` class)

| Topic | Rule |
|---|---|
| Profile | the collection's `EmbeddingProfileId`, normally the `EmbeddingProfiles` row with `IsDefault` (`bge-m3`, 1024, cosine, `MaxInputTokens` 8192; data-model §2.1); other profiles only by platform admins (R3, `../../02-architecture-review.md:45-59`) |
| Input | the embedded string of §6.5 (prefix + body); no instruction prefix (BGE-M3 dense retrieval uses none, *to verify*); queries are embedded raw by the Knowledge API with the same NFC normalisation |
| Cache | per document: one `SELECT … WHERE "Classification" = @l AND "EmbeddingProfileId" = @p AND "TextHash" = ANY(@h)`; misses deduplicated, embedded, inserted `ON CONFLICT DO NOTHING` in their own short transaction (6a §7.6); `LastUsedAt` touched at most once a day per row; unused 30 days → deleted (platform plan §8). Dify's cached embedding is the precedent (`../../platforms/dify.md:218`) |
| Batching | ≤ 64 inputs and ≤ 16,384 tokens per request (`TokenCount` sum; gateway limit *to verify*), all misses of one document |
| Request | `POST /v1/embeddings` `{model: "bge-m3", input: [...], encoding_format: "float"}`; headers by the engine (6a §7.7): `x-ume-on-behalf-of` = owner team of the collection (from gateway release 2, P2; weakness review §5), `x-ume-max-residency: OnPrem` always (the worker's `OnPrem` key), `x-ume-run-id`, `x-ume-step: embed` |
| Response checks | count equals inputs; each vector 1024 finite floats (else fatal `embed.bad_vector`); `x-ume-residency` ≤ `OnPrem` (else fatal, 5003); stored as returned (cosine is scale-invariant) |
| Canary | at startup and hourly the worker embeds a fixed Swedish canary text and compares it with `EmbeddingProfiles.CanaryEmbedding`: cosine < 0.999 → the `embed` class stops on this instance, health `unhealthy`, alert: the gateway alias now serves another model, which would silently mix vector spaces |
| Concurrency | P1: fixed `Worker:Concurrency:embed` from configuration. **P2** (weakness review §1): AIMD of 6a §18: baseline = lowest per-minute p95 of the last 24 h (floor 200 ms); above 2× baseline halve, else +1 per minute up to `Worker:Concurrency:embed` (deployment, 6a §9.1) |
| Chat protection at the gateway | the worker's own key (the pilot team's `OnPrem` key until gateway release 2, then a delegating key) with the gateway's existing `MaxConcurrentRequests` cap (429 + `Retry-After`, honoured), so a misconfigured worker cannot starve chat's query embeddings. A separate BGE-M3 query route only when the performance pass's §6 trigger fires ([`../performance-pass.md`](../performance-pass.md) §3.9, §6) |
| Failures | 6a §7.3 table; a 400 for one input fails the document (`embed.input_rejected`, a bug: 400 tokens ≪ 8192); partial results of a failed batch are discarded and the batch retried whole |

**Profile migration (P3 outline).** New profile row; `rag_ensure_profile_index` (data-model §3.2); a `reprocess` per
affected source builds invisible sets keyed by the new profile id from cached chunk outputs (embedding is the cost); the
evaluation compares both profiles on the full collection (§14.5); the swap sets `Collections.EmbeddingProfileId` and
swaps sets per document; old sets kept 14 days; the Knowledge API embeds queries per profile while both exist
(knowledge plan §3).

## 12. Noise-only content changes (6b §12.2 open item)

**Decision: a noise-only change creates no new chunk set.**

A new `ContentHash` whose extracted text is unchanged (6b counts it as `rag.connector.change.noise`) would otherwise
create a new chunk set: identical chunks rewritten, as many HNSW and GIN inserts, the old set superseded, for nothing; a
noisy site would churn its whole index.

1. `ChunkSets.InputHash` = `OutputHash` of the *Clean* output the set was built from (the chunk stage's input, 6a §8).
2. In the fenced segment commit, when the live set has the same pipeline version, site-profile version and profile
   and an `InputHash` equal to the new *Clean* `OutputHash`, the set is **re-keyed** (6a §14 rule 4):
   `ContentHash = @new` in one row. No `embed` job is enqueued; the run item is `unchanged`, reason `change.noise`.
3. This only works if `OutputHash` covers content, not fetch circumstances: the hash of a `ParsedDocument` covers
   blocks, tables, pages with signals, title, language and breadcrumb, and **excludes** volatile facts (fetch time,
   timings, parser build) (6a §8).
4. `SourceModifiedAt` of the chunks is not touched: noise is not a modification. The chunks are the same, so the
   personal-data state stays valid.
5. The same rule serves files whose bytes change without content change (re-exported PDF): they are re-parsed
   (raw-byte hash) but not re-chunked or re-embedded.

## 13. Evaluation sets

### 13.1 Generation (`eval` job `generate-set`, `top-up-set`)

| Step | Rule |
|---|---|
| When | after a collection's first sync runs finish; weekly top-up check (scheduler, 6a §9.6); on demand from the admin |
| Size (answered 20) | target `N = min(100, 3 × D, eligible chunks)` for `D` live documents; over-generate 1.6 × N to survive filtering |
| Eligible chunks | live sets of the collection; body ≥ 50 tokens; not from PII-flagged sets (keeps personal data out of stored questions); not `quality.largeTable` rows beyond the first group |
| Stratification | by document: round-robin over documents in seeded random order, ≤ `⌈N/D⌉ + 1` per document; by document type: proportional to chunk share, each type with ≥ 5 % of chunks gets ≥ 2; by kind: table row groups and lists at least their share, min(5, available) |
| Seed | `SHA-256(setId ‖ round)`; the sample is reproducible |
| Model | `Settings.EvaluationModel` (on-prem chat model in the gateway catalogue; must be a `GatewayModels` row servable `OnPrem`, admin design §4.10) with ceiling `OnPrem`; `x-ume-step: eval-generate`; temperature 0.3; JSON output |

Prompt outline (Swedish, versioned as `PromptVersion`):

```
System: Du skriver testfrågor för en sökfunktion hos Umeå kommun.
Användare: Dokument: <titel>. Avsnitt: <rubrikväg>. Text: <chunktext>.
Skriv en fråga som en invånare eller medarbetare skulle ställa och som texten besvarar.
Regler: fristående (inga "texten", "dokumentet", "ovan"), 6–25 ord, på svenska, citera inte texten ordagrant,
inga personuppgifter. Svara med JSON: {"fraga": "...", "svar": "...", "typ": "fakta|procedur|definition|tabell"}.
```

Questions are Swedish whatever the document language (BGE-M3 is cross-lingual). **Filtering** (cheap first):

1. Rules: Swedish (§2 detector), 6–25 words, ends with `?`, no deictic phrases (`texten`, `dokumentet`, `ovan`,
   `nedan`, `detta avsnitt`), no pattern hit from §9.1.
2. **Answer leakage**: the normalised expected answer is not a substring of the question; share of the question's
   content words found verbatim in the chunk ≤ 0.8.
3. Judge (`Settings.EvaluationJudgeModel`, default the same model, temperature 0, `x-ume-step: eval-judge`): rates
   *answerable from the text*, *understandable without the text*, *specific to this content* on 1–5; keep only all ≥ 4.
4. Near-duplicates: BGE-M3 cosine ≥ 0.92 between kept questions → keep one.
5. Never filter on whether retrieval finds the document: that would bias the set towards the current pipeline.

Expected documents: the source chunk's document plus every live document of the collection with a chunk of the same
`TextHash` (duplicated pages). Stored: `Question`, `ExpectedAnswer`, `ExpectedDocumentIds`, `SourceChunkId`
(provenance only), `SourceContentHash`, `Origin` (model, prompt version, seed, judge scores), `Strata` (§19).

**Cost** per kept question ≈ 2,000 on-prem tokens (generation 1.6 × ≈ 880, judge ≈ 620): a full set ≈ 200,000 tokens,
charged to the collection's owner team; shown before generation.

### 13.2 Why documents, not chunks

- Chunk ids and boundaries change with every pipeline version; a chunk-keyed set would be invalidated by exactly the
  change a publish report must judge, and would penalise a better chunker for cutting differently.
- Users and citations work per document (knowledge plan §2); "the right document is in the top 10" is what an
  assistant needs to answer.
- Duplicates across documents are expressible (`ExpectedDocumentIds` is an array).
- Cost: long documents make document recall easier. Mitigation: per-question `ExpectedRank` and MRR show position.

### 13.3 Regeneration as content changes

- A question becomes **stale** when an expected document is deleted, or its content hash differs from
  `SourceContentHash` and a judge call on that document's best-matching chunk no longer rates it answerable. Only
  changed documents are re-checked.
- Weekly top-up: retire stale questions (`RetiredAt`), regenerate to the target size with a new round seed; also when
  `D` grew by ≥ 25 % since the last round. Retired questions keep their results for history.
- An evaluation run uses the set as it was when the run started; a top-up never runs during a run of the collection.

### 13.4 Human and other layers

- **P1**: editors add questions with expected documents and edit or retire generated ones (`edit-eval-question`
  command, 6a §9.1); `Layer = human`. Every run includes all human questions.
- **Pilot gold set** (P1a, before go-live; [weakness review §2](../weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch)): 100–150 answerable real questions
  (expected source URL(s) as `ExpectedDocumentIds`, the facts a correct answer contains as `ExpectedAnswer`), 30–50
  unanswerable off-topic and near-miss questions (no expected documents; expected outcome "not found") and at least
  10 temporal questions where an outdated document competes (they check the age decay and `demote` patterns of
  [weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered): the current document
  ranks above the outdated one). Stored as `human` questions through the same command
  (in P1a through Scalar, no journey pages); owned by the pilot department with the platform team; the start of
  register item B1. Unanswerable questions do not count in recall@10 and MRR@10; for them the run records the coverage
  status, best distance and best keyword score (§13.6).
- **P1a three-way run** (weakness review §2): an `on-demand` run of the gold set in three variants: vector-only (mode
  `vector`), hybrid, and hybrid without the decompounder on the query side. The result is recorded; hybrid and the
  decompounder stay only if they beat vector-only.
- **P2**: questions rewritten from flagged chat feedback (plan §7: only the rewritten, personal-data-free question is
  kept, with the collection's classification); skill test questions run by the skill gate in `rag-admin` as the
  tools design specifies.
- Results are reported per layer, and **reports lead with the gold-set (`human`) results**; the generated layer is a
  regression signal only (weakness review §2).

### 13.5 Running retrieval as the Knowledge API does

- Shared project **`Ume.Rag.Retrieval`** (used by `Ume.Rag.Api`, the worker and the admin test panel): runs the
  keyword and vector candidate statements (knowledge design §6.2) with the collection's `RetrievalSettings` and
  profile literal, the same `SwedishDecompounder` on the query, then fuses in .NET (RRF, then the age decay and
  `demote` multipliers, weakness review §3) and applies the same dedup by
  `(DocumentId, TextHash)`, variant collapse and per-document cap (default 3) as the API (knowledge design §6.5, §8);
  `k = 10`; mode hybrid; no filters; `expand=none`.
- Scope: the collection over its live sets (`CollectionId = ANY($scope)` and `DeletedAt IS NULL`, as the API; no
  chunk-set scope), following the API's exact/HNSW rule (≤ 10,000 chunks exact, knowledge design §6.4).
- Query embedding through the same embedding client and normalisation as the API, cached in `EmbeddingCache` by the
  question's hash.
- Runs in the worker on the ingestion pool (R1: the data plane never pays for evaluation).
- Contract test: evaluator and `POST /v1/retrieve` return the same ids in the same order.

### 13.6 Metrics

Per question: `ExpectedRank` = rank of the first result whose `DocumentId` ∈ `ExpectedDocumentIds` (null if not in the
top 10). `recall@10` = share with a rank, reported with its counts (`found / questions`); `MRR@10` = mean of `1/rank`
(0 without). Comparison with the previous run of the collection: questions won, lost and net, listed so an editor can
open the lost ones. No intervals or significance tests; a blocking gate is reconsidered only when measured run-to-run
variance shows the numbers can tell versions apart (§14.2). With retrieval deterministic for a fixed index, a re-run
reproduces every number (test, §17).

Coverage (weakness review §2): per question the run also records the coverage status, best distance and best keyword
score (knowledge design §7). Over `human` questions the report shows false not-found (answerable questions with `low`)
and not-found on unanswerable questions, and proposes `coverageMaxDistance` and `coverageMinKeywordScore` as the
values that separate answerable from unanswerable questions best. These are retrieval-level numbers; the go-live gate
(`../README.md`) is graded by people on the answers.

## 14. Evaluation runs (advisory)

```csharp
public interface IEvaluator
{
    Task<EvaluationRunResult> RunAsync(EvaluationSubject subject, CancellationToken ct);  // full collection
}
public sealed record EvaluationSubject(EvaluationKind Kind, Guid CollectionId, Guid? PipelineVersionId,
    JsonElement? CandidateSettings);
public enum EvaluationKind { PublishReport, Nightly, OnDemand, Answers }  // worker: the first three; rag-admin: Answers (§14.8)
```

### 14.1 Publish report

After the `reprocess` run of a publish or rollback finishes (6a §10.4), the `eval` job `publish-report` runs the
collection's full set on the **full collection** with its real index and real query path, and compares with the last
run before the publish. The admin shows recall@10 and MRR with counts, the change, and the lost questions next to the
run; the user keeps the version or rolls back in one click (6a §12). A collection with fewer than 10 questions shows
"too few questions to compare".

### 14.2 Reading the comparison

The report advises; it never blocks. One question in a 50-question set is 2 points, so the admin shows counts and the
lost questions rather than a verdict. The nightly runs give the run-to-run variance of an unchanged collection; when
it is measured, a gate can be reconsidered (simplification pass §8).

### 14.3 New collections

A collection goes live when its owner publishes it. Its first evaluation runs after its first syncs (set generation,
then a run); the result is shown on the collection page as an absolute number with counts, with no floor.

### 14.4 Overrides

Removed (simplification pass): nothing blocks, so nothing needs an override.

### 14.5 Retrieval-setting and embedding-profile changes

| Change | Run | Shown |
|---|---|---|
| `Collections.RetrievalSettings` (`vectorWeight`, `keywordWeight`, `candidates`, `maxChunksPerDocument`, `recency` (weakness review §3); knowledge design §12.1) | `on-demand` with `CandidateSettings` before saving, full set on the full collection (cheap: no rebuild) | current vs candidate, advisory |
| Embedding profile (P3) | full collection under each profile after the background re-embed | old vs new profile, advisory |
| Coverage thresholds (`coverageMaxDistance`, `coverageMinKeywordScore`) or `expandMaxTokens` only | none | does not change ranking |

### 14.6 Parse-profile auto-apply

Removed (simplification pass): parse strategies are not learned automatically (§3.5). Site-profile checks are 6b's.

### 14.7 Timing and cost

| Run | Work | Typical time |
|---|---|---|
| Publish report, nightly | ≤ 100 queries on the full collection | < 1 min |
| On-demand settings comparison | 2 × 100 queries | < 1 min |
| Set generation | ≈ 200k on-prem LLM tokens | model-bound, ≈ 10–20 min |
| Answer-level run (P2, weakness review §1; `rag-admin`) | ≤ 100 answers + judge calls on-prem | model-bound, minutes |

The re-processing itself is forecast by the publish plan (6a §12.2); the `eval` class (concurrency 1) runs only
generation and measuring.

### 14.8 Nightly runs

- **Retrieval**: nightly per published collection: full set, kind `nightly`; feeds the trend on the admin
  evaluation page and `rag.evaluation.recall_at_10` (repo-and-deployment §9). Alert (admin overview) when recall@10
  falls 5 points below the 4-week median. Onyx found an ingestion bug only through poor evaluation scores
  (`../../platforms/onyx.md:322`); this makes that routine.
- **Answers** (P2, weakness review §1): nightly per assistant, the evaluation sets of its collections are answered through
  `Ume.Rag.Answering` with the assistant's settings, and an on-prem judge (`Settings.EvaluationJudgeModel`, ceiling
  `OnPrem`, `x-ume-step: eval-answer`) rates **faithfulness** (every claim supported by the cited chunks) and
  **citation precision** (cited chunks support the claims); results per assistant with counts. The `uncited_answer`
  rate from live chat is shown next to them (chat design). Run kind `answers`; it runs in **`rag-admin`**, which
  already runs `Ume.Rag.Answering` for the test panel and holds the gateway keys of every level. The worker runs only
  retrieval evaluation (through `Ume.Rag.Retrieval`) and references neither `Ume.Rag.Answering` nor `Ume.Rag.Tools`.

## 15. Observability

Meter `Ume.Rag` (6a §17), low-cardinality dimensions only:

| Metric | Type | Dimensions |
|---|---|---|
| `rag.parse.pages` | counter | strategy (`text-layer`, `docling-text`, `docling-tables`, `docling-ocr`, `docling-ocr-force`, `rerender`, `blank`, `missing`) |
| `rag.parse.duration_per_page` (existing) | histogram | strategy |
| `rag.parse.range_size` | histogram | strategy |
| `rag.html.extracted` | counter | outcome (`profile`, `preset`, `fallback`, `empty-root`) |
| `rag.clean.dropped_blocks` | counter | rule |
| `rag.chunk.count`, `rag.chunk.tokens` | histogram | document type |
| `rag.pii.sets` | counter | outcome (`clean`, `flagged`, `reflagged`, `held`, `ner_pending`) |
| `rag.pii.ner.calls`, `rag.pii.ner.units` | counter, counter | –, `sent`/`prefiltered` |
| `rag.embed.cache` | counter | `hit`/`miss` |
| `rag.embed.canary_similarity` | gauge | profile |
| `rag.chunkset.rekeyed` | counter | reason (`noise`, `version`) |
| `rag.evaluation.recall_at_10` (existing), `rag.evaluation.runs` | gauge, counter | collection; kind |
| `rag.evaluation.llm_tokens` | counter | step (`generate`, `judge`, `answer`) |

Traces: stage spans (6a §17) gain `strategy`, `ranges`, `pii.types_fired`, `ner.units`, `embed.cache_hits`; never
text, names or evaluation questions (content: shown only to roles that may see the collection, never logged).

## 16. Performance (R1: 10,000 pages/hour)

Per typical web page (≈ 600 words, ≈ 8 chunks), planning numbers *to measure* in the R1 test:

| Stage | Worker CPU | Other resource | Note |
|---|---|---|---|
| *Detect* | ≈ 1 ms | – | HTML: no inspect call |
| *HTML extraction*, *Clean* | in 6a's 80 ms | – | – |
| *Split*, *Chunk*, decompounder | ≈ 5–15 ms | – | tokenizer dominates; `CountTokens` on spans |
| *Personal data*, patterns | ≈ 2 ms | – | compiled regex, source-generated |
| *Personal data*, NER | ≈ 2 ms (client) when gated in | ≈ 0.3–1.0 s of the parser NER process for a list-like page | only list-like pages of `public` collections; BERT-base ≈ 0.17 GFLOP per token |
| *Embed* | ≈ 2 ms | ≈ 3,600 tokens on the gateway's BGE-M3 | the bottleneck to verify (6a §23) |

Parser-bound documents add inspect (≈ 20 ms per PDF) and conversion (parser §14); umea.se has more PDFs than HTML
pages, so the parser path dominates its first full crawl. Evaluation uses the ingestion pool.

## 17. Tests

| Kind | Project / location | Content |
|---|---|---|
| Golden per stage and version | `Ume.Rag.Worker.Tests`, `tests/fixtures/processing/` | input → expected output for `detect`, parse planning (signals → plan), HTML extraction (presets and profiles), `plain-text`, `csv-table`, `clean`, split, `chunk` (incl. breadcrumb prefix and `SearchText`), metadata, quality facts, `personal-data` (with a fake NER client); a changed output without a version bump fails (6a §19) |
| umea.se acceptance (P1) | `tests/fixtures/umea/` (≈ 20 saved pages, HTML and PDF) | the example page (simplification pass §13.2) extracts to exactly its main text without share widget, short-URL line and child-link list, breadcrumb "Kommun och politik › Kommunens organisation › Förvaltningar och verksamheter › Teknik och fastighet" (path *to verify* on the live page), date 2026-01-15; the set is a regression corpus |
| Tokenizer parity | same | 1,000 passages, counts equal to the Hugging Face tokenizer (§6.1) |
| Swedish fixture corpus | `tests/fixtures/processing/` (public or synthetic, `MANIFEST.csv` as parser §15.3) | umea.se pages with deep navigation and contact blocks; a protokoll with `§` items and justering block; föreskrifter with `kap.`/`§`/`Bilaga`; a taxa with fee tables across pages; a report with TOC, footnotes and `1.2.3` numbering; hyphenation and suspended compounds; a PDF whose layout headings have wrong levels; a page with only headings; a 300-row XLSX; a two-column newsletter |
| Planner | `Ume.Rag.Worker.Tests` | every §3.1 rule at and around its thresholds; range sizing with fake timings; halving and missing-page outcomes; `Strategy` string grammar and truncation |
| PII precision/recall | `tests/fixtures/pii/` (synthetic only) | generated Swedish texts with Skatteverket test personnummer and samordningsnummer, organisationsnummer, OCR references, bankgiro, case numbers, phone numbers in every §9.1 format, obfuscated emails, names from SCB statistics in running text, tables, all caps, genitive and hyphenated forms; **targets**: personnummer/samordningsnummer precision and recall ≥ 0.99, email ≥ 0.99, phone ≥ 0.95, names (NER) recall ≥ 0.85 and precision ≥ 0.80; the NER model is accepted for P1 only if it reaches them (answered 18) |
| NER gating and client | `Ume.Rag.Worker.Tests`, `Ume.Rag.ContractTests` | NER called only for `public` + not flagged + ≥ 5 contact values; offsets with emoji and combining characters (code points vs UTF-16); label mapping |
| Flag lifecycle | `Ume.Rag.IntegrationTests` | flagged set in a `public` collection swaps in `held` and the old content stops being served; clear → `live` without re-processing; clearance carried to the next set at equal counts, re-flag on a higher count; NER down → `pending`, `held`, sweep completes it; non-`public` collections never wait; 5103 emitted once per transition |
| Noise | `Ume.Rag.IntegrationTests` | noise-only HTML change → set re-keyed, zero chunk inserts, `EnsureCurrent` satisfied; real change → new set |
| Evaluation reproducibility | `Ume.Rag.IntegrationTests` with FakeGateway (deterministic embeddings and LLM answers) | same seed → same sample, questions and metrics; a re-run gives identical results; evaluator vs `/v1/retrieve` identical order; win/loss counts against a previous run |

## 18. Milestones

| Phase | Deliverable |
|---|---|
| P1 | *Detect* with inspect; parse routing, per-page rules, ranges (fixed sizes from configuration), joining, re-render; built-in and admin parse rules, observations; HTML extraction with site profiles and presets (Sitevision first), breadcrumb, date, description and keywords; text and CSV parsers; *Clean*; split with re-levelling; *Chunk* with the BGE-M3 tokenizer, breadcrumb prefix, row groups, `SearchText`; metadata; quality facts; *Personal data* with patterns, gated `/v1/ner`, flag rules, `held` sets, clear command, carry-over and re-flag, 5103; *Embed* with cache and canary; noise re-key; generated sets with filtering; human questions (P1a: the pilot gold set, its three-way run and the coverage-threshold proposal, weakness review §2); `Ume.Rag.Retrieval`; publish reports, nightly runs, on-demand settings comparison; umea.se acceptance corpus; tests of §17 |
| P2 | from weakness review §1: answer-level nightly run (in `rag-admin`), range sizing by EWMA (§3.3), AIMD embed concurrency (§11); parse-rule proposals from observations (approval required); `pii-mask`; feedback-derived questions; parent-child chunks only if §5.3's condition is met; `halfvec` evaluation (data-model §12.16); legacy Office, RTF and email (with child documents); describe pictures; WordPress and Episerver/Optimizely presets |
| P3 | embedding-profile migration tooling; keyed PII fingerprints if count-based re-flagging proves too weak |

## 19. Changes to other documents

Simplification pass (2026-10-30); owners of these documents apply them. Rows of the step 6c review that still hold were
applied then and are not repeated.

| Document | Change |
|---|---|
| `parser/design.md` §6.1, §8 | the clean-text-layer strategy is the parser's `text-layer` fast path (pypdfium2), requested per range like the Docling strategies; ranges are all parser outputs; the worker no longer merges PdfPig pages |
| `parser/design.md` §7.8, §10.1, §13.1 | `/v1/ner` unchanged as a contract; `NER_LANES` sizing and `capacity.ner_slots`-driven worker concurrency removed (one NER process; calls are rare); `ner_ready` stays in `/v1/info` |
| `platform/data-model.md` §2.2 `ChunkSets` | `PiiCounts jsonb`, `PiiFlagged`/`PiiFlaggedAt` move from `Documents` to `ChunkSets`; the clearance (`PiiClearedAt`, `PiiClearedBy`, `PiiClearedCounts`) stays on `Documents` (a decision on the document); `State` `writing`/`live`/`held`/`superseded`; `InputHash` = *Clean* `OutputHash`; key column `SiteProfileVersionId uuid NULL` |
| `platform/data-model.md` §2.2 `Chunks`, `Documents` | `Chunks.SearchText text` (worker-computed, §6.5) and `SearchVector` over it with built-in `swedish`; `Documents.Description text NULL`, `Documents.Keywords text[] NULL`, `Documents.SiteProfileVersionId` (the last extraction) |
| `platform/data-model.md` §2.2 profiles, evaluation | `ParseProfiles.Origin` `builtin`/`admin` with versions; `EvaluationRuns`: `Kind` (`publish-report`/`nightly`/`on-demand`/`answers`), `QuestionCount`, `Found`, `Wins`, `Losses`, `ComparedRunId`, `Faithfulness`, `CitationPrecision`; no statistics, decision or override columns: drop `McNemarP`, CI columns, `Decision`, override columns, `Scope = sub-corpus`; `Collections.PublishedAt` stays as the owner's publish, `PublishGateRunId` and `Settings.GateFloor`/`GateSampleSize` removed |
| `platform/data-model.md` §4.1 | `Jobs.Class` CHECK without `ner` |
| `platform/data-model.md` §2.2 evaluation (weakness review §2) | `EvaluationResults` records per question the coverage status, best distance and best keyword score (§13.6); `EvaluationRuns` records the retrieval variant of an `on-demand` run (`hybrid`, `vector`, `hybrid-no-decompounder`) for the three-way run (§13.4) |
| `platform/repo-and-deployment.md` §1, §2 | `SwedishDecompounder` (in `Ume.Rag.Retrieval` or `Ume.Rag.Text`) used by the worker; the worker references neither `Ume.Rag.Answering` nor `Ume.Rag.Tools` (answer-level run and skill gate in `rag-admin`); `tests/fixtures/umea/` |
| `ingestion-worker/design-connectors.md` | site profiles: storage, versions, learning, auto-apply check (uses 6c §3.7 extraction in preview mode), commands `approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile`, re-learning trigger from `quality.boilerplateShare`/`emptyRoot` (§8); reliability of `lastmod` feeds §7's date order |
| `knowledge-api/*` | scope by scalar `CollectionId` and live sets only; query side runs `SwedishDecompounder`; citations may show `Documents.Description`; no `building` collections |
| `admin/plan.md` (step 10) | PII list per chunk set with highlight preview and clear (reason); publish screen with the advisory report (counts, wins/losses, lost questions) and one-click rollback; evaluation page: layers, stale questions, generation cost, answer-level results; parse rules in the generic platform-admin form |
| `identity/design.md` §10 | 5103 adds `ChunkSetId`; audit action `gate.override` removed |
| `platform/plan.md` §9 | evaluation advisory; collections go live when published |

## 20. Answered questions (2026-10-29)

1. **Flag rules**: option (a): personnummer, samordningsnummer and external email always; names, phones and own-domain
   emails only together in list-like documents (§9.4).
2. **NER capacity**: option (a), revised 2026-10-30: names are checked only when they can change the flag (§9.3);
   such sets stay out of the `public` collection until checked (§9.6).
3. **Evaluation models**: default the gateway's on-prem general chat model for generation and judging (settings).
4. **Collection go-live**: revised 2026-10-30: a collection goes live when its owner publishes it; no floor (§14.3).
5. **Chunking conflict** with knowledge answered 3 (raised in review): flat chunks with section expansion at query time
   in the knowledge API.

## 21. To verify

- `Microsoft.ML.Tokenizers` 2.0.1: count parity with Hugging Face for BGE-M3 (Unigram with `nmt_nfkc` precompiled
  charsmap), whitespace and dummy-prefix options to match XLM-R, thread safety of one shared instance, speed on
  5,000-character pages.
- KB NER: actual labels returned for persons with the pinned revision (`PER` in `config.json`, `PRS` in the card);
  quality on our fixtures; SUC 3.0 terms for derived weights.
- Gateway embeddings: maximum inputs and tokens per request for the on-prem BGE-M3 provider; whether BGE-M3 is served
  without instruction prefixes; throughput (6a §23).
- The parser's `text-layer` fast path output: block shape, heading levels and whether it labels furniture (§4.2).
- SmartReader as fallback for hosts without a profile; the Sitevision preset against the ≈ 20 saved umea.se pages;
  the example page's breadcrumb path on the live page.
- Skatteverket's published test personnummer and samordningsnummer (format, open-data licence) and SCB name
  statistics (licence) for fixtures and the gazetteer.
- Docling `timings` granularity for per-page milliseconds (range sizing falls back to `docling_ms / pages`).
- `SwedishDecompounder` output for `SearchText` vs the query side (same parts for the same word), and the P0
  comparison with Postgres `ispell` (simplification pass §3).
- On-prem LLM through the gateway: JSON output mode, and that judge scores at temperature 0 are stable enough for
  reproducible filtering; run-to-run variance of nightly runs on an unchanged collection (input to any future gate).
- Cost per assistant of the answer-level nightly run in `rag-admin`.
- All planning numbers of §14.7 and §16, measured in the R1 test.

## 22. Deferred or removed (simplification pass)

See [`../simplification-pass.md`](../simplification-pass.md).

| Mechanism | Where it went |
|---|---|
| `pdf-fast-path` (PdfPig) and PdfPig/Docling page merging | dropped; all PDFs go to the parser, `text-layer` strategy (§3.1, §4.1) |
| HTML fallback to the parser's HTML backend; render branch; `degraded` for short pages | dropped; renderer P2 (6b); short pages are an info note (§8) |
| Legacy Office, RTF, email and child documents | P2 (§3.2) |
| *Quality check* as a routing node | dropped; quality facts only (§8) |
| Parse-profile learning with auto-apply, trial runs, confidence threshold, automatic rollback, kill switch; try-and-score | dropped; built-in and admin parse rules, P2 proposals with approval (§3.5) |
| `ner` job class, NER-lane sizing (`NER_LANES`, `capacity.ner_slots` concurrency), NER result LRU, NER for every document | dropped; gated NER inside the processing job (§9.3, §9.6) |
| Personal-data state on `Documents`, per-type maximum across pipelines, membership recompute on flag | replaced by state on the chunk set and `held` sets (§9.4, §9.5) |
| Publish gate (`IPublishGate`), sub-corpus shadow sets, escalation 50 → 100, Wilson/bootstrap/McNemar, decision rule, `building` state and 0.70 floor, platform-admin override | dropped; advisory harness on the full collection (§13.6, §14) |
| Skill gate details (retrieval and routing layers) | tools design (simplification pass §10) |
| Fleet-wide Redis embedding cap, scale batching, bulk embed share | P2, first multi-host deployment ([`../performance-pass.md`](../performance-pass.md) §3.9) |
| Answer faithfulness as a P3 assistant layer | moved earlier: nightly answer-level run from late P1 (§14.8) |
| `ChunkSets` evaluation scope with exact search | dropped with the gate; collection scope only (§13.5) |

## 23. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Mechanism | Where it went |
|---|---|
| Nightly answer-level judge (`answers` runs, §14.8) | P2 |
| Range sizing from measured time per page (EWMA, §3.3) | P2; fixed range sizes per strategy from configuration in P1 |
| AIMD embedding concurrency (§11) | P2; fixed `Worker:Concurrency:embed` in P1 |
| Site-profile learning and its check (§3.7) | P2 (6b §24); P1 extraction uses the manual profile |
