# To verify: package, behaviour and estimate checks

One register of the "To verify" items of every detail document (collected in the consistency pass, detail step 11).
The per-document "To verify" sections stay the source with the full context; this file is the index to plan and track
them. When an item is resolved, record the result in the document's section and strike the row here. Version checks
follow postponed decision V1 and the 7-day package rule (`platform/repo-and-deployment.md` §12.12); licences feed
postponed decision P2 ([`postponed-decisions.md`](postponed-decisions.md)).

Phases: P0 foundation, P1 MVP/pilot (gateway releases B, C, A happen in P1), P2 broaden, P3 scale (README). *How*:
**test** (a unit, integration or manual test that proves the behaviour), **source** (read the package's source, release
notes or documentation at the pinned version), **measure** (benchmark, R1 release test or pilot data), **ask** (a person
or team named in the row).

Document names are relative to `plans/`; "gateway design" is `gateway-changes/design.md`.

## Postgres and pgvector

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 1 | `shared_ispell` on Postgres 17 as the fallback for Hunspell memory per backend | `platform/repo-and-deployment.md` §12.6; `platform/data-model.md` §12.18; postponed H1 | P0 | measure (`images/postgres/test.sh`) |
| 2 | `ALTER ROLE … SET hnsw.*` works before pgvector is loaded, so `app-roles.sql` may set the role defaults | `knowledge-api/design.md` §6.4 | P0 | test |
| 3 | `plainto_tsquery('content.rag_swedish', …)::text` shape when Hunspell returns several lexemes for one compound (the `' & '` → `' \| '` rewrite) | `knowledge-api/design.md` §6.2 | P0 | test (`SwedishTextSearchTests`) |
| 4 | Planner: the exact variant uses the `CollectionIds` GIN bitmap, never HNSW; K and V use the partial indexes under generic plans | `knowledge-api/design.md` §16.2 | P1 | test (`EXPLAIN` in integration tests) |
| 5 | Keyword statement K cost for frequent terms with OR semantics; stop list if > 30 ms p95 | `knowledge-api/design.md` §6.2, §11 | P1 | measure (retrieval load test) |
| 6 | Exact-scan cost at 10,000 TOASTed `vector(1024)` values within 25 ms p95; else lower `Settings.Retrieval.exactScanMaxChunks` or move to `halfvec` | `knowledge-api/design.md` §6.4, §20 | P1 | measure |
| 7 | `starts_with` filters need no index | `knowledge-api/design.md` §6.3 | P1 | test |
| 8 | Chunk-set scope `ChunkSetId = ANY(…)` uses a bitmap scan and exact ordering for a 2,000-chunk gate sub-corpus | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 9 | Recursive-CTE loose index scan over `IX_Jobs_Ready` on the partitioned `Jobs` table | `ingestion-worker/design-engine.md` §23 | P1 | test |
| 10 | `pg_advisory_xact_lock` dedup under 500-row batch enqueues (`max_locks_per_transaction`) | `ingestion-worker/design-engine.md` §23 | P1 | test |
| 11 | CloudNativePG image requirements for our Postgres image | `platform/repo-and-deployment.md` §10 | P3 | source |

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
| 19 | Npgsql 10 `NpgsqlBatch` `BEGIN READ ONLY; SET LOCAL hnsw.ef_search; SELECT; COMMIT` in one round trip; auto-prepare per variant | `knowledge-api/design.md` §6.4 | P1 | test |
| 20 | Npgsql batch with `SELECT … FOR UPDATE` + inserts as one round trip (COPY not needed) | `chat-api/design.md` §12.2 | P1 | test |
| 21 | Npgsql binary `COPY` of `vector` via `Pgvector` inside a transaction; HNSW insert cost; a 20,000-chunk transaction vs `idle_in_transaction_session_timeout` | `ingestion-worker/design-engine.md` §23 | P1 | test + measure |
| 22 | Npgsql EF Core 10.0.3: `xmin` as `uint` row version on every root; `ExecuteUpdate` behaviour | `admin/design.md` §4.3 | P1 | test |
| 23 | `config."ConfigVersion"` read after `SaveChanges` in the same transaction returns the bumped value | `admin/design.md` §4.4 | P1 | test |
| 24 | EF Core needs `e.Ignore(...)` for non-mapped `VirtualKey` members | gateway design §16 | P1 (release A) | test |
| 25 | `OnTokenValidated` properties reach the cookie ticket; `ctx.Fail` + `OnRemoteFailure` redirect on .NET 10 | gateway design §16 | P1 (release C) | test |
| 26 | `Microsoft.ML.Tokenizers` 2.0.1: parity with Hugging Face for BGE-M3, options, thread safety, speed | `ingestion-worker/design-processing.md` §6.1, §21 | P1 | test + measure |
| 27 | `System.Uri` escaping and IDN vs the canonicaliser rules; `IdnMapping` UTS 46 flags in .NET 10 | `ingestion-worker/design-connectors.md` §3.1, §22 | P1 | test |
| 28 | `SocketsHttpHandler.ConnectCallback` with HTTP/2 + SNI; decompression byte counting; Brotli | `ingestion-worker/design-connectors.md` §22 | P1 | test |
| 29 | Playwright .NET `RouteAsync("**/*")` + `FulfillAsync` coverage, redirects, `ServiceWorkers = Block`, WebSocket and DNS-prefetch bypass; `Microsoft.Playwright` = the renderer's `playwright-core` exact version | `ingestion-worker/design-connectors.md` §22; `platform/repo-and-deployment.md` §2.2, §5.4 | P1 | source + test |
| 30 | PdfPig 0.1.x extractor, segmenter and reading-order names; two-column behaviour; per-letter font size | `ingestion-worker/design-processing.md` §3.7, §21 | P1 | test (fixtures) |
| 31 | SmartReader output on umea.se page templates | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 32 | Kestrel per-endpoint request body limits when roles share a host (knowledge routes; chat 2 MB; admin uploads 60 MB while the rest keeps 1 MB); nginx `proxy_request_buffering off` | `knowledge-api/design.md` §3.6; `chat-api/design.md` §3.2; `admin/design.md` §2.2 | P1 | test |
| 33 | Kestrel client-certificate listener vs health probes; shared Data Protection key ring in `AddRagDatabase` | `tools/design.md` §2.2, §8.3 | P2 | test |
| 34 | `Microsoft.OpenApi` 3.11.0: YAML reader package; OpenAPI 3.0 `nullable` and Swagger 2.0 parameter conversion | `tools/design.md` §8.2 | P2 | source + test |
| 35 | Azure Key Vault SDK versions | `platform/repo-and-deployment.md` §2.2 | when K1 is decided (P2) | source |

