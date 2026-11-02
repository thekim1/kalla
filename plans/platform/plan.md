# Platform foundation: plan

Shared conventions and infrastructure for every service. Mirrors the LLM gateway (`~/git/llmgateway`) wherever
possible, because **the gateway team builds and operates both** (decision 2026-10-29), on separate hosts and
databases. Incorporates the architecture review
(`../../02-architecture-review.md`, items R1–R7) and the simplification pass
([`../simplification-pass.md`](../simplification-pass.md), 2026-10-30).

## 1. Repository and solution layout

One repository, one solution, like the gateway:

```
src/
  Ume.Rag.Domain/            entities, enums, port types, classification, access policy, shared contracts
  Ume.Rag.Infrastructure/    EF Core DbContext, Postgres queue + outbox, rate-limit and cache seams (in-memory; Redis from P2), gateway client, blob storage
  Ume.Rag.Retrieval/         search core and SwedishDecompounder, shared by the knowledge API, the worker and the admin test panel
  Ume.Rag.Answering/         knowledge and chat answer core without HTTP (API; admin test panel, answer-level judge, P2 skill gate)
  Ume.Rag.Pipelines/         pipeline templates and versioned parameter sets, validator, per-stage cache keys (admin and worker)
  Ume.Rag.ServiceDefaults/   OpenTelemetry, health, problem responses, security headers (copy of gateway's)
  Ume.Rag.Api/               data plane: Knowledge API + Chat API + MCP server endpoint groups (role setting)
  Ume.Rag.Tools/             P2: tool hub (IToolHub), in-process in the chat role and in the admin; see ../tools
  Ume.Rag.Worker/            ingestion worker: connectors, fixed pipelines, scheduler, retrieval evaluation runs
  Ume.Rag.AdminApi/          control plane: BFF + admin endpoints, serves the admin UI
  Ume.Rag.MigrationService/  one-shot migrator (DDL role), dev seeder
  Ume.Rag.AppHost/           Aspire orchestration for local dev
  Ume.Rag.FakeGateway/       OpenAI-compatible fake gateway: chat, 1024-dim embeddings, residency headers, catalog (the gateway's FakeLlm returns 8-dim vectors, so it cannot be reused)
  admin-ui/                  Vue 3 + Pinia, gateway design system
  parser/                    Python sidecar (Docling), own Dockerfile
templates/mcp-server/        P2: template for municipal MCP servers (see ../tools)
tests/
deploy/                      compose files, Postgres init and pgBackRest configuration, backup scripts (gateway style)
docs/
```

Full tree, build conventions, images, Compose, release and CI: [`repo-and-deployment.md`](repo-and-deployment.md).

`Ume.Rag.Api` runs Knowledge, Chat and the MCP server as endpoint groups selected by a role setting, so the MVP is
one container and they can be split later without code changes. Chat calls retrieval through an `IRetriever`
interface: in-process when co-deployed, HTTP when split.

## 2. Deployables and resource zones (R1)

Ingestion must never compete with chat for CPU or database capacity, so containers are placed in two **resource
zones** from day one, even when they share a host (P2 adds a third, `tools-hosted`, for hosted MCP servers):

| Zone | Container | Image | Notes |
|---|---|---|---|
| data plane | `rag-api` | Ume.Rag.Api | Knowledge + Chat + MCP server roles; from P2 also the in-process tool hub (tools design §2) |
| tools-hosted | `ume-mcp-<name>` | one per hosted MCP server | P2; own zone (`cpu_shares` 256, `cpus` 0.5, `mem_limit` 384m each), networks `mcp` and its own `mcp-<name>-egress` (tools design §2.2, §8.3); first and only in P2: `ume-mcp-kommun`, starting with the mock fee calculator |
| data plane | `postgres` | `ume-rag/postgres`: stock `pgvector/pgvector` (pgvector 0.8.7 on Postgres 17; tag *to verify*) + PGDG `pgbackrest` | WAL archiving and backups run inside it (repo-and-deployment §7.4). Managed Postgres is allowed where pgvector ≥ 0.8 and the needed settings are available (decided 2026-10-30, simplification pass §3) |
| control | `rag-admin` | Ume.Rag.AdminApi | Admin API + static admin UI; test panel, answer-level evaluation and (P2) the skill gate and tool discovery through the in-process hub |
| ingestion | `rag-worker` | Ume.Rag.Worker | Hard CPU/memory limits, lower CPU priority |
| ingestion | `rag-parser` | parser | Hard limits; no egress; internal network only; first to move to its own host |
| ingestion | `rag-clamav` | ClamAV | Scans uploads and fetched files before parsing; signatures updated directly from database.clamav.net (decided: no mirror) |
| – | `rag-migrate` | Ume.Rag.MigrationService | One-shot on deploy |

