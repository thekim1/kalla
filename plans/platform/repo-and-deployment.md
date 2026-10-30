# Platform repository, build and deployment: detailed design

Detail round, step 2. Covers the repository, build conventions, projects, local development, images, production
Compose, release bundle, CI/CD, observability, Kubernetes and the ordered P0 checklist. Builds on [`plan.md`](plan.md)
§1, §2, §7, §10, §11 and [`data-model.md`](data-model.md) (roles, `init.sql`/`app-roles.sql`, migration service, Redis
prefix, partitions, Swedish search). Choices the plans leave open and contradictions found are in §12. Rewritten in the
simplification pass ([`../simplification-pass.md`](../simplification-pass.md), 2026-10-30); what went is listed in §13.

**Rule**: copy the gateway (`~/git/llmgateway`, commit `e25b491`, release 0.8.5) wherever it fits. Every file below is
marked **copy** (as-is apart from names), **adapt** (same structure, our content) or **new**. Versions marked
*to verify* could not be checked against the gateway or the NuGet check in
[`../../platforms/kernel-memory-and-dotnet.md`](../../platforms/kernel-memory-and-dotnet.md) §5 (NuGet, 2026-10-29).

## 1. Repository layout

Code lives in the design repository `ume-rag-platform` (decided 2026-10-30): the tree below sits at the repo root beside
`plans/`, `platforms/` and the numbered design documents, `docs/` holds both the design-side `stakeholder-report.html`
and the operations documents, and `verify.yml` ignores changes under `plans/**`, `platforms/**` and the root `*.md`
files. Solution `UmeRag.slnx` (gateway: `UmeLlmGateway.slnx`),
namespaces and assembly names `Ume.Rag.*`, image repositories `ume-rag/<service>`, Compose project `ume-rag`.

```
.editorconfig  .gitattributes  .gitignore  global.json  Directory.Build.props  Directory.Build.targets
Directory.Packages.props  dotnet-tools.json  aspire.config.json  UmeRag.slnx  README.md  CHANGELOG.md
.githooks/pre-commit                 admin UI lint (copy) + parser ruff check (new)
.github/workflows/                   P0: verify.yml release.yml entra.yml; P1: perf.yml evaluation.yml; P2: deploy-e2e.yml flake-hunt.yml scale.yml parser-robustness.yml (§8.1)
src/
  Ume.Rag.Domain/                    entities, classification, IAccessPolicy, ports (IRetriever, IBlobStore, IKeyProvider, IJobQueue, IReranker)
  Ume.Rag.Retrieval/                 search core (keyword/vector candidates, RRF, dedup, diversity, coverage; knowledge design §2.1) and `SwedishDecompounder` (`sv_SE` word list as an embedded resource, used by the worker too), shared by the knowledge API, the evaluator (processing design §13.5) and the admin test panel
  Ume.Rag.Answering/                 knowledge core (KnowledgeService without HTTP) and chat answer core (RagAnswerStrategy, PromptBuilder, TokenBudget, Condenser, AnswerFilter, CitationEmitter, LanguageGuess); referenced by Api and AdminApi only (never the worker), so the test panel runs the real chat path (admin design §2.1) and the admin runs the answer-level judge (`answers` runs, late P1) and the P2 skill gate; P2: AgentAnswerStrategy, AgentPromptBuilder, BuiltInTools, ToolCallStreamParser, SkillFileCache (tools design §2.1)
  Ume.Rag.Pipelines/                 fixed pipeline templates per source type, versioned parameter sets, validator, per-stage cache keys, re-processing plan (every document in P1; from effect tags in P2, weakness review §1) (admin + worker; engine design §4)
  Ume.Rag.Infrastructure/            RagDbContext, queue + LISTEN/NOTIFY, config-version monitor, `IRateLimitStore`/`ISharedCache` (in-memory; Redis from P2) and RedisKeys, Postgres admin session store, gateway client, blob stores, key providers, SecurityEvents
  Ume.Rag.ServiceDefaults/           copy of the gateway's (OpenTelemetry, health, ShutdownDrain, WebDefaults, problem responses)
  Ume.Rag.Api/                       data plane: Knowledge, Chat, MCP endpoint groups (Rag:Roles)
  Ume.Rag.Worker/                    ingestion: connectors, fixed pipelines, site profiles, scheduler, retrieval evaluator (recall/MRR through Ume.Rag.Retrieval), retention + partition jobs
  Ume.Rag.AdminApi/                  control plane BFF; serves admin-ui/dist as wwwroot
  Ume.Rag.MigrationService/          one-shot migrator, app-roles.sql, dev seeder
  Ume.Rag.FakeGateway/               OpenAI-compatible fake gateway (chat, 1024-dim embeddings, residency headers, catalog)
  Ume.Rag.AppHost/                   Aspire orchestration
  Ume.Rag.Tools/                     P2: tool hub library (IToolHub), hosted in-process by Ume.Rag.Api's chat role and by Ume.Rag.AdminApi (skill gate, discovery, drift polling): registry snapshot, MCP client, credential resolver, policy re-check, audit (tools design §2.1)
  admin-ui/                          Vue 3 + Pinia + Vue Router, design system copied from the gateway
  parser/                            Python sidecar (FastAPI + Docling), own Dockerfile, own tests
contracts/pipeline/v2/               definition.schema.json (template id + parameter set, engine design §3.1)
contracts/parser/v1/                 convert-request, convert-result, inspect-result, info, error, ner-request, ner-result (.schema.json; generated by the parser, checked in CI; parser design §7, NER §7.8)
contracts/skill/v1/                  manifest.schema.json (`ume-rag.json` skill manifest, tools design §3.2)
contracts/admin/v1/                  error-codes.json (admin API error codes; CI checks the UI's Swedish texts cover every code, admin design §4.1)
images/
  postgres/Dockerfile                stock pgvector image (digest-pinned) + PGDG `pgbackrest` package and `pgbackrest.conf` (§5.2)
  renderer/Dockerfile                P2, only if needed: Playwright server + Chromium headless shell
  clamav/Dockerfile                  ClamAV + freshclam updating directly from database.clamav.net (decided: no mirror, §5.5)
templates/mcp-server/                P2: `dotnet new ume-mcp-server` template, packaged as `Ume.Rag.Templates` (contents: tools design §8.1)
infra/entra/                         Bicep (Graph extension) for RAG's own app registrations, applied by entra.yml (§8.1)
tests/
  Directory.Build.props              copy
  Ume.Rag.TestKit/                   TestPostgres (stock pgvector image), TestSecrets, TestClock, CapturingLoggerProvider, TestCategory (copy + adapt); `FakeMcpServer` (P2, tools design §11.2)
  Ume.Rag.Domain.Tests/              access policy, classification, residency matrix, cache keys
  Ume.Rag.Api.Tests/                 WebApplicationFactory + FakeGateway: chat, knowledge, keys, fail-closed config
  Ume.Rag.Worker.Tests/              pipeline stages (golden fixtures), site profiles (umea.se regression corpus), queue, SSRF guard, ClamAV client
  Ume.Rag.AdminApi.Tests/            BFF, OIDC claim mapping (copy of OidcClaimMappingTests), audit, outbox
  Ume.Rag.Tools.Tests/               P2: tool hub (normaliser, resolvers, drift, policy re-check; tools design §11.1)
  Ume.Rag.Pipelines.Tests/           parameter validation, cache keys, re-processing plan, template validity (engine design §19)
  Ume.Rag.Retrieval.Tests/           fusion, diversity, coverage, section assembly, query normalisation, SwedishDecompounder cases (knowledge design §16.1)
  Ume.Rag.IntegrationTests/          migrations, DatabaseRoleTests, SwedishTextSearchTests (a decompounded part matches through Postgres), RunItems partitions, job dedup, retrieval statements
  Ume.Rag.ContractTests/             worker parser client vs parser image (golden responses)
  e2e/                               Playwright TypeScript browser tests (copy of the gateway's setup)
  evaluation/smoke/                  fixture collection (documents) + 30 questions, used by CI
  fixtures/                          documents for parser and node tests (Swedish PDFs, DOCX, scanned pages); `processing/`, `pii/` (processing design §17); `umea/` (≈ 20 saved umea.se pages, HTML and PDF: the site-profile regression corpus, simplification pass §13.2)
benchmarks/
  release-test/                      R1 test: k6 chat streams during crawl + OCR (from the gateway's benchmarks/compare/k6); chat overhead run (k6/chat.js, FakeGateway, chat design §16.2)
  Ume.Rag.Benchmarks/                BenchmarkDotNet: RetrievalBenchmarks, ChatBenchmarks (chat design §16.2), InteractionAudit (copy of the gateway's; knowledge design §11)
deploy/
  compose.prod.yaml                  data plane + control zone (host A), incl. postgres (pgBackRest inside, §7.4)
  compose.ingestion.yaml             ingestion zone (same host in the pilot, host B later)
  compose.ingestion-remote.yaml      overlay for a separate ingestion host
  compose.rolling.yaml               P2: b slots for rag-api and rag-admin
  compose.hardening.yaml             overlay for `aspire publish` output (deploy/generated/)
  compose.openwebui.yaml             optional Open WebUI pilot stack: env of chat design §4.3 (title, tag and follow-up generation off), feedback pruning through its HTTP API; chat-copy retention is open item W2 (chat design §13.3)
  compose.mcp.yaml                   P2: hosted MCP servers (one container each, zone `tools-hosted`; tools design §8.3)
  init-deployment.sh  rolling-update.sh  build-release-bundle.sh  Test-Deployment.ps1
  backup-postgres.sh  restore-pitr.sh  backup-uploads.sh  restore-blobs.sh  escrow-keys.sh
  postgres/                          init.sql, app-roles.sql, pg_hba.conf, postgresql.conf, upgrade-extensions.sql, test.sh (image test)
  pgbackrest/                        pgbackrest.conf (copied into `ume-rag/postgres`), repo.conf.example (repository settings, written by init-deployment; §7.4)
  redis/                             redis.conf, users.acl.example (P2, first multi-host deployment)
  proxy/nginx.conf.example           reverse proxy (one slot; two slots each from P2)
  otel/                              collector-siem.example.yaml (security category), dashboards/*.json
  host/                              rag-ingestion.slice, docker-user-firewall.sh (egress rules per network), rag-backup.service + rag-backup.timer (pgBackRest backups, §7.4)
  kubernetes/                        later (§10)
  tests/                             compose-rolling-e2e.sh, verify-data.sh, spa-smoke.mjs (copy + adapt)
dev/keycloak/ume-rag-realm.json      realm with the gateway's claims + rag-* roles
docs/                                local-development, production-setup, runbook, upgrade-guide, zero-downtime-updates,
                                     kubernetes, security-and-compliance, security-events, architecture, adr
scripts/Start-Dev.ps1  scripts/Start-E2E.ps1
```