## LLM gateway

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 36 | OpenAI .NET SDK default `encoding_format` | gateway design §6, §16 | P1 (release B) | source + test |
| 37 | Ollama `/v1/embeddings` with `dimensions` (low priority) | gateway design §16 | P1 (release B) | test |
| 38 | WebSocket clients can set custom headers on upgrade | gateway design §16 | P1 (releases B, A) | test |
| 39 | Compose nested default `${A:-${B:-true}}` | gateway design §16 | P1 (release C) | test |
| 40 | No gateway code reads `key.TeamId` as owner after step 1b except the listed places | gateway design §16 | P1 (release A) | grep at implementation |
| 41 | Release C `memberGroups`/`code` fields; proposed `GET /v1/catalog/routes`; route-alias residency under `x-ume-max-residency` vs `RouteSelector.EffectiveResidencies` after release B | `admin/design.md` §4.10 | P1 | test + ask (gateway team) |
| 42 | The usage-only final chunk keeps `choices: []` for every adapter | `chat-api/design.md` §8.3 | P1 | test |
| 43 | Gateway embeddings: maximum inputs and tokens per request | `ingestion-worker/design-processing.md` §11, §21 | P1 | source + ask (gateway team) |
| 44 | On-prem LLM through the gateway: JSON output mode; judge stability at temperature 0 | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 45 | Streamed `tool_calls` deltas through every adapter; `cached_tokens` in usage; provider tool-name rules; on-prem tool calling, Swedish tool choice, the `tools` feature tag | `tools/design.md` §6.4 | P1 (release B tests) / P2 | test |

## Open WebUI

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 46 | Image tag (designed against v0.12.5) and the JWT fields and fallback at the chosen tag | `platform/repo-and-deployment.md` §4.1; `identity/design.md` §16; postponed V1 | P0 | source |
| 47 | Stop button closes the upstream stream; a `source` event before the content chunk renders the chip; `metadata.external` + `#page=` with text-fragment links | `chat-api/design.md` §9.3 | P1 | test |
| 48 | Backend enforcement of `USER_PERMISSIONS_CHAT_FILE_UPLOAD=false`; full table list and `updated_at` units for the pruning job | `chat-api/design.md` §13.3, §15.1 | P1 | source + test |
| 49 | Creating the feedback-import admin service account with `ENABLE_LOGIN_FORM=false` | `chat-api/design.md` §14.2 | P1 | test |
| 50 | `{{MESSAGE_ID}}` non-empty for every completion and regeneration | `chat-api/design.md` §11.4 | P1 | test |
| 51 | Feedback `data.reason` always a fixed key; shape of older imported feedback | `admin/design.md` §8.1 | P1 | source |
| 52 | `status` events from an external connection forwarded, stored and rendered with `source` events; `hidden: true`; `: ping` comments ignored | `tools/design.md` §6.7 | P2 | test |

