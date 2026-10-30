# Progress: Stage 0 (main session)

Owner: main session. Brief: [`README.md`](README.md) §1. Checklist: [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §11 #1, #2, #4.
Branch `stage-0`. Tick a box in the same commit as the work it describes; add the PR or commit after the item.

## 1. Root build files (#1)
- [ ] `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `.editorconfig`, `.gitattributes`, `dotnet-tools.json`, `aspire.config.json` copied or adapted from the gateway ([repo-and-deployment §1](../platform/repo-and-deployment.md) table, §2)
- [ ] `.githooks/pre-commit` adapted (admin UI lint + parser ruff)
- [ ] `README.md` with build, test and run commands
- [ ] **Done:** `dotnet build` of the solution passes with warnings as errors

## 2. Projects (#2)
- [ ] Every P0 project of [§3](../platform/repo-and-deployment.md) under `src/` (no `Ume.Rag.Tools`, no MCP template), incl. `Ume.Rag.Retrieval`, `Ume.Rag.Pipelines`, `Ume.Rag.Answering` skeletons
- [ ] Every `tests/` project and `tests/Directory.Build.props`
- [ ] All registered in `UmeRag.slnx`
- [ ] Empty `src/Ume.Rag.Domain/Ports/IJobQueue.cs`, `IBlobStore.cs`; folders `src/Ume.Rag.Infrastructure/Jobs/`, `Blobs/`
- [ ] **Done:** `dotnet test` runs (0 tests is fine)

## 3. Packages
- [ ] `Directory.Packages.props` pinned under the 7-day rule, matched to the gateway's pins ([repo-and-deployment §2.2](../platform/repo-and-deployment.md))
- [ ] [`to-verify.md`](../to-verify.md) #12, #13, #14, #15, #16 (versions) checked and recorded in the register

## 4. ServiceDefaults and health (#4)
- [ ] `Ume.Rag.ServiceDefaults` copied (`TelemetryName = "Ume.Rag"`)
- [ ] `/health/live`, `/health/ready`, `/version`, `--health-check` in every service
- [ ] **Done:** the probe exits 0/1 in a container; OTLP traces visible in the Aspire dashboard

## 5. AppHost and TestKit
- [ ] AppHost with every P0 resource ([repo-and-deployment §4.1](../platform/repo-and-deployment.md)): postgres (stock pgvector tag), keycloak, fake-gateway, parser and clamav placeholders, migrations, api, worker, adminapi, admin-ui
- [ ] **Done:** `aspire start` brings the .NET resources to healthy (placeholders may be unhealthy)
- [ ] `Ume.Rag.TestKit` with `TestPostgres` on the stock pgvector tag; one smoke test opens a connection

## 6. CI and coordination
- [ ] `.github/workflows/verify.yml` backend job (build + test), ignoring `plans/**`, `platforms/**`, root `*.md`
- [ ] Ownership check: every PR's changed paths against [`.github/stream-ownership.txt`](../../.github/stream-ownership.txt) by branch prefix (`scripts/check-ownership.sh`)
- [ ] **Done:** green on a PR; a test PR from `a/…` touching `src/Ume.Rag.Api/` fails the check
- [ ] Aspire agent skills copied from the gateway (`.agents/skills/`, `.github/skills/`)
- [ ] GitHub labels `stream-a`, `stream-b`, `main` (confirm with the user first)

## 7. Hand-off
- [ ] Stage 0 merged to `main`; issue opened for each stream announcing **H1**
- [ ] [`progress.md`](../progress.md) P0 Stage 0 box ticked
