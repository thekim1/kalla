# Ume.Rag

RAG, crawling and chat platform for Umeå kommun: small .NET 10 services on top of the team's LLM gateway
([thekim1/llmgateway](https://github.com/thekim1/llmgateway)), a Python Docling parser and PostgreSQL with pgvector,
orchestrated with Aspire 13.6. The design lives beside the code: start at [`AGENTS.md`](AGENTS.md) and
[`HANDOVER.md`](HANDOVER.md).

**Status: P0 (foundation), in integration.** Built so far:
- the Postgres image (stock pgvector + pgBackRest) and the schema with its database roles;
- the Docling parser sidecar (PDF) with its v1 contract;
- the ClamAV image;
- the worker's job queue, dispatcher and reaper, and the write-once blob store;
- keys and clients in gateway style, the FakeGateway with its residency-ceiling handler, and the config-version library;
- the admin BFF with Keycloak sign-in, its write path and the admin UI skeleton (Swedish);
- production Compose with host units, backups, point-in-time restore and key escrow;
- Entra as Bicep (for P2);
- the on-prem capacity scripts.

Chat, retrieval and ingestion pipelines come in P1. Progress: [`plans/progress.md`](plans/progress.md).

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

Container health probe: every .NET service image runs its own probe with `--health-check` (exit 0 healthy, 1 not),
against `UME_HEALTHCHECK_URL` (default `https://localhost:8443/health/ready`).

## Docs

- [`docs/local-development.md`](docs/local-development.md): the dev stack in detail and the start scripts.
- [`docs/production-setup.md`](docs/production-setup.md): host sizing, `init-deployment.sh`, Compose files.
- [`docs/runbook.md`](docs/runbook.md): backups, restore, key escrow, certificates, operations.
- [`docs/upgrade-guide.md`](docs/upgrade-guide.md): upgrading between releases.

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

CI runs on request, to save Actions minutes: `verify.yml` and `parser.yml` run on a pull request only while it carries
the label `ci` (add it when the pull request is ready for review; merge only on a green run), and not on a push to
`main`. The schedules of `perf.yml`, `evaluation.yml` and `e2e.yml` are off; run them from the Actions tab.

## Packages

Every package version is pinned exactly and was released at least 7 days before it was pinned; images are pinned by
digest (`AGENTS.md`, 7-day package rule).

## Licence

Copyright © 2026 Umeå kommun. Licensed under the European Union Public Licence v. 1.2 (EUPL-1.2), see
[`LICENSE`](LICENSE). The EUPL is published in every official EU language, Swedish included, and each version is
equally valid (<https://interoperable-europe.ec.europa.eu/collection/eupl/eupl-text-eupl-12>). Third-party components
keep their own licences.