No `site/` (the gateway's GitHub Pages product page) until the user-facing name is decided (plan §12.3).

| Gateway file | Here | Mode | Change |
|---|---|---|---|
| `global.json` | same | copy | – |
| `Directory.Build.props` | same | adapt | `<Product>Ume Rag</Product>`; same suppressions |
| `Directory.Build.targets` | same | copy | same `UmeServiceContainer` opt-in and digest-pinned base image |
| `Directory.Packages.props` | same | adapt | §2.2 |
| `.editorconfig`, `.gitattributes` | same | copy | `.gitattributes` adds `images/**` and `src/parser/**/*.sh` |
| `dotnet-tools.json` | same | copy | `dotnet-ef` 10.0.12 |
| `aspire.config.json` | same | adapt | path `src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj` |
| `tests/Directory.Build.props` | same | copy | TestKit name |
| `src/*.ServiceDefaults/*` | `Ume.Rag.ServiceDefaults` | copy | `TelemetryName = "Ume.Rag"` |
| `src/*.FakeLlm/FakeLatency.cs` | `Ume.Rag.FakeGateway` | copy | rest new (§4.3) |
| `src/*.MigrationService/*` | `Ume.Rag.MigrationService` | adapt | `RunItems` partition maintenance (data-model §10) |
| `src/*.AppHost/Program.cs` | `Ume.Rag.AppHost` | adapt | §4.1 |
| `scripts/Start-Dev.ps1`, `Start-E2E.ps1` | same | adapt | wait for `admin-ui`; volume `ume-rag-e2e-postgres` |
| `dev/keycloak/ume-realm.json` | `ume-rag-realm.json` | adapt | §4.2 |
| `deploy/*` | `deploy/*` | adapt | §6, §7 |
| `.github/workflows/*` | same names | adapt | §8; `pages.yml` not copied |
| `.githooks/pre-commit` | same | adapt | adds parser lint |
| `src/admin-ui/{.npmrc,eslint.config.js,tsconfig*.json,tailwind.config.cjs,vite.config.ts}` | `src/admin-ui/` | copy | port 5174 |
| `src/admin-ui/src/styles/*`, `components/ui/*`, `components/layout/*`, `scripts/subset-icons.py` | same | copy | design system (§2.4) |
| `tests/e2e/{playwright.config.ts,helpers.ts,package.json,.npmrc}` | `tests/e2e/` | adapt | – |

## 2. Build conventions

### 2.1 .NET

| Setting | Value | Source |
|---|---|---|
| SDK | `10.0.100`, `rollForward: latestFeature`; CI installs `10.0.401` | `global.json`, `verify.yml` |
| Test runner | Microsoft.Testing.Platform, xUnit v3, Shouldly | `global.json`, `tests/Directory.Build.props` |
| Language | `net10.0`, `LangVersion latest`, `Nullable enable`, `ImplicitUsings enable`, `InvariantGlobalization false` | `Directory.Build.props` |
| Warnings | `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile` (for IDE0005); same `NoWarn` list with one reason each | `Directory.Build.props` |
| Raised rules | CA2016, CA1848, CA1873, CA1304–CA1311, CA5394 as warnings (= errors) | `.editorconfig` |
| Packages | `ManagePackageVersionsCentrally`, `CentralPackageTransitivePinningEnabled=false` | `Directory.Packages.props` |
| Version | `0.0.0-local`; release overrides `-p:Version` from the tag | `Directory.Build.props`, `release.yml` |
| Containers | SDK container publish (`-t:PublishContainer`), **no Dockerfiles for .NET**; base `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6…4374`, user 1654 | `Directory.Build.targets` |
| Aspire | AppHost SDK `Aspire.AppHost.Sdk/13.6.1`, `AspireUseCliBundle`, stays on the 13.6 family together with the gateway | AppHost csproj, `docs/upgrade-guide.md` |

### 2.2 Initial `Directory.Packages.props`

| Group | Package | Version | Status |
|---|---|---|---|
| Aspire AppHost | `Aspire.Hosting.Docker`, `.PostgreSQL`, `.Redis`, `.JavaScript` | 13.6.1 | gateway |
| | `Aspire.Hosting.Keycloak` | 13.6.1-preview.1.26506.6 | gateway |
| | `CommunityToolkit.Aspire.Hosting.Ollama` | 13.6.0 | gateway (optional local BGE-M3, §4.4) |
| | `MessagePack` | 2.5.307 | gateway (GHSA pin) |
| | `Aspire.Hosting.Python` | 13.6.1 | *to verify* (only for `Parser:Mode=local`, §4.1) |
| Runtime (gateway) | `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.ServiceDiscovery` | 10.11.0 | gateway |
| | `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting` | 1.19.1 | gateway |
| | `OpenTelemetry.Instrumentation.AspNetCore`, `.Http`, `.Runtime` | 1.19.0 | gateway |
| | `Microsoft.Extensions.Hosting`, `Microsoft.EntityFrameworkCore.Relational`, `.Design`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, `.OpenApi`, `.Authentication.JwtBearer`, `.Authentication.OpenIdConnect` | 10.0.13 | gateway |
| | `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | gateway |
| | `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, `Aspire.StackExchange.Redis` | 13.6.1 | gateway |
| | `Scalar.AspNetCore` | 2.18.0 | gateway |
| Vectors | `Pgvector` / `Pgvector.EntityFrameworkCore` | 0.3.2 / 0.3.0 | NuGet-checked; EF 10 compatibility *to verify* in P0 |
| | `CommunityToolkit.VectorData.PgVector`, `Microsoft.Extensions.VectorData.Abstractions` | 1.0.2 / 10.11.0 | **not added**: retrieval is hand-written SQL (knowledge design §6) |
| AI | `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI` | 10.11.0 | NuGet-checked (`.OpenAI` *to verify*: listed together); **not used by the chat role**, whose gateway calls are a raw `HttpClient` + copied `SseReader` (response headers must be read before the body, chat design §2.1) |
| | `Microsoft.ML.Tokenizers` | 2.0.1 | NuGet-checked (chunk sizing) |
| Ingestion | `AngleSharp` 1.8.6, `SmartReader` 0.11.1 | as listed | NuGet-checked (the worker parses HTML, text, Markdown and CSV only; every PDF goes to the parser) |
| | `Microsoft.Graph` | 6.9.0 | NuGet-checked; P2, worker only (SharePoint). The P2 Graph member-group lookup needs no SDK: the admin copies the gateway's plain `GraphTokenClient` + `HttpClient` (identity design §2.4, gateway design §4.4) |
| | `Microsoft.Playwright` | same as renderer image (§5.4) | P2, only with the renderer; *to verify*; exact match required |
| | `Cronos` | *to verify* | schedules (worker) |
| Pipelines | JSON Schema 2020-12 validator (candidate `JsonSchema.Net`) | *to verify* (engine design §23: licence, maintenance, 2020-12 support) | P1, `Ume.Rag.Pipelines` (template parameter schemas, admin and worker; generic schema-driven admin forms) |
| Text | `sv_SE` Hunspell word list (LibreOffice dictionaries, pinned commit and SHA-256) | – | embedded resource of `SwedishDecompounder` (data-model §3.1); licence in postponed P2 |
| MCP | `ModelContextProtocol`, `ModelContextProtocol.AspNetCore` | 2.3.0 | NuGet-checked (knowledge design step 7: attribute and transport names); P2 (MCP endpoint group, tool hub, template); recheck at P2 start. No further JSON-Schema, YAML or MSAL package in P2; MSAL in P3 for OBO |
| Blobs | `AWSSDK.S3` (only dependency `AWSSDK.Core`) | ≥ 7 days old at pin time (4.0.104.6 checked 2026-10-30) | `Ume.Rag.Infrastructure` `S3BlobStore` (P1, contract-tested against the chosen server; `Minio` not used) ([`../performance-pass.md`](../performance-pass.md) §3.13) |
| Keys | `Azure.Security.KeyVault.Keys`, `Azure.Identity` | *to verify* | `IKeyProvider` for Azure; OpenBao via plain `HttpClient` (transit API), no package |
| Testing | `xunit.v3` 4.0.2, `xunit.runner.visualstudio` 4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0, `Shouldly` 4.3.0, `NSubstitute` 6.2.0, `WireMock.Net` 2.19.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, `Microsoft.Extensions.TimeProvider.Testing` 10.11.0, `Testcontainers.PostgreSql` / `.Redis` 4.16.0 | as listed | gateway (no `EntityFrameworkCore.Sqlite`: every DB test needs pgvector) |
| Benchmarks | `BenchmarkDotNet` | 0.15.8 | gateway |

### 2.3 Frontend (`src/admin-ui`)

- Same `package.json` scripts and toolchain as the gateway: `dev` (Vite), `build` (`vue-tsc -b && vite build`), `test`
  (Vitest), `lint` (`eslint . --max-warnings 0`), `typecheck`. Runtime dependencies copied with the gateway's ranges:
  `vue` ^3.5.43, `pinia` ^4.0.3, `vue-router` ^5.3.1, `@fontsource-variable/geist(-mono)` ^5.3.0, `material-symbols`
  ^0.47.6; dev: `vite` ^8.3.3, `vitest` ^5.0.3, `typescript` ~6.0.3, `tailwindcss` / `@tailwindcss/vite` ^4.3.3,
  `eslint` ^10.12.0 with `eslint-plugin-vue` and `eslint-plugin-vuejs-accessibility`.
- **No new runtime packages** beyond the gateway's (no canvas library, no UI kit, no i18n library).
- **Swedish UI**: the gateway's UI texts are English, so `utils/labels.ts`, `utils/problem.ts`, `utils/format.ts` and
  `index.html` (`lang="sv"`) are adapted, not copied; `utils/messages.ts` (error codes → Swedish) is new (admin design
  §4.2, §5.1).
- **Size budget**: a CI build step reports gzip sizes and fails above budget (initial JS ≤ 200 KB, lazy route ≤ 100 KB;
  admin design §10.2).
- **npm policy copied unchanged**: `.npmrc` with `ignore-scripts=true`, `min-release-age=7`, `allow-git=none` in
  `src/admin-ui` and `tests/e2e`; `npm ci` only in CI; `npm audit --audit-level=low` fails the build.
- **Design system**: the gateway has no package; it is `src/styles/tokens.css`, `main.css`, `icons.css`,
  `components/ui/*`, `components/layout/*` and `tailwind.config.cjs`. They are copied and listed in
  `src/admin-ui/design-system.lock` (gateway commit + SHA-256 per file). A CI step reports drift as a warning, never a
  failure, so the two UIs stay a family without coupled releases (admin plan, answered question 1).

### 2.4 Python parser (`src/parser`)

```
src/parser/
  pyproject.toml   uv.lock   .python-version (3.12)   Dockerfile   README.md
  ume_parser/      app.py (FastAPI), supervisor.py, slot.py, inspect.py, textlayer.py (pypdfium2 text-layer fast path), convert.py (Docling), ner.py (`/v1/ner` lane, parser design §7.8),
                   signals.py (garble detector), limits.py, sniff.py, models.py (pydantic contract), info.py, health.py, settings.py
  tests/           test_contract.py, test_convert.py (fixtures from ../../tests/fixtures), test_offline.py
  scripts/         download_models.py, export_schema.py (writes contracts/parser/v1/*.json), bench.py, ocr_check.py (OCR acceptance check, parser design §9.3), update_goldens.py
```

| Topic | Choice |
|---|---|
| Lock tool | **uv** (lockfile with hashes for every wheel; `uv sync --frozen --no-dev` in the image). pip-tools rejected: two tools for one job |
| npm-policy equivalent | `[tool.uv] no-build = true` (wheels only, so no package runs `setup.py`: same intent as `ignore-scripts`), `exclude-newer` 7 days before the lock date (same intent as `min-release-age`), `uv lock --check` in CI, `pip-audit` against the export |
| Packages | `docling-slim` with selected extras (2.145 reviewed, parser design §12), `pypdfium2`, `defusedxml`, `psutil`, `pillow`, `transformers`/`tokenizers`/`safetensors` (NER, already pulled in by `docling-ibm-models`), `fastapi`, `uvicorn`, `pydantic`, `python-multipart`, `opentelemetry-instrumentation-fastapi` + OTLP exporter; dev: `pytest`, `ruff`, `pip-audit`. Exact versions *to verify* at lock time (V1) |
| PyTorch | CPU wheels from the PyTorch CPU index (`[[tool.uv.index]]` with `explicit = true`), saving ~2 GB against the CUDA build |
| Models | Docling layout and TableFormer, KB BERT NER (`KBLab/bert-base-swedish-cased-ner`, rev `f3c83af`, `/opt/models/kb-ner`) downloaded at **image build** by `scripts/download_models.py` with pinned revisions and SHA-256 into `/opt/models`; runtime sets `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`, Docling `artifacts_path=/opt/models`; `test_offline.py` converts a PDF with networking disabled |
| OCR | **Tesseract 5 CLI, `swe` only** (Debian package); P1 acceptance check: å/ä/ö recall ≥ 95 % on a 20-page Umeå sample (parser design §9.3; postponed P1 closed) |
| Worker tokenizer | `Microsoft.ML.Tokenizers` 2.0.1 (MIT) with BGE-M3's `sentencepiece.bpe.model` (a Unigram model, pinned revision and SHA-256) as a worker resource; parity test against Hugging Face (processing design §6.1) |
| Licences | `src/parser/LICENSES.md`, generated at build from package metadata, Debian copyright files and the model table (parser design §12; before the pilot) |

## 3. Projects and roles

| Project | SDK | References | Image | Runs as |
|---|---|---|---|---|
| `Ume.Rag.Domain` | `Microsoft.NET.Sdk` | – (no packages except `Microsoft.Extensions.AI.Abstractions` types if needed) | – | – |
| `Ume.Rag.Infrastructure` | `Microsoft.NET.Sdk` + `Microsoft.AspNetCore.App` | Domain; Npgsql EF, Pgvector, Aspire Npgsql/Redis, DataProtection EF, M.E.AI.OpenAI (not on the chat path: `IGatewayClient` uses a raw `HttpClient`, chat design §2.1) | – | – |
| `Ume.Rag.ServiceDefaults` | `Microsoft.NET.Sdk`, `IsAspireSharedProject` | OTel, resilience, service discovery | – | – |
| `Ume.Rag.Retrieval` | `Microsoft.NET.Sdk` | Domain, Infrastructure; Pgvector (knowledge design §2.1) | – | – |
| `Ume.Rag.Pipelines` | `Microsoft.NET.Sdk` | Domain; JSON Schema validator (§2.2) (engine design §4) | – | – |
| `Ume.Rag.Answering` | `Microsoft.NET.Sdk` | Domain, Retrieval, Infrastructure | – | – |
| `Ume.Rag.Api` | `Microsoft.NET.Sdk.Web` | Infrastructure, ServiceDefaults, Answering; JwtBearer, OpenApi, Scalar, MCP SDK and `Ume.Rag.Tools` (P2) | `ume-rag/api` | `rag-api` (+ `rag-api-b` from P2) |
| `Ume.Rag.Worker` | `Microsoft.NET.Sdk.Web` (health + metrics endpoint) | Infrastructure, ServiceDefaults, Pipelines, Retrieval (retrieval evaluator, decompounder; neither `Ume.Rag.Answering` nor `Ume.Rag.Tools`); AngleSharp, SmartReader, Graph (P2), Playwright (P2, only with the renderer), Cronos, Tokenizers | `ume-rag/worker` | `rag-worker` |
| `Ume.Rag.AdminApi` | `Microsoft.NET.Sdk.Web` | Infrastructure, ServiceDefaults, Answering, Pipelines, `Ume.Rag.Tools` (P2); OIDC, OpenApi, Scalar (the P2 Graph member-group lookup uses a plain `HttpClient`, §2.2) | `ume-rag/admin` (`PublishAdminUi=true`) | `rag-admin` (+ `rag-admin-b` from P2) |
| `Ume.Rag.MigrationService` | `Microsoft.NET.Sdk.Worker` | Infrastructure, ServiceDefaults | `ume-rag/migrations` | `rag-migrate` |
| `Ume.Rag.FakeGateway` | `Microsoft.NET.Sdk.Web`, `ContainerFamily noble-chiseled` (as FakeLlm) | ServiceDefaults | `ume-rag/fake-gateway` (tests, R1 test) | dev/CI only |
| `Ume.Rag.AppHost` | `Aspire.AppHost.Sdk/13.6.1` | all runnable projects; links `ServiceDefaults/HealthEndpoints.cs` as the gateway does | – | dev |
| `Ume.Rag.Tools` (P2) | `Microsoft.NET.Sdk` | Domain, Infrastructure, MCP SDK | – (in `ume-rag/api`; its own host `rag-tools` only with the first external, OBO or write tool, P3) | chat role of `rag-api`; `rag-admin` |

Every service project starts with `if (await WebDefaults.ExitIfHealthProbeAsync(args)) return;`, then
`AddDeploymentSecrets()` (key-per-file from `Security:SecretsDirectory`), `AddServiceDefaults()`, `AddRagDatabase()`.

**One binary, several roles.** Configuration switches, validated at start (`ValidateOnStart`, unknown values fail):

| Setting | Values | Default | Effect |
|---|---|---|---|
| `Rag:Roles` (env `Rag__Roles`) on `Ume.Rag.Api` | comma list of `knowledge`, `chat`, `mcp` | `knowledge,chat` (P1), `knowledge,chat,mcp` (P2) | maps only those endpoint groups; health reports the active roles; `/version` lists them |
| `Retrieval:Mode` | `InProcess`, `Http` | `InProcess` | `chat` without `knowledge` requires `Http` and `Retrieval:BaseUrl`; `IRetriever` is then the HTTP client with a `ume-rag-sk-` service key |
| `Worker:Classes` on `Ume.Rag.Worker` | job classes (`preview`, `command`, `sync`, `fetch`, `parse`, `embed`, `eval`; P2 `attachment`; crawling is part of `sync`/`fetch`; data-model §4.2) | all | lets a later deployment run e.g. a `parse`-only worker next to the parser host; per-instance concurrency per class is the deployment setting `Worker:Concurrency:<class>` (defaults = engine design §9.1 table), `Settings.WorkerConcurrency` keeps only fleet-wide shares and caps ([`../performance-pass.md`](../performance-pass.md) §3.4) |
| `Worker:Maintenance` | `true`/`false` | `true` | partition maintenance, content retention, blob purge and the drift sweep; `true` on exactly one instance until P2, where several instances may run it, each task as a lease leader (`content."LeaderLeases"`, failover ≤ 30 s, [`../performance-pass.md`](../performance-pass.md) §3.5) |
| `Admin:Retention` on `Ume.Rag.AdminApi` | `true`/`false` | `true` | runs the config/chat/audit retention steps; each batch is idempotent under `pg_try_advisory_xact_lock(<step>)`, so the P2 a/b slots share or skip work; `false` is an off switch ([`../performance-pass.md`](../performance-pass.md) §3.5) |

Splitting Chat from Knowledge later is a Compose change: a second `rag-api` service with `Rag__Roles=chat`,
`Retrieval__Mode=Http`; no code change (plan §1).

**Settings rule (one for every document).** Deployment and infrastructure knobs (timeouts, pool sizes, concurrency
limits of one process, endpoint-protection limits such as `Admin:RequestsPerMinute`, `Api:AuthFailuresPerMinute`,
`Knowledge:DownloadsPerMinutePerAddress`, `Mcp:CallsPerMinutePerUser`, URLs, modes, secrets) are .NET configuration `Section:Key` (env `Section__Key`, appsettings or a
secret file), fixed per deployment and changed by a redeploy. Values that editors or platform admins change at run time
(models, thresholds, product quotas such as `Settings.Chat.MessagesPerMinutePerUser` and
`Settings.Tools.CallsPerMinutePerUser`, allow-lists, defaults, caps) are columns (or jsonb sections) of the database singleton
`config."Settings"` and are written `Settings.<Section>.<Key>` (`Settings.<Key>` for top-level columns; key case as
the section's jsonb defines it), validated by the admin API (admin design §4.6 V30, V32) and read at the confirmed config version. A value
lives in exactly one of the two places; a document that needs it names it in that form.

Knowledge-role settings (knowledge design §2.3, §3.5, §4.6, §5.3, §6, §9, §13): `Knowledge:OpenToClientKinds`
(default `["internal"]` in P1), `Knowledge:PublicBaseUrl` (= `UME_RAG_API_PUBLIC_URL`, for download links),
`Knowledge:DownloadGrantLifetime` (7 days; `sensitive` 1 hour), `Knowledge:DownloadsPerMinutePerAddress` (120),
`Knowledge:PublishApiReference` (default `true`, off in production until P2); `Retrieval:EmbeddingTimeout` (1,500 ms),
`Retrieval:MaxConcurrentSearches` (4 = ⌊(pool 12 − 3) ÷ 2⌋, data-model §1.1), `Retrieval:ExpandNeighbourChunks` (8), `Retrieval:Index` (`postgres` \|
`opensearch` \| `shadow`). The exact-scan threshold and the total expansion budget are **platform values in
`config."Settings".Retrieval`** (`exactScanMaxChunks` 10,000, `expandTotalMaxTokens` 8,000; data-model §2.1, knowledge
design §12.1), not deployment settings, so the API, the evaluator (worker) and the admin test panel always agree. The
admin host also needs `Gateway:*` for test-panel query embeddings (knowledge design §4.2).

Chat-role settings (chat design §3.7, §8, §12, §14.2): `Chat:PublishApiReference` (default `true`, off in production
until P2), `Chat:CondenseTimeout` (600 ms, hard; falls back to the raw question), `Chat:GatewayHeadersTimeout` (15 s, proposed), `Chat:StreamIdleTimeout` (60 s),
`Chat:MaxAnswerDuration` (240 s), `Chat:WriterEnqueueTimeout` (2 s), `Chat:CharsPerToken` (3); `Gateway:RequireDelegationEcho` (default `true`; decides when a missing delegation echo fails
closed; the ceiling header is always sent, plan §6); `OpenWebUi:BaseUrl`, `OpenWebUi:AdminApiKey` (feedback import), `OpenWebUi:ClientId`, `OpenWebUi:JwtSecret`,
`OpenWebUi:MaxLifetimeSeconds` (600) (user JWT, identity design §5.2); per instance `OpenWebUi:Instances:<name>:*`.
Platform-wide chat values (condense model, link allow-list, per-user rate) live in `config."Settings".Chat`
(data-model §2.1).

Tool settings (P2, tools design §2–§7), on the chat role of `rag-api` and on `rag-admin` (the hub runs in-process in both; no `ToolHub:*` settings):
`Tools:DriftCheckInterval` (15 min), `Tools:MaxResultChars` (16,000), `Tools:MaxArgumentBytes` (8 KiB),
`Tools:MaxPooledClients` (200), `Tools:AllowWrite` (`false`, `ValidateOnStart`), `Agent:HoldBackChars` (200),
`Agent:StepTimeEstimate` (8 s), `Agent:SkillFileCacheBytes` (32 MB); on the mcp role `Mcp:CallsPerMinutePerUser` (60).
Platform-wide tool values (agent defaults and hard caps, skill gate model) live in `config."Settings".Tools`.

Admin settings (admin design §2.2–§2.3, §4.10): `Admin:GatewayAdminUrl` (cross-links to the gateway admin UI),
`Admin:RequestsPerMinute` (120), `Admin:MaxSessionHours` (12), `Admin:AuditSourceAddress` (`Full`) (identity design §1.4),
`Admin:Retention` (above); `OrgSync:*` for the organisation and `GatewayModels` sync (interval 5 min, Data API base URL,
P2 `OrgSync:MaxAgeHours` 24 with the Graph member-group lookup (identity design §2.1); key `OrgSync__DataApiKey`, §6.3).

Worker settings besides `Worker:*` (connectors design §7, §8): `Crawl:AllowedPrivateRanges` (fed from
`UME_RAG_CRAWL_ALLOW`, §7.1); P2 with the renderer `Renderer:MaxPages` (2). Platform-wide crawl values live in `config."Settings".Crawl`.

Platform settings (data-model §5): `Config:MaxUnconfirmedSeconds` (90 s grace window for security-relevant decisions
on every service).

Scale-out settings ([`../performance-pass.md`](../performance-pass.md) §3.1, §3.4, §3.8, §3.9, §3.13; all deployment
values): `Worker:Concurrency:<class>` (per instance, above); P2 `Embedding:GlobalMaxInFlight` (fleet-wide embedding cap
via a Redis lease, default 2 × provider replicas); `Parser:Endpoints` (optional multi-endpoint parser list, not used in the
3- and 5-host topologies); `BlobStore:Kind` (`FileSystem` | `S3`; `FileSystem` on the single-host pilot),
`BlobStore:S3:ServiceUrl`, `BlobStore:S3:Bucket` (`rag`); `ConnectionStrings__ragdb-listen`
(LISTEN on its own connection string, same server today, so the pooled strings stay pooler-safe) and
`ConnectionStrings__ragdb-read` (read replica for K, V, H+D and the evaluator, only when its trigger fires).

## 4. Local development

### 4.1 AppHost resource graph (`src/Ume.Rag.AppHost/Program.cs`)

Same structure as the gateway's `Program.cs`: `AddDockerComposeEnvironment("compose").WithDashboard(false)`, generated
persistent secrets through the same `Secret(name, length)` helper, `WaitFor`/`WaitForCompletion(migrations)`,
`WithHttpHealthCheck(HealthEndpoints.ReadyPath)`, and feature toggles read from AppHost user secrets.

| Resource | Aspire call | Toggle | Notes |
|---|---|---|---|
| `postgres` + database `ragdb` | `AddPostgres("postgres").WithImage("pgvector/pgvector", <pinned tag>)` `.WithDataVolume(DevelopmentVolumes:Postgres)` | – | the stock pgvector image `ume-rag/postgres` is built from (§5.2; pgBackRest is not needed in dev), superuser in dev as in the gateway |
| `redis` | `AddRedis("redis", password: Secret("redis-password", 48))` | `Redis:Enabled` (default false: the pilot shape, in-memory `IRateLimitStore`/`ISharedCache`) | P2 path: sets `ConnectionStrings:redis`; prefix `ume-rag:` |
| `keycloak` | `AddKeycloak("keycloak").WithRealmImport("../../dev/keycloak")` | off when `Oidc:Authority` is set | env `UME_RAG_OIDC_CLIENT_SECRET`, `UME_RAG_DEV_USER_PASSWORD`, `UME_RAG_API_CLIENT_SECRET` |
| `fake-gateway` | `AddProject<Projects.Ume_Rag_FakeGateway>` | `FakeGateway:Enabled` (default true) | §4.3; `FakeGateway:Latency:*` passed through as in the gateway |
| `ollama` | `AddOllama("ollama").AddModel("bge-m3")` | `Ollama:Enabled` (default false, dev only, throws in publish mode as the gateway does) | real BGE-M3 embeddings behind the fake gateway |
| `parser` | `AddDockerfile("parser", "../parser")` | `Parser:Mode` = `container` (default) / `local` (`AddUvicornApp` from `Aspire.Hosting.Python`, hot reload) / `off` | internal endpoint only |
| `clamav` | `AddDockerfile("clamav", "../../images/clamav").WithVolume("ume-rag-clamav-db")` | `ClamAv:Enabled` (default true; `false` = worker uses an EICAR-only fake scanner) | dev freshclam uses the public mirror |
| `migrations` | `AddProject<…MigrationService>` | `Seed:Enabled` | `WaitFor(database)`; seeds dev key, BGE-M3 profile, demo data |
| `api` | `AddProject<…Api>("api", "https")` | – | `Rag__Roles=knowledge,chat,mcp`; `Gateway__BaseUrl` = fake or real gateway |
| `worker` | `AddProject<…Worker>("worker", "https")` | – | references parser, clamav |
| `mcp-kommun` | `AddProject<Ume.Mcp.Kommun>` (from `templates/mcp-server/`) | P2 | `ume-mcp-kommun`, first tool the mock fee calculator (tools design §8.5), reached by the api's in-process tool hub only |
| `adminapi` | `AddProject<…AdminApi>("adminapi", "https")` | – | `Oidc__ClientId=ume-rag-admin`; `Oidc:*` passed through when external |
| `admin-ui` | `AddViteApp("admin-ui", "../admin-ui")`, port **5174** (gateway: 5173), `PublishWithContainerFiles(adminUi, "wwwroot")` | – | both stacks can run side by side |
| `open-webui` | `AddContainer("open-webui", "ghcr.io/open-webui/open-webui", <pinned tag>)` | `OpenWebUi:Enabled` (default false) | env of chat design §4.3 (one connection = our api endpoint with the seeded dev key, `X-Rag-Owui-*` headers, title, tag and follow-up generation off); tag *to verify* (designed against v0.12.5) |

Ports in `launchSettings.json` and the AppHost's dashboard port are chosen outside the gateway's (gateway admin
7212, Vite 5173), so a developer can run the gateway's AppHost and ours at once (§4.4).

### 4.2 Keycloak realm (`dev/keycloak/ume-rag-realm.json`)

Realm `ume` (same name as the gateway's, so issuer paths look the same), adapted from `dev/keycloak/ume-realm.json`:

| Element | Content |
|---|---|
| User profile | declares `departmentCodes`, `teamCodes` (Keycloak ≥ 24 drops undeclared attributes) |
| Client `ume-rag-admin` | confidential, code + PKCE, redirect URIs `https://localhost:5174/signin-oidc` and the adminapi https port; mappers copied: `roles` (realm role mapper), `departmentCodes`, `teamCodes` (user attribute mappers); **new** `groups` mapper (group membership, full path off: department scope from groups in the claim) |
| Client `ume-rag-api-dev` | client credentials (as the gateway's `ume-data-dev`), audience mapper `ume-rag-api`, roles mapper; service-account roles `Knowledge.Read.All`, `Chat.Invoke.All` for integration and machine-token tests |
| Client `ume-rag-chat-dev` | public, code + PKCE, loopback redirect; default scope `Chat.Use`, optional `Knowledge.Read`, `Mcp.Use`; delegated-token and MCP tests |
| Client `open-webui-dev` | for `OpenWebUi:Enabled` with OIDC login |
| Client scopes | `Chat.Use`, `Knowledge.Read`, `Mcp.Use` (optional, audience mapper `ume-rag-api`, in `scope`) |
| Realm roles | `rag-platform-admin`, `rag-department-admin`, `rag-pipeline-editor`, `rag-reviewer`, `rag-skill-editor` (P2), `rag-editor`, `rag-viewer` (identity design §1.3), plus app permissions `Knowledge.Read.All`, `Chat.Invoke.All` |
| Groups | `Byggnadsnämnden`, `rag-pilot-users` (all test users) |
| Users | `rag-platform-admin` (`departmentCodes` 1000, 3000, 5000), `rag-department-admin` (1000), `rag-pipeline-editor` (1000), `rag-reviewer` (1000), `rag-skill-editor` (1000, P2), `rag-viewer` (1000), `rag-group-member` (no attribute; member of group `Byggnadsnämnden`, a member group of department 3000, tests department scope from groups), `rag-no-role` (1000, no role: every `/api` call 403), `rag-chat-user` (no admin role; delegated chat tokens) — codes as the gateway's demo data. Full realm: identity design §11 |
| Passwords / secrets | from `UME_RAG_DEV_USER_PASSWORD`, `UME_RAG_OIDC_CLIENT_SECRET`, `UME_RAG_API_CLIENT_SECRET` (realm import placeholders, as the gateway) |

### 4.3 FakeGateway (not the gateway's FakeLlm)

The gateway's FakeLlm cannot be reused: its `/v1/embeddings` returns **8-dimensional** vectors (`Vector()` takes 8
hash bytes) and it sends no `x-ume-residency` header. `Ume.Rag.FakeGateway` copies `FakeLatency.cs` and the chat
streaming code, and adds:

| Endpoint / behaviour | Purpose |
|---|---|
| `POST /v1/embeddings`: **1024 dimensions**, L2-normalised feature hashing of word unigrams and character trigrams | texts that share words are close, so retrieval and evaluation tests mean something |
| `POST /v1/chat/completions` (stream and not), answers built from the last user message and any `[n]` context, so citation tests can assert | chat, condense, evaluation generation and judging |
| Stream modes (chat design §18.4): configurable usage-only final chunk (on/off), broken stream mid-way, stall (> idle timeout), missing `[DONE]`, empty deltas, `finish_reason: length`; echoes `x-ume-session` | streaming edge cases |
| `GET /v1/models`; per-model residency in `appsettings.json` (`OnPrem`, `Eu`, `External`: the gateway's `DataResidency` names); lists only models with a target within the request's `x-ume-max-residency`, as the gateway does for delegating keys (gateway design §3.4) | model validation on assistant save (plan §6) |
| Response header `x-ume-residency`; honours `x-ume-max-residency` (case-insensitive names; other values 400 `invalid_request`; `403 max_residency_not_met` if no configured target fits) and answers `400 invalid_request` without it for keys flagged `RequireMaxResidency` (every dev key, on every endpoint incl. `/v1/models`); three dev keys with per-level ceilings (`OnPrem`, `Eu`, open) and a key/level mismatch refusal; records `x-ume-on-behalf-of` and echoes it (gateway design §3, §5.2) | fail-closed and key-layer tests (plan §6, §7) |
| `GET /v1/catalog/departments` (Data API shape incl. `memberGroups`; department 3000 lists `Byggnadsnämnden`) and `/v1/catalog/teams` (incl. `code`, combined gateway release) | organisation sync without the gateway |
| Fault injection: model names `fake-fail-500`, `fake-slow`, `fake-wrong-residency`, `fake-drained` (503 with `Retry-After`: targets within the ceiling exist but are drained) | resilience and residency tests |
| Rerank endpoint (P1, scores from word overlap; path as the gateway's P2 endpoint, *to verify*) | the `IReranker` seam and the evaluator's with/without comparison |
| `FakeGateway:Upstream:Embeddings=http://ollama…` | real BGE-M3 vectors from Ollama when enabled |
| Scripted tool modes (P2): a fixture lists the assistant messages per step (`tool_calls` with ids and arguments, streamed in deltas, or text) | deterministic agent-loop tests (tools design §11.2) |

Feature request for the gateway (§12.2), delivered in the combined gateway release (gateway design §6): FakeLlm honours the
OpenAI `dimensions` parameter and a `fake-embed-<n>` model-name suffix, so the gateway's own fake serves 1024-dim
vectors when we run against a real local gateway.

### 4.4 Start scripts, seed data, real gateway

- `scripts/Start-Dev.ps1 [-Empty]` and `scripts/Start-E2E.ps1 [-Stop] [-Keep]`: copies with the AppHost path, volume
  `ume-rag-e2e-postgres` and `FakeGateway__Enabled=true` for E2E.
- **Seed** (migration service, only when `config."Collections"` is empty; never in Production): BGE-M3 platform
  embedding profile with its fixed id (model `bge-m3`, 1024 dims); demo departments 1000/3000/5000 synced from the
  fake catalog; a `public` collection *Umeå kommun – exempel* with an upload source holding
  `tests/evaluation/smoke/documents/*` (no crawl needed offline) and its 30 smoke questions as the evaluation set;
  assistant *Demo-assistent* (`public`); client *Open WebUI (dev)* at ceiling `internal` with key `ume-rag-sk-` from
  `Secret("dev-key", 43)`; a local `IKeyProvider` key file under the AppHost's user-secrets directory.
- **Against a real local gateway** (gateway AppHost running): AppHost user secrets `FakeGateway:Enabled=false`,
  `Gateway:BaseUrl=https://localhost:<gateway port>`, `Gateway:Keys:<level>=<gateway dev keys>`. In the gateway's admin,
  add the Ollama provider with model `bge-m3` (the gateway's `Ollama:Enabled` brings `qwen2.5:0.5b` for chat) and an
  alias matching our profile's model name, or (from the combined gateway release) point that alias at FakeLlm model
  `fake-embed-1024`. Older gateways' FakeLlm embeddings (8 dims) are refused by our dimension check with a clear error.
- **Gateway key PII policy**: the `OnPrem` keys used for embeddings (api and worker) must have gateway `PiiPolicy` `Off` or `Allow` for `/v1/embeddings`, never `Redact`/`Block` (a redacting key would
  silently change queries containing personnummer); the knowledge role checks it at startup with a canary embedding
  (knowledge design §5.2, §5.6).

## 5. Container images

### 5.1 Overview

| Image | Built by | Base | User | Root fs | Health probe | Expected size |
|---|---|---|---|---|---|---|
| `ume-rag/api`, `worker`, `admin`, `migrations` | `dotnet publish -t:PublishContainer` | `aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6…` (gateway's) | 1654 | read-only, tmpfs `/tmp` | `dotnet Ume.Rag.<X>.dll --health-check` (`ExitIfHealthProbeAsync`, `UME_HEALTHCHECK_URL` default `https://localhost:8443/health/ready`) | 160–220 MB (admin + UI ~230 MB) |
| `ume-rag/fake-gateway` | SDK container | `noble-chiseled` | 1654 | read-only | same | ~130 MB |
| `ume-rag/postgres` | `images/postgres/Dockerfile` (a few lines, §5.2) | stock `pgvector/pgvector`, digest-pinned (pgvector 0.8.7 on Postgres 17, tag *to verify*) + PGDG `pgbackrest` package | 999 (`postgres`) | read-only, tmpfs `/tmp` (pgBackRest lock path), `/var/run/postgresql` | `pg_isready -d ragdb` | stock image + the `pgbackrest` package, *to verify* |
| `ume-rag/parser` | `src/parser/Dockerfile` | `python:3.12-slim-trixie` (digest-pinned; parser design §12) | 10001 | read-only, tmpfs `/tmp` (1 GB: tmpfs counts against the memory limit) | `python -m ume_parser.health` (HTTPS GET `/health/ready`, exit code; no curl) | target ≤ 3.5 GB, *to verify* (parser design §12; torch CPU + models; Tesseract `swe` only; no LibreOffice, EasyOCR or `python-oxmsg`) |
| `ume-rag/clamav` | `images/clamav/Dockerfile` | `clamav/clamav-debian:1.4` (LTS, digest-pinned; *to verify* tag) | 100 (`clamav`) | read-only, volume `/var/lib/clamav`, tmpfs `/run/clamav`, `/tmp` | `clamdscan --ping 3` | ~350 MB + ~350 MB signatures in the volume |

All Dockerfiles: digest-pinned `FROM`, multi-stage, `apt-get` versions from the pinned base only, no package caches,
`LABEL org.opencontainers.image.source/version/revision`, `USER` numeric, no shell needed at runtime for the .NET
images (chiseled). Compose adds `read_only`, `cap_drop: [ALL]`, `no-new-privileges` (the gateway's `x-hardened`
anchor). The renderer image (`images/renderer/`, Playwright server + Chromium) is built only if the renderer is
adopted (P2, §5.4).

### 5.2 Postgres (stock image + pgBackRest)

- **Image**: `ume-rag/postgres`, derived from the stock `pgvector/pgvector` image for pgvector 0.8.7 on Postgres 17
  (`FROM` by digest) with only the PGDG `pgbackrest` package and `pgbackrest.conf` added (simplification pass §14);
  pinned by digest in the release bundle (`POSTGRES_IMAGE`). No compile, no dictionary, no extension build. Supersedes
  the "own container everywhere" decision (simplification pass §3). Managed Postgres is allowed where pgvector ≥ 0.8 and the settings of
  §6.2 are available.
- **Init scripts are mounted, not baked**, as in the gateway: `deploy/postgres/init.sql` →
  `/docker-entrypoint-initdb.d/10-roles.sql` (roles from `/run/postgres-secrets/*_password`, database `ragdb`,
  `CREATE EXTENSION vector` as bootstrap superuser, data-model §1); `postgresql.conf` and `pg_hba.conf` mounted.
- **Image test** (CI job `images`, `deploy/postgres/test.sh`): start the pinned image, run `init.sql`; `SELECT
  extversion FROM pg_extension WHERE extname='vector'` equals the expected version; an HNSW index builds on 1024-dim
  vectors; `pg_stat_statements` loads; `wal_compression=lz4` is accepted; `pgbackrest` runs as uid 999 on the
  read-only root file system and `archive_command` pushes WAL from inside the container to a local test repository
  (`pgbackrest check`). The Swedish search tests are .NET tests (`SwedishTextSearchTests`, data-model §3.1).
- **P0 comparison** (one-off, recorded in `docs/`): the .NET decompounder against Postgres `ispell` with the same
  `sv_SE` word list, in a throwaway container that has the dictionary files, on a word sample (to verify, §14).

### 5.3 Parser image (`src/parser/Dockerfile`)

Stage 1 `python:3.12-slim-trixie` + uv (copied from the pinned `ghcr.io/astral-sh/uv` image): `uv sync --frozen
--no-dev --no-install-project` (`docling-slim` with selected extras, `pypdfium2`, `defusedxml`, `psutil`, `pillow`),
then `python scripts/download_models.py --out /opt/models` (pinned revisions; the **only** step with network). Stage 2
same base, apt from a dated `snapshot.debian.org` (≥ 7 days old): `tini tesseract-ocr tesseract-ocr-swe` + fonts,
`libgl1 libglib2.0-0`;
copy the venv and models; `USER 10001`; env `HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 OMP_NUM_THREADS=${PARSER_THREADS}`;
`ENTRYPOINT ["tini","--"]`, `CMD ["python","-m","ume_parser"]`: a supervisor that starts uvicorn (API only) and
`PARSER_WORKERS` pre-warmed conversion processes plus one inspect process and one NER process `ner-0` (parser design
§10.1; env `NER_THREADS` 2, `NER_QUANTIZE` 1, `NER_QUEUE` 4, `NER_PERSON_LABELS`; called only when the personal-data
stage's gate allows, processing design §9.6). **No `uvicorn --workers` and no `--limit-concurrency`**: the latter
counts every connection, so health probes would be refused while all slots are busy. Request limits: 60 MB body, 20
pages per request, 2 000 pages per document, 120 s deadline, inspect 10 s (parser design §11.1). GPU variant later as
a second target (`--target gpu`).

### 5.4 Renderer image (P2, only if needed)

Built only if a P1-start check shows that pilot sites need JavaScript (umea.se is server-rendered; simplification
pass §1, §13). The design stays in connectors design §8 (Playwright server + Chromium headless shell, exact
`playwright-core` = `Microsoft.Playwright` version, worker-side SSRF on every browser request, no egress, Chromium
sandbox via Chrome's seccomp profile).

### 5.5 ClamAV image (`images/clamav/Dockerfile`)

Official ClamAV LTS image plus our `freshclam.conf` and `clamd.conf`. **Decided: no internal mirror**; freshclam uses
the official `database.clamav.net` directly, checks 4 times a day; `clamd` on TCP 3310 for the worker (`INSTREAM`),
`StreamMaxLength 60M` (uploads are ≤ 50 MB), `MaxFileSize 60M`, `MaxScanSize 200M`, `ConcurrentDatabaseReload no` on a
16 GB host (avoids doubling memory during reload; scans wait ~30 s) and `yes` on 24 GB. If `database.clamav.net` is
unreachable the signatures age; the worker reads their age with `zVERSION` every 10 minutes (connectors design §11) and
reports `degraded` after 48 h and `unhealthy` after 7 days, and then stops ingesting new files (fail closed).

## 6. Production Compose

### 6.1 Networks

Fixed subnets so the host firewall (`deploy/host/docker-user-firewall.sh`, `DOCKER-USER` chain) can target each one.
`(+b)` = the P2 b slots; the pilot runs one slot.

| Network | Subnet | `internal` | Members | Egress |
|---|---|---|---|---|
| `edge` | 172.30.1.0/24 | no | rag-api(+b), rag-admin(+b) | admin: OIDC authority only in P1 (`login.microsoftonline.com`, no Graph), from P2 Graph for the member-group lookup; api: the Open WebUI host (feedback import, chat design §14.2); api from P2: OIDC authority (JWKS), Graph (P2 audiences), OBO (P3) (identity design §7–§9) |
| `data` | 172.30.2.0/24 | **yes** | postgres, rag-api(+b), rag-admin(+b), rag-worker, rag-migrate | none |
| `backup` | *to allocate* | no | postgres (pgBackRest `archive-push` and backups, §7.4); the upload copy job | firewall: only the backup target (§7.4) |
| `llm` | 172.30.3.0/24 | no | rag-api(+b), rag-worker, rag-admin(+b) | firewall: only the gateway's address and port (gateway's `llm` name) |
| `ingestion` | 172.30.4.0/24 | **yes** | rag-worker, rag-clamav (P2: rag-renderer, if adopted) | none |
| `parser` | 172.30.5.0/24 | **yes** | rag-worker, rag-parser | none (parser has no other network) |
| `crawl` | 172.30.6.0/24 | no | rag-worker (a P2 renderer is **not** on it: the worker fetches and fulfils every browser request, connectors design §8.3) | firewall: drop RFC 1918, link-local, loopback, the host's own addresses except the allow-list `UME_RAG_CRAWL_ALLOW` (municipal hosts); app-level `IEgressGuard` in addition |
| `mirror` | 172.30.7.0/24 | no | rag-clamav | firewall: only `database.clamav.net` (decided: no internal mirror) |
| `mcp` (P2) | *to allocate* | **yes** | rag-api(+b) and rag-admin(+b) (in-process tool hub; skill gate, discovery, drift polling), hosted MCP servers; never rag-worker | none |
| `mcp-<name>-egress` (P2) | one per hosted server (172.30.8.0/21 proposed, tools design §15) | no | that MCP server | firewall: only its wrapped system's address and port |

Ports, as the gateway (`${…_BIND:-127.0.0.1}`): `rag-api` `127.0.0.1:8543`, `rag-admin` `127.0.0.1:9543` (P2:
`rag-api-b` `:8544`, `rag-admin-b` `:9544`) → container 8443. The municipal reverse proxy terminates public TLS and
re-encrypts to these (gateway runbook *Reverse proxy and PKI*; `init-deployment.sh --proxy` writes the nginx file).
Nothing else is published. These networks are per host; across hosts (§6.4) services talk over the routed LAN with
TLS, never a Docker overlay network.
The proxy keeps the gateway's 1 MB body limit except on the admin upload route
(`PUT /api/upload-batches/{batchId}/files/{n}`): `client_max_body_size 60m` and `proxy_request_buffering off` there
(admin design §2.2).

### 6.2 Resource zones on the pilot host (8 vCPU)

Compose keys per service: `cpus`, `cpu_shares` (cgroup v2 maps it to `cpu.weight`), `mem_reservation`, `mem_limit`,
`pids_limit`. The ingestion services also get `cgroup_parent: rag-ingestion.slice`, a systemd slice installed by
`deploy/host/rag-ingestion.slice` that caps the **whole zone** (requires cgroup v2 with the systemd cgroup driver: *to
verify* on the host image).

| Container | Zone | `cpus` | `cpu_shares` | `mem_reservation` | `mem_limit` 16 GB | `mem_limit` 24 GB | `pids_limit` | Notes |
|---|---|---|---|---|---|---|---|---|
| postgres | data | 3.0 | 1024 | 2g | 3g | 5g | 512 | `shm_size: 1g`; settings below; limit sized from the P0 measurement, including `archive-push` and the scheduled `pgbackrest backup` running inside it (§7.4) |
| rag-api (P2 + rag-api-b) | data | 2.0 each | 1024 | 384m | 1g each | 1.5g each | 512 | `DOTNET_GCHeapHardLimitPercent=75` |
| rag-admin (P2 + rag-admin-b) | control | 1.0 each | 512 | 256m | 512m each | 768m each | 256 | `DOTNET_GCHeapHardLimitPercent=75` |
| rag-migrate | – | 1.0 | 512 | – | 512m | 512m | 128 | one-shot; `DOTNET_GCHeapHardLimitPercent=75` |
| rag-worker | ingestion | 1.5 | 256 | 512m | 1.5g | 2g | 512 | `DOTNET_GCHeapHardLimitPercent=75` |
| rag-parser | ingestion | **4.0** (half the host) | 128 | 2g | 3.5g (1 worker × 4 threads) | 6g (2 workers × 2 threads) | 256 | `PARSER_WORKERS`, `PARSER_THREADS`, `PARSER_SLOT_MAX_RSS_MB`, `NER_*`; the NER process (`ner-0`, ≈ 0.5 GB model) shares this limit with a 2 800 MB slot watchdog, tight at 3.5g: measured in P0/R1 (*to verify*) |
| rag-clamav | ingestion | 1.0 | 128 | 1.2g | 1.5g | 3g | 128 | `ConcurrentDatabaseReload` §5.5 |
| `rag-ingestion.slice` | ingestion | `CPUQuota=500%` | `CPUWeight=25` | – | `MemoryMax=7G` | `MemoryMax=11G` | – | zone cap |
| open-webui (optional) | edge | 1.0 | 512 | 512m | not co-hosted | 1.5g | 256 | §12.8 |
| `ume-mcp-<name>` (P2) | tools-hosted | 0.5 each | 256 | – | 384m each | 384m each | – | hosted MCP servers, `compose.mcp.yaml` (tools design §8.3) |

Sum of memory limits (ingestion containers counted at the slice cap, the one-shot migrator included), pilot with one
slot (six long-running containers, no Redis): 16 GB host ≈ 12 GB (OS and Docker keep ~2 GB; limits are not all
reached at once); 24 GB host ≈ 18.75 GB, ≈ 20.25 GB with Open WebUI. P2 adds the b slots, Redis (256m,
[`../performance-pass.md`](../performance-pass.md) §3.12) and 384m per hosted MCP server (`ume-mcp-kommun`): ≈ 14.1 GB on a
16 GB host and ≈ 21.6 GB on a 24 GB host, so P2 uses the 24 GB host **with Open WebUI on its own VM** (decided by the user
2026-10-30 in the consistency pass); the in-process tool hub may need a larger `rag-api` limit, measured in P2. Under
CPU contention the data plane (weight 1024) gets about 4× the CPU of each ingestion container; the slice keeps the ingestion zone at 5 cores even when every ingestion container is
busy. The R1 release test (§8.3) confirms or corrects these numbers.

**Postgres settings** (`deploy/postgres/postgresql.conf`, mounted and passed with `-c config_file=`; 16 GB / 24 GB):
`max_connections=100` (data-model §1.1), `shared_buffers=768MB / 1536MB`, `effective_cache_size=2GB / 3500MB`,
`work_mem=8MB`, `maintenance_work_mem=512MB / 1GB` (HNSW builds), `max_parallel_maintenance_workers=2`,
`max_worker_processes=8`, `wal_level=replica`, `archive_mode=on`,
`archive_command = 'pgbackrest --stanza=ragdb archive-push %p'` (synchronous, inside the container, §7.4), `archive_timeout=60`, `wal_compression=on`, `ssl=on` with the internal CA, `log_statement=none` (gateway),
`autovacuum_vacuum_scale_factor=0.02` and `autovacuum_vacuum_cost_limit=2000` (data-model §4.4),
`shared_preload_libraries='pg_stat_statements'` (capacity metrics, §9; was empty). `wal_compression=lz4` instead of `on`
if the stock image's build supports it (*to verify*, `deploy/postgres/test.sh`). `DOTNET_GCHeapHardLimitPercent=75` is set on
**every** .NET container (api, worker, admin, migrations), not only the api ([`../performance-pass.md`](../performance-pass.md) §4).

**Multi-host database profile** (P2; 64 GB database host, D in [`../performance-pass.md`](../performance-pass.md) §5; estimate, confirmed by the scale test):
`max_connections=200` (5-host budget, data-model §1.1), `shared_buffers=16GB`, `effective_cache_size=48GB`,
`autovacuum_work_mem=2GB`, `autovacuum_max_workers=5`, `max_parallel_maintenance_workers=4`, `max_wal_size=16GB`,
`checkpoint_timeout=15min`, `random_page_cost=1.1`, `effective_io_concurrency=200`; per-table settings on `Chunks`,
`CrawlHosts`, `CrawlRuns` are in data-model §2.2 ([`../performance-pass.md`](../performance-pass.md) §3.2).

### 6.3 Volumes, secrets, hardening

| Volume | Mounted in | Notes |
|---|---|---|
| `postgres-data` | postgres (rw; backups run inside it, a restore runs in a one-off container of the same image, §7.4) | LUKS-encrypted host disk (plan §8a baseline) |
| `rag-blobs` → `/data/blobs` | rag-worker (rw), rag-admin (rw, uploads), rag-api (**read-only**, citation downloads, knowledge design §3.5) | one container `rag/`, write-once keys (data-model §9); single host only: beyond one host the S3-compatible object store (bucket `rag`) replaces this volume (§6.4). Master key files (file key provider, plan §8a): `rag-chat-<level>` only in rag-api and rag-admin; `rag-blob-internal` in rag-worker and rag-admin (uploads) and read-only in rag-api (downloads); `public` blobs are not encrypted. P2: rag-api also writes `chat-tmp/` (chat attachments, chat design §15.2), so its mount becomes read-only plus a writable `chat-tmp/` sub-mount |
| `clamav-db` → `/var/lib/clamav` | rag-clamav | signatures |
| `openwebui-data` | open-webui | its own SQLite DB on an encrypted volume (§12.8) |

Secrets and certificates are files, never environment variables (gateway `compose.prod.yaml`): `${UME_RAG_SECRETS_DIR}/<service>`
→ `/run/app-secrets` (key-per-file: `ConnectionStrings__ragdb`, `ConnectionStrings__redis` (P2, multi-host only; without it the in-memory `IRateLimitStore`/`ISharedCache` are used), `Security__KeyPepper`,
`Gateway__Keys__OnPrem` / `__Eu` / `__External` (delegating gateway keys per level, plan §6: api and admin hold all three, the worker only `OnPrem` and `Eu`, never the open key; its non-on-prem LLM calls send `Eu`), `BlobStore__S3__AccessKey`/`BlobStore__S3__SecretKey` (**per service**, only with `BlobStore:Kind=S3`; bucket policies mirror the mounts above: worker read/write, admin write on `uploads/` and read, api read-only, P2 write on `chat-tmp/`; [`../performance-pass.md`](../performance-pass.md) §3.13), `Security__CrawlKeyPepper` (worker, P2: HMAC of host names in the Redis crawl leases, [`../performance-pass.md`](../performance-pass.md) §3.6), `Oidc__ClientSecret`, `OpenWebUi__JwtSecret`, `OrgSync__DataApiKey` (admin), `KeyProvider__*`,
`Security__DownloadGrantKeys__<kid>` and `Security__CacheKeyPepper` (api; knowledge design §4.6, §5.4), `Security__ConversationIdKeys__<kid>` and `OpenWebUi__AdminApiKey` (api; chat design §11.1, §14.2), `Security__ToolAuditKey` (api and admin; P2), `Api__ServiceKey` (worker, P2: client `rag-internal`, posts chat-attachment chunks
to the api's internal route, chat design §15.2; issued in the admin UI)), `${UME_RAG_CERTS_DIR}/<service>` →
`/run/certs` (service cert/key, `ca.pem`, `data-protection.pfx`; P2: the tool identity's Entra client-credentials certificate for
hosted MCP servers, in api and admin only, never the worker, tools design §2.2, §4.6), Postgres secrets → `/run/postgres-secrets`, pgBackRest repository settings and passphrase
(`${UME_RAG_SECRETS_DIR}/pgbackrest/repo.conf`) → `/etc/pgbackrest/conf.d/repo.conf` (postgres only, read-only). Hosted MCP servers get `${UME_RAG_SECRETS_DIR}/mcp-<name>` (tools
design §8.3).

Hardening: every service uses the `x-hardened` anchor (`read_only`, `cap_drop: [ALL]`, `no-new-privileges`, tmpfs
`/tmp`), numeric `user`, `restart: unless-stopped`, `stop_grace_period` 60 s (api/admin), 120 s (worker: finishes or
releases its job leases). `compose.hardening.yaml` is the overlay for `aspire publish` output in `deploy/generated/`,
verified by `Test-Deployment.ps1`, exactly as in the gateway; `compose.prod.yaml` is self-contained and kept in sync.

### 6.4 Moving the ingestion zone to its own host

From day one the ingestion zone is a separate file: the pilot runs
`docker compose -p ume-rag -f deploy/compose.prod.yaml -f deploy/compose.ingestion.yaml --env-file deploy/.env up -d --wait`.

| Step | Host A (data plane) | Host B (ingestion) |
|---|---|---|
| Compose | `compose.prod.yaml` only | `compose.ingestion.yaml -f compose.ingestion-remote.yaml` |
| Postgres | publish `5432` on host A's LAN address; `pg_hba.conf` adds `hostssl ragdb ume_rag_worker <host B>/32 scram-sha-256` | worker connection string `Host=<host A>;SSL Mode=VerifyFull` |
| Blobs | switch `IBlobStore` to S3 (`BlobStore:Kind=S3`, `AWSSDK.S3`; server: the municipality's S3-capable storage if IT offers one, else SeaweedFS, never MinIO built from archived source; postponed decision S2) or Azure Blob (plan §12.1) for admin uploads and worker; migrate `rag-blobs` with `rag-blob-migrate` command | – |
| Gateway | – | worker on its own `llm` network to the gateway |
| Sizes | ingestion rows removed; postgres gets 4 cpus / 6 GB | parser gets ≥ 6 cpus, 2–4 workers |

`init-deployment.sh --role data|ingestion|all` writes the matching `.env` and only that host's secrets (host B gets
the worker's secrets only).

**Three and five hosts** ([`../performance-pass.md`](../performance-pass.md) §5, answered 2026-10-30: Compose per host, roles per host; P2 onwards).
Same images on every host; each host runs a chosen set of services and `Worker:Classes`; all share one Postgres, one
Redis and one object store (single node on its own encrypted disk) on the data host D.

| Topic | Rule |
|---|---|
| Networks | routed LAN + host firewall + TLS, no Docker overlay; each host publishes only the ports its peers need on its LAN address (D: 5432, 6379 TLS, 443 S3); `deploy/host/docker-user-firewall.sh` gains a peer list per role; `pg_hba.conf` lists each peer host per role |
| Compose files | `compose.data.yaml`, `compose.api.yaml`, `compose.admin.yaml`, `compose.ingestion.yaml` (exists), `compose.parser.yaml`; `init-deployment.sh --role data\|api\|ingestion\|processing\|all` writes that host's `.env`, certificates and only its secrets |
| Host-local only | parser (each parser host runs its own `parse` worker on its internal `parser` network), clamd and a P2 renderer if adopted (one per fetch host): never published on the LAN |
| Blobs | the object store replaces the `rag-blobs` volume beyond one host; the upload copy (§7.4) by bucket replication or `rclone`/server tooling |
| Must be single | Postgres primary (plus a streaming standby on another host, required before production beyond the pilot, §7.4), Redis, object store, migrations (one-shot), lease leaders |
| Connections | Σ over instances of pool + LISTEN per role within the role's limit (data-model §1.1), checked at deploy time by `deploy/postgres/connection-budget.sh` |
| Secrets | generated once on the operator's machine, copied per role; master key files reach more hosts, so postponed decision K1 is decided before the first multi-host deployment |

## 7. Release bundle and upgrades

### 7.1 `init-deployment.sh` (adapted; same idempotency: never overwrites secrets)

| Option | Default | Writes |
|---|---|---|
| `--oidc-authority URL` | prompted | `UME_RAG_OIDC_AUTHORITY` |
| `--oidc-client-id ID` | `ume-rag-admin` (RAG's own registration, identity §2) | `UME_RAG_OIDC_CLIENT_ID` |
| `--oidc-client-secret-file FILE` | – | `secrets/admin/Oidc__ClientSecret` (own credential, never the gateway's) |
| `--api-host`, `--admin-host` | prompted (examples: `rag-api.intern.umea.se`, `rag-admin.intern.umea.se`) | certificates, `UME_RAG_API_PUBLIC_URL`, `UME_RAG_ADMIN_PUBLIC_HOST` |
| `--gateway-url URL` | prompted | `UME_RAG_GATEWAY_URL` (HTTPS; its CA appended with `--ca-file`) |
| `--org-sync-key-file FILE` | – | `secrets/admin/OrgSync__DataApiKey` (gateway Data API key `ume-dk-…`, `catalog.read` only; identity design §2.1) |
| `--gateway-key-file LEVEL=FILE` (repeated for `OnPrem`, `Eu`, `External`) | – (keys issued by the gateway team per deployable) | `secrets/{api,admin}/Gateway__Keys__<level>`; `secrets/worker/` only `OnPrem` and `Eu` (refuses `External` for the worker) |
| `--key-provider file\|openbao\|azure` + `--openbao-addr`, `--openbao-role-file` / `--keyvault-uri` | `file` refuses Production unless `--allow-file-keys` (test hosts and the pilot: postponed decision K1, plan §8a) | `KeyProvider__*`; with `file`: the master key files `secrets/keys/rag-chat-<level>`, `rag-blob-internal` (mode 0400), then a reminder to run `escrow-keys.sh` (§7.4) |
| `--backup-target URL` | prompted | pgBackRest `repo1` in `secrets/pgbackrest/repo.conf` (IT's backup storage: S3-compatible or a mounted share; decided with IT) and the upload copy target |
| `--crawl-allow CIDR[,…]` | none | `.env`, `deploy/host/` firewall input and `Crawl__AllowedPrivateRanges` for the worker's `IEgressGuard` (connectors design §7). (`--clamav-mirror` dropped: no mirror, §5.5) |
| `--role data\|api\|ingestion\|processing\|all` | `all` | §6.4 (multi-host roles: that host's `.env`, certificates and only its secrets) |
| `--proxy`, `--upstream-host`, `--rolling`, `--no-chown`, PKI file options, `--renew-certs`, `--renew-ca`, `--yes` | as gateway | as gateway |

It generates: the internal CA and service certificates (api, admin, worker, parser, postgres), the Data
Protection certificate, passwords for `bootstrap`, `ume_rag_migrator`, `ume_rag_api`, `ume_rag_admin`, `ume_rag_worker`, the pgBackRest repository passphrase (escrowed, §7.4), the key
pepper, the download-grant key (`Security__DownloadGrantKeys__<kid>`, ≥ 32 bytes; a new `kid` on rotation) and the query
cache-key pepper (`Security__CacheKeyPepper`), the conversation-id key (`Security__ConversationIdKeys__<kid>`, ≥ 32 bytes, chat design §11.1), the tool audit HMAC key (`Security__ToolAuditKey`, P2, tools design §9.1), the Open WebUI JWT secret, P2: the crawl key pepper (`Security__CrawlKeyPepper`), the renderer WebSocket path secret (if adopted) and, with `--role data` on the first multi-host deployment, the Redis certificate, password and ACL from `users.acl.example` (user `ume-rag`, `~ume-rag:*`) with `ConnectionStrings__redis` for api and worker, and `deploy/.env` (non-secret settings only).
The Open WebUI admin API key (`OpenWebUi__AdminApiKey`) is not generated: an operator creates the service account and
its key in Open WebUI and places the file (chat design §14.2).
**Gateway keys per deployable** (plan §6; [`../performance-pass.md`](../performance-pass.md) §3.9): the worker's keys
are its own and carry the gateway's existing per-key `MaxConcurrentRequests` cap, so a misconfigured worker fleet
cannot starve chat's query embeddings. A separate BGE-M3 query route/replica follows only when its trigger fires (query
embedding p95 during ingestion > 2 × idle).
Ownership: 999 (postgres; P2 redis), 1654 (.NET services), 10001 (parser), 10002 (P2 renderer), 100 (clamav).

### 7.2 `build-release-bundle.sh`

Same as the gateway's: `API_IMAGE ADMIN_IMAGE WORKER_IMAGE MIGRATIONS_IMAGE PARSER_IMAGE CLAMAV_IMAGE
POSTGRES_IMAGE` (`ume-rag/postgres`; + `RENDERER_IMAGE` if adopted), each pinned by digest, are written into every `${X_IMAGE:-…}` default; the
script fails on an unpinned image. Output `ume-rag-X.Y.Z.tar.gz` with `deploy/` (compose files, scripts,
`postgres/`, `pgbackrest/`, `redis/`, `proxy/`, `otel/`, `host/`). No Portainer stack in P0/P1 (§12.9).

### 7.3 Updates (single slot in the pilot, rolling from P2)

`rolling-update.sh` (adapted): pull → **extensions** (`upgrade-extensions.sql`, `ALTER EXTENSION vector UPDATE`, as the
bootstrap user through `docker compose exec postgres`, only when the pinned `POSTGRES_IMAGE` changed; the migrator
cannot, since it is not superuser) → `run --rm migrations` → services. **Pilot (one slot)**: `rag-api` and `rag-admin`
are replaced together in a short announced maintenance window (`release.yml` ships them from the same release: the
admin test panel and answer judge run the same `Ume.Rag.Answering` as the api). **From P2 (a/b slots)**: replace `rag-api`/`rag-api-b`, then
`rag-admin`/`rag-admin-b`, each only while the other is healthy (gateway logic). Always: **worker stop-then-start** (no
overlap: its pool would exceed the role's connection limit, and job leases make a short pause harmless) → parser and
clamav recreated if their image changed (the worker retries the in-flight range).

**Several hosts** (P2, §6.4, [`../performance-pass.md`](../performance-pass.md) §5.4): `rolling-update.sh --role` per
host, in order: extensions + migrations once (from D's operator shell) → A1 then A2 (each a/b as above) → admin → F → P
(worker stop-then-start per host; parser recreated). Mixed versions are safe because migrations are expand/contract and
jobs that need a newer build carry `MinBuild` in their payload (older instances requeue them without an attempt).

Migrations are expand/contract only (data-model §10, gateway ADR 017); every release notes *Rolling update: yes/no*.
Postgres image changes that restart Postgres itself are never rolling (a short data-plane outage, announced).

### 7.4 Backup, recovery and key escrow

Targets for the pilot: **RPO ≤ 5 min, RTO ≤ 4 h** (simplification pass §4).

| What | Tool | Notes |
|---|---|---|
| Postgres | **pgBackRest** inside the `postgres` container (`ume-rag/postgres`, §5.2): continuous WAL archiving by `archive_command` (`archive-push`, synchronous, so no spool volume; `archive_timeout` 60 s), weekly full, daily differential, 35-day retention (proposed); repository `repo1` on the backup target, encrypted (`repo1-cipher-type=aes-256-cbc`, passphrase escrowed). Scheduled backups: the host timer `rag-backup.timer` runs `backup-postgres.sh`, i.e. `docker compose exec -T postgres pgbackrest --stanza=ragdb --type=full\|diff backup` (expiry follows each backup); no long-running sidecar. `postgres` is on the `backup` network (§6.1) | **point-in-time restore** with `restore-pitr.sh --target-time` (stops api, admin, worker and postgres; `pgbackrest restore --type=time` in a one-off container of the `postgres` image on `postgres-data` (`docker compose run --rm`); starts Postgres in recovery, then the services) |
| Uploads | `backup-uploads.sh`: copies new `uploads/` objects to the backup target **within 15 minutes** (the only content that cannot be fetched again); write-once keys make the copy incremental | `rclone`/bucket replication beyond one host (§6.4) |
| Derived blobs | raw fetches, parse and stage outputs | re-creatable: backed up nightly or not at all (a restore re-fetches and re-parses) |
| Secrets | pepper, `data-protection.pfx`, `secrets/` | separately, through the secret manager (gateway runbook) |
| Long-lived keys | `escrow-keys.sh`: blob master key `rag-blob-internal`, chat master keys `rag-chat-<level>`, the pgBackRest passphrase, exported to the offline escrow (sealed media in two places, or the key service's own backup) | retention ≥ the data they protect; re-run after every rotation; restore drill reads them back |
| Shred-class keys | per-month chat shred keys, only once `Settings.ChatShreddingEnabled` is on (after A1) | never escrowed beyond their shred date; key-service snapshots that hold them are kept no longer than the shred schedule, and a key-service restore is followed by `reconcile-shredded-keys` (re-destroys keys listed as destroyed in `audit."AuditLog"`) |

Rules:

1. **Blobs are write-once**: a key is never overwritten and a deletion is a tombstone purged after the backup
   retention (35 days), so a point-in-time restore of the database always finds the blobs it references.
2. After a database restore the worker's orphan audit reports blobs without a referencing row (left to the purge) and
   documents whose derived blob is missing (re-fetched or re-parsed on the next sync; a missing upload is restored from
   the upload copy).
3. **Restore drill** (P0, then quarterly; runbook): a point-in-time restore onto an isolated host with blobs and the
   escrowed keys, then a chat history read, a document preview and a search query; record the achieved RPO and RTO.
4. **Standby**: a streaming replica (pgBackRest-seeded, on another host) is an **availability** requirement before
   production beyond the pilot (P2); it also serves `ConnectionStrings__ragdb-read` when that trigger fires.
5. Health: the last successful backup age (`pgbackrest info`, read by `backup-postgres.sh`) and the WAL archive age
   (`pg_stat_archiver`) are exported as metrics (§9; how the timer's result reaches the collector is *to verify*, §14);
   alerts when the last backup is older than 26 h, the timer unit failed, or the newest archived WAL is older than the
   RPO (5 min) (proposed).

### 7.5 Upgrade guide (`docs/upgrade-guide.md`)

Same structure as the gateway's: a section per release (*Rolling update: yes/no*, migrations, new grants, settings),
then the gateway's six-step *Upgrade procedure* and *Certificate and secret rotation*. RAG additions:

| Change type | Procedure |
|---|---|
| pgvector minor | new pinned `POSTGRES_IMAGE`; extensions step (§7.3) |
| Postgres major (17 → 18) | not rolling: `pg_upgrade` where possible (else `pg_dump`/`pg_restore` into a new volume), then a new pgBackRest stanza and full backup |
| Word list or decompounder update | changes `SearchText`: the release ships a `reindex-fts` worker command (recomputes the column in batches); search quality is checked by the evaluation smoke set before and after |
| Parser image (Docling, models) | the parse cache key includes the parser build, so old outputs stay valid; re-parsing is an opt-in publish plan |
| Embedding profile change | never a migration: a background re-embed with the advisory evaluation report (plan §9) |

## 8. CI/CD

**Decided (2026-10-29): GitHub Actions for everything, as in the gateway; no Azure DevOps.** App registrations
(identity §2 approval packages) are also created by a GitHub workflow (`entra.yml`, below).

### 8.1 Workflows

| Workflow | Phase | Trigger | Mirrors | Content |
|---|---|---|---|---|
| `verify.yml` | P0 | push to main, PR, dispatch, `workflow_call` | gateway `verify.yml` | §8.2 |
| `release.yml` | P0 | tag `v*.*.*` | gateway `release.yml` | verify → build and push all images to `ghcr.io/<owner>/ume-rag/<service>:X.Y.Z` (+ `latest` for non-pre-releases) → bundle pinned by digest → smoke test (init-deployment, `up --wait`, `/v1/models` = 401 without a key, admin `/` = 200, a Swedish compound query through the API, `pgbackrest check` in the postgres container, parser `/v1/info` from the worker container) → GitHub release with bundle, SBOM, `SHA256SUMS` |
| `deploy-e2e.yml` | P2 (with a/b slots) | PR touching `deploy/**`, `images/**`, ServiceDefaults | gateway `deploy-e2e.yml` | rolling update under load behind nginx (`compose-rolling-e2e.sh`), fails on one lost request; kind cluster only once §10 exists |
| `flake-hunt.yml` | P2 | weekly Mon 03:17, dispatch | copy | 5 runs incl. `Category=Flaky` |
| `parser-robustness.yml` | P2 | weekly, nightly once stable; dispatch | new | Atheris fuzzing of our own parser code (zip pre-check, sniffing, name sanitiser; the attachment splitter with email), 10 minutes each, and the mutation run against the container (parser design §15.4) |
| `perf.yml` | P1 (before the pilot) | weekly + before each release (dispatch) | new | R1 release test (§8.3) |
| `scale.yml` | P2 | dispatch (manual) on 3 test hosts: before the P2 multi-host rollout and after changes to the scale decisions; not per release | new | scale test ([`../performance-pass.md`](../performance-pass.md) §7): `tests/scale/Ume.Rag.FakeSites` (synthetic multi-host site), `tests/scale/Ume.Rag.ScaleSeed` (1M/5M/10M-chunk binary-COPY seed), FakeGateway with a GPU throughput model, 300-VU k6 chat; same pass rule as R1 for chat p95 TTFT |
| `evaluation.yml` | P1 (before the pilot) | nightly, dispatch | new | evaluation sets against the **test** deployment with real BGE-M3 (self-hosted runner, §12.4) |
| `entra.yml` | P0 (package 1) | dispatch, PR touching `infra/entra/**` (plan only) | new | `azure/login` with OIDC federation (no stored secret) → `what-if`/plan on PR → apply after approval by the GitHub environment `entra-test` / `entra-prod` (required reviewers = the municipality's approvers, or a link to their change ticket); Bicep with the Microsoft Graph extension (or Terraform `azuread`, §12.5) in `infra/entra/`, which owns **only RAG's own registrations** (`ume-rag-admin` and the API registration; never the gateway's); deployer identity `Application.ReadWrite.OwnedBy` + `Group.Create` only, so admin consent, role assignments and client secrets stay manual steps (identity design §12) |

### 8.2 What runs on every PR (`verify.yml`)

| Job | Steps | Source of the pattern |
|---|---|---|
| deploy-checks | image defaults stay `:latest` dev images; `docker compose … config --quiet` for prod, prod+ingestion, ingestion-remote (P2: prod+rolling) | gateway |
| images | build `ume-rag/postgres` from `images/postgres/Dockerfile` and run `deploy/postgres/test.sh` (pgvector version, HNSW build, `pg_stat_statements`, `wal_compression=lz4`, `archive-push` from inside the image, §5.2); build the parser image only when `src/parser/**` changed (else pull the main-branch image) | new |
| backend | `dotnet build UmeRag.slnx -c Release`; `dotnet test --filter-not-trait "Category=Flaky"` — unit, API (WebApplicationFactory + FakeGateway), **integration against the pinned pgvector image** (`TestPostgres.Image` = `UME_RAG_TEST_POSTGRES_IMAGE`) and, for the P2 Redis implementations of `IRateLimitStore`/`ISharedCache`, Redis (`TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=mirror.gcr.io/`), `DatabaseRoleTests`, `SwedishTextSearchTests`, the gateway-client ceiling handler test per call site (plan §6); vulnerable-package audit (gateway's `dotnet list … --vulnerable` + python assert) | gateway + new |
| base-image scan | Trivy 0.67.2 on the chiseled base, the `ume-rag/postgres`, parser and ClamAV images (`HIGH,CRITICAL`, `--ignore-unfixed`) | gateway (extended) |
| ui | `npm ci`, `lint`, `typecheck`, `test`, `build`, `npm audit --audit-level=low` in `src/admin-ui` and `tests/e2e`; design-system drift report (warning) | gateway |
| parser | `uv lock --check`, `ruff check`, `pytest` (incl. supervisor tests with a fake converter and `test_offline.py` with network disabled), `export_schema.py` + `git diff --exit-code contracts/` , `pip-audit` | new |
| contract | `Ume.Rag.ContractTests` start the parser image (Testcontainers) and convert `tests/fixtures/parser/*` with the worker's client; compare structure to golden JSON (tolerant to timings and versions); responses and error fixtures validated against `contracts/parser/v1/` | new |
| evaluation-smoke | AppHost-free: migration service + api + worker + FakeGateway + parser against Testcontainers; ingest `tests/evaluation/smoke`, run 30 questions; recall@10 ≥ 0.8 and every answer has a citation | new |
| e2e | browser tests (gateway `tests/e2e` setup): the admin **pilot journey** (admin design §5.4, §11.4) run with mouse and keyboard only, against a static fixture site container (nginx, Swedish pages and PDFs); the extended accessibility spec (axe, target size, reflow; zero CSP violations); role journeys | gateway (nightly + release, not every PR: same as the gateway only checks types on PR) |
| sbom | `dotnet publish` + `Microsoft.Sbom.DotNetTool@4.1.13`, artifact `ume-rag-spdx`; Syft SBOMs for the non-.NET images (*to verify* tool choice) | gateway (extended) |

### 8.3 R1 release test (`perf.yml`, P1)

Runs before the pilot and then before each release, on the pilot-shaped **test host** (8 vCPU / 16 GB, self-hosted runner, §12.4) with the release bundle and
FakeGateway as the model (latency profile `FirstTokenMs=400`, `TokenIntervalMs=25`, `JitterPercent=30`, as in the
gateway's `benchmarks/compare`). Phase 1 idle: k6 (from `benchmarks/compare/k6`) sends streamed chat at 5 req/s for
10 min; phase 2 the same during a full crawl of a static copy of 2 000 umea.se pages (served by nginx on the runner) plus
an OCR batch of 50 scanned PDFs. Pass: chat p95 TTFT (phase 2) ≤ 1.10 × phase 1, no error responses, no Postgres
connection refusals. Output: a Markdown report as the gateway's `latency_report.py`, attached to the release. The same
harness runs the chat **overhead run** (chat design §16.2 item 3: Open WebUI-shaped requests, `rag.chat.overhead`
p50/p95/p99).

### 8.4 Supply chain

| Item | Gateway today | Here |
|---|---|---|
| Actions | pinned by major tag (`actions/checkout@v4`, `setup-dotnet@v5`, …) | same tags at first; **pin by commit SHA** proposed for both repos (§12.10) |
| Images | base digests pinned; release images referenced by digest in the bundle | same, for every image incl. the stock pgvector image |
| SBOM | SPDX via `Microsoft.Sbom.DotNetTool` | same + image SBOMs |
| Signing / provenance | none (listed as the operator's responsibility in `security-and-compliance.md`) | propose `actions/attest-build-provenance` for pushed images in both repos (§12.10) |
| Dependency audits | `dotnet list --vulnerable`, `npm audit` | same + `pip-audit`, Trivy on every image |
| npm / Python policy | `.npmrc` (§2.3) | same + uv `no-build`, `exclude-newer` |

## 9. Observability

- **ServiceDefaults copied**: OTel logs (formatted message + scopes), metrics (`AddMeter("Ume.Rag")`, ASP.NET Core,
  HttpClient, runtime) and traces (`AddSource(appName)`, `AddSource("Ume.Rag")`, health filtered, `url.query` removed);
  JSON console outside Development; OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` (`UME_RAG_OTEL_ENDPOINT`) is set.
  Npgsql (and, from P2, Redis) tracing comes from the Aspire client integrations. The parser uses
  `opentelemetry-instrumentation-fastapi` with the same endpoint; the worker propagates `traceparent`, so one trace
  covers fetch → scan → parse → embed. Chat traces continue into the gateway (it already traces incoming requests).
- **No content in telemetry** (plan §8a): no prompts, chunk text, document titles or URLs of `sensitive` sources;
  query strings stripped (gateway); the worker's trace filter also removes `url.full` and `server.address` from fetch
  spans (connectors design §16). A unit test scans captured telemetry from a test chat for the test content.
- **Security events**: category `Ume.Rag.Security` (`SecurityEvents` copied from the gateway, `Printable()` included),
  same collector rules (`deploy/otel/collector-siem.example.yaml`, filter extended to both categories). Gateway ids are
  1001–3003 (2007, 2008, 3003 added by the combined gateway release); ours **5001–6999**: `5001 rag.auth.failed`, `5002 rag.access.denied`, `5003 rag.residency.refused`,
  `5004 rag.config.stale_refused`, `5005 rag.ceiling.refused`, `5101 rag.egress.blocked` (SSRF), `5102
  rag.malware.detected`, `5103 rag.pii.flagged`, `6001 rag.admin.change`, `6002 rag.admin.signin`, `6003
  rag.admin.signout`, `6004 rag.conversation.access`; 5101/5102 attributes per connectors design §7, §11 (SIEM: any 5102; P2 with the renderer: 5101 from `Render`); the engine design adds `5105 rag.pipeline.refused` and log scopes `ExecutionId`, `Stage`; the parser design adds `5104 rag.parser.limit_exceeded` (worker); identity adds `5006 rag.auth.throttled`, `5007` (reserved for the P2 Graph member-group lookup), `5008 rag.key.unexpected_use`, `5009 rag.mcp.client_refused`, `5010 rag.obo.refused`,
  `6005 rag.admin.session.extended`, `6006 rag.admin.access.denied`, `6007 rag.admin.data.read`; the chat design adds `5011 rag.gateway.delegation_missing`; the tools design adds (from `rag-api` and `rag-admin`) `5012 rag.tool.definition_changed`,
  `5013 rag.tool.upstream_refused`, `5014 rag.content.tag_neutralised`, `5015 rag.tool.result_refused` (tools design §9.3)
  (full table: identity design §10). Names and ids are a public contract (add, never rename).
- **Log scopes** (`BeginScope` in middleware and job runner): `ConfigVersion` (every service, every request and job),
  `RunId`, `JobId`, `JobClass`, `DocumentId`, `SourceId` (worker), `AssistantId`, `ClientId`, `Classification`,
  `ConversationId` hash (api).
- **Metrics** (meter `Ume.Rag`): `rag.chat.ttft` (histogram, by `level`, `client_kind`, `outcome`), `rag.retrieval.duration`, `rag.retrieval.coverage` with near-threshold distances (never query text),
  `rag.gateway.request.duration` and `rag.gateway.residency_refusals`, `rag.jobs.queued` / `rag.jobs.age` by class,
  `rag.parse.duration_per_page`, `rag.clamav.detections`, `rag.config.version` per instance, `rag.config.lag` and `rag.config.unconfirmed_seconds` (grace window),
  `rag.db.pool.in_use` per role, `rag.retention.deleted` by table, `rag.backup.age` (from `pgbackrest info`) and `rag.backup.wal_archive_age` (from `pg_stat_archiver`; §7.4), `rag.ingest.incomplete_runs` per source, `rag.evaluation.recall_at_10` by collection;
  ingestion adds `rag.jobs.*`, `rag.engine.*`, `rag.runs.*`, `rag.index.*`, `rag.scheduler.lag`, `rag.preview.duration`,
  `rag.reconcile.drift` (engine design §17), `rag.connector.*`, `rag.crawl.*`, `rag.render.*`, `rag.clamav.*`,
  `rag.deletion.*` (connectors design §16), `rag.parse.*`, `rag.chunk.*`, `rag.pii.*`, `rag.embed.*`, `rag.evaluation.*`
  (processing design §15), and the parser `rag.parser.*` (parser design §13.2), plus the egress canary in worker health
  (connectors design §7);
  retrieval adds `rag.retrieval.stage.duration`, `.embedding.cache`, `.embedding.failures`, `.degraded`, `.coverage`,
  `.candidates`, `.results`, `.collections_skipped`, `.concurrency_rejections`, `.stats.flush` and
  `rag.documents.downloads` / `.download.bytes` (knowledge design §15); chat adds `rag.chat.overhead`,
  `.stage.duration`, `.requests`, `.condense`, `.citations`, `.filtered`, `.tokens`, `.prompt.estimate_ratio`,
  `.stream.stalls`, `.writer.queue` / `.queue_bytes` / `.batch.duration` / `.dropped`, `.feedback`, `.uncited`,
  `.gateway.pii` (chat design §17; low-cardinality dimensions only, never assistant slugs on hot histograms); tools add
  `rag.agent.*`, `rag.tools.*`, `rag.mcp.requests`, `rag.skills.loads`, `rag.skills.gate` (tools design §10); the admin
  adds `rag.admin.validation.refusals`, `.jobs.enqueued`, `.sync`, `.test_panel` (+ `.duration`),
  `.conversation_access`, `.uploads` (+ `.bytes`), `.publish` (admin design §10.3); capacity metrics ([`../performance-pass.md`](../performance-pass.md)
  §3.14): `rag.jobs.slots.busy` / `.slots.total`, `rag.crawl.host_budget.utilisation`, P2 `rag.crawl.inflight_refusals` and
  `rag.embed.global_refusals`, `rag.embed.tokens_per_second`, `rag.embed.queue_wait` (by lane `ingest`/`query`),
  `rag.parser.slots_busy` / `slots_ready`, `rag.parser.ner.queue`, `rag.db.connections` per role vs limit,
  `rag.db.pool.wait`, `rag.db.hnsw.bytes`, `rag.db.hnsw.cache_hit`, `rag.db.vacuum.duration`, `rag.db.dead_tuples`,
  `rag.db.wal.bytes_per_second`, `rag.db.replication.lag` (P2 standby), `rag.blob.duration`, P2 `rag.leader.changes`.
  Postgres-side values come from `pg_stat_statements` (§6.2) and, from P2, the OpenTelemetry Collector's
  `postgresql` receiver (role `ume_rag_monitor`, data-model §1.1; metric set *to verify*).
- **Dashboards** (`deploy/otel/dashboards/`, Grafana JSON, *new*: the gateway ships none): *Data plane* (TTFT p50/p95,
  retrieval p50/p95 per stage, degraded rate, coverage `low` share per level, embedding cache hit rate, downloads,
  gateway latency, refusals), *Ingestion* (queue depth and age, parse throughput, failures, ClamAV),
  *Database* (connections per role vs. limits, pool waits, slow statements, partition sizes, backup and WAL archive age), *Config & security*
  (version lag per instance, fail-closed refusals, security events; admin refusals by code, conversation access events,
  sync health), *Tools* (P2: calls and outcomes per server, p95
  duration, drift observations, approvals pending, agent limit hits, cost per run p95, MCP requests; tools design §10),
  *Capacity* (per-stage saturation, queue age per class, host budget utilisation, embedding tokens/s vs cap, HNSW
  memory, connections vs budget; [`../performance-pass.md`](../performance-pass.md) §3.14). The Aspire dashboard is used in development only.

## 10. Kubernetes (later; mirrors `docs/kubernetes.md`)

Same shape as the gateway's `deploy/kubernetes`: `base/` (api ×2, admin ×2, worker ×1, migrations Job, probes on
`/health/*`, PodDisruptionBudgets, restricted Pod Security Standard), `components/datastores` (`ume-rag/postgres` as a
StatefulSet, pgBackRest inside; Redis), `components/ingestion` (worker, parser, clamav), `example/`, `create-secrets.sh`,
`deploy.sh`. What changes against Compose:

| Compose | Kubernetes |
|---|---|
| internal networks, firewall on bridges | NetworkPolicies: parser ingress only from worker, egress none; crawl egress via an egress gateway or CIDR policy |
| CPU shares, slice | requests/limits, `PriorityClass` `rag-data-plane` > `rag-ingestion`, ResourceQuota per namespace (`ume-rag`, `ume-rag-ingestion`) |
| one host | ingestion on its own node pool (`nodeSelector` + taint); data plane never schedules OCR (plan §2) |
| file blob volume | S3-compatible or Azure Blob only (no RWX volume) |
| Postgres container | the pgvector image in the StatefulSet; or CloudNativePG (its own backups and standby; pgvector in its images *to verify*); or managed Postgres (pgvector ≥ 0.8) |
| rolling-update.sh | `deploy.sh`: extensions Job (when needed) → migrations Job → rolling update |

## 11. P0 checklist (ordered)

P0 is a skeleton that builds, deploys, is observable and can be restored (simplification pass §1). References: plan
§11, identity §8 (P0), [`../README.md`](../README.md) P0 row.

| # | Task (each a small, verifiable deliverable) | Done when | Refs |
|---|---|---|---|
| 1 | Add the code layout to this repo (§1); copy `global.json`, `Directory.Build.*`, `.editorconfig`, `.gitattributes`, `dotnet-tools.json`, `aspire.config.json`, `.githooks/pre-commit` | `dotnet build` of an empty solution passes with warnings as errors | plan §11.1 |
| 2 | Projects from §3 (empty endpoints; P2 `Ume.Rag.Tools` and the MCP template not yet), incl. the `Ume.Rag.Retrieval`, `Ume.Rag.Pipelines` and `Ume.Rag.Answering` skeletons, `UmeRag.slnx`, `tests/` projects and `TestKit` | `dotnet test` runs (0 tests is fine) | plan §11.1, knowledge design §17, engine design §20 |
| 3 | `verify.yml` (backend, ui, deploy-checks, images) | green PR in GitHub | plan §11.1 |
| 4 | `Ume.Rag.ServiceDefaults` copied; health `/health/live`, `/health/ready`, `/version`, `--health-check` in every service | probe exits 0/1 in a container; OTLP traces visible in the Aspire dashboard | plan §11.2 |
| 5 | Postgres: `ume-rag/postgres` (pinned stock pgvector image + PGDG `pgbackrest`) + `deploy/postgres/test.sh`; `SwedishDecompounder` with the `sv_SE` word list and its unit tests; P0 comparison against Postgres `ispell` recorded | `bygglovsansökan` → `bygglov`, `ansökan` passes; image test green; comparison recorded (data-model §14) | plan §11.3, knowledge P0 |
| 6 | `deploy/postgres/init.sql`, `pg_hba.conf`, `app-roles.sql` from data-model §1; `Ume.Rag.MigrationService` with schemas, config outbox, unpartitioned `Jobs` with the partial unique `DedupKey` index, day-partitioned `RunItems`, `rag_partition_maintain()`; role defaults `hnsw.ef_search`/`hnsw.iterative_scan`/`hnsw.max_scan_tuples`; `content.rag_or_query`; `DatabaseRoleTests`, connection caps and `statement_timeout` asserted; `SwedishTextSearchTests` (a part matches through Postgres); pool sizes measured | integration tests green against the pinned image; pool measurement recorded | plan §11.3, knowledge design §17 |
| 7 | Config-version library (`ConfigVersionMonitor`: LISTEN `rag_config` + 2 s poll, grace window `Config:MaxUnconfirmedSeconds`) and log scope; serialised config writes (`FOR UPDATE` on the version row) | tests: a revoked key is refused as soon as the notification arrives; keyed requests get 503 after the window without confirmation; readiness stays green within it; two concurrent saves cannot both pass a cross-rule | plan §4, §11.2 |
| 8 | `Ume.Rag.FakeGateway` (1024-dim embeddings, residency headers, per-level dev keys, `400` without a ceiling for `RequireMaxResidency` keys, `403 max_residency_not_met` and 503 `Retry-After` modes, `/v1/models` filtered by the ceiling, rerank fake, catalog); gateway client `DelegatingHandler` that refuses a call without an explicit ceiling or with a key of another level | API tests use it; a wrong-residency answer is aborted; the handler test covers every call site that exists | README P0 (fake gateway), plan §6 |
| 9 | `dev/keycloak/ume-rag-realm.json` (incl. `rag-reviewer`, admin design §12); OIDC/BFF and claim mapping copied from the gateway admin API (`UME_RAG_OIDC_*`); `OidcClaimMappingTests` copied; audit actor `oid` | sign-in to the empty admin UI as each test user shows the right roles and departments (incl. a department from a group claim) | identity §8 P0 |
| 10 | Keys in gateway style (`ume-rag-sk-`, HMAC + pepper, prefix, rotation grace) in `config."Keys"` | key auth tests copied from the gateway's `KeyTests` pass | README P0 (keys) |
| 11 | Admin UI skeleton with the copied design system in Swedish (`labels.ts`/`messages.ts` skeleton), Vite on 5174, `design-system.lock`; admin API write path (`AdminContext`: change + outbox + audit in one transaction), `PermissionMatrix` role tests, org sync against the FakeGateway catalogue, `contracts/admin/v1/error-codes.json` export | lint/typecheck/test/build green; shell looks like the gateway's; a saved change bumps the config version with one audit entry; synced departments visible | admin P0 (admin design §12) |
| 12 | AppHost (§4.1) with postgres (pinned pgvector image), keycloak, fake-gateway, parser (P0 image with Docling, Tesseract `swe` and models baked, size measured; supervisor with one slot; health + `/v1/info` with `build_hash`; parser design §16), clamav, migrations, api, worker, adminapi, admin-ui; `Start-Dev.ps1`, `Start-E2E.ps1`; seeder | `.\scripts\Start-Dev.ps1` brings every resource to healthy on a clean machine | plan §11.4, parser P0 |
| 13 | Parser contract skeleton: `contracts/parser/v1/*.json`, `ContractTests`, parser CI job | contract job green; `test_offline.py` passes with networking disabled | parser P0 |
| 14 | Single-host `compose.prod.yaml` + `compose.ingestion.yaml` with networks, zones and limits (§6; one slot), `init-deployment.sh`, `rag-ingestion.slice`, firewall script; **pgBackRest** inside the postgres image with WAL archiving, `rag-backup.timer` + `backup-postgres.sh`, `restore-pitr.sh`, `backup-uploads.sh`, `escrow-keys.sh` (§7.4) | `docker compose config` passes in CI; **point-in-time restore drill** on the test host documented with achieved RPO/RTO; escrowed keys read back | plan §11.5 |
| 15 | `release.yml` + `build-release-bundle.sh`; tag `v0.1.0` | release with digest-pinned bundle and SBOM; smoke test green | plan §10 Releases |
| 16 | Deploy `v0.1.0` to the test host (8 vCPU / 16 GB) behind the municipal proxy | health green through the proxy | plan §11.5 |
| 17 | `infra/entra/` + `entra.yml` (plan only on PR); approval package 1 (RAG's own registrations: `ume-rag-admin` with redirect URIs and `rag-*` roles incl. `rag-reviewer`, its own credential, the `open-webui` registration, pilot groups) **submitted** | workflow run awaiting the environment approval; ticket id recorded | plan §11.7, identity §2/§8 |
| 18 | One combined gateway release agreed with the gateway team: design and ADR drafts 018–020 (`../gateway-changes/adr-drafts.md`), key flags `RequireMaxResidency`, `ApplyTeamPolicy`, the 503/403 split, FakeLlm `dimensions` (§12.2) | design and drafts accepted by the gateway team; the release scheduled before the pilot | README P0, gateway-changes plan |
| 19 | Docs: `local-development.md`, `production-setup.md` (sizing from §6.2), `runbook.md` (incl. restore and escrow), `upgrade-guide.md`, `security-events.md` | reviewed by the operations owner | plan §10 |
| 20 | Worker dispatcher skeleton (after #6): `WorkerInstances` heartbeat, leases, reaper (reclaim ≠ attempt; poison suspect after 3 reclaims), `LISTEN rag_jobs`; one `command` kind end to end (admin enqueues, worker applies) | integration test: the command is applied; a killed worker's job is reclaimed within 45 s without using an attempt | engine design §20 (P0) |
| 21 | Scale seams (after #6, #20): `IBlobStore` write-once contract (`IfAbsent`, tombstones) with the file-system implementation; `IJobQueue` seam (Postgres implementation); pooler-safe SQL rules + scan test; `ConnectionStrings__ragdb-listen`; `Worker:Concurrency:<class>` and `Worker:Classes` per instance; `DOTNET_GCHeapHardLimitPercent=75` on every .NET service (the halfvec HNSW index follows in P1) | unit test finds no forbidden session-state SQL; contract tests pass on the file-system store (an overwrite attempt is refused) | [`../performance-pass.md`](../performance-pass.md) §8 (P0) |

`perf.yml` and the nightly `evaluation.yml` follow in P1, before the pilot (§8.1).

**Who does what (decided 2026-10-30):** the main session runs Stage 0 (#1, #2 and the shared files), then two
streams build the rest in parallel; the split, file ownership and hand-offs are in [`../p0/`](../p0/). The parts that
need other people are owned by the user, outside both streams: submitting Entra package 1 (#17), agreeing the combined
gateway release with the gateway team (#18; it is built in P1, before the pilot), the backup target and key escrow
with IT (`to-verify.md` #145) and access to the test host (#16).

## 12. Open decisions and contradictions found

**Review (2026-10-29):** accepted 1, 2 (added to `gateway-changes` §3c), 3, 6, 7, 8, 9, 10 (as a proposal for both repos), 12, 13, 14.
User decisions: 5 Bicep, 11 postponed (file keys in the pilot, Key Vault later), 15 ClamAV direct without a mirror; 4 (self-hosted runner) waits for IT security.
Review fix: Chromium keeps its sandbox via a seccomp profile (§5.4). Rows 3, 6, 7, 8, 11 and 12 are restated in the
decided form of the simplification pass (2026-10-30).

| # | Topic | Finding | Recommendation |
|---|---|---|---|
| 1 | FakeLlm reuse (plan §1 "or reuse gateway FakeLlm") | FakeLlm embeddings are 8-dim and it sends no residency header | Own `Ume.Rag.FakeGateway` (§4.3), copying `FakeLatency.cs`; update plan §1 wording |
| 2 | Real local gateway | Its FakeLlm cannot feed our 1024-dim profile | Delivered in the combined gateway release (`../gateway-changes/design.md` §6: `dimensions`, `fake-embed-1024`); before it, Ollama `bge-m3` in the gateway |
| 3 | Connection caps vs rolling slots (data-model §1.1) | Two slots double a role's connections | Smaller pools (api 12 with `Retrieval:MaxConcurrentSearches` 4, admin 5, worker 10 per instance; `max_connections` 100), limits with room for the P2 b slots; worker stop-then-start (§7.3); measured in P0, confirmed in the R1 test |
| 4 | Self-hosted runner | BGE-M3 and the test gateway are on-prem; GitHub-hosted runners cannot reach them | **Open: IT security must approve** one ephemeral, environment-protected runner on the test host for `perf.yml`/`evaluation.yml`. Fallback: run both manually on the test host with a script |
| 5 | Entra IaC tool | Bicep vs Terraform | **Decided: Bicep** with the Microsoft Graph extension in `infra/entra/` |
| 6 | Hunspell memory per backend | Ispell dictionaries load per backend; with many backends this can be GBs inside a 3 GB Postgres limit | **Closed** (simplification pass §3, postponed H1): no dictionary in Postgres; decompounding in .NET (data-model §3.1) |
| 7 | P0 Entra registrations | Identity §2/§9.6 say none is needed for P0 and package 1 is only *submitted* in P0 | P0 = package 1 (RAG's own registrations) submitted (checklist #17) |
| 8 | Open WebUI hosting | Chat-api §6 configures it but no plan says where it runs or what database it uses | Separate Compose project `compose.openwebui.yaml` on the pilot host (24 GB) or its own VM, own SQLite on an encrypted volume (never in `ragdb`), no pruning sidecar; ceiling `internal`. Env per chat design §4.3: **one connection** (ours, no gateway connection), title, tag and follow-up generation off, `X-Rag-Owui-*` headers, uploads, admin chat access and export off, API keys restricted to the feedback endpoints; feedback older than `ClientRetentionDays` pruned through Open WebUI's HTTP API, never SQL against its database (chat design §13.3). Chat copies of other users cannot be pruned at v0.12.5 without `ENABLE_ADMIN_CHAT_ACCESS=true`: they stay unpruned in the pilot (stated in Open WebUI's data-handling profile) until register item **W2** is decided together with A1 |
| 9 | Portainer | The gateway ships `compose.portainer.yaml`; operations may expect it | Skip in P0/P1 (init-deployment covers the pilot host); add if operations ask |
| 10 | Supply chain in both repos | Gateway pins actions by tag and signs nothing | Propose for both repos together (compatibility): actions pinned by SHA, `attest-build-provenance` on images |
| 11 | Pilot key service | Envelope encryption needs a production `IKeyProvider` from P1 | **Postponed: file key provider in the pilot** with the rules in plan §8a (`--key-provider file --allow-file-keys`, chat master keys only in api/admin, the blob master key in worker/admin and read-only in api (§6.3), long-lived keys escrowed (§7.4), no `sensitive` data); Azure Key Vault later via the `rewrap` command (`../postponed-decisions.md` K1) |
| 12 | Package versions | Docling, FastAPI, MCP SDK (2.3.0 checked, knowledge design), Playwright (P2, if the renderer is adopted), Cronos, the JSON Schema validator (engine design §23), Azure Key Vault SDK, `Aspire.Hosting.Python`, the `pgvector/pgvector` tag for 0.8.7 on Postgres 17 (0.8.7 checked, knowledge design), ClamAV tag, Open WebUI tag, the pgBackRest package | Fix at P0 step 2/12 with the npm/uv 7-day rule; record here |
| 13 | Aspire minor | `kernel-memory-and-dotnet.md` lists `Aspire.Hosting.PostgreSQL` 13.6.4; the gateway pins 13.6.1 | Stay on 13.6.1 with the gateway; upgrade both together |
| 14 | Blob volume shared by admin and worker | File volume works only on one host; uploads arrive at the admin | Accepted for the pilot (plan §12.1); S3/Azure Blob is a precondition of §6.4 |
| 15 | ClamAV updates | Plan §2 assumed a controlled mirror | **Decided: direct, no mirror**: freshclam updates from `database.clamav.net`, allowed through the host firewall for the `mirror` network only |

## 13. Deferred or removed (simplification pass)

See [`../simplification-pass.md`](../simplification-pass.md) (section numbers there).

| Item | Where it went |
|---|---|
| `images/postgres` (pgvector build + Hunspell), its dictionary and per-backend memory tests (§3) | Removed: stock pgvector image, .NET decompounder; H1 closed |
| `pg_dump` scripts, `backup-blobs.sh`, the ≤ 30-day rule for every key backup, `reconcile-retention` (§4) | Replaced: pgBackRest with WAL and PITR, upload copy, two key classes with escrow |
| Tier SQL files, tier loop, tier test matrix, per-tier buckets, `Tier` log scope (§5) | Removed: separate deployment if ever needed (T1/K2) |
| `rag-tools` container, role `ume_rag_tools`, `tools`/`tools-egress` networks, `ToolHub__*`, its secrets (§10) | P2 hub in-process in `rag-api` and `rag-admin`; a separate service only with the first external, OBO or write tool (P3) |
| `tools/Ume.Rag.McpGen`, `Microsoft.OpenApi`; `Mcp:AskAssistantTimeout` (§10) | Dropped; P3 |
| Renderer image, resource and settings, `Microsoft.Playwright`; `PdfPig` (§1, §9) | P2 if needed; removed (every PDF to the parser) |
| EasyOCR, `ocr_bakeoff.py`, `tesseract-ocr-eng`, `PARSER_OCR_DEFAULT`; `NER_LANES`; LibreOffice, `soffice-sandboxed`, `office.py`, `mail/` and `python-oxmsg` (§1) | Dropped; P2 with legacy Office, RTF and email |
| Vue Flow and the canvas budget; Open WebUI task model, SQL pruning and the pruning sidecar (§1, §2) | Dropped; generation off, feedback pruning via its API, chat copies open (W2) |
| `Gateway__ServiceKey`, `Gateway:AttributionMode`, `Gateway:SendResidencyCeiling`, interim keys; shared Entra registration (§6, §7) | Replaced: keys per level and deployable; RAG's own registrations |
| a/b slots, `deploy-e2e.yml`, `LeaderLeases`, `connection-budget.sh`, `ume_rag_monitor`, Redis leases, `Security__CrawlKeyPepper`; `flake-hunt.yml`, `scale.yml`, Atheris; `OrgSync:MaxAgeHours` (§1, §7) | P2 |
| `perf.yml`, nightly `evaluation.yml` (§1) | P1, before the pilot |
| `redis` in the pilot: container, its `data` network membership and limits row, `ConnectionStrings__redis`, Redis password, ACL and certificate; Redis in the default AppHost (§14) | P2 (first multi-host deployment, enabled by `ConnectionStrings:redis`); P1 uses the in-memory `IRateLimitStore`/`ISharedCache` and the Postgres admin session store |
| `rag-pgbackrest` sidecar, `ume-rag/pgbackrest` image, `PGBACKREST_IMAGE`, `pgbackrest-spool` volume (§14) | Replaced: pgBackRest inside `ume-rag/postgres`, backups from a host timer |

## 14. To verify (simplification pass)

| Item | Where | Phase | How |
|---|---|---|---|
| `pgvector/pgvector` tag and digest for pgvector 0.8.7 on Postgres 17; its user id; `pg_stat_statements` and `wal_compression=lz4` available | §5.1, §5.2, §6.2 | P0 | source + test (`deploy/postgres/test.sh`) |
| `ume-rag/postgres`: the PGDG `pgbackrest` package installs on the stock image's base (PGDG repository already configured there); synchronous `archive-push` from inside the container on a read-only root file system; `docker compose exec postgres pgbackrest backup` from `rag-backup.timer`; restore in a one-off container of the image; how the timer's result reaches `rag.backup.age` | §5.1, §5.2, §7.4, §9 | P0 | test |
| RPO ≤ 5 min and RTO ≤ 4 h achieved in the point-in-time restore drill on the test host | §7.4 | P0 | measure |
| Backup target with IT (S3-compatible or share), encryption at rest, and where the escrow lives | §7.1, §7.4 | P0 | ask (IT operations) |
| FakeGateway rerank endpoint path matching the gateway's planned P2 endpoint | §4.3 | P1 | ask (gateway team) |
| Open WebUI environment variables that switch off title, tag and follow-up generation at the pinned tag | §4.1, §12.8 | P0 | source |
| Parser image ≤ 3.5 GB without LibreOffice and EasyOCR | §5.1, §5.3 | P0 | measure |
| `rag-api` memory limit with the in-process tool hub and one hosted MCP server | §6.2 | P2 | measure |
| CloudNativePG images include pgvector ≥ 0.8 | §10 | P3 | source |
