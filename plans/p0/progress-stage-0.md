# Progress: Stage 0 (main session)

Owner: main session. Brief: [`README.md`](README.md) §1. Checklist: [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §11 #1, #2, #4.
Branch `stage-0`. Tick a box in the same commit as the work it describes; add the PR or commit after the item.

## 1. Root build files (#1)
- [x] `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `.editorconfig`, `.gitattributes`, `dotnet-tools.json`, `aspire.config.json` copied or adapted from the gateway ([repo-and-deployment §1](../platform/repo-and-deployment.md) table, §2)
- [x] `.githooks/pre-commit` adapted (admin UI lint + parser ruff)
- [x] `README.md` with build, test and run commands
- [x] **Done:** `dotnet build` of the solution passes with warnings as errors

## 2. Projects (#2)
- [x] Every P0 project of [§3](../platform/repo-and-deployment.md) under `src/` (no `Ume.Rag.Tools`, no MCP template), incl. `Ume.Rag.Retrieval`, `Ume.Rag.Pipelines`, `Ume.Rag.Answering` skeletons
- [x] Every `tests/` project and `tests/Directory.Build.props`
- [x] All registered in `UmeRag.slnx`
- [x] Empty `src/Ume.Rag.Domain/Ports/IJobQueue.cs`, `IBlobStore.cs`; folders `src/Ume.Rag.Infrastructure/Jobs/`, `Blobs/`
- [x] **Done:** `dotnet test` runs (0 tests is fine)

## 3. Packages
- [x] `Directory.Packages.props` pinned under the 7-day rule, matched to the gateway's pins ([repo-and-deployment §2.2](../platform/repo-and-deployment.md))
- [x] [`to-verify.md`](../to-verify.md) #12, #13, #14, #15, #16 (versions) checked and recorded in the register

## 4. ServiceDefaults and health (#4)
- [x] `Ume.Rag.ServiceDefaults` copied (`TelemetryName = "Ume.Rag"`)
- [x] `/health/live`, `/health/ready`, `/version`, `--health-check` in every service
- [x] **Done:** the probe exits 0/1 in a container; OTLP traces visible in the Aspire dashboard

## 5. AppHost and TestKit
- [x] AppHost with every P0 resource ([repo-and-deployment §4.1](../platform/repo-and-deployment.md)): postgres (stock pgvector tag), keycloak, fake-gateway, parser and clamav placeholders, migrations, api, worker, adminapi, admin-ui
- [x] **Done:** `aspire start` brings the .NET resources to healthy (placeholders may be unhealthy)
- [x] `Ume.Rag.TestKit` with `TestPostgres` on the stock pgvector tag; one smoke test opens a connection

## 6. CI and coordination
- [x] `.github/workflows/verify.yml` backend job (build + test), ignoring `plans/**`, `platforms/**`, root `*.md`
- [x] Ownership check: every PR's changed paths against [`.github/stream-ownership.txt`](../../.github/stream-ownership.txt) by branch prefix (`scripts/check-ownership.py`)
- [x] **Done:** green on a PR; a test PR from `a/…` touching `src/Ume.Rag.Api/` fails the check (#1 green: changes, ownership, backend; #2 from `a/test-ownership`: ownership failed with `src/Ume.Rag.Api/Program.cs: owned by 'b'`, run 41069321845; closed)
- [x] Aspire agent skills copied from the gateway (`.agents/skills/`, `.github/skills/`)
- [x] GitHub labels `stream-a`, `stream-b`, `main` (confirm with the user first)

## 7. Hand-off
- [ ] Stage 0 merged to `main`; issue opened for each stream announcing **H1**
- [ ] [`progress.md`](../progress.md) P0 Stage 0 box ticked

## Evidence and notes (2026-10-30)

- Build: `dotnet build UmeRag.slnx -c Release` succeeds with warnings as errors (SDK 10.0.113 locally, `rollForward:
  latestFeature`). Test: `dotnet test --solution UmeRag.slnx -c Release` passes 10 tests: health tests in Api, AdminApi
  and Worker tests, and `TestPostgresSmokeTests`, which reports `vector` 0.8.7 on the pinned image.
- Probe: `ume-rag/api` (chiseled-extra base, user 1654) built with `-t:PublishContainer`. `--health-check` exits 0
  against a running listener and 1 with no listener.
- AppHost: `aspire run` (CLI 13.6.1 through `AspireUseCliBundle`). postgres, ragdb, keycloak, fake-gateway, api,
  worker and adminapi are Healthy; migrations Finished with exit 0; the clamav placeholder is Running; the parser
  placeholder is Unhealthy (allowed). `aspire otel traces api` and `adminapi` list `GET /version` spans. On WSL the
  CLI asks to trust the dev certificate in the Windows store (`dotnet dev-certs https --trust`) on the first run.
- Package pins and release dates (NuGet, 2026-10-30): gateway pins unchanged; `Aspire.Hosting.Python` 13.6.1
  (2026-10-07); `Pgvector` 0.3.2 (2025-05-20); `Pgvector.EntityFrameworkCore` 0.3.0 (2025-12-21);
  `Microsoft.Extensions.AI` and `.OpenAI` 10.11.0 (2026-10-14); `Microsoft.ML.Tokenizers` 2.0.1 (2026-10-15); `Cronos`
  0.13.0 (2026-04-29). Not pinned yet: AngleSharp 1.8.6 (2026-10-25, under 7 days), SmartReader, and the P1/P2
  packages without a checked version (JSON Schema validator, Azure Key Vault, AWSSDK.S3, Graph, MCP, Playwright).
  Images: `pgvector/pgvector:0.8.7-pg17-trixie@sha256:7a7e9f22…a84` (2026-10-01); placeholders on
  `busybox:1.37.0-glibc@sha256:42a9ff19…5ee` (2026-09-23).

Stage 0 choices the design leaves open (main session; folded into the design at the first sync point):

1. `src/parser/Dockerfile` and `images/clamav/Dockerfile` are busybox placeholders, so the AppHost uses
   `AddDockerfile` from the start. Stream A replaces the files and the AppHost does not change.
2. The parser listens on port 8000 (provisional; the design names no port) and the worker gets `Parser__BaseUrl`.
   ClamAV gets `ClamAv__Host` and `ClamAv__Port` on 3310.
3. The admin UI is registered only once `src/admin-ui/package.json` exists, and the Keycloak realm is imported only
   once `dev/keycloak/` exists. Both appear without an AppHost change when stream B adds them. Open WebUI is not
   registered (optional, `OpenWebUi:Enabled`; whoever adds `compose.openwebui.yaml` adds it).
4. Ownership globs are anchored at the repository root (`*.md` means root files only); the header of
   `.github/stream-ownership.txt` says so. The check is `scripts/check-ownership.py`, not `.sh`: gitignore-style
   globs are simpler in Python, and runners have python3.
5. Microsoft.Testing.Platform exits with 8 when a project runs zero tests. Domain, Retrieval, Pipelines and
   ContractTests pass `--ignore-exit-code 8` until their first test; each csproj says to remove it then.
6. `verify.yml` has no `paths-ignore`: the `ownership` job runs on every PR, design-only ones included, and a
   `changes` job skips `backend` when only `plans/**`, `platforms/**` or root `*.md` changed.
7. `Ume.Rag.Infrastructure` already references `Npgsql.EntityFrameworkCore.PostgreSQL` (planned in §3), so TestKit
   can open a connection. `IJobQueue` suppresses CA1711 (a "Queue" suffix), because the name is fixed by the design.
