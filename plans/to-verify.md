# To verify: package, behaviour and estimate checks

One register of the "To verify" items of every detail document (collected in the consistency pass, detail step 11).
The per-document "To verify" sections stay the source with the full context; this file is the index to plan and track
them. When an item is resolved, record the result in the document's section and strike the row here. Version checks
follow postponed decision V1 and the 7-day package rule (`platform/repo-and-deployment.md` §12.12); licences feed
postponed decision P2 ([`postponed-decisions.md`](postponed-decisions.md)).

Phases: P0 foundation, P1 MVP/pilot (the one combined gateway release happens in P1), P2 broaden, P3 scale (README). *How*:
**test** (a unit, integration or manual test that proves the behaviour), **source** (read the package's source, release
notes or documentation at the pinned version), **measure** (benchmark, R1 release test or pilot data), **ask** (a person
or team named in the row).

Document names are relative to `plans/`; "gateway design" is `gateway-changes/design.md`. Rows whose subject the
simplification pass ([`simplification-pass.md`](simplification-pass.md)) removed are struck with "dropped
(simplification pass)"; its new checks are rows 127–140, the follow-up round of that pass added rows 141–160, the container cut (§14
there: six containers, no Redis in P1, pgBackRest inside the Postgres image) added rows 161–163, and many assistants per
department (§15 there: assistant versions, test questions, derived mode) added rows 164–166.

