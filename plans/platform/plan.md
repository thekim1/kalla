# Platform foundation: plan

Shared conventions and infrastructure for every service. Mirrors the LLM gateway (`~/git/llmgateway`) wherever
possible, because **the gateway team builds and operates both** (decision 2026-10-29), on separate hosts and
databases. Incorporates the architecture review
(`../../02-architecture-review.md`, items R1–R7).

## 1. Repository and solution layout

One repository, one solution, like the gateway:

```
src/
  Ume.Rag.Domain/            entities, enums, port types, classification, access policy, shared contracts
  Ume.Rag.Infrastructure/    EF Core DbContexts, Postgres queue + outbox, Redis keys, gateway client, blob storage
  Ume.Rag.ServiceDefaults/   OpenTelemetry, health, problem responses, security headers (copy of gateway's)
  Ume.Rag.Api/               data plane: Knowledge API + Chat API + MCP server endpoint groups (role setting)
  Ume.Rag.Tools/             tool hub: MCP client, tool policy, credentials, skills runtime (see ../tools)
  Ume.Rag.Worker/            ingestion worker: connectors, pipeline engine, scheduler, evaluation runs
  Ume.Rag.AdminApi/          control plane: BFF + admin endpoints, serves the admin UI
  Ume.Rag.MigrationService/  one-shot migrator (DDL role), dev seeder
  Ume.Rag.AppHost/           Aspire orchestration for local dev
  Ume.Rag.FakeGateway/       OpenAI-compatible fake gateway: chat, 1024-dim embeddings, residency headers, catalog (the gateway's FakeLlm returns 8-dim vectors, so it cannot be reused)
  admin-ui/                  Vue 3 + Pinia + Vue Flow, gateway design system
  parser/                    Python sidecar (Docling), own Dockerfile
templates/mcp-server/        template for municipal MCP servers (see ../tools)
tests/
deploy/                      compose files, Postgres image, init and backup scripts (gateway style)
docs/
```

Full tree, build conventions, images, Compose, release and CI: [`repo-and-deployment.md`](repo-and-deployment.md).

`Ume.Rag.Api` runs Knowledge, Chat and the MCP server as endpoint groups selected by a role setting, so the MVP is
one container and they can be split later without code changes. Chat calls retrieval through an `IRetriever`
interface: in-process when co-deployed, HTTP when split.

## 2. Deployables and resource zones (R1)

Ingestion must never compete with chat for CPU or database capacity, so containers are placed in two **resource
zones** from day one, even when they share a host:

| Zone | Container | Image | Notes |
|---|---|---|---|
| data plane | `rag-api` | Ume.Rag.Api | Knowledge + Chat + MCP server roles |
| data plane | `rag-tools` | Ume.Rag.Tools | P2; tool hub |
| data plane | `postgres` | Postgres 17 + pgvector + Hunspell `sv_SE` | **Our own image**, Azure and on-prem alike (decision) |
| data plane | `redis` | Redis | Rate limits, caches |
| control | `rag-admin` | Ume.Rag.AdminApi | Admin API + static admin UI |
| ingestion | `rag-worker` | Ume.Rag.Worker | Hard CPU/memory limits, lower CPU priority |
| ingestion | `rag-parser` | parser | Hard limits; no egress; internal network only; first to move to its own host |
| ingestion | `rag-renderer` | Playwright + Chromium | Headless rendering for JavaScript-only pages (P1); crawler egress rules apply |
| ingestion | `rag-clamav` | ClamAV | Scans uploads and fetched files before parsing; signature updates through a controlled mirror |
| – | `rag-migrate` | Ume.Rag.MigrationService | One-shot on deploy |

- **Pilot**: one host is allowed: **8 vCPU / 16 GB minimum, 24 GB recommended** (Docling models, ClamAV signatures
  and Chromium need several GB together), with Docker CPU shares and limits giving the data plane priority. The
  parser gets at most half the cores. BGE-M3 runs at the gateway's on-prem provider, not on this host.
- **Production**: ingestion zone on its own host or node pool; the data plane host never runs OCR.
- **Database protection**: one connection pool per service role with a hard cap (worker smallest), a
  `statement_timeout` per role, chunk writes in batches, and queue/run tables partitioned by day so old partitions
  are dropped rather than deleted (no vacuum storms).
- **Release test**: chat p95 time to first token is measured during a full crawl plus an OCR batch, with the
  gateway's benchmark harness, and must stay within 10% of the idle figure.

