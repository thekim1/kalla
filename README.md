# Ume.Rag

RAG, crawling and chat platform for Umeå kommun: small .NET 10 services on top of the team's LLM gateway
([thekim1/llmgateway](https://github.com/thekim1/llmgateway)), a Python Docling parser and PostgreSQL with pgvector,
orchestrated with Aspire 13.6. The design lives beside the code: start at [`AGENTS.md`](AGENTS.md) and
[`HANDOVER.md`](HANDOVER.md).

P0 skeleton: every service answers `/health/live`, `/health/ready` and `/version` and does nothing else yet.

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
the fake gateway, the parser and ClamAV placeholders, the migration service, api, worker and adminapi. The admin UI
(`https://localhost:5174`) joins once `src/admin-ui` exists. The resource graph and its switches (`Parser:Mode`,
`ClamAv:Enabled`, `FakeGateway:Enabled`, `Ollama:Enabled`, `Redis:Enabled`, `Oidc:Authority`) are in
[`plans/platform/repo-and-deployment.md`](plans/platform/repo-and-deployment.md) §4.1. Set them as AppHost user secrets:

```bash
dotnet user-secrets --project src/Ume.Rag.AppHost set Parser:Mode off
```

Container health probe: every .NET service image runs its own probe with `--health-check` (exit 0 healthy, 1 not),
against `UME_HEALTHCHECK_URL` (default `https://localhost:8443/health/ready`).

## Packages

Every package version is pinned exactly and was released at least 7 days before it was pinned; images are pinned by
digest (`AGENTS.md`, 7-day package rule).