## MCP and skills

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 53 | MCP C# SDK 2.3.0 (version re-checked at P2 start): `MapMcp("/mcp/skills/{slug}")` route value in `ConfigureSessionOptions` (stateless); `McpAuthenticationHandler` composed with our JWT bearer; `notifications/progress` in stateless mode; cancellation sends `notifications/cancelled`; pooled client vs a stateful `2025-11-25` server after a session 404; `ListToolsAsync` pagination; knowledge MCP names | `tools/design.md` §4.2, §7; `identity/design.md` §8; `knowledge-api/design.md` §14; postponed M1 | P2 start | source + test |
| 54 | MCP clients (VS Code, Copilot Studio, Claude): use of resources and prompts, per-request tool `enum`, 120 s `ask_assistant`; Client ID Metadata Documents in Entra and in the clients | `tools/design.md` §7.2, §7.5; `identity/plan.md` §6; postponed M1 | P2 start | test (each client) |
| 55 | Agent Skills spec changes after `69ef37e`; `evals/evals.json` stays guidance only | `tools/design.md` §3.1–§3.2 | P2 | source |
| 56 | Data sources of the first real tools | `tools/plan.md` §7 | P2 | ask (departments) |

## Docling, parser, OCR and NER

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 57 | Python package versions at lock time (`docling-slim` and its parts, FastAPI, …); Python versions supported by the pinned Docling | `platform/repo-and-deployment.md` §2.4; `parser/design.md` §12, §19; postponed V1 | P0 | source |
| 58 | Docling: absolute page numbers under `page_range`; `max_num_pages`, `max_file_size` | `parser/design.md` §19 | P0 | test |
| 59 | Docling: bitmap-area threshold option name; `do_cell_matching` | `parser/design.md` §9.1, §19 | P0 | source |
| 60 | Docling: legacy LibreOffice timeout through backend options; `.docm` handling | `parser/design.md` §4, §19 | P0 | test |
| 61 | Docling: OCR word confidence on converted pages | `parser/design.md` §6.4, §19 | P0 | test |
| 62 | `DoclingDocument`: `content_layer` values, `SectionHeaderItem.level` on PDFs, `export_to_markdown(page_no)`, `self_ref`, cell fields, cross-page table merging | `parser/design.md` §7.3, §8.2, §19 | P0 | test |
| 63 | Excel backend `data_only`, hidden sheets, several tables per sheet; PPTX slide = page; DOCX `page_no` | `parser/design.md` §7.4, §19 | P0 | test |
| 64 | `python-oxmsg` embedded messages and reference attachments | `parser/design.md` §5.1, §19 | P0 | test |
| 65 | Docling pipeline caching per option set and its memory cost | `parser/design.md` §10.2, §19 | P0 | measure |
| 66 | Layout model name, Hugging Face repos, revisions and sizes; TableFormer sizes | `parser/design.md` §12, §19 | P0 | source |
| 67 | Docling `timings` granularity for per-page milliseconds | `ingestion-worker/design-processing.md` §21 | P1 | test |
| 68 | LibreOffice `-nogui` package names and size on trixie; `--convert-to` filter names | `parser/design.md` §4, §19 | P0 | source |
| 69 | LibreOffice registry keys (macro security, macro execution, link update, update check) | `parser/design.md` §4, §19 | P0 | source + test |
| 70 | No local-file link leak with links set to "never update" (the fixture is the proof) | `parser/design.md` §15.3, §19 | P1 | test |
| 71 | LibreOffice under `RLIMIT_AS` 1.5 GB for large spreadsheets | `parser/design.md` §19 | P1 | test |
| 72 | lxml ≥ 5 `resolve_entities`; openpyxl, python-docx and python-pptx external entities; openpyxl with `defusedxml` | `parser/design.md` §11, §19 | P0 | test |
| 73 | uvicorn disconnect detection during long awaits; `h11_max_incomplete_event_size` | `parser/design.md` §10.4, §19 | P0 | test |
| 74 | `snapshot.debian.org` for `debian-security` in CI (rate limits) | `parser/design.md` §12, §19 | P0 | test |
| 75 | EasyOCR: Swedish in the Latin model | `parser/design.md` §9.2, §19 | P0 | test |
| 76 | Debian `tesseract-ocr-swe` data variant (`fast` or `best`) | `parser/design.md` §9.2, §19 | P0 | source |
| 77 | OCR engine for Swedish (Tesseract vs EasyOCR bake-off) | `parser/plan.md` §5 Q1; `parser/design.md` §9.3; postponed P1 | P1 | measure (`ocr_bakeoff.py`) |
| 78 | Parser performance targets, warm-up and re-warm, slot RSS, image size (≤ 4.0 GB with NER) | `parser/design.md` §14, §19; `parser/plan.md` §5 Q1–Q2 | P0 (image size), P1 (`bench.py`) | measure |
| 79 | KB NER model: labels returned (`PER`/`PRS`), `transformers` 5 tokenizer loading, quality on the fixtures, CPU fp32 vs int8 | `ingestion-worker/design-processing.md` §21; `parser/design.md` §7.8, §12 | P1 | test |
| 80 | Parser memory with the NER lane inside the 3.5g limit; NER lane capacity and latency for Swedish names (≈ 0.3–1.0 s per page) | `platform/repo-and-deployment.md` §6.2; `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-processing.md` §21 | P0 (memory), P1/R1 (throughput) | measure |
| 81 | clamd `zVERSION` format and date parsing (ClamAV 1.4); the `INSTREAM` reply on `StreamMaxLength` | `ingestion-worker/design-connectors.md` §22 | P1 | test |