**Decided (2026-10-29): our Postgres and Redis are separate instances from the gateway's**, each system with its own
database. Reasons: the gateway's ADR 015 (other teams' data goes through interfaces, never the database; we read
organisation data through the gateway's API); the gateway stores no content while ours holds documents and chat
logs, so backups, admin access and retention must not be shared; our Postgres needs a custom image (pgvector,
Swedish Hunspell); ingestion load must never reach the gateway's database (R1); and a later `sensitive` tier has its
own keys and hosting. Operations reuse the gateway's backup/restore scripts, migrator/role pattern and runbook.

## 3. Data ownership

One Postgres database, one schema per owner, one DB role per service with least privilege (gateway pattern).
Tables, roles, indexes, queue, encryption, retention and blob layout in detail: [`data-model.md`](data-model.md).

| Schema | Written by | Read by | Contents |
|---|---|---|---|
| `config` | admin | api, tools, worker | collections, sources, pipeline versions, assistants, skills, tool servers, clients, keys, embedding profiles, settings, **config outbox** |
| `content` | worker (admin only inserts `Jobs`, incl. `command` jobs for its decisions; api only via the retrieval-counter function) | api (read-only), admin (read) | **document store** (platform-wide), collection index (chunks, embeddings), runs, jobs, parse observations, parse profiles, evaluation sets and results, node catalogue manifest |
| `chat` | api | admin (audited read) | conversations, messages, feedback, tool-call records |
| `audit` | all (append-only) | admin | audit entries |

**Two content layers (R2).** The *document store* holds one document per canonical identity (normalised URL,
SharePoint item id, file hash), fetched and parsed once. The *collection index* holds each collection's chunks and
embeddings, referencing documents. Several departments using the same umea.se pages share one fetch, one parse and,
when their pipelines match, one set of chunks. Classification and access live on collections; the document store is
never queried directly by clients.

**Classification in the document store**: every document carries the classification of the source that fetched it,
and **documents are shared only between sources of the same level**. The same URL fetched by a `public` and a
`sensitive` source is stored twice, so a sensitive copy never feeds a lower-level collection, and the separate
`sensitive` tier (§8a) can hold its documents without cross-tier references.

Blob storage (`IBlobStore`): uploaded files, raw fetched content and node outputs. Implementations: file system
volume, S3-compatible, Azure Blob.

## 4. Configuration changes: transactional and versioned (R7)

- Every change in `config` writes a row to a **config outbox** in the same transaction, with a monotonically
  increasing **config version**.
- Postgres `LISTEN/NOTIFY` wakes the data plane, tool hub and worker (the same mechanism wakes the job queue); each
  service also polls the current version every few seconds, so a missed notification costs seconds, not a TTL.
- Caches hold the version they were built from. **Security-relevant entities** (keys, clients, access rules,
  classification, tool permissions) are used only if the cache is at the current version; if the version cannot be
  confirmed, the request is refused (fail closed). Other settings may serve slightly stale.
- Redis is used for rate limits and short-lived caches only, not as the source of change events.

## 5. Identity and access

Detailed in [`../identity/plan.md`](../identity/plan.md). In short:

- People sign in exactly as in the gateway: the same OIDC/BFF mechanism, the same claims (`roles`,
  `departmentCodes`, `teamCodes`) and configuration keys (`UME_RAG_OIDC_*`), preferably the **same Entra
  registration** as the gateway admin with namespaced `rag-*` roles. Departments without a department attribute come
  from department member groups, a feature added to both systems (`../gateway-changes/plan.md` feature C).
- No Entra registration is needed for P0 or development (Keycloak + keys); admin and Open WebUI sign-in must be
  approved before the pilot.
- Every caller is a **client** from the client registry (R5): an Entra app registration, an `ume-rag-sk-`/`ume-rag-pk-` key, or a
  trusted front end such as Open WebUI. Each client has a **classification ceiling** and a data-handling profile.
- One `IAccessPolicy` in `Ume.Rag.Domain` decides (subject, client, resource, action) for every service.
- **Organisation data** (departments, teams, codes) is read from the gateway, the single source of truth, because
  gateway delegation (R4) charges budgets by team code.

## 6. Classification

Levels `public` < `internal` < `sensitive` on sources, collections, skills, tool servers, assistants and clients.

- A collection's level is at least the highest level of its sources; an assistant's level is at least that of every
  linked collection, skill and tool server. A `public` assistant (widget) can only link `public` content.
