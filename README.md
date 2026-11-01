# Ume.Rag

RAG, crawling and chat platform for Umeå kommun: small .NET 10 services on top of the team's LLM gateway
([thekim1/llmgateway](https://github.com/thekim1/llmgateway)), a Python Docling parser and PostgreSQL with pgvector,
orchestrated with Aspire 13.6. The design lives beside the code: start at [`AGENTS.md`](AGENTS.md) and
[`HANDOVER.md`](HANDOVER.md).

**Status: P1 (pilot), code first.** P0 is done and released as
[`v0.1.0`](https://github.com/thekim1/ume-rag-platform/releases/tag/v0.1.0) (2026-10-31). P1 is built on FakeGateway,
fixtures and the dev Keycloak first; the live parts (the real gateway, the shared Keycloak, the umea.se crawl, the gold
set, the pilot member, the test host) come as one block at the end. Built so far:
- ingestion end to end: URL lists, crawls (one frontier per source, robots, sitemaps, freshness and removal checks) and
  uploads (ClamAV, fail closed) through parse, clean, chunk, personal data, embed and the index writer;
- the parser for PDF, Office/ODF, images and scans (Docling, OCR), the Sitevision preset and the umea.se profile;
- the engine: runs and finalizer, scheduler, drift sweep, `reprocess`/`retry-failed` with an optional cache bypass,
  source settings-change effects, preview and preflight, publish with per-document swap and rollback within 14 days;
- evaluation: on-demand three-way runs, publish reports, nightly runs with the recall@10 alert, generated question sets;
- retrieval core (hybrid Swedish keyword + vector search) and the start of the knowledge API;
- the pilot stack: production Compose, test-host run book, Open WebUI with its feedback service account, backups,
  point-in-time restore and key escrow.

Next: the gateway client, the admin set-up endpoints, the chat API for Open WebUI and the Keycloak realm additions.
Overview for stakeholders (Swedish): [`docs/phase1-guide.html`](docs/phase1-guide.html). Progress:
[`plans/progress.md`](plans/progress.md).

## Requirements

.NET SDK 10.0.1xx or later (CI uses 10.0.401), Docker 28+, Node 24.15+ (admin UI) and uv (parser).

## Build and test

```bash
dotnet build UmeRag.slnx -c Release --nologo
dotnet test --solution UmeRag.slnx -c Release --no-build --filter-not-trait "Category=Flaky"
```

`--filter-not-trait` skips tests marked `[Trait(TestCategory.Name, TestCategory.Flaky)]`, as CI does. The integration
tests start the pinned pgvector image with Testcontainers, so Docker must be running. `UME_RAG_TEST_POSTGRES_IMAGE`
runs them against another image (CI: the built `ume-rag/postgres`).

The worker's container tests need locally built ClamAV and parser images (Testcontainers wants a dot in a local
registry name):

```bash
docker build -t ume-rag.local/clamav:dev images/clamav
docker build -t ume-rag.local/parser:ci src/parser
UME_RAG_TEST_CLAMAV_IMAGE=ume-rag.local/clamav:dev UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci \
  dotnet test --project tests/Ume.Rag.Worker.Tests -c Release --no-build
```

The pre-commit hook runs the admin UI lint and the parser's ruff check on staged changes. Enable it once per clone:

```bash
git config core.hooksPath .githooks
```

## Run locally

```bash
aspire run      # or: dotnet run --project src/Ume.Rag.AppHost
```

The Aspire dashboard opens at `https://localhost:17470`. The AppHost starts postgres (stock pgvector image), keycloak,
the fake gateway, the parser and ClamAV (built from their Dockerfiles), the migration service, api, worker, adminapi
and the admin UI (`https://localhost:5174`). The resource graph and its switches (`Parser:Mode`,
`ClamAv:Enabled`, `FakeGateway:Enabled`, `Ollama:Enabled`, `Redis:Enabled`, `Oidc:Authority`) are in
[`plans/platform/repo-and-deployment.md`](plans/platform/repo-and-deployment.md) §4.1. Set them as AppHost user secrets:

```bash
dotnet user-secrets --project src/Ume.Rag.AppHost set Parser:Mode off
```

To run against the interim gateway instead of FakeGateway, see [`docs/p1a-local-run.md`](docs/p1a-local-run.md).
Addresses and keys go only in user secrets or secret files, never in the repository.

On Linux or WSL, `aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj` runs the same stack in the
background (`aspire describe`, `aspire logs <resource>`, `aspire stop`); `Seed__Enabled=false` in the environment starts
it without demo data. Milestone 1, ingestion and `/v1/retrieve` against the interim gateway, passed on 2026-11-01
([`docs/p1a-local-run.md`](docs/p1a-local-run.md) §4).

Container health probe: every .NET service image runs its own probe with `--health-check` (exit 0 healthy, 1 not),
against `UME_HEALTHCHECK_URL` (default `https://localhost:8443/health/ready`).

## Docs

- [`docs/local-development.md`](docs/local-development.md): the dev stack in detail and the start scripts.
- [`docs/p1a-local-run.md`](docs/p1a-local-run.md): the local P1a run, against FakeGateway now and the interim gateway later.
- [`docs/production-setup.md`](docs/production-setup.md): host sizing, `init-deployment.sh`, Compose files.
- [`docs/runbook.md`](docs/runbook.md): backups, restore, key escrow, certificates, operations.
- [`docs/test-host.md`](docs/test-host.md): the pilot test host, its caps, backups and the restore drill.
- [`docs/security-events.md`](docs/security-events.md): the security event ids.
- [`docs/upgrade-guide.md`](docs/upgrade-guide.md): upgrading between releases.
- [`docs/phase1-guide.html`](docs/phase1-guide.html) (Swedish) and [`docs/stakeholder-report.html`](docs/stakeholder-report.html): overviews for stakeholders.

## CI

`verify.yml` runs on every pull request and push to `main`:
- `ownership` (stream paths);
- `backend` (build and tests);
- `images`;
- `deploy-checks` (Compose and host scripts);
- `admin-ui`.

`parser.yml` runs the `parser`, `parser-image` and `contract` jobs only when the parser or its contract changed.
`entra.yml` plans the Entra registrations.
`release.yml` builds, pins and publishes a release on a `v*.*.*` tag (dispatch: a dry run that pushes nothing).
`perf.yml` runs the R1 release test (weekly, dispatch; a dry run on FakeGateway on GitHub's runner, the real run on the
test host). `evaluation.yml` runs the nightly evaluation on the test deployment. `e2e.yml` runs the browser tests
(nightly, and before a release).

**CI runs on the self-hosted runner `ume-rag`** (Decisions table, "CI on the self-hosted runner for every pull
request"): `verify.yml` and `parser.yml` run on every pull request that is not a draft (on open, each push, and when it
leaves draft), with no label. GitHub-hosted minutes are used up, so nothing runs on GitHub's runners, and the schedules
of `perf.yml`, `evaluation.yml` and `e2e.yml` stay off until the test host exists. GitHub can't enforce required checks
on this repository, so merge only on a green run (`gh pr checks <number>`).

The main session also verifies each pull request locally after merging `origin/main` into the branch: the build,
`Ume.Rag.Worker.Tests` twice (it surfaces order-dependent failures), `Ume.Rag.Pipelines.Tests`, `Ume.Rag.ContractTests`
and the ownership check, with the output in the pull request body:

```bash
git diff --name-only origin/main...HEAD | python3 -I scripts/check-ownership.py <branch>
```

## Packages

Every package version is pinned exactly and images are pinned by digest (`AGENTS.md`). The 7-day rule (a version at
least 7 days old when pinned) is suspended during development and applies again before the first production
deployment, when every younger pin is reviewed (Decisions table, "7-day rule in development").

## Licence

Copyright © 2026 Umeå kommun. Licensed under the European Union Public Licence v. 1.2 (EUPL-1.2), see
[`LICENSE`](LICENSE). The EUPL is published in every official EU language, Swedish included, and each version is
equally valid (<https://interoperable-europe.ec.europa.eu/collection/eupl/eupl-text-eupl-12>). Third-party components
keep their own licences.