## Postgres and pgvector

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| ~~1~~ | ~~`shared_ispell` on Postgres 17 as the fallback for Hunspell memory per backend~~ dropped (simplification pass): no dictionary in Postgres | postponed H1 (closed) | – | – |
| 2 | `ALTER ROLE … SET hnsw.*` (`ef_search`, `iterative_scan`, `max_scan_tuples` 100,000) works before pgvector is loaded, so `app-roles.sql` may set the role defaults; visible via `SHOW` on each role's connections | `knowledge-api/design.md` §6.4, §20; `platform/data-model.md` §1.1, §14 | P0 | test |
| ~~3~~ | ~~`plainto_tsquery('content.rag_swedish', …)::text` shape when Hunspell returns several lexemes for one compound~~ dropped (simplification pass): the keyword query is built from lexeme arrays | `knowledge-api/design.md` §6.2 | – | – |
| 4 | Planner: the exact variant uses the scalar `CollectionId` B-tree, never HNSW; K and V use the partial indexes under generic plans | `knowledge-api/design.md` §16.2 | P1 | test (`EXPLAIN` in integration tests) |
| 5 | Keyword statement K cost for frequent terms with OR semantics; stop list if > 30 ms p95 | `knowledge-api/design.md` §6.2, §11 | P1 | measure (retrieval load test) |
| 6 | Exact-scan cost at 10,000 TOASTed `vector(1024)` values within 25 ms p95; else lower `Settings.Retrieval.exactScanMaxChunks` or move to `halfvec` | `knowledge-api/design.md` §6.4, §20 | P1 | measure |
| 7 | `starts_with` filters need no index | `knowledge-api/design.md` §6.3 | P1 | test |
| ~~8~~ | ~~Chunk-set scope `ChunkSetId = ANY(…)` for a 2,000-chunk gate sub-corpus~~ dropped (simplification pass): evaluation runs on the full collection | `ingestion-worker/design-processing.md` §21 | – | – |
| 9 | Recursive-CTE loose index scan over `IX_Jobs_Ready` on the unpartitioned `Jobs` table (per-department fairness) | `ingestion-worker/design-engine.md` §9.3 | P2 (first multi-host deployment) | test |
| 10 | Dedup by the partial unique index on `DedupKey WHERE State IN ('queued','running')` under 500-row batch enqueues (`ON CONFLICT` inference with the index predicate) | `ingestion-worker/design-engine.md` §23; `simplification-pass.md` §9 | P1 | test |
| 11 | CloudNativePG image requirements with pgvector (its own images or the stock pgvector image) | `platform/repo-and-deployment.md` §10 | P3 | source |
| 127 | Stock `pgvector/pgvector` image tag and digest for pgvector 0.8.7 on PostgreSQL 17 (7-day rule); its user id; ships `pg_stat_statements` | `simplification-pass.md` §3; `platform/data-model.md` §14; `platform/repo-and-deployment.md` §5.1, §14; postponed V1 | P0 | source + test (`deploy/postgres/test.sh`) |
| 128 | .NET `SwedishDecompounder` vs Postgres `ispell` on a word sample: recall difference of compound parts; same parts for `SearchText` and the query side; one integration test that a part matches through the built-in `swedish` configuration | `simplification-pass.md` §3; `platform/data-model.md` §14; `ingestion-worker/design-processing.md` §21 | P0 | measure + test |
| 129 | pgBackRest WAL archiving (`archive_timeout` 60 s) and weekly full / daily differential backups from the host timer (`docker compose exec postgres pgbackrest backup`); point-in-time restore drill meets RPO ≤ 5 min and RTO ≤ 4 h on the test host | `simplification-pass.md` §4, §14; `platform/repo-and-deployment.md` §7.4, §14 | P0 | test + measure |
| 161 | `ume-rag/postgres` (stock pgvector image + PGDG `pgbackrest`): the package installs on the stock base; synchronous `archive-push` from inside the container on a read-only root file system (no spool volume); restore in a one-off container of the image; how the backup timer's result reaches `rag.backup.age` | `simplification-pass.md` §14; `platform/repo-and-deployment.md` §5.2, §7.4, §14 | P0 | test (`deploy/postgres/test.sh`, test host) |
| 130 | Managed Postgres (Azure Database for PostgreSQL) with pgvector ≥ 0.8 and the needed settings (`shared_preload_libraries`, HNSW settings, `performance-pass.md` §3.2) | `simplification-pass.md` §3; `performance-pass.md` §3.2 | P2 | source |
| 141 | Swap transaction: flipping `DeletedAt` on a set's chunks is a non-HOT update that inserts every newly visible chunk into the partial HNSW and GIN indexes inside the swap; duration and WAL p95 per document for sets of 50–500 chunks during a large publish beside chat traffic, and for the largest sets (20,000 chunks) | `platform/data-model.md` §14; `ingestion-worker/design-engine.md` §23 | P1 | measure |
| 142 | `content.rag_or_query` and the `rag_tsquery_or_agg` aggregate over the built-in `tsquery_or` on the stock image: an OR query, `NULL` for stop words only; `plainto_tsquery('simple', lexeme)` accepts every lexeme the `swedish` parser emits (hosts, URLs, paths, hyphens, `'`, `\`), fuzzed over the test corpus | `platform/data-model.md` §14; `knowledge-api/design.md` §6.2, §20 | P0 | test |
| 143 | `coverageMinKeywordScore` default: `ts_rank_cd` of on-topic vs off-topic questions on the pilot collections, calibrated from the coverage log events of the first pilot weeks | `knowledge-api/design.md` §7, §20 | P1 | measure (pilot data) |
| 144 | Chat bucket-key creation race: one `DataKeys` row per (level, expiry month) under concurrent first writes | `platform/data-model.md` §14 | P1 | test |

## .NET, Npgsql, EF Core and NuGet packages

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 12 | Every version marked *to verify* in `Directory.Packages.props` against NuGet and the gateway's pins | `platform/repo-and-deployment.md` header, §2.2, §12.12; postponed V1 | P0 | source |
| 13 | `Pgvector` 0.3.2 `UseVector()` with Npgsql 10.0.x; `Pgvector.EntityFrameworkCore` 0.3.0 with EF Core 10 | `knowledge-api/design.md` §6.6; `platform/repo-and-deployment.md` §2.2 | P0 | source + test |
| 14 | `Aspire.Hosting.Python` 13.6.1 (only for `Parser:Mode=local`) | `platform/repo-and-deployment.md` §2.2 | P0 | source |
| 15 | `Microsoft.Extensions.AI.OpenAI` version (listed together with `Microsoft.Extensions.AI` 10.11.0) | `platform/repo-and-deployment.md` §2.2 | P0 | source |
| 16 | `Cronos`: version, 5-field syntax, DST behaviour | `platform/repo-and-deployment.md` §2.2; `ingestion-worker/design-engine.md` §23 | P0 | source + test |
| 17 | JSON Schema 2020-12 validator (`JsonSchema.Net` candidate): licence, maintenance, `if`/`then`, speed, regex timeout; RFC 8785 canonicalisation. The regex timeout decides whether tool arguments reuse it (tools §4.7) | `ingestion-worker/design-engine.md` §4, §23; `platform/repo-and-deployment.md` §2.2; `tools/design.md` §4.7 | P1 (reuse decision at P2 start) | source + test |
| 18 | Npgsql/Aspire tracing records statement text only, never parameter values | `knowledge-api/design.md` §15 | P0 | test (telemetry capture) |
| 19 | Npgsql 10 auto-prepare (`Max Auto Prepare`) keeps one prepared statement per V/K variant per connection; the HNSW settings are role defaults, so no `SET LOCAL` batch | `knowledge-api/design.md` §6.4, §6.6, §20 | P1 | test |
| 20 | Npgsql batch with `SELECT … FOR UPDATE` + inserts as one round trip (COPY not needed) | `chat-api/design.md` §12.2 | P1 | test |
| 21 | Npgsql binary `COPY` of `vector` via `Pgvector` inside a transaction; HNSW insert cost; a chunk set written with `DeletedAt` set in batches of ≤ 1,000, then a short swap per document | `ingestion-worker/design-engine.md` §23 | P1 | test + measure |
| 22 | Npgsql EF Core 10.0.3: `xmin` as `uint` row version on every root; `ExecuteUpdate` behaviour | `admin/design.md` §4.3 | P1 | test |
| 23 | Version lock: `SELECT … FOR UPDATE` on `config."ConfigVersion"` before `SaveChanges`, then the bumped value read after the outbox trigger in the same transaction; no deadlock with the trigger's own update of that row; lock waits at the admin's real write rate | `admin/design.md` §4.4, §15 | P1 | test + measure (`rag.admin.config_lock.wait`) |
| 24 | EF Core needs `e.Ignore(...)` for non-mapped `VirtualKey` members | gateway design §16 | P1 (combined release) | test |
| 25 | `OnTokenValidated` properties reach the cookie ticket; `ctx.Fail` + `OnRemoteFailure` redirect on .NET 10 | gateway design §16 | P1 (combined release) | test |
| 163 | Postgres admin ticket store (`config."AdminSessions"`, `PostgresAdminSessionStore`) with the copied BFF code (`AdminTicketStore`, `AdminSessionLimit`): sign-out ends every copy of the cookie, expired rows are not returned and are deleted by the retention step, extension stops at 12 h; the copied `AdminSessionTests` pass | `simplification-pass.md` §14; `platform/data-model.md` §2.1, §14; `identity/design.md` §1 | P0 | test |
| 26 | `Microsoft.ML.Tokenizers` 2.0.1: parity with Hugging Face for BGE-M3, options, thread safety, speed | `ingestion-worker/design-processing.md` §6.1, §21 | P1 | test + measure |
| 27 | `System.Uri` escaping and IDN vs the canonicaliser rules; `IdnMapping` UTS 46 flags in .NET 10 | `ingestion-worker/design-connectors.md` §3.1, §22 | P1 | test |
| 28 | `SocketsHttpHandler.ConnectCallback` with HTTP/2 + SNI; decompression byte counting; Brotli | `ingestion-worker/design-connectors.md` §22 | P1 | test |
| 29 | Playwright .NET `RouteAsync("**/*")` + `FulfillAsync` coverage, redirects, `ServiceWorkers = Block`, WebSocket and DNS-prefetch bypass; `Microsoft.Playwright` = the renderer's `playwright-core` exact version | `ingestion-worker/design-connectors.md` §22; `platform/repo-and-deployment.md` §2.2, §5.4 | P2 (renderer) | source + test |
| ~~30~~ | ~~PdfPig 0.1.x extractor, segmenter and reading-order names~~ dropped (simplification pass): every PDF goes to the parser | `ingestion-worker/design-processing.md` §3.7, §21 | – | – |
| 31 | SmartReader as the fallback for hosts without a site profile; the Sitevision preset against the ≈ 20 saved umea.se pages | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 32 | Kestrel per-endpoint request body limits when roles share a host (knowledge routes; chat 2 MB; admin uploads 60 MB while the rest keeps 1 MB); nginx `proxy_request_buffering off` | `knowledge-api/design.md` §3.6; `chat-api/design.md` §3.2; `admin/design.md` §2.2 | P1 | test |
| 33 | Kestrel client-certificate listener vs health probes; shared Data Protection key ring in `AddRagDatabase` (separate `rag-tools` service) | `tools/design.md` §2.2, §8.3 | P3 | test |
| ~~34~~ | ~~`Microsoft.OpenApi` 3.11.0 for the OpenAPI → MCP generator~~ dropped (simplification pass): generator dropped | `tools/design.md` §8.2 | – | – |
| 35 | Azure Key Vault SDK versions | `platform/repo-and-deployment.md` §2.2 | when K1 is decided (P2) | source |
| 146 | Generic forms: the .NET 10 OpenAPI generator (or our own `FormSchemas`) emits DataAnnotations ranges, required members and enums as JSON Schema keywords that `FormModel` can render; `jsonb` members round-trip | `admin/design.md` §5.8, §15 | P1 | test |

## LLM gateway

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 36 | OpenAI .NET SDK default `encoding_format` | gateway design §6, §16 | P1 (combined release) | source + test |
| 37 | Ollama `/v1/embeddings` with `dimensions` (low priority) | gateway design §16 | P1 (combined release) | test |
| 38 | WebSocket clients can set custom headers on upgrade | gateway design §16 | P1 (combined release) | test |
| 39 | Compose nested default `${A:-${B:-true}}` | gateway design §16 | P1 (combined release) | test |
| 40 | No gateway code reads `key.TeamId` as owner after step 1b except the listed places | gateway design §16 | P1 (combined release) | grep at implementation |
| 41 | Combined release `memberGroups`/`code` fields (claims-only); `/v1/models` at a ceiling with a delegating key lists only models with a target within it (admin V8 relies on it); proposed `GET /v1/catalog/routes`; route-alias residency under `x-ume-max-residency` | `admin/design.md` §4.6, §4.10, §15; gateway design §3.4, §3.6 | P1 (combined release) | test + ask (gateway team) |
| 42 | The usage-only final chunk keeps `choices: []` for every adapter | `chat-api/design.md` §8.3 | P1 | test |
| 43 | Gateway embeddings: maximum inputs and tokens per request | `ingestion-worker/design-processing.md` §11, §21 | P1 | source + ask (gateway team) |
| 44 | On-prem LLM through the gateway: JSON output mode; judge stability at temperature 0; run-to-run variance of nightly retrieval runs on an unchanged collection (input to any future gate) | `ingestion-worker/design-processing.md` §21 | P1 | test + measure |
| 45 | Streamed `tool_calls` deltas through every adapter; `cached_tokens` in usage; provider tool-name rules; on-prem tool calling, Swedish tool choice, the `tools` feature tag | `tools/design.md` §6.4 | P1 (combined release tests) / P2 | test |
| 133 | Gateway rerank endpoint for `bge-reranker-v2-m3` on-prem (a separate P2 gateway release; the gateway has none today): latency over 30 passages on the on-prem hardware within the proposed 500 ms timeout | `simplification-pass.md` §8; `knowledge-api/design.md` §6.7, §20; gateway design §7.4 | P2 | measure |
| 147 | The gateway's attempt loop can tell that every attempted candidate was circuit-open or cooling down at selection (`CircuitStatus` after `SelectCandidatesAsync`, `Stores.cs:175`), for the 503 with `Retry-After` | gateway design §3.3, §16 | P1 (combined release) | test |
| 148 | Path and request shape of the P2 rerank endpoint agreed with the gateway team, and the on-prem runtime serving `bge-reranker-v2-m3`, before the FakeGateway rerank fake is fixed | gateway design §7.4, §16; `platform/repo-and-deployment.md` §4.3, §14 | P1 | ask (gateway team) |
| 149 | Interactive gateway headers timeout 15 s: TTFT p99 of the slowest pilot answer model for a full 16k-token prompt stays below it | `chat-api/design.md` §8.3, §22 | P1 | measure |

## Open WebUI

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 46 | Image tag (designed against v0.12.5) and the JWT fields and fallback at the chosen tag | `platform/repo-and-deployment.md` §4.1; `identity/design.md` §16; postponed V1 | P0 | source |
| 47 | Stop button closes the upstream stream; a `source` event before the content chunk renders the chip; `metadata.external` + `#page=` with text-fragment links | `chat-api/design.md` §9.3 | P1 | test |
| 48 | Backend enforcement of `USER_PERMISSIONS_CHAT_FILE_UPLOAD=false` | `chat-api/design.md` §13.3 | P1 | source + test |
| 49 | Creating the feedback-import admin service account with `ENABLE_LOGIN_FORM=false` | `chat-api/design.md` §14.2 | P1 | test |
| 50 | `{{MESSAGE_ID}}` non-empty for every completion and regeneration | `chat-api/design.md` §11.4 | P1 | test |
| 51 | Feedback `data.reason` always a fixed key; shape of older imported feedback | `admin/design.md` §8.1 | P1 | source |
| 134 | Open WebUI v0.12.5 settings that switch off title, tag and follow-up generation (none of them reaches the RAG assistants) | `simplification-pass.md` §1 | P0 | source + test |
| 135 | Feedback import and feedback pruning through Open WebUI's HTTP API only (no SQL against its database): feedback list paging by `updated_at`, admin `DELETE /api/v1/evaluations/feedback/{id}` with an endpoint-restricted API key, admin service account. Chat copies cannot be pruned without `ENABLE_ADMIN_CHAT_ACCESS`: postponed W2 | `simplification-pass.md` §1, §11a; `chat-api/design.md` §13.3, §14.2, §22 | P1 | source + test |
| 52 | `status` events from an external connection forwarded, stored and rendered with `source` events; `hidden: true`; `: ping` comments ignored | `tools/design.md` §6.7 | P2 | test |
| 165 | Open WebUI shows an assistant's description and contact: an `info.meta.description` in our `/v1/models` entry survives the model merge (default metadata, workspace overrides) and appears in the model selector (a top-level `description` does not, `src/lib/utils/localizedContent.ts:165-168`); sending `info.meta` leaves capabilities unchanged; else the platform team enters it in Open WebUI's model editor | `chat-api/design.md` §3.1, §22; `simplification-pass.md` §15 | P1 | source + test |

## MCP and skills

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 53 | MCP C# SDK 2.3.0 (version re-checked at P2 start): `MapMcp("/mcp/skills/{slug}")` route value in `ConfigureSessionOptions` (stateless; per-skill endpoints P3); `McpAuthenticationHandler` composed with our JWT bearer; `notifications/progress` in stateless mode; cancellation sends `notifications/cancelled`; pooled client vs a stateful `2025-11-25` server after a session 404; `ListToolsAsync` pagination; knowledge MCP names | `tools/design.md` §4.2, §7; `identity/design.md` §8; `knowledge-api/design.md` §14; postponed M1 | P2 start | source + test |
| 54 | MCP clients (VS Code, Copilot Studio, Claude): use of resources and prompts, per-request tool `enum`, 120 s `ask_assistant` (P3); Client ID Metadata Documents in Entra and in the clients | `tools/design.md` §7.2, §7.5; `identity/plan.md` §6; postponed M1 | P2 start | test (each client) |
| 55 | Agent Skills spec changes after `69ef37e`; `evals/evals.json` stays guidance only | `tools/design.md` §3.1–§3.2 | P2 | source |
| 56 | Data sources of the first real tools | `tools/plan.md` §7 | P2 | ask (departments) |

## Docling, parser, OCR and NER

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 57 | Python package versions at lock time (`docling-slim` and its parts, FastAPI, …); Python versions supported by the pinned Docling | `platform/repo-and-deployment.md` §2.4; `parser/design.md` §12, §19; postponed V1 | P0 | source |
| 58 | Docling: absolute page numbers under `page_range`; `max_num_pages`, `max_file_size` | `parser/design.md` §19 | P0 | test |
| 59 | Docling: bitmap-area threshold option name; `do_cell_matching` | `parser/design.md` §9.1, §19 | P0 | source |
| 60 | Docling: legacy LibreOffice timeout through backend options; `.docm` handling | `parser/design.md` §4, §19 | P2 (legacy Office) | test |
| 61 | Docling: OCR word confidence on converted pages | `parser/design.md` §6.4, §19 | P0 | test |
| 62 | `DoclingDocument`: `content_layer` values, `SectionHeaderItem.level` on PDFs, `export_to_markdown(page_no)`, `self_ref`, cell fields, cross-page table merging | `parser/design.md` §7.3, §8.2, §19 | P0 | test |
| 63 | Excel backend `data_only`, hidden sheets, several tables per sheet; PPTX slide = page; DOCX `page_no` | `parser/design.md` §7.4, §19 | P0 | test |
| 64 | `python-oxmsg` embedded messages and reference attachments | `parser/design.md` §5, §19 | P2 (e-mail) | test |
| 65 | Docling pipeline caching per option set and its memory cost | `parser/design.md` §10.2, §19 | P0 | measure |
| 66 | Layout model name, Hugging Face repos, revisions and sizes; TableFormer sizes | `parser/design.md` §12, §19 | P0 | source |
| 67 | Docling `timings` granularity for per-page milliseconds | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 68 | LibreOffice `-nogui` package names and size on trixie; `--convert-to` filter names | `parser/design.md` §4, §19 | P2 (legacy Office) | source |
| 69 | LibreOffice registry keys (macro security, macro execution, link update, update check) | `parser/design.md` §4, §19 | P2 (legacy Office) | source + test |
| 70 | No local-file link leak with links set to "never update" (the fixture is the proof) | `parser/design.md` §15.3, §19 | P2 (legacy Office) | test |
| 71 | LibreOffice under `RLIMIT_AS` 1.5 GB for large spreadsheets | `parser/design.md` §19 | P2 (legacy Office) | test |
| 72 | lxml ≥ 5 `resolve_entities`; openpyxl, python-docx and python-pptx external entities; openpyxl with `defusedxml` | `parser/design.md` §11, §19 | P0 | test |
| 73 | uvicorn disconnect detection during long awaits; `h11_max_incomplete_event_size` | `parser/design.md` §10.4, §19 | P0 | test |
| 74 | `snapshot.debian.org` for `debian-security` in CI (rate limits) | `parser/design.md` §12, §19 | P0 | test |
| ~~75~~ | ~~EasyOCR: Swedish in the Latin model~~ dropped (simplification pass): Tesseract `swe` only | `parser/design.md` §9.2, §19 | – | – |
| 76 | Debian `tesseract-ocr-swe` data variant (`fast` or `best`); whether `tesseract-ocr` pulls `tesseract-ocr-eng` as a dependency | `parser/design.md` §9.2, §19 | P0 | source |
| ~~77~~ | ~~OCR engine for Swedish (Tesseract vs EasyOCR bake-off)~~ dropped (simplification pass): replaced by the acceptance check #131 | postponed P1 (closed) | – | – |
| 78 | Parser performance targets, warm-up and re-warm, slot RSS; image size ≤ 3.5 GB without LibreOffice and EasyOCR | `parser/design.md` §12, §14, §19; `platform/repo-and-deployment.md` §5.1, §14; `parser/plan.md` §5 Q1–Q2 | P0 (image size), P1 (`bench.py`) | measure |
| 79 | KB NER model: labels returned (`PER`/`PRS`), `transformers` 5 tokenizer loading, quality on the fixtures, CPU fp32 vs int8 | `ingestion-worker/design-processing.md` §21; `parser/design.md` §7.8, §12 | P1 | test |
| 80 | Parser memory with the NER model loaded inside the 3.5g limit; latency of the gated `/v1/ner` call for Swedish names (≈ 0.3–1.0 s per page) | `platform/repo-and-deployment.md` §6.2; `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-processing.md` §21 | P0 (memory), P1/R1 (throughput) | measure |
| 131 | Tesseract `swe` acceptance check: å/ä/ö recall ≥ 95 % on a 20-page Umeå sample (EasyOCR reconsidered only if it fails) | `simplification-pass.md` §1; `parser/design.md` §9; postponed P1 (closed) | P1 | measure |
| 132 | pypdfium2 text-layer fast path in the parser: throughput (pages/s per slot) and the share of umea.se PDF pages it takes vs Docling layout and OCR | `simplification-pass.md` §9; `performance-pass.md` §3.8 | P1 | measure |
| 150 | Parser `text-layer` fast path (pypdfium2): per-character font size through `FPDFText_GetFontSize`, line and paragraph grouping from character boxes, reading order on two-column pages and whether inspect needs a column signal, block shape, heading levels and furniture labels as the worker expects, agreement with Docling text on clean pages | `parser/design.md` §19; `ingestion-worker/design-processing.md` §4.2, §21 | P0 | test |
| 81 | clamd `zVERSION` format and date parsing (ClamAV 1.4); the `INSTREAM` reply on `StreamMaxLength` | `ingestion-worker/design-connectors.md` §22 | P1 | test |

## Entra ID and Keycloak

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 82 | Graph `checkMemberGroups` on `/users/{id}`: is `User.ReadBasic.All` needed | `identity/design.md` §2.4, §16; gateway design §16 | P2 (Graph member-group lookup) | test (tenant) |
| 83 | Keycloak client-credentials tokens carry `client_id` and no `sid` | `identity/design.md` §7.1, §16 | P0 | test |
| 84 | Graph Bicep extension: version; `passwordCredentials` not writable; `members` on groups; what-if; `groupMembershipClaims` settable on `Microsoft.Graph/applications` | `identity/design.md` §12.2–§12.4, §16; postponed V1 | P0 (checklist #17) | source + test |
| 85 | Entra `acct` and `idp` values for B2B guests in ID and access tokens | `identity/design.md` §16; gateway design §16 | P1 (combined release) | test (guest account) |
| 151 | ID tokens of `ume-rag-admin` carry `oid` with the scopes the BFF requests (`openid profile email`) | `identity/design.md` §1.1, §16 | P0 | test (tenant) |
| 152 | `groupMembershipClaims = SecurityGroup` on `ume-rag-admin` (and the gateway's admin registration) puts cloud and synced member groups as object ids into the ID token's `groups` claim; how many pilot admins exceed the token's group limit (overage) | `identity/design.md` §2.5, §16; gateway design §16 | P1 (combined release) | test (tenant) + ask (IT) |
| 86 | `GET /groups?$search` with `GroupMember.Read.All` | gateway design §16 | P2 (Graph member-group lookup) | test |
| 87 | `private_key_jwt` client credentials from on-prem for the tool identity `ume-rag-tools` (in-process hub in `rag-api` and `rag-admin`); app-only tokens with `idtyp = app` + `Tools.Invoke`; Keycloak audience mapper for dev | `tools/design.md` §4.6, §8.1, §15 | P2 | test |
| 88 | Graph: `driveItem` subscription maximum lifetime; unique-permission listing under `Sites.Selected`; delta after folder moves | `ingestion-worker/design-connectors.md` §14, §22 | P2 | test |

## Platform and host

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 89 | ClamAV image tag `clamav/clamav-debian:1.4` | `platform/repo-and-deployment.md` §5.1 | P0 | source |
| 90 | cgroup v2 and the systemd driver on the host image (ingestion slice) | `platform/repo-and-deployment.md` §6.2 | P0 | test (test host) |
| 91 | Syft as SBOM tool for the non-.NET images | `platform/repo-and-deployment.md` §8.2 | P0 | test |
| 92 | Docker IPv6 on the `crawl` network and the IPv6 firewall rules | `ingestion-worker/design-connectors.md` §7, §22 | P1 | test |
| 93 | `DOCKER-USER` rule and subnet per `mcp-<name>-egress` network of a hosted server that wraps systems (172.30.8.0/21 proposed) | `tools/design.md` §8.3, §15 | P2 | test |
| 94 | About-page URL for the crawler's user agent; umea.se `robots.txt` allows it | `ingestion-worker/design-connectors.md` §22; postponed U1 | P1 (before the first production crawl) | ask (web editors) |
| 145 | Backup target with IT (S3-compatible or share), encryption at rest, and where the offline key escrow lives | `platform/repo-and-deployment.md` §7.1, §7.4, §14; `simplification-pass.md` §4 | P0 | ask (IT operations) |
| 153 | `rag-api` memory limit with the in-process tool hub and one hosted MCP server | `platform/repo-and-deployment.md` §6.2, §14 | P2 | measure |
| 162 | In-process `IRateLimitStore` and query-embedding cache (`ISharedCache` in-memory) in the one-slot pilot: a `rag-api` restart or maintenance-window update resets rate-limit windows and empties the cache without errors; cache hit rate and query-embedding p95 just after a restart; memory within the cache's size cap | `simplification-pass.md` §14; `platform/data-model.md` §11; `knowledge-api/design.md` §5.4 | P1 | test + measure |

## UI and accessibility

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 95 | Browser PDF viewers with `CSP default-src 'none'` and `#page=n` on download URLs | `knowledge-api/design.md` §3.5, §10.4 | P1 | test (Edge, Chrome, Firefox) |
| 96 | DOS-lagen scope for an intranet admin app and its tillgänglighetsredogörelse | `admin/design.md` §10.1 | P1 | ask (municipal web/accessibility coordinator) |
| ~~97~~ | ~~Vue Flow 1.48.2 canvas checks~~ dropped (simplification pass): canvas dropped | `admin/design.md` §6.8, §10.2 | – | – |

## Models and performance estimates

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 98 | Embedding throughput of the gateway's on-prem BGE-M3 (tokens/s, batch size) and its effect on chat query embeddings | `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-processing.md` §21 | P1 (R1) | measure |
| 99 | BGE-M3 without a query instruction prefix; on-prem query-embedding p95 on production hardware | `knowledge-api/design.md` §5.1, §11; `ingestion-worker/design-processing.md` §11, §21 | P1 | measure |
| 100 | Planning numbers: CPU and statements per page | `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-connectors.md` §17, §22; `ingestion-worker/design-processing.md` §14.7, §16, §21 | P1 (R1) | measure |
| 101 | Ingestion targets: 10k pages/h without moving chat p95 > 10 %; unchanged re-crawl runs no parse/embed; one fetch and parse per URL for two departments (caches); restart loses no work | `ingestion-worker/plan.md` §9 | P1 (R1) | measure |
| 102 | Knowledge database part p95 < 100 ms at 100k chunks | `knowledge-api/plan.md` §4; `knowledge-api/design.md` §11 | P1 | measure (load test) |
| 103 | Character estimate ÷ 3 vs real tokenizers (Swedish, English), via `rag.chat.prompt.estimate_ratio` | `chat-api/design.md` §6.3 | P1 | measure |
| 104 | On-prem condense model latency p95 within the 600 ms timeout | `chat-api/design.md` §5, §16 | P1 | measure |
| 136 | Condense skip heuristic (length, no anaphora): share of follow-ups skipped and wrongly skipped, and retrieval recall of skipped vs condensed follow-ups, on real pilot questions | `simplification-pass.md` §8; `chat-api/design.md` §5, §22 | P1 | measure (pilot data) |
| 105 | `LanguageGuess` ≥ 97 % on real pilot questions | `chat-api/design.md` §10.3 | P1 | measure (pilot data) |
| 106 | Admin API p95 budgets and the overview query at 100k run items | `admin/design.md` §10.2 | P1 (R1) | measure |
| 107 | ≈ 100 tokens per catalogue entry, ÷ 3 for skill bodies; `Agent:StepTimeEstimate` 8 s | `tools/design.md` §3.4, §6.5 | P2 | measure |
| 108 | Exact cosine over ≤ 2,000 decrypted attachment chunks within 5 ms | `chat-api/design.md` §15.2 | P2 | measure |
| 154 | Conversational check: no false positives on real questions (a short question of stop words plus one content word is not conversational); the .NET Snowball stop lists match Postgres's | `chat-api/design.md` §6.2, §22 | P1 | test + measure (pilot data) |
| 155 | Answer-level nightly run in `rag-admin`: agreement of the on-prem judge's faithfulness and citation-precision scores with human ratings on a sample (before editors see them); cost per assistant | `chat-api/design.md` §18.8, §22; `ingestion-worker/design-processing.md` §14.8, §21 | P1 (late) | measure |
| 167 | Owner-team scoping (P2): the `teamCodes` claim is filled for department staff in Entra (or the IdP) and matches `Teams.Code` from the gateway's organisation data; otherwise member groups on teams (a gateway change) | `identity/design.md`, `admin/design.md` §3.2, `platform/data-model.md` `DepartmentSettings` | P2 start | check the claim with test users |
| 156 | Static skill budget: ≈ 4,000 tokens of skill bodies in the system prompt keeps tool choice and answer quality on the pilot models (static vs progressive on the Bygglov skill) | `tools/design.md` §3.9, §15 | P2 | measure |
| 157 | Tool bulkhead values: `MaxConcurrentCalls` 8, circuit 5 failures / 30 s, in the R1 agent scenario | `tools/design.md` §2.2, §15 | P2 | measure |
| 166 | Assistant tests: judge cost and duration of an `assistant-tests` run (≤ 30 questions, an answer and a judge call each) per publish across a department's assistants; the verdict against free-text expected behaviour agrees with editors on a sample | `chat-api/design.md` §18.8, §22; `platform/data-model.md` §2.2 | P1 | measure |

## Licences and external data

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 109 | Model and library licences in the parser image: Docling layout and TableFormer models, `olefile`, pypdfium2 (pdfium built without V8/XFA) | `parser/design.md` §9.2, §11, §12, §19; postponed P2 | P0 | source |
| 110 | KB BERT NER model (MIT) and the SUC 3.0 terms for derived weights | `ingestion-worker/design-processing.md` §21; `parser/design.md` §12; postponed P2 | P1 | source + ask (KB) if unclear |
| 111 | Skatteverket test personnummer/samordningsnummer data and SCB name statistics (gazetteer) | `ingestion-worker/design-processing.md` §9.3, §21; postponed P2 | P1 | source |
| 112 | Google's robotstxt test corpus | `ingestion-worker/design-connectors.md` §18, §22; postponed P2 | P1 | source |
| 113 | `sv_SE` Hunspell word list (used by the .NET decompounder) | `platform/repo-and-deployment.md` §5.2; postponed P2 | P1 (before the pilot) | source |

## Performance and scale

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 114 | The chosen S3 server (municipal S3 service or SeaweedFS) passes the `IBlobStore` contract test with `AWSSDK.S3` (`WHEN_REQUIRED` checksums, path style, `UseChunkEncoding`/`DisablePayloadSigning`, `If-None-Match: *`, range GET, multipart) | `performance-pass.md` §3.13; postponed S2 | P1 | test |
| 115 | `AWSSDK.S3` version at pin time (7-day rule; 4.0.104.6 read in the pass) and its `AWSSDK.Core` | `performance-pass.md` §3.13; `platform/repo-and-deployment.md` §2.2; postponed V1 | P1 | source |
| 116 | Licence or service terms of the chosen object store for the municipality (SeaweedFS Apache-2.0, or the municipal S3 service's terms; MinIO excluded) | `performance-pass.md` §3.13; postponed S2, P2 | P2 | ask (legal) |
| 117 | HNSW element size on disk for `vector(1024)` vs `halfvec(1024)` vs `bit(1024)` (`pg_relation_size`) and the recall delta of halfvec on the evaluation sets | `performance-pass.md` §3.3; `platform/data-model.md` §3.2, §12.16 | P1 | measure |
| 118 | Filtered HNSW recall and latency by selectivity band at 1M/5M/10M chunks with `relaxed_order` and per-query `hnsw.max_scan_tuples` | `performance-pass.md` §3.3, §7; `knowledge-api/design.md` §6.4 | P2 (scale test) | measure |
| 119 | Npgsql binary `COPY` into `Chunks` with `Vector` and the generated `SearchVector`; HNSW insert cost at 10M chunks (extends #21) | `performance-pass.md` §3.1, §4; `ingestion-worker/design-engine.md` §14 | P1/P2 | test + measure |
| 120 | `NOTIFY` lock waits at ≈ 85 notifying commits/s with and without the debounce | `performance-pass.md` §3.4; `platform/data-model.md` §4.3 | P2 (scale test) | measure |
| 121 | PgBouncer (1.24.1 trixie / 1.26 PGDG) with Npgsql 10 auto-prepare, `No Reset On Close`, TLS on both sides, SCRAM `auth_query` | `performance-pass.md` §3.1 | P3 (by trigger) | test |
| 122 | `wal_compression = lz4` available in the stock pgvector image | `performance-pass.md` §3.2; `platform/repo-and-deployment.md` §6.2 | P0 | test (image test job) |
| 123 | OpenTelemetry Collector `postgresql` receiver metric set (connections per role, dead tuples, index sizes) | `performance-pass.md` §3.14; `platform/repo-and-deployment.md` §9 | P1 | source + test |
| 124 | .NET 10 GC defaults in containers (DATAS with Server GC; `DOTNET_GCHeapHardLimitPercent`) | `performance-pass.md` §4; `platform/repo-and-deployment.md` §6.2 | P0 | source + test |
| 125 | BGE-M3 tokens/s per GPU class and on CPU at the gateway's provider; head-of-line blocking of a query embedding behind 64- vs 32-input batches (extends #98) | `performance-pass.md` §3.9; `ingestion-worker/design-processing.md` §11 | P1 (R1) | measure |
| 126 | uvicorn client-certificate verification for the optional multi-endpoint parser mode (`PARSER_TLS_CLIENT_CA`) | `performance-pass.md` §3.8; `parser/design.md` §13.1 | P3 | test |
| 140 | `rag-api` pool 12 with `Retrieval:MaxConcurrentSearches` 4 = ⌊(pool − 3) ÷ 2⌋ (each search may hold 2 connections while K and V overlap); admin 5, worker 10, `max_connections` 100: pool waits and 503s at 20 msg/s next to the chat writer and the stats flush | `simplification-pass.md` §9; `performance-pass.md` §3.1; `knowledge-api/design.md` §6.6, §20; `platform/data-model.md` §1.1, §14 | P0 (measure), P1 (R1) | measure |
| 164 | Config snapshot with many assistants: build time and memory of the api snapshot with 300 published assistant versions (instructions up to 20 KB each), reloaded whole on any assistant change (reload by outbox `EntityId` if over 50 ms); draft saves never bump the config version | `platform/data-model.md` §5, §14; `simplification-pass.md` §15 | P1 | measure + test |

## umea.se and site profiles

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 137 | Site-profile learning on umea.se: 30–50 sampled pages give the Sitevision preset, main root `main`, boilerplate (share widget, short-URL line, "Undersidor" list kept as links only), breadcrumb `nav[aria-label="Brödsmulor"]` without `sr-only` text, date pattern "Sidan uppdaterades", identity rule by node id (id-only URL gives the same content hash on sampled pages), `lastmod` reliability across runs | `simplification-pass.md` §13 | P1 | test |
| 138 | umea.se acceptance page (`…/digitalomstallningochit.4.88db09b1724ee319ca45d.html`): exactly its main text, breadcrumb "Kommun och politik › Kommunens organisation › Förvaltningar och verksamheter › Teknik och fastighet" (path to verify on the live page), date 2026-01-15; ≈ 20 saved umea.se pages (HTML and PDF) as the regression corpus | `simplification-pass.md` §13.2 | P1 | test |
| 139 | Pilot source inventory at P1 start: share of legacy `.doc`/`.xls`/`.ppt`, RTF and e-mail (Z1), and whether any pilot site needs JavaScript (renderer) | `simplification-pass.md` §1; postponed Z1 | P1 (start) | measure |
| 158 | Sitevision fingerprint and the preset's session parameters (`sv.*` query parameters) on two or three other Sitevision municipal sites | `ingestion-worker/design-connectors.md` §22 | P1 | test |
| 159 | Site-profile re-learn thresholds (empty-root rate, boilerplate share, text-yield change) on the umea.se corpus | `ingestion-worker/design-connectors.md` §22 | P1 | measure |
| 160 | The worker's preview returns site-profile before/after text for a host without a profile within the 2-minute preview deadline | `admin/design.md` §15 | P1 | test |