## Entra ID and Keycloak

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 82 | Graph `checkMemberGroups` on `/users/{id}`: is `User.ReadBasic.All` needed | `identity/design.md` §2.4, §16; gateway design §16 | P0 | test (tenant) |
| 83 | Keycloak client-credentials tokens carry `client_id` and no `sid` | `identity/design.md` §7.1, §16 | P0 | test |
| 84 | Graph Bicep extension: version; `passwordCredentials` not writable; `members` on groups; what-if; `uniqueName` adoption | `identity/design.md` §12.2–§12.4, §16; postponed V1 | P0 (checklist #17) | source + test |
| 85 | Entra `acct` and `idp` values for B2B guests in ID and access tokens | `identity/design.md` §16; gateway design §16 | P1 (release C) | test (guest account) |
| 86 | `GET /groups?$search` with `GroupMember.Read.All` | gateway design §16 | P1 (release C) | test |
| 87 | `private_key_jwt` from on-prem for `rag-tools`; `idtyp = app` + `Tools.Invoke`; Keycloak audience mapper | `tools/design.md` §4.6, §8.1 | P2 | test |
| 88 | Graph: `driveItem` subscription maximum lifetime; unique-permission listing under `Sites.Selected`; delta after folder moves | `ingestion-worker/design-connectors.md` §14, §22 | P2 | test |

## Platform and host

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 89 | ClamAV image tag `clamav/clamav-debian:1.4` | `platform/repo-and-deployment.md` §5.1 | P0 | source |
| 90 | cgroup v2 and the systemd driver on the host image (ingestion slice) | `platform/repo-and-deployment.md` §6.2 | P0 | test (test host) |
| 91 | Syft as SBOM tool for the non-.NET images | `platform/repo-and-deployment.md` §8.2 | P0 | test |
| 92 | Docker IPv6 on the `crawl` network and the IPv6 firewall rules | `ingestion-worker/design-connectors.md` §7, §22 | P1 | test |
| 93 | `DOCKER-USER` rule and subnet per `mcp-<name>-egress` (172.30.8.0/21) | `tools/design.md` §4.6, §8.3 | P2 | test |
| 94 | About-page URL for the crawler's user agent; umea.se `robots.txt` allows it | `ingestion-worker/design-connectors.md` §22; postponed U1 | P1 (before the first production crawl) | ask (web editors) |

## UI and accessibility

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 95 | Browser PDF viewers with `CSP default-src 'none'` and `#page=n` on download URLs | `knowledge-api/design.md` §3.5, §10.4 | P1 | test (Edge, Chrome, Firefox) |
| 96 | DOS-lagen scope for an intranet admin app and its tillgänglighetsredogörelse | `admin/design.md` §10.1 | P1 | ask (municipal web/accessibility coordinator) |
| 97 | Vue Flow 1.48.2: no runtime `<style>` or CSP violation; replaceable English ARIA texts; canvas chunk gzip ≤ 180 KB; `connectOnClick` by keyboard; version re-check | `admin/design.md` §6.8, §10.2 | P2 | test |

## Models and performance estimates

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 98 | Embedding throughput of the gateway's on-prem BGE-M3 (tokens/s, batch size) and its effect on chat query embeddings | `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-processing.md` §21 | P1 (R1) | measure |
| 99 | BGE-M3 without a query instruction prefix; on-prem query-embedding p95 on production hardware | `knowledge-api/design.md` §5.1, §11; `ingestion-worker/design-processing.md` §11, §21 | P1 | measure |
| 100 | Planning numbers: CPU and statements per page | `ingestion-worker/design-engine.md` §18, §23; `ingestion-worker/design-connectors.md` §17, §22; `ingestion-worker/design-processing.md` §14.7, §16, §21 | P1 (R1) | measure |
| 101 | Ingestion targets: 10k pages/h without moving chat p95 > 10 %; unchanged re-crawl runs no parse/embed; one crawl and parse for two departments; restart loses no work | `ingestion-worker/plan.md` §9 | P1 (R1) | measure |
| 102 | Knowledge database part p95 < 100 ms at 100k chunks | `knowledge-api/plan.md` §4; `knowledge-api/design.md` §11 | P1 | measure (load test) |
| 103 | Character estimate ÷ 3 vs real tokenizers (Swedish, English), via `rag.chat.prompt.estimate_ratio` | `chat-api/design.md` §6.3 | P1 | measure |
| 104 | On-prem condense model latency p95 within the 400 ms budget | `chat-api/design.md` §5, §16 | P1 | measure |
| 105 | `LanguageGuess` ≥ 97 % on real pilot questions | `chat-api/design.md` §10.3 | P1 | measure (pilot data) |
| 106 | Admin API p95 budgets and the overview query at 100k run items | `admin/design.md` §10.2 | P1 (R1) | measure |
| 107 | ≈ 100 tokens per catalogue entry, ÷ 3 for skill bodies; `Agent:StepTimeEstimate` 8 s | `tools/design.md` §3.4, §6.5 | P2 | measure |
| 108 | Exact cosine over ≤ 2,000 decrypted attachment chunks within 5 ms | `chat-api/design.md` §15.2 | P2 | measure |

## Licences and external data

| # | Item | Where | Phase | How |
|---|---|---|---|---|
| 109 | Model and library licences in the parser image: Docling layout and TableFormer models, EasyOCR weights, `olefile`, pypdfium2 (pdfium built without V8/XFA) | `parser/design.md` §9.2, §11, §12, §19; postponed P2 | P0 | source |
| 110 | KB BERT NER model (MIT) and the SUC 3.0 terms for derived weights | `ingestion-worker/design-processing.md` §21; `parser/design.md` §12; postponed P2 | P1 | source + ask (KB) if unclear |
| 111 | Skatteverket test personnummer/samordningsnummer data and SCB name statistics (gazetteer) | `ingestion-worker/design-processing.md` §9.3, §21; postponed P2 | P1 | source |
| 112 | Google's robotstxt test corpus | `ingestion-worker/design-connectors.md` §18, §22; postponed P2 | P1 | source |
| 113 | `sv_SE` Hunspell dictionary | `platform/repo-and-deployment.md` §5.2; postponed P2 | P1 (before the pilot) | source |

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
| 121 | PgBouncer (1.24.1 trixie / 1.26 PGDG) with Npgsql 10 auto-prepare, `No Reset On Close`, `connect_query` warm-up, TLS on both sides, SCRAM `auth_query` | `performance-pass.md` §3.1 | P3 (by trigger) | test |
| 122 | `wal_compression = lz4` available in our Postgres image | `performance-pass.md` §3.2; `platform/repo-and-deployment.md` §6.2 | P0 | test (`images/postgres/test.sh`) |
| 123 | OpenTelemetry Collector `postgresql` receiver metric set (connections per role, dead tuples, index sizes) | `performance-pass.md` §3.14; `platform/repo-and-deployment.md` §9 | P1 | source + test |
| 124 | .NET 10 GC defaults in containers (DATAS with Server GC; `DOTNET_GCHeapHardLimitPercent`) | `performance-pass.md` §4; `platform/repo-and-deployment.md` §6.2 | P0 | source + test |
| 125 | BGE-M3 tokens/s per GPU class and on CPU at the gateway's provider; head-of-line blocking of a query embedding behind 64- vs 32-input batches (extends #98) | `performance-pass.md` §3.9; `ingestion-worker/design-processing.md` §11 | P1 (R1) | measure |
| 126 | uvicorn client-certificate verification for the optional multi-endpoint parser mode (`PARSER_TLS_CLIENT_CA`) | `performance-pass.md` §3.8; `parser/design.md` §13.1 | P3 | test |