- A client may only use assistants at or below its ceiling (R5).
- **Residency matrix** (decision 2026-10-29), for every model call: chat, condense, LLM pipeline steps, evaluation
  generation and judging, tool servers:

  | Level | Allowed processing | Ceiling header sent |
  |---|---|---|
  | `public` | Any provider the gateway allows | none |
  | `internal` | On-prem or EU providers | `x-ume-max-residency: EU` |
  | `sensitive` | On-prem only | `x-ume-max-residency: OnPrem` |

  Enforced in three layers (R4): the gateway request carries the ceiling header, the response's `x-ume-residency` is
  checked before any byte is streamed (or before an ingestion result is stored), and the call is aborted (fail closed)
  if it is above the ceiling. The level of a call is the highest level of the data in it (e.g. an `internal`
  assistant answering from a `public` collection still sends `EU`).
- **Embeddings are always on-prem** (R3): the platform embedding profile is an on-prem multilingual model, so
  indexing never sends content out of the municipality, whatever its classification.

## 7. Gateway integration (R4)

Detailed in [`../gateway-changes/plan.md`](../gateway-changes/plan.md):

- One **platform service key** with delegated attribution (`x-ume-on-behalf-of: <team code>`): gateway budgets,
  policies and usage reports stay per team, and we hold one secret instead of one per team.
- A **per-request residency ceiling** (`x-ume-max-residency: OnPrem`) that can only narrow what the key allows.
- **Interim**, until the gateway release ships: per-team linked keys (encrypted with Data Protection), plus the
  `x-ume-residency` response check, which already catches a key whose residencies were widened later.

## 8. Retention and personal data

| Data | Default | Where set |
|---|---|---|
| Conversations, messages, tool-call records | 90 days (`sensitive`: 30 days); see §8a | Assistant within the per-level limits (§8a) |
| Feedback | follows its conversation | – |
| Documents, chunks | as long as the source has them; soft delete with 14-day grace | Source |
| Parse output (last version per document) | as long as the document exists | – |
| Other intermediate node outputs | 30 days | Platform setting |
| Uploads | while the source exists | – |
| Evaluation sets and results | per collection, until removed | Collection |
| Jobs (terminal) / run items / runs | 7 days / 90 days / 13 months (proposed) | Platform setting |
| Parse observations / retrieval statistics | 180 days / 13 months (proposed) | Platform setting |
| Embedding cache entries | unused for 30 days (proposed) | Platform setting |
| Quarantined files (ClamAV) | 30 days (proposed) | Platform setting |
| Config outbox | 30 days (proposed) | Platform setting |
| Assistant daily totals (statistics without content) | 3 years (proposed) | Platform setting |
| Audit | separate, longer (e.g. 2 years) | Platform setting |

Copies held by clients are covered by the client's data-handling profile (R5): e.g. Open WebUI's own chat database is
pruned to the same 90 days. Nightly retention job per schema, logged and audited. Pilot blocker: confirm with the
registrar/archivist whether chat logs are allmänna handlingar and whether a gallringsbeslut covers 90-day deletion.

## 8a. Data protection policy per classification level

