# P0 work split

P0 is a skeleton that builds, deploys, is observable and can be restored. The ordered checklist with every item's
done-when criterion is [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §11 (single source;
the briefs here only assign and order it). Decided 2026-10-30 (Decisions table, "P0 set-up").

Three steps: **Stage 0** (main session, alone) → **stream A and stream B in parallel** → **integration** (release
`v0.1.0`, deploy to the test host).

| Brief | For |
|---|---|
| [`stream-a.md`](stream-a.md) | Stream A: platform and the ingestion side (images, parser, Compose, backups, CI, release, worker dispatcher) |
| [`stream-b.md`](stream-b.md) | Stream B: application and the serving side (schema, config library, keys, identity, FakeGateway, admin) |

## 1. Stage 0 (main session)

Fixes every file both streams would otherwise create or reorder, so the streams afterwards fill in files rather than
add shared ones.

| Deliverable | Done when |
|---|---|
| Checklist #1: root build files copied from the gateway (`global.json`, `Directory.Build.*`, `.editorconfig`, `.gitattributes`, `dotnet-tools.json`, `aspire.config.json`, `.githooks/pre-commit`), `README.md` with the build, test and run commands | `dotnet build` passes with warnings as errors |
| Checklist #2: every P0 project of §3 and every `tests/` project, all registered in `UmeRag.slnx`; empty port files `IJobQueue.cs`, `IBlobStore.cs`; folders `Infrastructure/Jobs`, `Infrastructure/Blobs` | `dotnet test` runs (0 tests is fine) |
| Checklist #4: `Ume.Rag.ServiceDefaults` copied; `/health/live`, `/health/ready`, `/version`, `--health-check` in every service | probe exits 0/1 in a container; OTLP traces in the Aspire dashboard |
| AppHost with **every** P0 resource registered (postgres on the stock pgvector tag, keycloak, fake-gateway, parser and clamav as placeholder containers, migrations, api, worker, adminapi, admin-ui) | `aspire start` brings the .NET resources to healthy; placeholders may be unhealthy |
| `Directory.Packages.props` pinned under the 7-day rule (`to-verify.md` #12–#16) | every version recorded with its release date |
| `TestKit` with `TestPostgres` on the stock pgvector tag | one smoke test opens a connection |
| `verify.yml` backend job only (build + test); ownership check of every PR against `.github/stream-ownership.txt` by branch prefix | green on a PR; a test PR from `a/…` touching `src/Ume.Rag.Api/` fails the check |
| Aspire agent skills copied from the gateway (`.agents/skills/`, `.github/skills/`) | – |
| GitHub labels `stream-a`, `stream-b`, `main` | – |

## 2. Ownership

`.github/stream-ownership.txt` is the authority, and CI enforces it. In short:

| Owner | Paths |
|---|---|
| **A** | `deploy/**` (except the three Postgres role and access files), `images/**`, `infra/**`, `scripts/**`, `.github/**`, `.githooks/**`, `src/parser/**`, `contracts/parser/**`, `src/Ume.Rag.Worker/**`, `src/Ume.Rag.Pipelines/**`, the `Jobs` and `Blobs` parts of Infrastructure and their two ports, worker/pipeline/contract tests, `tests/fixtures/**`, `docs/**` (except `security-events.md`) |
| **B** | every other path under `src/` and `tests/` (incl. AppHost, TestKit, IntegrationTests, admin UI, e2e), `deploy/postgres/{init.sql,app-roles.sql,pg_hba.conf}`, `contracts/admin/**`, `dev/**`, `docs/security-events.md` |
| **shared** (append-only, own PR) | `UmeRag.slnx`, `Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `.gitignore` |
| **main** | design documents (`plans/**`, `platforms/**`, root `*.md`), root build files, the ownership map, agent instructions |

Why this seam: the two sides differ in language and folder, so they barely touch; the interfaces between them are
already specified (database schema, job queue, parser contract, gateway headers); A's waiting on people (test host,
IT, Entra) overlaps B's heavier coding; and the same seam continues into P1 (§6).

**Single owner of the schema.** B owns every migration. A needs a table or column → issue to B with the data-model
section it implements. Migration ordering is the classic collision, so it has exactly one writer.

## 3. Hand-offs

| # | From → to | What | Until it lands |
|---|---|---|---|
| H1 | main → A, B | Stage 0 merged | – (nothing starts before) |
| H2 | B → A | Migrations of #6 merged: `Jobs`, `WorkerInstances`, outbox, `RunItems`, roles | A builds images, parser, Compose and CI first; the dispatcher (#20) waits |
| H3 | A → B | `ume-rag/postgres` image built in CI (#5) | `TestPostgres` stays on the stock pgvector tag (same base, same extension) |
| H4 | A → B | Worker applies the `command` job kind (#20); test enqueues through `IJobQueue` | B's admin write path (#11) enqueues once both exist; small follow-up PR by B |
| H5 | B → A | Service images build (Stage 0's `UmeServiceContainer`) and every health probe answers | Compose (#14) uses the Stage 0 images |
| H6 | user → A | Test-host access, backup target and key escrow location (`to-verify.md` #145) | A prepares #14 scripts and runs the restore drill locally; the documented drill waits |
| H7 | A, B → user | Bicep for Entra package 1 (#17, A), realm and claim mapping (#9, B) | the user submits package 1 |

## 4. Sync points

1. **After H1**: both streams start.
2. **After H2** (aim: early, B does #6 first): main session checks schema against `data-model.md` and folds the
   first results files into `to-verify.md`.
3. **Integration**: every item except #15 and #16 merged → main session reviews deviations and results, then A cuts
   `v0.1.0` (#15) and deploys with the user (#16).

## 5. Rules (both streams)

The working rules are in [`../../AGENTS.md`](../../AGENTS.md) (owned paths, shared files, deviations, results, PRs).
In addition:

- Work in a branch of your own prefix and merge to `main` daily; long-lived branches are where collisions grow.
- Two agents in one clone: one worktree each (`git worktree add ../rag-a`, `../rag-b`).
- Requests between streams go through GitHub issues with the owner's label, never through shared files.
- Items owned by the user (#16 access, #17 submission, #18 gateway-team agreement, `to-verify.md` #145) are not
  stream work; a stream that needs one waits on its hand-off in §3 and continues with the next item.

## 6. P1 continuation (outline)

The same seam carries on. A: connectors and crawl, site profiles, parser formats, fixed pipelines, index writer,
evaluation runs in the worker. B: retrieval and knowledge API, chat API, assistants and versions, admin journey
pages, feedback, the combined gateway release integration. The ingestion pages of the admin UI sit on the seam; their
split is decided with the P1 briefs.