- **Pilot**: one host is allowed: **8 vCPU / 16 GB minimum, 24 GB recommended** (Docling models and ClamAV
  signatures need several GB together), with Docker CPU shares and limits giving the data plane priority. The parser
  gets at most half the cores. The embedding model runs behind the gateway, not on this host (EU-hosted until the
  on-prem hardware exists, §6). The pilot runs **one
  slot** per service; updates use a short announced maintenance window (a/b slots come with P2). Six long-running
  containers (`rag-api`, `rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`, `postgres`) plus the one-shot
  `rag-migrate` (simplification pass §14): **no Redis** (rate limits and the query-embedding cache in process behind
  `IRateLimitStore`/`ISharedCache`, admin sessions in Postgres) and no backup sidecar (scheduled backups run
  `pgbackrest backup` inside `postgres` from a host timer).
- **Production**: ingestion zone on its own host or node pool; the data plane host never runs OCR. Scale-out is
  **Compose per host, roles per host** (decided 2026-10-30; cross-host rules, failure domains and Kubernetes:
  repo-and-deployment §6.4, §10): same images, `Worker:Classes` and
  deployment settings per instance, one Postgres (with a streaming standby, §10), one Redis (enabled by
  `ConnectionStrings:redis`) and one S3-compatible
  object store (the municipality's S3 service if IT offers one, else SeaweedFS; decided with IT before the first
  multi-host deployment, postponed decision S2), routed LAN with TLS and host firewalls, no overlay network:

  | Topology | Hosts and what they run |
  |---|---|
  | 3 hosts (P2) | **D** data (8–16 vCPU, 64 GB, NVMe): postgres (+ standby elsewhere), redis, object store, OTel collector; **A** data plane + control (8 vCPU, 16 GB): rag-api a/b, rag-admin a/b (Open WebUI on its own VM); **I** ingestion (16 vCPU, 32 GB): rag-worker (all classes), rag-parser, rag-clamav |
  | 5 hosts (P3, when its trigger fires) | **D** as above; **A1**/**A2**: two rag-api instances each (`Rag__Roles=knowledge,chat[,mcp]`), rag-admin a on A1 and b on A2; **F** fetch: worker `preview,command,sync,fetch,eval`, clamav, small parser for inspect/preview (crawl egress host); **P** processing: worker `parse,embed`, parser 4 × 2 slots (16–24 vCPU, 48 GB), no crawl network. A sixth host is a second **P** (same Compose files, more parser), which is how ingestion grows beyond 10× |

  Up to five hosts the connection budget fits `max_connections` 100 without a pooler; PgBouncer is required on
  Kubernetes and otherwise comes only by trigger (repo-and-deployment §6.2, §10). The other P3 steps (read routing to
  the standby, a broker, a binary-quantized index, OpenSearch O1, a CUDA parser, Kubernetes, Redis HA, a distributed
  object store, a query lane) are each taken only when their scaling trigger fires (performance pass §6).
- **Database protection**: one connection pool per service role with a hard cap (starting values per instance api 12,
  admin 5, worker 10, measured in P0; retrieval limited to 4 concurrent searches per api instance; data-model §1.1), a `statement_timeout` per role, chunk writes in batches of ≤ 1,000 rows with one
  short swap transaction per document, `RunItems` partitioned by day so old partitions are dropped rather than
  deleted, and `Jobs` unpartitioned with terminal jobs deleted nightly in batches (data-model §4).
- **Release test**: chat p95 time to first token is measured during a full crawl plus an OCR batch, with the
  gateway's benchmark harness, and must stay within 10% of the idle figure (`perf.yml`, from P1). The same rule
  applies at scale in the manual **scale test** (`scale.yml`, P2, before the multi-host rollout: chat p95 TTFT during
  a 100k-page crawl + OCR batch ≤ 1.10 × idle; [`../performance-pass.md`](../performance-pass.md) §7).

**Decided (2026-10-29): our Postgres and (from P2) Redis are separate instances from the gateway's**, each system with its own
database. Reasons: the gateway's ADR 015 (other teams' data goes through interfaces, never the database; we read
organisation data through the gateway's API); the gateway stores no content while ours holds documents and chat
logs, so backups, admin access and retention must not be shared; ingestion load must never reach the gateway's
database (R1); and a later `sensitive` deployment has its own keys and hosting (§8a). Operations reuse the gateway's
migrator/role pattern and runbook.

## 3. Data ownership

One Postgres database, one schema per owner, one DB role per service with least privilege (gateway pattern).
Tables, roles, indexes, queue, encryption, retention and blob layout in detail: [`data-model.md`](data-model.md).

| Schema | Written by | Read by | Contents |
|---|---|---|---|
| `config` | admin | api, worker | collections, sources, pipeline versions, assistants (published, draft and superseded versions; test questions; templates), skills, tool servers, clients, keys, embedding profiles, settings, **config outbox** |
| `content` | worker (admin only inserts `Jobs`, incl. `command` jobs for its decisions; api only via the retrieval-counter function) | api (read-only), admin (read) | documents and chunk sets per source, chunks, caches (stage outputs, embeddings), site profiles, runs, jobs, parse observations and rules, evaluation sets and results |
| `chat` | api | admin (audited read) | conversations, messages, feedback, tool-call records |
| `audit` | all (append-only) | admin | audit entries |

**Share by cache, not by row (R2).** A document is (source, identity) and a chunk set is (document, pipeline
version, embedding profile); a source belongs to exactly one collection, so every chunk carries one scalar
`CollectionId` and membership changes never rewrite chunks. Reuse between departments comes from content-addressed
caches: from P2 (weakness review §1) the shared crawl frontier makes one request per URL per freshness window for every
source that wants it (P1: one frontier per source, shared host budgets); raw
content is keyed by content hash, parse output by (content hash, parser settings, parser build), embeddings by (text
hash, profile). Every copy after the first is cheap and nothing couples departments. Classification and access live on
collections; documents carry their source's level.

Blob storage (`IBlobStore`): uploaded files, raw fetched content and stage outputs; **write-once** keys (new content =
new key), deletion = tombstone + delayed purge. Implementations: file system volume, S3-compatible, Azure Blob.

## 4. Configuration changes: transactional and versioned (R7)

- Every change in `config` writes a row to a **config outbox** in the same transaction, with a monotonically
  increasing **config version**. Admin config transactions are serialised (`SELECT … FOR UPDATE` on the version row
  before the cross-rules run), so two concurrent saves cannot each pass a cross-rule.
- Postgres `LISTEN/NOTIFY` wakes the data plane and worker (the same mechanism wakes the job queue); each service also
  polls the current version every few seconds, so a missed notification costs seconds, not a TTL.
- Caches hold the version they were built from. **Security-relevant entities** (keys, clients, access rules,
  classification, tool permissions) use the last confirmed snapshot for up to `Config:MaxUnconfirmedSeconds` (90 s);
  beyond that, keyed requests are refused with 503. Notifications still apply changes (revocations included) at once.
  Readiness does not fail on staleness within the window, so one stalled database does not drain every instance at
  once. Other settings may serve slightly stale.
- Redis (from the first multi-host deployment, P2; none in the pilot) is used for shared rate limits, the shared
  query-embedding cache and in-flight leases only, not as the source of change events; every Redis use has a stated
  Redis-down behaviour, and the P1 home of each use is listed in data-model §11.

## 5. Identity and access

Detailed in [`../identity/plan.md`](../identity/plan.md). In short:

- People sign in exactly as in the gateway: the same OIDC/BFF mechanism and code, the same claims (`roles`,
  `departmentCodes`, `teamCodes`) and configuration keys (`UME_RAG_OIDC_*`), on **RAG's own Entra registrations**
  (`ume-rag-admin`) with `rag-*` roles; RAG's Bicep owns only RAG's registrations. The audit actor is `oid` (fallback
  `sub`). Departments without a department attribute come from **groups in the claim** (claims-only member groups,
  gateway release 2 in P2, weakness review §5; in P1 department scope comes from the `departmentCodes` claim only,
  identity plan §3); the Graph-based member-group lookup is P2.
- No Entra registration is needed for P0 or development (Keycloak + keys); admin and Open WebUI sign-in must be
  approved before the pilot.
- Every caller is a **client** from the client registry (R5): an Entra app registration, an `ume-rag-sk-`/`ume-rag-pk-` key, or a
  trusted front end such as Open WebUI. Each client has a **classification ceiling** and a data-handling profile.
- One `IAccessPolicy` in `Ume.Rag.Domain` decides (subject, client, resource, action) for every service; an admin
  "explain access" endpoint returns the failing policy step and reason.
- **Organisation data** (departments, teams, codes) is read from the gateway, the single source of truth, because
  gateway delegation (R4) charges budgets by team (the team id is sent; gateway design §5.2).

## 6. Classification

Levels `public` < `internal` < `sensitive` on sources, collections, skills, tool servers, assistants and clients.

- A collection's level is at least the highest level of its sources; an assistant's level is at least that of every
  linked collection and skill. A `public` assistant (widget) can only link `public` content.
- **Tool servers have two levels** (tools design §5.3): **clearance** (highest level of conversation data the server may
  receive; assistant level ≤ clearance, and clearance is residency-bound: `sensitive` ⇒ `OnPrem`, `internal` ⇒ `OnPrem`
  or `Eu`, `External` ⇒ `public`) and **result level** (level of what it returns; assistant level ≥ result level). So
  the effective level of an agent run is fixed at its start and never rises with tool results.
- A client may only use assistants at or below its ceiling (R5).
- **Residency matrix** (decision 2026-10-29), for every model call: chat, condense, LLM pipeline steps, evaluation
  generation and judging, tool servers:

  | Level | Allowed processing | Ceiling header sent | Gateway key |
  |---|---|---|---|
  | `public` | Any provider the gateway allows | `x-ume-max-residency: External` | open |
  | `internal` | On-prem or EU providers | `x-ume-max-residency: Eu` | `Eu`-max |
  | `sensitive` | On-prem only | `x-ume-max-residency: OnPrem` | `OnPrem`-only |

  Enforced structurally in three layers (R4): an **explicit ceiling on every call** (a `DelegatingHandler` in the
  gateway client refuses a request without one and checks that the key matches the level; unit-tested per call site;
  the gateway refuses a request without the header, key flag `RequireMaxResidency`); **delegating keys per level**,
  issued per deployable that needs them, with `AllowedIpRanges` (the worker never holds the open key; until gateway
  release 2 the pilot team's own keys per level, §7); and the
  response's `x-ume-residency` checked before any byte is streamed or an ingestion result stored (fail closed). The
  level of a call is the highest level of the data in it. Values are the gateway's `DataResidency` names (gateway
  design §3.1). The gateway answers **503 with `Retry-After`** when targets within the ceiling are drained or
  circuit-open, and `403 max_residency_not_met` only when no configured target is within it.
  **Same effective level on every turn** (chat design §8.2): for a client that keeps history (Open WebUI) the level is
  **max(assistant level, client ceiling)** from the first message. The admin validates an assistant's model against
  the gateway's `/v1/models` at that ceiling on save (in the pilot, `public` assistants in Open WebUI use EU or on-prem
  models).
- **Embeddings** (R3): the target platform embedding profile is an on-prem multilingual model (BGE-M3), so indexing
  never sends content out of the municipality, whatever its classification. **Until the on-prem model hardware exists
  (register M2) the pilot embeds through the gateway with the EU-hosted `text-embedding-3-large` at 1024 dimensions**,
  requested with OpenAI's `dimensions` parameter (Decisions "M2 and test hosting", "Pilot embedding model, U-b"); the
  profile's ceiling is `EmbeddingProfiles.MaxResidency` = `Eu`, covered by the processor agreement in the DPIA (G1), so
  no `sensitive` content is indexed on it. Moving to on-prem BGE-M3 re-embeds every chunk.

## 7. Gateway integration (R4)

Detailed in [`../gateway-changes/plan.md`](../gateway-changes/plan.md):

- **Two gateway releases** (reasoning: [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):
  - **Release 1, before the pilot**: residency ceiling with the key flag `RequireMaxResidency` and the 503/403 split;
    `/v1/models` lists only models within the request's ceiling, which the admin's model validation relies on; FakeLlm
    `dimensions`. ADR 018.
  - **Release 2, in P2**, before the first second-department or group audience: delegation (`x-ume-on-behalf-of:
    <team id>`: delegated requests charge the effective team's budgets and attribution, but **request policy comes
    from the key only**, key flag `ApplyTeamPolicy`, false for delegating keys) and member groups in claims-only
    mode. ADRs 019–020.
- **Keys per level** (§6), not one platform key; each deployable holds only the levels it needs. **Pilot
  attribution** until release 2: the pilot department's team's own gateway keys per level (no `x-ume-on-behalf-of`;
  the team keeps no request policy); every P1 gateway call is charged to that team. This is the one exception to "no
  interim per-team keys"; the gateway is unchanged by it. From release 2: delegating keys per level, and the team's
  keys are revoked.
- Tests and CI use the FakeGateway. P1a develops against an interim test/dev gateway as it is (v0.8.5: keys enforce
  `AllowedResidencies`, answers carry `x-ume-residency`, which RAG checks); release 1 lands in P1b, and go-live waits for
  it (Decisions "P1a plan answers", "Interim gateway for P1a").

## 8. Retention and personal data

Ingestion additions (connectors design §9.3, §11): quarantined files kept 30 days, metadata only; deleted upload
blobs are tombstoned and purged with the blob purge delay.

| Data | Default | Where set |
|---|---|---|
| Messages, tool-call records | 90 days per message (`sensitive`: 30 days); see §8a. In the pilot (history mode `none`, weakness review §4) the rows hold metadata only | Assistant within the per-level limits (§8a) |
| Conversations | deleted when their last message has expired | – |
| Feedback | follows its message (pilot: ratings only, weakness review §4) | – |
| Documents, chunk sets, chunks | as long as the source has them; soft delete with 14-day grace; superseded chunk sets 14 days (rollback) | Source |
| Parse output | while a live document references it | – |
| Other stage outputs (cache) | unused 30 days | Platform setting |
| Uploads | while the source exists | – |
| Blob tombstones | purged after the Postgres backup retention (35 days, proposed), so every restore point finds its blobs | Platform setting |
| Evaluation sets and results | per collection, until removed | Collection |
| Assistant versions / assistant test runs | superseded versions 1 year, at least the last 20 kept / last 20 runs per assistant (proposed) | Platform setting |
| Jobs (terminal) / run items / runs | 7 days / 90 days / 13 months (proposed) | Platform setting |
| Parse observations / retrieval statistics | 180 days / 13 months (proposed) | Platform setting |
| Embedding cache entries | unused for 30 days (proposed) | Platform setting |
| Quarantined files (ClamAV) | 30 days (proposed) | Platform setting |
| Config outbox | 30 days (proposed) | Platform setting |
| Assistant daily totals (statistics without content) | 3 years (proposed) | Platform setting |
| Audit | separate, longer (e.g. 2 years) | Platform setting |
| Postgres backups (pgBackRest) | 35 days (proposed) | Deployment |
| Admin sessions (`config."AdminSessions"`) | until expiry (≤ 12 h); expired rows deleted nightly | – |

Copies held by clients are covered by the client's data-handling profile (R5), never by SQL against their databases.
Open WebUI's feedback is pruned to the same 90 days through its HTTP API; its chat copies cannot be pruned for other
users at v0.12.5 without `ENABLE_ADMIN_CHAT_ACCESS=true`, so they stay unpruned in the pilot (stated in its
data-handling profile) until register item W2 is decided (within the DPIA, G1; weakness review §4). Nightly retention
job per schema, logged and audited. Postponed A1 (named owner before P0 ends): confirm with the registrar/archivist
whether chat logs are allmänna handlingar and whether a gallringsbeslut covers 90-day deletion; A1 gates stored chat
content and crypto-shredding (§8a). It no longer blocks the pilot, which runs history mode `none` until A1 is answered
(weakness review §4).

**Open WebUI is the primary copy** (weakness review §4). Because the pilot assistant runs history mode `none`, every
chat and feedback snapshot with content exists only in Open WebUI's own store (its database and volume). The
**DPIA** (register item G1, in P1b, finished before go-live) covers that store, its volume and who holds its
admin role, decides W2 and records the residual risk of user input (§8a). **Public-records requests** for a
conversation are answered from Open WebUI's copy in the pilot (where, in general, the DPIA and the A1 owner decide);
the runbook has a section on finding and exporting one conversation for the registrar (repo-and-deployment §11 #19).

## 8a. Data protection policy per classification level

Decisions (2026-10-29, simplified 2026-10-30): three levels stay (`sensitive` = the municipality's highest category);
chat-log protection is **configurable per level**; isolation of `sensitive`, if ever needed, is a **separate
deployment** (postponed T1/K2).

**Baseline for every level**: encrypted volumes and encrypted backups (Postgres has no built-in transparent
encryption; LUKS on-prem, Azure disk encryption in Azure). No content in logs, traces, metrics or Redis at any level
(the query cache holds hashes and embeddings only).

**Pilot: history mode `none` until A1 is answered** ([weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)).
The pilot assistant stores metadata and ratings only, no message content. So P1 does **not** build the chat bucket
keys, chat content encryption, crypto-shredding or conversation review by `rag-reviewer`; the data model keeps their
columns and tables, and they are built when A1 allows stored content. Until then no assistant can be set to `stored` or
`session`, the matrix below is the target once they are built, and feedback comments stay in Open WebUI (mode `none`
refuses them, chat design §3.5). Blob encryption for `internal` uploads is unchanged.

**Policy matrix.** Platform admins set, per level, the defaults and the **limits** an assistant may choose within
(an assistant can tighten, never loosen). Proposed starting values:

| Setting | `public` | `internal` | `sensitive` |
|---|---|---|---|
| History mode (`stored` / `session` (24 h) / `none`) | stored | stored | stored, short retention |
| Retention (default / max) | 90 / 90 days | 90 / 90 days | 30 / 90 days |
| Application-level encryption of chat content | optional (default on) | **required** | **required** |
| Key scope | per (level, expiry month) bucket | per (level, expiry month) bucket | per (level, expiry month) bucket, separate master key |
| Admin review of conversations (`rag-reviewer` only; admin design §3.1, §8) | reason | reason | reason **+ second approver** |
| Admin reading a single feedback comment (`rag-reviewer` or `rag-department-admin`) | one-click reason, audited | one-click reason, audited | only through the review flow above |
| Allowed clients | per ceiling | per ceiling | only clients without their own history store |
| Feedback comments, tool results, cited source text | same policy as messages | same | same |

**Application-level (envelope) encryption of chat** (built when A1 allows stored content, weakness review §4).

- One data key (AES-256-GCM) per (level, expiry month) **bucket**, wrapped by that level's master key; each message is
  encrypted under the bucket of its own expiry month. Message text, titles, feedback comments, tool arguments and
  results, and the snapshot of cited source text are encrypted before they reach the database. Metadata (times,
  assistant, subject id, token counts, cost, outcome) stays readable for statistics and retention. A user deleting a
  conversation deletes its rows.
- Master keys are held in a key service behind `IKeyProvider`: Azure Key Vault / Managed HSM in Azure, OpenBao/HashiCorp
  Vault or an HSM on-prem, a local key file in development. **Postponed (2026-10-29): the pilot uses the file key
  provider**; Azure Key Vault is the intended target (`../postponed-decisions.md`, item K1). Pilot rules for file keys:
  - master keys are 256-bit random files in the deployment secrets directory (`secrets/keys/<key name>`, mode 0400),
    never mounted into Postgres; the chat keys (`rag-chat-<level>`, created only once A1 allows stored content) only
    into `rag-api` and `rag-admin`, the blob key
    (`rag-blob-internal`) into `rag-worker`, `rag-admin` (uploads) and read-only into `rag-api` (citation downloads;
    repo-and-deployment §6.3); created by `init-deployment.sh --key-provider file --allow-file-keys`;
  - **two key classes**: long-lived keys (blob master keys, chat master keys once they exist, and the pgBackRest
    repository passphrase; in P0/P1 the escrow covers the blob keys and the passphrase only) are **escrowed** offline, separately from database backups, with retention ≥ the data they protect; shred-class
    keys (per-month chat wrapping keys, only once shredding is switched on) follow the shred rule below;
  - the host disk is LUKS-encrypted (baseline), so a stolen disk or a database backup alone reveals nothing;
  - **no `sensitive` data** while file keys are used.

  Switching to Key Vault later: create the keys in Key Vault, run the `rewrap` command (unwraps every bucket key with
  the file key and re-wraps it with Key Vault; no message is re-encrypted), then delete the key files. On-prem hosts
  need network access to Key Vault (unwrapped keys are cached 5 minutes).
- Only the Chat API (a user's own history) and the admin review path (audited, with reason and, for `sensitive`, a
  second approver) can unwrap bucket keys. Database admins, a dumped table or a stolen backup see ciphertext only.
- **Crypto-shredding waits for A1.** Until the registrar answers A1, retention deletes rows only. The format supports
  shredding: if A1 allows deletion, a setting switches it on; new buckets are then wrapped by per-month shred keys in
  the key service, and destroying a month's shred key after its last expiry makes every backup copy of that month
  unreadable (data-model §7).
- Consequence: no server-side full-text search in other people's chats; a user's own history list shows decrypted
  titles through the Chat API.

**User input** (weakness review §4). Users can type anything into an `internal` assistant. Open WebUI shows a fixed
banner: "Skriv inte in sekretessbelagda uppgifter eller känsliga personuppgifter." (chat design §4.3). The pilot
client's ceiling stays `internal` only for EU providers covered by a processor agreement (PUB-avtal) and listed in the
DPIA (G1); if the DPIA requires more, the pilot runs on on-prem models only, set by model choice on its assistants
(answer and condense models without EU fallbacks; identity design §4.1). The DPIA records the residual risk. With no
on-prem model hardware yet (register M2) that fallback does not exist: if the DPIA refuses EU processing, the pilot
waits for hardware or another provider (accepted risk, Decisions "P1a plan answers").

**Content beyond chat logs.** `internal` blobs (uploads, raw content, parse outputs) are envelope-encrypted; `public`
blobs rely on the encrypted disk only. Chunk text cannot be column-encrypted because full-text search needs it, so
`sensitive` documents need isolation before they are onboarded.

**Isolation of `sensitive`.** Rows carry a plain `Classification` column and retrieval filters by level. If `sensitive`
needs isolation, it becomes a **separate deployment** (its own on-prem Postgres, volume, keys and credentials) reached
over the existing HTTP seam (`Retrieval:Mode=Http`). **Decision point** (T1, with K2): before the first `sensitive`
source is onboarded.

## 9. Evaluation as a platform capability (R6)

Detail: `../ingestion-worker/design-processing.md` §13–§14.

Every collection has an **evaluation set**: question/answer pairs generated automatically from sampled chunks by an
on-prem model (judge-filtered), plus human questions and rewritten feedback questions over time. Generation runs at the
collection's ceiling, `Eu` under M2 (Decisions "Evaluation models at the collection's ceiling"); the pilot's
nightly runs use the deployment's own (EU) embedding profile (Decisions "P1b briefs", O1 and O4). The harness is
**advisory**: it runs on the full collection after each publish and nightly, reports recall@10 and MRR with counts
compared with the previous run on the publish screen, and the owner decides to keep or roll back (previous chunk sets
are kept 14 days for one-click rollback). A collection goes live when its owner publishes it. The worker runs the
retrieval evaluation; from P2 (weakness review §1) a nightly on-prem judge in `rag-admin` (which already runs `Ume.Rag.Answering`
and holds the keys of every level) measures answer faithfulness and citation precision; the P2 skill gate runs there
too. From P2 each assistant also has a small **test-question set** (question, expected behaviour, optional expected source or
phrase) that `rag-admin` re-runs with the answer judge after every publish of a new assistant version, advisory like
the collection report (run kind `assistant-tests`, data-model §2.2). A blocking gate is reconsidered only when measured
run-to-run variance shows it can tell versions apart. The postponed Swedish benchmark becomes the curated part of
these sets.

**Pilot gold set and go-live gate** (reasoning: [weakness review §2](../weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch)). The main session drafts the
candidate gold set from the Turkos crawl: about 120–150 answerable questions, each with its expected source URL(s) and
facts, 30–50 unanswerable ones (off-topic and near-miss, expected outcome "not found") and at least 10 temporal ones,
in residents' wording rather than page headings, short and long mixed, each marked as a draft. DoIT (the pilot
department) reviews, edits and approves them, and may add its own, before they go in as `human` questions (the start
of register B1; Decisions "Gold set drafted by the main session, reviewed by DoIT"). Reports lead with their results;
generated questions are a regression signal only. The
same set calibrates both not-found thresholds (vector side alone until `coverageMinKeywordScore` is calibrated) and
decides, run three ways, whether hybrid and the decompounder beat vector-only. The pilot goes live when department
reviewers, grading the gold-set answers by hand, find correct answers ≥ 80 %, citation precision ≥ 90 %, false
not-found ≤ 10 % and off-topic questions answered "not found" ≥ 80 % (starting thresholds, confirmed by the
department when the set is written). This gate applies once; publish stays advisory. **Grading**: a script
(`scripts/grading/grading.py`) sends each gold-set question through the chat API as the pilot assistant and writes
question, answer, citations and coverage to a CSV; reviewers grade it in a spreadsheet, and the graded file with its
totals is attached to the go-live decision. P1 has no grading UI. **Pilot success** = the gate is met at go-live and
still met at the end of the pilot (re-run monthly), and the department decides to continue.

## 10. Cross-cutting engineering

- **.NET 10, Aspire 13**, minimal APIs, EF Core with Npgsql + `Pgvector.EntityFrameworkCore`, central package
  management, the gateway's `.editorconfig` and analyzers.
- **Observability**: OpenTelemetry traces, metrics and logs from every service; one trace follows a chat message
  through retrieval, tool calls and the gateway; ingestion runs carry the run id; config version in every log scope.
- **Security**: crawler egress blocks private ranges unless allow-listed (SSRF); parser and MCP servers have no egress
  beyond what they need; the worker parses only HTML, text, Markdown and CSV, every PDF and binary format goes to the
  parser; uploads limited by size and type; secrets as files, key-per-file, never environment variables
  (repo-and-deployment §6.3); prompt injection rules in `../tools/plan.md` §6.
- **Testing**: unit; integration against real Postgres (and Redis for the P2 implementations of the seams; Aspire/Testcontainers); fake gateway; end-to-end
  browser tests; evaluation sets in CI on a fixture collection; the R1 release test (P1).
- **Backup and recovery**: pgBackRest with continuous WAL archiving (`archive_timeout` 60 s) and point-in-time
  restore; RPO ≤ 5 min, RTO ≤ 4 h for the pilot; uploads copied to the backup target within 15 minutes; a
  point-in-time restore drill on the test host before go-live (P1b), then quarterly. A streaming standby is an **availability** requirement before production beyond
  the pilot (P2). Detail: repo-and-deployment §7.4.
- **Releases**: release bundle with compose files, init script, backup/restore, upgrade guide; rolling update from
  P2 (a/b slots), a short announced maintenance window in the single-host pilot.

## 11. P0 deliverables

P0 is a skeleton that builds, deploys, is observable and can be restored (ordered checklist: repo-and-deployment §11).
**P0 is finished** (`v0.1.0`, 2026-10-31). Items 5, 7, 8 and 9 changed after it was planned, as noted in each item
(Decisions "M2 and test hosting", "P1 pilot sign-in").

1. Repository skeleton, projects, `verify.yml` (incl. the image test job), builds and tests in CI.
2. ServiceDefaults, health endpoints, OpenTelemetry; config-version library with the grace window (§4).
3. Migration service with schemas, roles, connection caps, config outbox, `Jobs` and day-partitioned `RunItems`, on
   the stock pgvector image; .NET `SwedishDecompounder` test (`bygglovsansökan` → `bygglov`, `ansökan`) plus one
   integration test that a part matches through Postgres.
4. FakeGateway, Keycloak realm, keys, admin UI skeleton and write path, AppHost, parser contract, worker dispatcher
   skeleton.
5. Single-host Compose with resource zones; pgBackRest with WAL archiving and a **point-in-time restore drill**;
   `release.yml`. The test-host deploy and the test host's restore drill run in P1b, before go-live.
6. Scale seams: pooler-safe SQL rule and scan test; `IBlobStore` with the write-once contract; `IJobQueue` over the
   Postgres queue; `Worker:Classes` and `Worker:Concurrency:<class>`.
7. Entra approval package 1 (RAG's own registrations) built and kept, not submitted: the pilot signs in with the
   Keycloak shared with the gateway, and package 1 is submitted for P2; no Entra registration is needed for P0 itself;
   gateway release 1 agreed with the gateway team and scheduled before the pilot (release 2 in P2, weakness review §5).
8. Data protection (weakness review §4, owned by the user, outside the build streams): the DPIA (register G1) with
   the data protection officer, moved to P1b and finished before go-live; A1 has a named owner. Key escrow covers the blob master key and the pgBackRest
   passphrase only (no chat master keys before A1 allows stored content).
9. On-prem capacity measured on the real gateway hardware (weakness review §5, checklist #22): BGE-M3 embedding
   throughput and condense and answer model latency, recorded against the performance-pass estimates. Register M2 is
   triggered (no on-prem model hardware yet), so this run waits until the hardware exists and is no P1b gate; until
   then the pilot's models are EU-hosted (§6).
10. Owners and dates (weakness review §5, owned by the user): A1, G1, W1, W2, N1, U1 and P2 each have a named person
    and a date (`postponed-decisions.md`, phase-start rule).

## 12. Answered questions (2026-10-29)

1. **Blob storage in the pilot**: a file system volume behind `IBlobStore` (write-once contract); switch to
   S3-compatible or Azure Blob when ingestion moves to its own host.
2. **Virus scanning**: yes, a ClamAV container in the ingestion zone scans every upload and fetched file before
   parsing; infected files are quarantined and reported in the run.
3. **Name**: keep `Ume.Rag` as the working name; the user-facing name is decided before the pilot.
4. **Embedding model**: **BGE-M3** (multilingual, 1024 dimensions, 8k-token input, MIT licence) is the on-prem
   platform profile, served through the gateway, once the on-prem model hardware exists (register M2). Until then the
   pilot profile is the EU-hosted `text-embedding-3-large` at 1024 dimensions through the gateway, with `dimensions`
   sent on every embedding call and `MaxInputTokens` 8191 (§6; Decisions "Pilot embedding model, U-b"); moving to
   BGE-M3 re-embeds every chunk (a P1b/P2 item).

## 13. Deferred or removed (simplification pass)

See [`../simplification-pass.md`](../simplification-pass.md).

| Item | Where it went |
|---|---|
| Own Postgres image with Hunspell `sv_SE` ("own container everywhere") | Removed: stock pgvector image, .NET decompounder (§3 there) |
| Platform-wide document store, shared chunk sets | Removed: per-source documents and chunk sets, reuse by caches (§2 there) |
| Tier resolver and data tiers | Removed: separate deployment if ever needed (T1/K2) |
| Per-conversation keys, 7-day epochs, always-on shred buckets, one ≤ 30-day key backup rule | Removed: bucket keys, shredding after A1, two key classes with escrow (§4–§5 there) |
| Interim linked keys, releases B → C → A, one platform service key, shared Entra registration | Removed: two gateway releases (§7), keys per level, RAG's own registrations (§6–§7 there) |
| Evaluation gates, `building` state, parse auto-apply, `ner` job class | Removed (§1, §2, §8 there) |
| Graph member-group lookup; `rag-renderer` (only if needed); a/b slots and the multi-host seams | P2 |
| Separate `rag-tools` service (with the first external, OBO or write tool) | P3 |
| 10-second fail-closed config rule | Replaced by the grace window (§9 there) |
| Editable assistant mode (`rag`/`agent`), knowledge through skills, assistant settings edited in place (§15 there) | Removed: mode derived from tool-bearing skills, direct collections only, draft and published assistant versions with test questions (data-model §2.1) |
| `redis` and `rag-pgbackrest` containers in the pilot (eight long-running containers → six, §14 there) | Redis: P2 (first multi-host deployment, `ConnectionStrings:redis`), in-memory seams and Postgres admin sessions in P1; pgBackRest inside the Postgres image |

## 14. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer), P1 is
split into **P1a**, a walking skeleton (URL list, umea.se crawl with a hand-checked Sitevision preset, upload, hybrid
retrieval with citations, chat API, one assistant in Open WebUI; done when a pilot member gets a cited answer from
umea.se content), and **P1b**, pilot hardening (feedback, the go-live gate of §9, freshness, data protection, admin
journey pages); phase table in [`../README.md`](../README.md). Moved out of P1:

| Item | Where it went |
|---|---|
| Crawl frontier shared between sources (claims, releaser) | P2; P1 has one frontier per source with shared host budgets (§3) |
| Nightly answer-level judge, per-assistant test-question sets | P2 (§9) |
| Assistant version diff, templates, copy, department map | P2 (admin plan §8) |
| Site-profile learning, publish plans from effect tags, adaptive embedding concurrency and range sizing | P2 (ingestion plan §13) |

Per [weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| Stored chat content in P1 (history modes `stored`/`session` for the pilot assistant) | When A1 allows stored content; the pilot runs history mode `none` (§8a) |
| Chat bucket keys, chat content encryption, chat master keys `rag-chat-<level>` and their escrow | When A1 allows stored content; the data model keeps the columns and tables (§8a, data-model §7) |
| Crypto-shredding | Still behind A1 (now also behind stored content) |
| Conversation review and reading feedback comments by `rag-reviewer`/`rag-department-admin` | When A1 allows stored content; feedback comments stay in Open WebUI (§8a) |

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| One combined gateway release before the pilot | release 1 (residency ceiling, FakeLlm `dimensions`) before the pilot; release 2 (delegation, member groups) in P2 (§7) |
| Delegating keys for the pilot; "no interim per-team keys" | the pilot team's per-level keys until release 2 (§7) |
| Department scope from groups in the claim in P1 | P2 with release 2 (§5) |
| On-prem throughput measured in P1 (R1) | When the on-prem model hardware exists (register M2; §11 item 9) |