Decisions (2026-10-29): three levels stay (`sensitive` = the municipality's highest category); chat-log protection is
**configurable per level**; the separate data tier is **designed for now and decided later**.

**Baseline for every level**: encrypted volumes and encrypted backups (Postgres has no built-in transparent
encryption; LUKS on-prem, Azure disk encryption in Azure). No content in logs, traces, metrics or Redis at any level
(the query cache holds hashes and embeddings only).

**Policy matrix.** Platform admins set, per level, the defaults and the **limits** an assistant may choose within
(an assistant can tighten, never loosen). Proposed starting values:

| Setting | `public` | `internal` | `sensitive` |
|---|---|---|---|
| History mode (`stored` / `session` (24 h) / `none`) | stored | stored | stored, short retention |
| Retention (default / max) | 90 / 90 days | 90 / 90 days | 30 / 90 days |
| Application-level encryption of content | optional (default on) | **required** | **required** |
| Key scope | per conversation | per conversation | per conversation, separate master key |
| Admin review of conversations | reason | reason | reason **+ second approver** |
| Allowed clients | per ceiling | per ceiling | only clients without their own history store |
| Feedback comments, tool results, cited source text | same policy as messages | same | same |

**Application-level (envelope) encryption.**

- Each conversation has its own data key (AES-256-GCM). Message text, titles, feedback comments, tool arguments and
  results, and the snapshot of cited source text are encrypted before they reach the database. Metadata (times,
  assistant, user `oid`, token counts, cost, outcome) stays readable for statistics and retention.
- Data keys are stored wrapped by a **master key per level** held in a key service behind `IKeyProvider`: Azure Key
  Vault / Managed HSM in Azure, OpenBao/HashiCorp Vault or an HSM on-prem, a local key file in development. For
  `sensitive` data that lives on-prem, the master key is on-prem too.
- Only the Chat API (a user's own history) and the admin review path (audited, with reason and, for `sensitive`, a
  second approver) can unwrap data keys. Database admins, a dumped table or a stolen backup see ciphertext only.
- **Crypto-shredding**: retention deletes the conversation's wrapped data keys in the live database. Backups still
  hold those key rows, so to reach backups the wrapping master keys are **monthly shred buckets** per level (by
  latest possible expiry): once a month's last conversation has expired, its bucket key is destroyed in the key
  service, and every backup copy becomes unreadable. Data keys rotate per 7-day epoch per conversation, so every
  message is gone within retention + 7 days. Key Vault purge protection delays destruction by its configured days
  (7–90); OpenBao key deletion must be enabled (`data-model.md` §7).
- Unwrapped keys are cached in memory for minutes only; encryption cost is negligible next to model latency.
- Consequence: no server-side full-text search in other people's chats; a user's own history list shows decrypted
  titles through the Chat API.

**Content beyond chat logs.** At `sensitive`, the documents themselves are equally sensitive: chunk text, embeddings
(text can partly be reconstructed from them), parse outputs and uploads. Chunk text cannot be column-encrypted
because full-text search needs it, which is why the separate tier below exists. Blobs (uploads, parse outputs) are
envelope-encrypted per document with the same key service.

**Separate data tier: designed now, decided later.**

- Storage access goes through a **tier resolver**: (classification) → (Postgres connection, blob container, master
  key). Default configuration: one tier for everything.
- Rules that keep a later split cheap: no foreign keys or joins across classification levels; content and chat rows
  carry their level; retrieval already fuses result lists with RRF (knowledge API §3), so an assistant spanning tiers
  queries each tier and fuses.
- Turning on a `sensitive` tier (its own on-prem Postgres instance, encrypted volume, master key and database
  credentials) is a configuration change plus a migration job that moves existing `sensitive` rows.
- **Decision point**: before the first `sensitive` source is onboarded.

## 9. Evaluation as a platform capability (R6)

Every collection has an **evaluation set**: question/answer pairs generated automatically from sampled chunks by an
on-prem model, plus human questions, rewritten feedback questions and skill test questions over time. Pipeline publish,
parse-profile auto-apply, retrieval-setting changes and embedding-profile changes are **gated** on it. Details in the
worker plan (§7) and admin plan. The postponed Swedish benchmark becomes the curated part of these sets.

## 10. Cross-cutting engineering

- **.NET 10, Aspire 13**, minimal APIs, EF Core with Npgsql + `Pgvector.EntityFrameworkCore`, central package
  management, the gateway's `.editorconfig` and analyzers.
- **Observability**: OpenTelemetry traces, metrics and logs from every service; one trace follows a chat message
  through retrieval, tool calls and the gateway; ingestion runs carry the run id; config version in every log scope.
- **Security**: crawler egress blocks private ranges unless allow-listed (SSRF); parser and MCP servers have no egress
  beyond what they need; uploads limited by size and type; secrets from env/secret store (Key Vault on Azure); prompt
  injection rules in `../tools/plan.md` §6.
- **Testing**: unit; integration against real Postgres/Redis (Aspire/Testcontainers); fake gateway; end-to-end
  browser tests; evaluation sets in CI on a fixture collection; the R1 release test.
- **Releases**: release bundle with compose files, init script, backup/restore, upgrade guide, rolling update.

## 11. P0 deliverables

1. Repository skeleton, builds and tests in CI.
2. Postgres image with pgvector and Swedish Hunspell; test proving compound splitting (`bygglovsansökan` → `bygglov`).
3. Migration service with schemas, roles, connection caps, partitioned queue/run tables, config outbox.
4. AppHost running everything locally with the fake gateway and Keycloak (Entra in test/prod).
5. ServiceDefaults, health endpoints, OpenTelemetry; config-version propagation library.
6. Compose files with resource zones for a single host; deploy to a test host.
7. Approval package 1 submitted (see identity plan §2); no Entra registration is needed for P0 itself.

## 12. Answered questions (2026-10-29)

1. **Blob storage in the pilot**: a file system volume behind `IBlobStore`; switch to S3-compatible or Azure Blob
   when ingestion moves to its own host.
2. **Virus scanning**: yes, a ClamAV container in the ingestion zone scans every upload and fetched file before
   parsing; infected files are quarantined and reported in the run.
3. **Name**: keep `Ume.Rag` as the working name; the user-facing name is decided before the pilot.
4. **Embedding model**: **BGE-M3** (multilingual, 1024 dimensions, 8k-token input, MIT licence) as the on-prem
   platform profile, served through the gateway; confirmed with the generated evaluation sets in P1.
