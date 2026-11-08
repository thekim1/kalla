# Platform data model and storage: detailed design

Detail round, step 1. Covers every table, role, index, queue, key and blob path of Ume.Rag. Builds on
[`../archive/area-plans/platform.md`](../archive/area-plans/platform.md) §2–4, §6, §8, §8a and §9 and the data parts of every service plan. Where this document had to choose
something the plans leave open, or found two plans disagreeing, the choice is listed in §12 with a recommendation.
Removed or deferred mechanisms are listed once, in §13 (reasoning: [`../archive/passes/simplification-pass.md`](../archive/passes/simplification-pass.md))
and §15 (reasoning: [`../archive/passes/weakness-review.md`](../archive/passes/weakness-review.md)); the rest of the document does not mention them.
Where the schema is built, the EF model and migrations (`src/Ume.Rag.Infrastructure/Persistence`) are the source;
this document describes them and the parts still to build.

**Gateway conventions mirrored** (`~/git/llmgateway`, user decision: be as compatible as possible):

| Convention | Gateway source | Here |
|---|---|---|
| One EF Core context, Npgsql, `AddNpgsqlDbContext`, production refuses superuser or non-`VerifyFull` TLS | `src/Ume.LlmGateway.Infrastructure/Persistence/GatewayDbContext.cs`, `InfrastructureExtensions.cs` (`AddGatewayDatabase`) | `RagDbContext`, `AddRagDatabase`, same checks |
| Identifiers: EF defaults, **PascalCase, quoted** (`"UsageRecords"."TeamId"`), plural table names, `IX_`/`CK_` names | `GatewayDbContext.cs`, `deploy/postgres/app-roles.sql` | Same, inside lowercase schemas (`content."Chunks"`) |
| Ids `Guid.CreateVersion7()`; append-only logs use `bigint` identity | `Domain/Entities/Entities.cs` (`Department`, `VirtualKey`, `AuditLogEntry`) | Same |
| `DateTimeOffset` stored UTC (`UtcDateTimeOffsetConverter`) → `timestamptz` | `GatewayDbContext.cs` | Same |
| Enums as strings with max length (`HasConversion<string>()`); policies as `jsonb` | `GatewayDbContext.cs` (`PolicyColumn`) | Same; classification is `varchar(20)` `public`/`internal`/`sensitive` |
| One-shot migrator: `MigrateAsync`, then roles SQL from `Migration:RolesSqlPath`, then dev seeder | `src/Ume.LlmGateway.MigrationService/MigrationWorker.cs` | Same (§10) |
| Roles: `init.sql` creates login roles from secret files; DDL role owns the database; `app-roles.sql` revokes and re-grants **table by table** in one transaction on every migration run; `DatabaseRoleTests` checks effective grants | `deploy/postgres/init.sql`, `deploy/postgres/app-roles.sql`, `tests/.../DatabaseRoleTests.cs`, `docs/runbook.md` (DB section), `docs/architecture.md` (DDL vs DML roles) | Same files under `deploy/postgres/`, same test |
| Retention only through `SECURITY DEFINER` step functions owned by the DDL role, called nightly by the admin API in batches; DML roles never get `DELETE` on audit | ADR 016 in `docs/adr.md`, `Migrations/20261012060353_DataRetention.cs` (`ume_retention_step`) | §8 |
| Keys: random secret shown once, `KeyHash` = hex HMAC-SHA256 with a pepper outside the DB, display `Prefix`, rotation with `GraceUntil`/`RotatedToKeyId`, Data-Protection-encrypted `EncryptedSecret` for audited reveal | `Domain/Services/VirtualKeyHasher.cs`, `VirtualKey` entity, `Infrastructure/Security/Security.cs`, ADR 004 / 007 | `config."Keys"`, prefixes `ume-rag-sk-` / `ume-rag-pk-` |
| Audit entry columns (`Actor`, `ActorName`, `ActorRoles`, `ActorDepartments`, `SourceAddress`, `SessionId`, `RequestId`, `UserAgent`, masked `Details`) | `AuditLogEntry`, `GatewayDbContext.cs` | `audit."AuditLog"` plus `Reason`, `Service`, `ConfigVersion`; actor = `oid` (fallback `sub`) |
| Data Protection key ring in the database (`DataProtectionKeys`) | `GatewayDbContext` implements `IDataProtectionKeyContext` | `config."DataProtectionKeys"` |
| Admin sessions on the server (`IAdminSessionStore` behind the BFF's `AdminTicketStore`; Redis in the gateway) | `Infrastructure/Stores/Stores.cs` (`IAdminSessionStore`), `AdminApi/AdminSessions.cs` | `PostgresAdminSessionStore` over `config."AdminSessions"` (§2.1); no Redis in P1 |
| Redis (P2, first multi-host deployment): one `RedisKeys` class, all keys under one prefix, ACL user limited to it | `Infrastructure/Stores/RedisKeys.cs`, `deploy/redis/users.acl.example` | `ume-rag:` prefix (§11) |

The gateway has no connection caps, `statement_timeout`, partitioning, schemas or `LISTEN/NOTIFY`; those are new here
(R1, R7) and follow Postgres defaults where nothing better is needed.

## 1. Database layout

One Postgres 17 instance on the **stock `pgvector/pgvector` image** (pgvector 0.8.7; tag *to verify*), or managed
Postgres where pgvector ≥ 0.8 and the settings below are available; database **`ragdb`** (gateway: `gatewaydb`),
owned by the DDL role. Extension `vector` is created by `init.sql` as the bootstrap superuser (it is not a trusted
extension). The `public` schema keeps only the extension; `REVOKE ALL ON SCHEMA public FROM PUBLIC` and `USAGE` for the
service roles, as in the gateway.

| Schema | Owner role | Written by | Read by |
|---|---|---|---|
| `config` | `ume_rag_migrator` | admin (plus `KeyLastUse` by api) | api, worker (table subsets) |
| `content` | `ume_rag_migrator` | worker (plus `Jobs` inserts by admin, counters by api via a function) | api (read-only, subset), admin (read) |
| `chat` | `ume_rag_migrator` | api | admin (read; decryption audited) |
| `audit` | `ume_rag_migrator` | all (append-only `INSERT`) | admin |

### 1.1 Roles, caps and timeouts

Created by `deploy/postgres/init.sql` from `/run/postgres-secrets/{migrator,api,admin,worker}_password` (the gateway's short names); `pg_hba.conf`
allows only `hostssl ragdb ume_rag_migrator,ume_rag_admin,ume_rag_api,ume_rag_worker all scram-sha-256` (plus
`ume_rag_monitor` from P2, below). pgBackRest runs inside the `postgres` container (`ume-rag/postgres`) and connects
over the local Unix socket, so it needs no network entry (repo-and-deployment §7.4).

| Role | Used by | `CONNECTION LIMIT` | Npgsql `Maximum Pool Size` per instance | `statement_timeout` | `lock_timeout` | `idle_in_transaction_session_timeout` |
|---|---|---|---|---|---|---|
| `ume_rag_migrator` | `rag-migrate` (DDL, owner of everything) | 3 | 2 | 0 (none; index builds) | 10s | 0 |
| `ume_rag_api` | `rag-api` (knowledge, chat, MCP; P2 in-process tool hub) | 26 | 12 (+1 LISTEN) | 5s | 1s | 10s |
| `ume_rag_admin` | `rag-admin` | 12 | 5 (+1 LISTEN) | 30s | 5s | 60s |
| `ume_rag_worker` | `rag-worker` | 12 | 10 (+1 LISTEN) | 60s | 5s | 60s |
| `ume_rag_monitor` (P2) | OTel Collector `postgresql` receiver (`pg_monitor`) | 2 | – | 5s | 1s | 10s |

`max_connections = 100` (`deploy/postgres/postgresql.conf`). Pool sizes are **starting values measured in P0**
(reasoning: simplification pass §9). The api pool of
12 serves `Retrieval:MaxConcurrentSearches` **4** = ⌊(pool − 3) ÷ 2⌋ (a search may hold 2 connections while K and V
overlap; 3 stay for chat writes and the stats flush; knowledge design §6.6); more searches come from more
instances, not a larger pool. A start-up check of the value against the pool size is still to build (today
`RetrievalEngine` only refuses values below 1). Limits leave room for a second instance of
api and admin (the P2 a/b slots) and for the worker's stop-then-start; the sum (3 + 26 + 12 + 12 + 3 reserved = 56)
stays well under `max_connections`. **Budget rule** (P2, first multi-host deployment; reasoning and per-topology
counts: performance pass §3.1): for each role, Σ over instances (`Maximum Pool Size` + 1 LISTEN) ≤ its
`CONNECTION LIMIT`; Σ limits + 3 (`superuser_reserved_connections`) + 2 (`ume_rag_monitor`) ≤ `max_connections`;
checked at deploy time by `deploy/postgres/connection-budget.sh`; the pilot's values are fixed in `.env`. **No
pooler up to 5 hosts**: a backend costs about 10 MB (no dictionaries load into Postgres, §3.1), and the Postgres
memory limit comes from the P0 measurement; PgBouncer (≥ 1.21, transaction mode) comes only when the budget no longer
fits `max_connections`, e.g. a further api host at 5-host scale (performance pass §6 trigger). There is no
`ume_rag_tools` role: the P2 tool hub runs in-process in `rag-api` and `rag-admin` (§1.2), and the role comes only with
a separate `rag-tools` service (P3, §13). Code is **pooler-safe from P0**: no session `SET`, session advisory locks,
`WITH HOLD` cursors, temporary tables other than `ON COMMIT DROP`, or SQL `PREPARE`; LISTEN runs on its own
`ConnectionStrings__ragdb-listen` (§4.3), and the worker's HNSW rebuild, the one session `SET`
(`statement_timeout = 0`), on its own `ConnectionStrings__ragdb-maintenance` (falls back to `ragdb`; never through a
pooler; knowledge design §6.4); a literal-scanner test checks the SQL in code and allows that one statement in that one
file (migrations are not scanned).
Caps and timeouts are applied in `init.sql` and **only** there: `app-roles.sql` runs as the `NOCREATEROLE` DDL role,
which Postgres refuses `ALTER ROLE` on
the service roles, so a later change of a cap, timeout or `hnsw.*` default is a superuser step in `docs/upgrade-guide.md`:

```sql
ALTER ROLE ume_rag_worker CONNECTION LIMIT 12;
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET statement_timeout = '60s';
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET lock_timeout = '5s';
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE ume_rag_migrator IN DATABASE ragdb SET maintenance_work_mem = '1GB';   -- HNSW builds
-- Retrieval defaults (knowledge design §6.4), no per-query SET: api, worker (evaluator), admin (test panel)
ALTER ROLE ume_rag_api IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE ume_rag_api IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE ume_rag_api IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
ALTER ROLE ume_rag_admin IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE ume_rag_admin IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE ume_rag_admin IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
```

No retrieval statement runs `SET LOCAL`; every collection uses these role defaults (knowledge design §6.4).

### 1.2 Grants (`deploy/postgres/app-roles.sql`)

Revoke-and-grant in one transaction on every migration run, tables named one by one, so a new table is unreadable until
listed here (copied from the gateway's file header). `S` select, `I` insert, `U` update, `D` delete, `X` execute.
The P2 tool hub runs in-process in `rag-api` (chat) and in `rag-admin` (skill gate, discovery, drift polling; tools
design §2), so both roles get the tool grants; there is no tool role before a separate `rag-tools` service (P3). The
worker runs only retrieval evaluation and reads no skill or tool tables and of the assistants only their published
links: the HNSW index health check samples every scope an assistant link uses (knowledge design §6.4), so it reads
`PublishedAssistantVersions` (`AssistantId`, `CollectionLinks`) and `Assistants` (`Id`, `State`, to skip archived
ones), column grants only. It reads `GatewayModels` for the evaluation model check (processing design §13.1).

| Object | admin | api | worker |
|---|---|---|---|
| `config` tables except below | S I U D; S on the view `PublishedAssistantVersions` (explain access decides as the api does, identity §4.9) | S on: `Departments`, `Teams`, `Settings`, `DataProtectionPolicies`, `EmbeddingProfiles`, `Collections`, `Sources` (columns `Id`, `CollectionId`, `Type`, `Classification`, `State`, `DeletedAt`, `ContainsThirdPartyText`, `DemoteRules`; knowledge design §6.3, §6.5), `Assistants`, `AssistantAudiences`, the view `PublishedAssistantVersions` (published versions only, §2.1; never `AssistantVersions` itself, so a draft cannot reach chat), `Skills`, `SkillVersions`, `SkillVersionLinks`, `SkillVersionFiles` (P2), `SkillShares` (P2), `Tools`, `ToolDefinitions`, `ToolApprovals`, `ToolDepartmentGrants` (P2), `GatewayModels`, `Clients`, `Keys`, `CollectionAudiences` (P2), `CollectionShares` | S on: `Departments`, `Teams`, `Settings`, `DataProtectionPolicies`, `EmbeddingProfiles`, `Collections`, `Sources`, `Pipelines`, `PipelineVersions`, `GatewayModels`; columns `AssistantId`, `CollectionLinks` of the view `PublishedAssistantVersions` and `Id`, `State` of `Assistants` |
| `config."ToolServers"` (P2) | S I U D | S | – |
| `config."KeyLastUse"` | S | S I U | – |
| `config."DataProtectionKeys"` | S I | S | S |
| `config."AdminSessions"` | S I U D | – | – |
| `config."ConfigVersion"`, `"ConfigOutbox"` | S, `U` on `ConfigVersion` only for the `FOR UPDATE` lock (§5; writes otherwise only via trigger) | S | S |
| `config."__EFMigrationsHistory"` | S | – | – |
| `content` tables | S (incl. test-panel retrieval, knowledge design §4.2), plus **I U on `EvaluationRuns`, `EvaluationResults`** (answer-level runs of kinds `answers` and `assistant-tests` and P2 skill-gate runs, which run in `rag-admin`) | S on `Documents` (columns `Id`, `SourceId`, `Classification`, `IdentityKind`, `IdentityHash`, `Title`, `Url`, `MediaType`, `DisplayName`, `SourceModifiedAt`, `PageCount`, `LiveChunkSetId`, `DeletedAt`, `Language`, `FetchedAt` (statement D and the citation fields, knowledge design §6.3, §10.1), `CanonicalIdentity`, `ContentHash` (the blob key and `ETag` of `/content`, knowledge design §3.5)), `ChunkSets`, `Chunks` | S I U D (all), and `MAINTAIN` on `content."Chunks"` for the HNSW rebuild (knowledge design §6.4) |
| `content."Jobs"` | S I | – | S I U D |
| `content."BlobTombstones"` (§9) | S I (the admin tombstones the upload blobs it replaces or deletes) | – | S I U D (all `content`; it tombstones stage outputs and quarantine and runs the purge sweep) |
| `chat` tables | S, plus **U on `Feedback` column `ReviewState`** (admin design §8.1) | S I U D | – |
| `audit."AuditLog"` | S I | I | I |
| `audit."ConversationAccess"` | S I | – | – |
| `audit."ToolCallEvents"`, `"ToolDefinitionObservations"` (P2) | S I | I | – |
| `audit."AuthFailures"` | S I | I | – |
| Functions (§3.3, §4, §5, §8) | X `chat.rag_retention_chat_step`, `audit.rag_retention_audit_step`, `config.rag_retention_config_step`, `content.rag_or_query` | X `content.rag_count_retrievals`, `content.rag_or_query` | X `content.rag_partition_maintain`, `content.rag_ensure_profile_index`, `content.rag_or_query` |
| (every role with `rag_or_query`) | X the aggregate `content.rag_tsquery_or_agg`, which `rag_or_query` calls with the caller's rights | | |
| Sequences | `USAGE, SELECT ON ALL SEQUENCES` in the schemas each role writes | | |

Partitioned tables are only accessed through their parent, so grants on the parent suffice.

## 2. Tables

Notation: `PK`, `FK`, `U` unique, `IX` index. All `Id uuid` are UUID v7 unless noted. Rows of `content` and `chat`
carry `"Classification" varchar(20) NOT NULL CHECK ("Classification" IN ('public','internal','sensitive'))` (the
source's or assistant's level; retrieval filters on it). Config rows referenced from `content` and `chat` are
referenced **by id without a FK**.

### 2.1 `config` (owner: admin API)

| Table | Purpose | Key columns | Keys and indexes | Classification rules |
|---|---|---|---|---|
| `Departments` | Mirror of gateway departments, incl. member groups | `Id uuid` (= gateway id), `Name varchar(200)`, `CostCenterCode varchar(50)`, `MemberGroups varchar(256)[]` (group claim values: Entra object ids, Keycloak/AD FS names; identity design §2.2), `IsActive bool`, `SyncedAt` | PK `Id`; U `CostCenterCode` (no GIN on `MemberGroups`: matched in memory from the config snapshot) | – |
| `Teams` | Mirror of gateway teams (attribution `x-ume-on-behalf-of` sends `Id`) | `Id uuid` (= gateway), `DepartmentId`, `Name`, `Code varchar(100)` (from `/v1/catalog/teams`, gateway release 2 in P2, weakness review §5; display only), `IsActive`, `SyncedAt` | FK `DepartmentId` → `Departments` (Restrict, as gateway); U `Code` | – |
| `DepartmentSettings` (P2) | RAG-side settings of a department (`Departments` is a gateway mirror, so it gets no local columns) | `DepartmentId` PK FK → `Departments`, `EditOwnerTeamOnly bool NOT NULL DEFAULT false` (`Edit`, `EditPipelines` and `EditSkills` on assistants, collections, sources and skills limited to members of their owner team, matched from the `teamCodes` claim to `Teams.Code`, which is therefore no longer display-only; department admins keep every right; simplification pass §15), `UpdatedAt`, `UpdatedBy` | PK `DepartmentId` | – |
| `Settings` | Singleton: platform defaults (deletion guard 20 %, crawl politeness, retention values from §8, `AuditRetentionDays` 730, `BlobPurgeDelayDays` 35, `ChatShreddingEnabled` false (switched on only if A1 allows deletion, §7), `RetentionLastRun*`; worker: `WorkerConcurrency jsonb`, `DegradedFailureRatio` 0.05, `ScheduleJitterMinutes` 90, `PreviewMaxUsd` 0.50, default schedules per source type (engine design §9–§11); connectors: `DeletionGuardMinCount` 5, `DeletionGuardMaxCount` 500, `Crawl jsonb` (`MunicipalHosts`, rates, `HostOverrides`, blocked hosts, `AllowedPorts`, `FreshnessHours` 12, window 22–06, limits 10 000/10, body caps HTML 10 MB / files 50 MB, redirects 5, `IdentityRulesVersion`, `IncompleteRunsAlert` 3; connectors design §3.1, §4–§7), `QuarantineRetentionDays` 30; processing: `PiiFlagRules jsonb`, `PiiOwnDomains text[]`, `NerMinScore` 0.80, `EvaluationModel`, `EvaluationJudgeModel` (default: the gateway's on-prem general chat model), `EvaluationMaxQuestions` 100; retrieval: `Retrieval jsonb` = `RetrievalSettings` defaults (1.0 / 1.0, 100, 3, `recency` null, 0.50, `coverageMinKeywordScore` null, 1,500) plus platform-only `exactScanMaxChunks` 10,000, `minHnswSelectivity` 0.2 %, `expandTotalMaxTokens` 8,000, knowledge design §12.1 (the download-grant lifetime is the deployment setting `Knowledge:DownloadGrantLifetime`, not a `Settings` value); chat: `Chat jsonb` = `CondenseModel` (the gateway's on-prem small instruct alias; the only task model), `MaxContextTokens`, default not-found texts sv/en, `AllowedLinkHosts` (`umea.se`, `*.umea.se`), `MessagesPerMinutePerUser` 20, chat design §5, §9.4, §10.1; the condense and gateway-headers timeouts are deployment settings `Chat:CondenseTimeout` 600 ms and `Chat:GatewayHeadersTimeout` 15 s, repo-and-deployment §3); tools: `Tools jsonb` = `AgentDefaults` (8 / 60 s / $0.05), `AgentHardCaps` (20 / 120 s / $0.25), `WriteToolsEnabled` false, `SkillGateModel`, `StaticSkillTokens` 4,000, `CallsPerMinutePerUser` 60 (tools design §3.7, §4.9, §6.5) | `Id smallint` | `CK_Settings_Singleton` (`"Id" = 1`), range checks like `CK_GatewaySettings_*` | – |
| `DataProtectionPolicies` | Plan §8a matrix, one row per level | `Classification` PK, `AllowedHistoryModes varchar(20)[]`, `DefaultHistoryMode`, `RetentionDefaultDays int`, `RetentionMaxDays int`, `EncryptionRequired bool`, `ReviewSecondApprover bool`, `ClientsWithoutHistoryOnly bool`, `ChatMasterKeyName`, `BlobMasterKeyName` (null for `public`: public blobs are not envelope-encrypted, §7) | `CK_…_Encryption` (`internal`/`sensitive` ⇒ `EncryptionRequired`); `CK_…_Retention` (default ≤ max ≤ 90) | Defines them |
| `EmbeddingProfiles` | R3 registry | `Id`, `Name`, `GatewayModel varchar(200)` (pilot: `text-embedding-3-large` from an `Eu` account; on-prem `bge-m3` once M2's hardware exists), `Dimensions int` (1024, CHECK 1–4,000), `Distance` (`cosine`), `MaxInputTokens` (8192), `CanaryText text`, `CanaryEmbedding vector` (detects a silently changed gateway model), `CanaryCheckedAt timestamptz NULL`, `CanarySimilarity real NULL` (the last on-demand check, processing §11; below 0.999 the worker refuses the profile), `IsDefault`, `State` (`active`/`building`/`retired`), `MaxResidency` (the ceiling embed and query-embed calls send; P1 README §8 O1), `RequestDimensions bool NOT NULL DEFAULT false` (true sends `"dimensions": <Dimensions>` on every embedding call with the profile, for a model such as `text-embedding-3-large` that is shortened to 1024 (the pilot profile); BGE-M3 profiles stay false and their requests omit the member) | U `Name`; partial U `IsDefault WHERE "IsDefault"` | The residency comes from `MaxResidency`: at most the platform ceiling, `Eu` until M2 (pilot: an EU-hosted model, user item U-b; P1 README §8 O1) |
| `Collections` | Search scope, access and classification | `Id`, `Slug varchar(100)`, `Name`, `DepartmentId`, `OwnerTeamId`, `Classification`, `EmbeddingProfileId` FK, `RetrievalSettings jsonb` (`vectorWeight`, `keywordWeight`, `candidates`, `maxChunksPerDocument`, `recency` (`null` = off or `{ halfLifeDays, weight, kinds }`, age decay after fusion; the `website` template sets 730 / 0.15 / `["pdf"]`; weakness review §3), `coverageMaxDistance`, `coverageMinKeywordScore`, `expandMaxTokens`; unset values fall back to `Settings.Retrieval`; top-k is per request, not here; knowledge design §12.1), `PublishedAt timestamptz NULL` (null = not yet published by its owner, invisible to retrieval except the admin test panel), `SharedWith varchar(20) NOT NULL DEFAULT 'department'` (`department` = only the owning department, `selected` = also the departments in `CollectionShares`, `municipality` = every active department; admin design §3.2), `ContactName varchar(200)`, `ContactEmail varchar(254)` (the responsible person or a functional mailbox, shown to departments that may link it), `DeletedAt` | U `Slug`; IX `DepartmentId`; `CK_Collections_SharedWith` | ≥ every source's level (validated on save) |
| `CollectionShares` | Departments a collection is shared with when `SharedWith = 'selected'` (rows are kept but unused at the other levels) | `CollectionId` FK (Cascade), `DepartmentId` FK (Restrict), `SharedBy`, `SharedAt`; no `Id`, so its outbox rows carry no `EntityId` | PK (`CollectionId`, `DepartmentId`) | – |
| `Sources` | One connector instance; belongs to exactly one collection | `Id`, `CollectionId` FK (**immutable**: chunks copy it, §2.2; moving a source = a new source), `Type` (`url-list`/`crawl`/`upload`/`sharepoint`), `Name`, `Classification`, `Settings jsonb` (fields tagged identity/scope/behaviour/cosmetic), `DemoteRules jsonb GENERATED ALWAYS AS ("Settings" -> 'demote') STORED` (crawl `demote` URL patterns with factors, read by retrieval without granting `Settings`; when several patterns match a URL the lowest factor applies, knowledge design §6.5; reasoning: weakness review §3), `Schedule varchar(100)` (cron), `PipelineId` FK, `ContainsThirdPartyText bool NOT NULL DEFAULT true` (set at approval; drives untrusted labelling, tools design §6), `State` (`active`/`paused`), `CreatedAt timestamptz NOT NULL DEFAULT now()` (the scheduler counts a new source's first run from it), `DeletedAt` | IX `CollectionId`; IX `PipelineId` | ≤ collection level |
| `Pipelines` | Parameter set for a source type's fixed pipeline (template); platform rows are the defaults | `Id`, `DepartmentId` (null = platform default), `Name`, `TemplateId varchar(50)` (one linear template per source type; stages in code, `Ume.Rag.Pipelines`), `PublishedVersionId` FK → `PipelineVersions` (nullable) | U (`DepartmentId`, `Name`) | – |
| `PipelineVersions` | Immutable once published: (template id, parameter set) as versioned JSON | `Id`, `PipelineId` FK, `Number int`, `State` (`draft`/`published`/`superseded`; the re-processing plan and the evaluation report are job results, engine design §12), `Definition jsonb` (template id, template version and parameters, `contracts/pipeline/v2/definition.schema.json`, engine design §3.1; validated against the template's parameter schema; each parameter carries effect tags, from which publish computes the re-processing plan), `CreatedBy`, `PublishedAt`, `EvaluationRunId uuid` (→ content, no FK; the newest succeeded `publish-report` for the version, linked by the admin from the job result so it outlives job retention; not audited. A version published in several collections keeps one; every collection's report stays in `EvaluationRuns`) | U (`PipelineId`, `Number`) | – |
| `Assistants` | Assistant = OpenAI model: identity, owner, audiences, retention and the version pointers; the versioned settings are in `AssistantVersions` (simplification pass §15) | `Id`, `Slug varchar(100)` (model name), `Name`, `Description varchar(500)` and `ContactName varchar(200)`, `ContactEmail varchar(254)` (the contact owner: a person or a functional mailbox; both shown where a client can display them, chat design §3.1), `DepartmentId`, `OwnerTeamId`, `Classification`, `HistoryMode`, `RetentionDays int`, `AllowedOrigins text[]`, `State` (`active`/`archived`), `PublishedVersionId uuid NULL` FK → `AssistantVersions` (the version chat serves; null = never published, not listed), `DraftVersionId uuid NULL` FK → `AssistantVersions` (the one open draft). No `Mode` column: the mode is derived (below) | U `Slug` | ≥ the published version's collections, skills and tool servers' **result level**; ≤ every reachable tool server's **clearance** (tools design §5.3); history/retention within the level's policy; checked on publish and whenever a linked object's level changes |
| `AssistantVersions` | Versioned settings: one open draft and one published version per assistant; published and superseded rows are immutable | `Id`, `AssistantId` FK (Cascade), `Number int`, `State` (`draft`/`published`/`superseded`), `GatewayModel` (validated against the gateway's `/v1/models` at the level's ceiling on save and on publish), `SystemPrompt text` (the instructions), `QuerySteps jsonb` (`{ condense: { enabled: true, model?: null }, retrieval: { topK: 10, filters, rerank: "off" (`off`/`chat`/`gateway`, absent = `off`; knowledge §6.7) }, history: { maxMessages: 10, maxTokens: 4000 }, answer: { contextTokens: 16384, maxOutputTokens: 1024, temperature?: null (unset: not sent), language: "auto", generalKnowledge: "off" }, attachments: { enabled: false } }`, `attachments` P2; chat design §5–§6, §10.3, §15.2; an assistant that uses tools (below) adds `agent: { maxToolCalls: 8, maxSeconds: 60, maxCostUsd: 0.05, maxParallelCalls: 4 }` and `answer.contextTokens` default 32,768, tools design §6.5, §6.9), `NotFound jsonb` (`sv`, `en`, `links[]{title,url}` = the contact links; empty texts fall back to `Settings.Chat`; chat design §10.1), `CollectionLinks jsonb NOT NULL DEFAULT '[]'` (`[{ collectionId, tags[] }]`: the direct collections, ≤ 20, each with ≤ 20 topic tags in the chunk tag format, processing §7; empty `tags` = the whole collection; the assistant's only knowledge, knowledge design §4.2), `Skills jsonb NOT NULL DEFAULT '[]'` (P2: `[{ skillId, excludedToolIds[] }]`, ≤ 20, always the skill's served version; tools design §5.1 layer 3), `RestoredFromVersionId uuid NULL`, `TestRunId uuid NULL` (the latest `assistant-tests` run, advisory; → `content."EvaluationRuns"`, no FK), `CreatedBy`, `CreatedAt`, `UpdatedAt`, `UpdatedBy`, `PublishedBy`, `PublishedAt` | U (`AssistantId`, `Number`); partial U (`AssistantId`) `WHERE "State" = 'draft'`; partial U (`AssistantId`) `WHERE "State" = 'published'` | links within the assistant's level (row above); collections of the assistant's own department or shared with it (`SharedWith`, `CollectionShares`; admin design §3.2) |
| `AssistantTestQuestions` | **Built in P2** (weakness review §1). The assistant's small test-question set, re-run on publish (advisory, §2.2 `EvaluationRuns` kind `assistant-tests`) | `Id`, `AssistantId` FK (Cascade), `Ordinal smallint`, `Question text` (≤ 2,000 characters), `ExpectedBehaviour text` (free text for the judge, ≤ 2,000), `ExpectedDocumentId uuid NULL` (a source that should be cited; → `content."Documents"`, no FK), `ExpectedPhrase varchar(200) NULL` (should appear in the answer), `CreatedBy`, `CreatedAt`, `UpdatedAt`, `UpdatedBy` | IX `AssistantId`; ≤ 30 per assistant (admin validation) | – (per assistant, not per version; read only by `rag-admin`) |
| `AssistantTemplates` | **Built in P2** (weakness review §1). Starting points for new assistants, maintained by the platform team (platform objects) | `Id`, `Name`, `Description`, `SystemPrompt text`, `NotFound jsonb`, `QuerySteps jsonb`, `IsActive bool`, `UpdatedAt`, `UpdatedBy` | U `Name` | – (copied into a new assistant's first draft; no link is kept; read only by `rag-admin`) |
| `AssistantAudiences` | Who may use it | `AssistantId`, `Kind` (`client`/`department`/`group`), `SubjectId uuid` (client or department id; null for `group`), `GroupValue varchar(256)` (`group` only; identity design §2.2) | U (`AssistantId`, `Kind`, `coalesce(SubjectId::text, GroupValue)`); `CK_` exactly one of `SubjectId`/`GroupValue` | P1 uses `client` only |
| `CollectionAudiences` (P2) | Which token callers may search a collection over HTTP/MCP (knowledge design §4.2) | `CollectionId`, `Kind` (`client`/`department`/`group`), `SubjectId uuid` (null for `group`), `GroupValue varchar(256)` (`group` only) | U (`CollectionId`, `Kind`, `coalesce(SubjectId::text, GroupValue)`); `CK_` exactly one of `SubjectId`/`GroupValue` (as `AssistantAudiences`) | – |
| `Skills` | Skill identity and **served pointer** (rollback = move the pointer) | `Id`, `Slug varchar(64)` (= Agent Skills `name`: ASCII `^[a-z0-9]+(-[a-z0-9]+)*$`, tools design §3.1), `Name` (display), `DepartmentId` (null = platform), `OwnerTeamId uuid NULL` (null for platform skills; P2 edit scoping, `DepartmentSettings`), `Classification`, `ServedVersionId` FK → `SkillVersions`, `CreatedBy` | U `Slug` | ≥ its tools' result levels (tools design §3.10) |
| `SkillShares` (P2) | Departments a skill is shared with | `SkillId` FK, `DepartmentId` FK, `SharedBy`, `SharedAt` | PK (`SkillId`, `DepartmentId`) | – |
| `SkillVersions` | Immutable versions; SemVer labels for display | `Id`, `SkillId` FK, `Version varchar(50)`, `VersionMajor/Minor/Patch int`, `Description`, `Instructions text`, `License varchar(200)`, `Metadata jsonb`, `Manifest jsonb` (`ume-rag.json`), `TestQuestions jsonb` (= `evals/evals.json`), `ContentHash bytea`, `State` (`draft`/`published`/`retired`), `CreatedBy`, `PublishedBy`, `PublishedAt`, `GateRunId uuid NULL` (the latest P2 skill-gate run, advisory; → `content."EvaluationRuns"`, no FK) | U (`SkillId`, `Version`); `CK_` increasing via app | – |
| `SkillVersionFiles` (P2) | Text reference and asset files of a version (no blobs, tools design §3.3) | `SkillVersionId` FK, `Path varchar(200)`, `MediaType varchar(100)`, `Content text`, `Bytes int`, `Sha256 bytea` | PK (`SkillVersionId`, `Path`) | – |
| `SkillVersionLinks` | Linked tools (a skill carries know-how and tools, never collections; simplification pass §15) | `SkillVersionId`, `ToolId` | PK both | skill level ≥ each tool's result level |
| `ToolServers` | Registered MCP servers (P2: `hosted` only) | `Id`, `Name`, `Slug varchar(20)` (model-name prefix, tools design §6.4), `Url varchar(500)`, `Hosting` (`hosted`), `AuthMode` (`client-credentials`), `AuthSettings jsonb` (e.g. `scope`; the credential is the platform tool identity's certificate, a deployment secret of `rag-api` and `rag-admin`, tools design §4.6), **`Clearance`** (highest level of conversation data it may receive), `ResultClassification` (level of what it returns), `Residency` (`OnPrem`/`Eu`/`External`), `ContainsThirdPartyText bool NOT NULL DEFAULT true` (set at approval), `TimeoutMs` (default 10,000, ≤ 30,000), `MaxResponseBytes` (default 262,144), `MaxConcurrentCalls smallint` (default 8; bulkhead per server), `OwnerDepartmentId`, `OwnerTeamId`, `SystemOwnerSubjects text[]` (admin `sub` values), `ProtocolVersion varchar(20)`, `ServerInfo jsonb`, `InstructionsSnapshot text` (admin only), `InstructionsHash bytea`, `LastDiscoveredAt`, `State` (`pending-approval`/`active`/`disabled`) | U `Name`; U `Slug`; `CK_ToolServers_Clearance` (`sensitive` ⇒ `OnPrem`; `internal` ⇒ `OnPrem`/`Eu`; `External` ⇒ `public`) | assistant level ≤ clearance and ≥ result level, so an agent run's effective level is fixed at its start (tools design §5.3) |
| `Tools` | One tool | `Id`, `ToolServerId` FK, `Name`, `Title`, `RiskLevel` (`read`/`write`; `read` only if pinned `ReadOnlyHint = true` and `DestructiveHint ≠ true` and confirmed at approval, tools design §5.5), `IsAllowed`, `RunsWithoutConfirmation`, `AllDepartments bool`, `CallsPerMinute int` (600), `CallsPerMinutePerUser int` (30), `PinnedDefinitionId` FK, `State` (`active`/`pending-approval`/`disabled-changed`/`removed`) | U (`ToolServerId`, `Name`) | `write` refused while write tools are off |
| `ToolDefinitions` | Pinned definitions; owners pre-register the expected hash before a deploy | `Id`, `ToolId` FK, `DefinitionHash bytea` (SHA-256 of canonical JSON, tools design §4.4), `Title`, `Description text`, `InputSchema jsonb`, `OutputSchema jsonb`, `Annotations jsonb`, `ObservationId bigint NULL` (→ `audit."ToolDefinitionObservations"`, no FK), `PreRegistered bool NOT NULL DEFAULT false`, `PreRegisteredBy` (an owner's expected hash before a deploy: one approver; unexpected drift needs two, tools design §4.5), `CapturedAt` | U (`ToolId`, `DefinitionHash`) | – |
| `ToolApprovals` | Two approvals per definition (tools design §4.5) | `ToolDefinitionId` FK, `ApproverKind` (`platform-admin`/`system-owner`), `Approver` (`sub`), `Comment`, `ApprovedAt` | PK (`ToolDefinitionId`, `ApproverKind`); app rule: the two `Approver`s differ | tool usable only with both rows |
| `ToolDepartmentGrants` (P2) | Departments allowed to use a tool (tools design §4.5) | `ToolId` FK, `DepartmentId` FK, `GrantedBy`, `GrantedAt` | PK (`ToolId`, `DepartmentId`) | – |
| `GatewayModels` (P1, admin design §4.10) | Gateway model catalogue synced every 5 min (tools design §6.4) | `Name varchar(200)`, `Kind` (`model`/`route`), `ModelKind` (`chat`/`embedding`/`rerank`: the gateway catalogue's `ModelKind` of the deployment; default `chat`, so manual route rows are `chat`), `Residencies varchar(10)[]` (a route alias carries its targets' residencies and is servable at a ceiling when any target is within it), `Source` (`catalog`/`manual`; route aliases are manual rows until the gateway exposes routes), `SupportsTools bool`, `SupportsToolsSource` (`catalog`/`manual`), `ContextWindow int NULL`, `InputPerMillionUsd`, `CachedInputPerMillionUsd`, `OutputPerMillionUsd`, `SyncedAt` | PK `Name` | – |
| `Clients` | Client registry (R5) | `Id`, `Name`, `Kind` (`open-webui`/`widget`/`integration`/`entra-app`/`mcp-client`/`own-client`/`teams`/`internal`), `OAuthClientId varchar(200)` (`azp`/`appid`/`client_id`; strings for Keycloak), `ClassificationCeiling`, `StoresHistory bool`, `ClientRetentionDays int`, `AllowedOrigins text[]`, `ToolPolicy varchar(10)` (`none`/`read`; P3 `confirm`; defaults per kind: `open-webui`, `own-client`, `teams`, `widget` → `read`, others `none`; tools design §5.1), `PortalUrl varchar(500) NULL` (link to the client's UI, admin design §5.4), `IsEnabled` | U `OAuthClientId` (nulls distinct); `CK_Clients_History` (`StoresHistory` ⇒ ceiling ≠ `sensitive`); `CK_Clients_Mcp` (`mcp-client` ⇒ ceiling ≠ `sensitive`) | ceiling caps every assistant it may use |
| `Keys` | Gateway-style keys | as gateway `VirtualKey`: `Id`, `ClientId` FK, `TeamId` FK → `Teams` (restrict; teams are deactivated, never deleted), `Kind` (`sk`/`pk`), `Name`, `Prefix varchar(20)` (`ume-rag-sk-AbC1`), `KeyHash varchar(64)` (hex HMAC-SHA256, pepper `Security__KeyPepper`), `EncryptedSecret`, `Scopes jsonb` (assistants/collections), `AllowedOrigins`, `AllowedIpRanges`, `RequestsPerMinute` (`int NULL`, 1–100,000: chat messages per minute when the key itself is the subject, null = `Settings.Chat.messagesPerMinutePerUser`; chat design §4.1 step 4), `IsEnabled`, `CreatedAt`, `CreatedBy`, `ExpiresAt`, `RevokedAt`, `GraceUntil`, `RotatedToKeyId` | U `KeyHash`; concurrency tokens `RotatedToKeyId`, `RevokedAt` (as gateway) | key's client ceiling applies |
| `KeyLastUse` | `LastUsedAt` split out so key use never bumps the config version | `KeyId` PK, `LastUsedAt` | – | – |
| `DataProtectionKeys` | ASP.NET Data Protection key ring | EF standard | – | – |
| `AdminSessions` | Admin BFF sessions (`PostgresAdminSessionStore`; replaces the gateway's Redis store, simplification pass §14) | `SessionId varchar(64)` (the cookie's random id), `Value bytea` (ticket, Data-Protection-encrypted with purpose `Ume.Rag.AdminApi.Session.v1`, so a writer cannot forge a session), `ExpiresAt timestamptz` (≤ 8 h per sign-in or extension, never past `Admin:MaxSessionHours` 12 h) | PK `SessionId`; IX `ExpiresAt` | – (no content; a read ignores expired rows; sign-out deletes the row) |
| `ConfigVersion`, `ConfigOutbox` | R7 (§5) | | | |

The gateway keys RAG uses (delegating keys per level, plan §6, and until gateway release 2 the pilot team's own keys
per level, weakness review §5: `Gateway__Keys__OnPrem`, `Gateway__Keys__Eu`,
`Gateway__Keys__External`; the worker holds only `OnPrem` and `Eu`) are deployment secrets, not rows
(repo-and-deployment §6.3).

**Change stamps and concurrency (admin design §4.3).** Every editable aggregate root (`Collections`, `Sources`,
`Pipelines`, `Assistants`, `Clients`, `Skills`, `ToolServers`, `EmbeddingProfiles`, `Settings`,
`DataProtectionPolicies`, `AssistantTemplates`) has `UpdatedAt timestamptz` and `UpdatedBy varchar(200)` (the admin's audit actor: Keycloak `sub` in P1, `oid` from P2; identity design §1.1),
set on every save of the root or a child row, and maps Postgres `xmin` as its EF concurrency token (DTO field
`version`; mismatch → 409). An assistant's draft is the exception: a draft save writes only its `AssistantVersions`
row, which carries its own stamps and `xmin` token, so editing a draft never touches `Assistants` (§5).

**Assistant versions** (simplification pass §15). Edits go to the draft (created on the first edit as a copy of the
published version, `Number` + 1); the admin test panel runs the draft. **Publish** is one config transaction: the
V-rules on the draft, draft → `published`, the previous published version → `superseded`, `PublishedVersionId` = the
draft, `DraftVersionId` = null; then (P2) `rag-admin` starts the `assistant-tests` run (§2.2). **Restore** copies a
superseded version into a new version (`RestoredFromVersionId`) and publishes it the same way; an open draft stays.
The diff (P2, weakness review §1) compares two versions' columns. Links are id arrays in the version, so a version is a self-contained
snapshot: `ConfigRules` refuse deleting a collection or skill that a draft or published version links, and a restore
whose links no longer pass the V-rules is refused with the rule's code. Draft, publish, history and restore are P1;
the cross-rules apply only to objects P1 has (skills, tool servers and shares join them in P2). **Copy** (P2) creates a new assistant whose first
draft copies the source's published (or draft) version, without audiences; a **template** (P2) fills a first draft the same
way; in P1 the first draft starts from `Settings.Chat` defaults. The data plane reads only published versions, through the view `config."PublishedAssistantVersions"`
(`AssistantVersions` joined on `Assistants.PublishedVersionId`; owner migrator; the api is granted the view, not the
table, and the admin reads the view for explain access, §1.2). **Mode** is not stored: an assistant runs the agent loop (P2) iff its published version links a skill
whose served version has a tool not in that link's `excludedToolIds`, and the fixed RAG path otherwise; the config
snapshot computes it (chat design §2.2) and the admin shows it as "Använder verktyg". Serving a skill version that
adds the first tool therefore switches linking assistants to the agent loop, which is why skill serve re-checks the
agent rules for every linking assistant (admin design V14, V18).

**Organisation mirror.** The admin API syncs `Departments`/`Teams` from the gateway Data API (`catalog.read`,
`GET /v1/catalog/departments`, `/teams`; `docs/data-access.md`) every 5 minutes and on demand; only changed rows are
written, so a sync without changes does not bump the config version. Member groups need a catalogue field (§12.9),
delivered with gateway release 2 in P2 (weakness review §5); until then `MemberGroups` stays empty.

**Schema details of the built `config` tables** (where they are narrower or more specific than the table above):
- **`Settings`**: scalars are columns and groups (`WorkerConcurrency`, `DefaultSchedules`, `Crawl`, `PiiFlagRules`,
  `Retrieval`, `Chat`) are `jsonb`. The deletion guard is `DeletionGuardPercent smallint` (1–100, admin V30).
  `TrustedProxies text[]` is a column too (identity design). There are no DB defaults and no seeded row: no row means
  the defaults in code, as in the gateway. `ChatShreddingEnabled` (A1) and `Tools` (P2) are not created yet; every
  other value above, the P1b ones (`PreviewMaxUsd`, the evaluation models) included, is a column.
- **`DataProtectionPolicies`**: no seeded rows; the CHECK allows history mode `none` only (§7); `ChatMasterKeyName` is
  not created (A1). Its outbox rows have `EntityId` null, because the key is `Classification`, not `Id`.
- **`EmbeddingProfiles`**: the development seeder's profile is `dev-embedding`, model `bge-m3` (FakeGateway), 1024
  dimensions, `MaxResidency = Eu` (§10). The migrator seeds the platform profile from the deployment knobs
  `EmbeddingProfile:*` (`Name`, `GatewayModel`, `Dimensions`, `MaxInputTokens`, `MaxResidency`, `RequestDimensions`
  (default false)) insert-if-absent with `CanaryText` and its HNSW index, and never changes an existing profile (a
  differing `RequestDimensions` is logged, not applied); the admin API records `CanaryEmbedding` (processing §11).
- **`Sources`**: `Type` has no `sharepoint` (P2).
- **`Pipelines`**: U (`DepartmentId`, `Name`) is nulls-not-distinct, so platform defaults are unique too. There is no
  FK on `Pipelines.DepartmentId`, `Collections.DepartmentId` or `Collections.OwnerTeamId`. **Platform pipelines** are
  seeded from the built-in templates by the migration service (engine design §13.1): one row per template name, one
  published version per template version.
- **Raw SQL, not mapped in EF**: the `vector` and `tsvector` columns (`Chunks.Embedding`, `Chunks.SearchVector`,
  `EmbeddingCache.Embedding`, `EmbeddingProfiles.CanaryEmbedding`), like `ConfigVersion.LastTransactionId`. So
  `EmbeddingCache` is SQL-only, and code that writes vectors registers the Pgvector plugin or uses SQL.
- **`Assistants`**: `RetentionDays` 1–90 (the policy maximum) and `HistoryMode` `none` only, as
  `DataProtectionPolicies`; a migration widens both when A1 allows. `PublishedVersionId` and
  `DraftVersionId` are composite FKs (`Id`, `AssistantId`) → `AssistantVersions` with NO ACTION, so a pointer names
  only a version of its own assistant; deleting an assistant still cascades to its versions. The pointers form a
  cycle, so a save writes the assistant, then the version, then the pointer; a publish that supersedes needs two
  saves in one transaction because of the partial unique index.
- **`AssistantVersions`**: `GatewayModel` is nullable for drafts and required (with `PublishedAt`/`PublishedBy`)
  once published; `Number ≥ 1`; at most 20 `CollectionLinks` entries (`jsonb_array_length`), checked in the database. `Skills` and `TestRunId` (P2)
  are not created. Published and superseded rows are immutable by application rule; no trigger enforces it.
- **`PublishedAssistantVersions`**: also filters `State = 'published'` and is a `security_barrier` view. The admin
  has `SELECT` on it (§1.2): explain access (identity §4.9) decides exactly as the data plane does, from the view that
  defines a published version; the admin already reads every `AssistantVersions` row, so the grant shows it nothing
  new.
- **`AssistantAudiences`**: has an `Id` PK. The designed key contains `coalesce(...)`, so it is a unique expression
  index, not a PK; the outbox `EntityId` is the audience id. The CHECK allows `client` only (P1).
- **`GatewayModels`**: no change stamps or `xmin` (not an editable root) and no "disabled" column (admin design
  §4.10); outbox `EntityId` null (the key is `Name`). The 5-minute sync writes only rows that changed (incl.
  `SyncedAt`), so a sync without changes does not bump the config version; a catalogue row whose deployment is
  disabled or no longer served is deleted; manual rows are never touched.
- `AssistantTestQuestions` and `AssistantTemplates` (P2) are not created.

### 2.2 `content` (owner: worker)

| Table | Purpose | Key columns | Keys and indexes |
|---|---|---|---|
| `WorkerInstances` | Running workers (heartbeat 10 s) | `InstanceId uuid`, `BuildVersion`, `StartedAt`, `LastHeartbeatAt`, `Classes varchar(20)[]` (job classes it runs, engine design §5.2) | PK |
| `NodeManifest` | Registered stage types and their parameter schemas (template forms in the admin, engine design §3.2) | `NodeType varchar(100)`, `TypeVersion int`, `ParamsSchema jsonb`, `Metadata jsonb` (job class, cache class, effect tags, display names, deprecated), `FirstRegisteredAt` | PK (`NodeType`, `TypeVersion`); schema immutable per pair |
| `WorkerNodeSupport` | Which live instance supports which stage version; publish refused unless a row exists with heartbeat < 60 s | `InstanceId` FK (Cascade), `NodeType`, `TypeVersion` | PK all three |
| `Jobs` | Queue, unpartitioned (§4) | see §4 | |
| `CrawlHosts` | Shared politeness budget per host | `Host varchar(253)` PK, `RequestsPerSecond numeric(5,2)`, `MaxConnections`, `NextAllowedAt`, `RobotsBlobPath`, `RobotsFetchedAt`, `Kind` (`municipal`/`external`), `RobotsStatus`, `CrawlDelaySeconds`, `BackoffFactor numeric(6,2)`, `PausedUntil`, `LatencyEwmaMs`, `BlockedAt` (connectors design §6) | `fillfactor = 50` (HOT updates of `NextAllowedAt`), aggressive autovacuum (`autovacuum_vacuum_scale_factor = 0.02`, `autovacuum_vacuum_cost_limit = 2000`; reasoning: [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.2) |
| `LeaderLeases` (P2) | Lease leaders for singletons once several worker instances run maintenance (partition maintenance, content retention, drift sweep; lease 30 s, renewed every 10 s; [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.5) | `Name varchar(50)` PK, `Owner uuid`, `Until timestamptz NOT NULL`, `Fence bigint NOT NULL DEFAULT 0` (writes carry it), `Checkpoint jsonb` | worker S I U |
| `SiteProfiles` | Site profile per (level, host) (P1: `manual` versions only, umea.se a hand-checked Sitevision preset; learning and `LastLearnedAt`/`NextLearnAfter`, `proposed` versions, `LearnedFromRunId` and `Sample` used from P2, weakness review §1), shared by every source crawling the host (simplification pass §13) | `Id`, `Classification`, `Host varchar(253)`, `PresetId varchar(50) NULL` (built-in CMS preset, e.g. `sitevision`), `AppliedVersionId uuid NULL` (→ `SiteProfileVersions`), `LastLearnedAt`, `NextLearnAfter` (re-learning at most weekly), `UpdatedAt` | U (`Classification`, `Host`) |
| `SiteProfileVersions` | Immutable profile versions; apply and roll back move `AppliedVersionId` | `Id`, `SiteProfileId` FK, `Number int`, `Profile jsonb` (CMS fingerprint, main-content root, boilerplate selectors and phrases, breadcrumb selector, date pattern, verified identity rule, URL normalisation, excluded URL types, change signal, JavaScript needed, link-list blocks kept for discovery), `Origin` (`learned`/`manual`/`preset`), `State` (`proposed`/`applied`/`superseded`/`rejected`), `LearnedFromRunId uuid NULL`, `Sample jsonb` (sampled URLs and sections, count, check result: largest main-text loss %, empty pages, boilerplate share before/after), `CreatedBy` (`system:worker` or admin `oid`), `CreatedAt`, `AppliedAt`, `AppliedBy` | U (`SiteProfileId`, `Number`); IX (`State`) `WHERE "State" = 'proposed'` |
| `CrawlFrontier` | Crawl frontier. **P1: one frontier per source**, one row per (level, source, URL) with `SourceId`, `RunId`, `Depth smallint`, `Leaf bool` and `RemovalCheck bool NOT NULL DEFAULT false` (the row is a removal check; connectors design §6.0). **Later (P2):** shared between sources, one row per (level, URL), with the per-source fields moved to `CrawlClaims` (reasoning: weakness review §1) | `Classification`, `UrlHash bytea`, `Url text`, `Host`, `State` (`idle`/`pending`/`released`), `NextFetchAt`, `LastFetchedAt`, `LastStatus smallint`, `RedirectTo`, `ETag`, `LastModified`, `ContentHash`, `SitemapLastMod` (the last response's validators and sitemap `lastmod` per URL, since one request serves every source; connectors design §6.1; `SitemapLastMod` is cleared when the URL leaves the sitemap, which triggers the removal check, weakness review §3) | P1: PK (`Classification`, `SourceId`, `UrlHash`); P2: PK (`Classification`, `UrlHash`); IX (`Host`, `NextFetchAt`) `WHERE "State"='pending'` |
| `CrawlClaims` | **Built in P2** (weakness review §1). One claim per (URL, source): one fetch serves all claims | PK (`Classification`, `UrlHash`, `SourceId`), `RunId`, `Depth smallint`, `Leaf bool`, `ClaimedAt` | IX (`RunId`) |
| `CrawlRuns` | Per crawl run limits and report data | `RunId` PK, `Classification`, `SourceId`, `Admitted int`, `MaxPages`, `MaxDepth`, `LimitHit`, `Seeds int`, `SeedsFailed` (the ratio of weakness review §3), `PausedHosts jsonb`, `TrapStats jsonb`, `Incremental bool NOT NULL DEFAULT false` (a `lastmod` incremental run), `Complete bool` (≥ 99 % of seeds resolved (fetched, `304`, skipped by `lastmod` or `Gone`), no host paused, no pending admitted URL, page limit not hit; connectors design §5.4, reasoning: weakness review §3; an alert fires after N runs without a complete traversal, worker design) | `fillfactor = 50` (HOT updates of `Admitted`), aggressive autovacuum (as `CrawlHosts`) |
| `ConnectorCheckpoints`, `Uploads`, `DeletionProposals`, `QuarantinedFiles` | Connector checkpoints; uploads, **one row per upload slot** (`Id` = the slot, used in the document identity); deletion proposals per run; quarantined files (30 days, `Origin` `upload`/`crawl`) | per connectors design §2.3, §9.1, §13.1, §11 | worker S I U D, admin S |
| `Documents` | One row per (source, identity) | `Id`, `SourceId` (no FK), `Classification` (the source's), `IdentityKind` (`url`/`sharepoint`/`upload`), `CanonicalIdentity text` (url: the final URL, or the site profile's verified identity rule; upload: `upload:<uploadId>`, the upload slot), `IdentityHash bytea` (SHA-256), `ContentHash bytea` (the version; uploads: replacing the file keeps the document id), `ContentHashVersion smallint NOT NULL` (noise rules version; a label only, the version is part of the HTML hash input, connectors design §12.1), `Title`, `Url`, `MediaType`, `Language`, `DisplayName varchar(300) NULL` (upload file name; never in `Title`), `Description text NULL`, `Keywords text[] NULL` (page metadata, processing design §7), `SourceModifiedAt`, `HttpLastModified timestamptz NULL` and `SitemapLastMod timestamptz NULL` (the last 200 response's `Last-Modified` and the URL's sitemap `lastmod` as parsed dates, chunk date candidates for `parse` and reprocess jobs that don't fetch, processing design §7; not validators, which stay on `CrawlFrontier`), `FetchedAt`, `CanonicalHint text NULL`, `SiteProfileVersionId uuid NULL` (the site profile version that extracted it), `FetchMode text NOT NULL DEFAULT 'http'` (`http`/`browser`: how its current content was fetched, connectors design §8.1), `ParseStrategy`, `ParseProfileId`, `ParseOutcome`, `ParseCacheKey bytea` (→ `NodeOutputs`), `PageCount`, `PipelineVersionId`, `LiveChunkSetId uuid NULL`, `CurrentExecutionId uuid NULL` (fence, engine design §7.5), `LastRunId uuid`, `PiiClearedAt`, `PiiClearedBy`, `PiiClearedCounts jsonb NULL` (the clearance is a person's decision on the document, by a department admin or platform admin, admin design §3.3; flags and counts live on `ChunkSets`; clearing releases the document's `held` set, and a later set whose counts do not exceed these is covered; processing design §9.5), `HeldAt`, `HeldBy` (rejected deletions), `ConsecutiveFailures smallint NOT NULL DEFAULT 0`, `FirstSeenAt`, `LastSeenAt`, `DeletedAt`, `PurgeAfter` | PK `Id`; **U (`SourceId`, `IdentityKind`, `IdentityHash`)**; IX `PurgeAfter WHERE "DeletedAt" IS NOT NULL`; IX (`SourceId`) `WHERE "DeletedAt" IS NULL` |
| `NodeOutputs` | Stage output cache index, content-addressed and shared by every document with the same input (blobs hold the data, §9) | `CacheKey bytea` PK (§9), `Classification`, `NodeType`, `NodeVersion`, `ParamsHash bytea`, `InputHash bytea`, `OutputHash bytea` (next key without loading the blob, engine design §8), `BlobPath` (a prefix for multi-blob outputs such as the parse output: `result.json`, `docling/*.json.gz`), `IsParseOutput bool`, `CreatedAt`, `LastUsedAt` | IX `LastUsedAt` |
| `ChunkSets` | One chunk-stage output per (document, pipeline version, site-profile version, embedding profile, content version) | `Id`, `DocumentId` FK, `Classification`, `CollectionId`, `PipelineVersionId`, `SiteProfileVersionId uuid NULL`, `EmbeddingProfileId`, `ContentHash`, `InputHash bytea NOT NULL` (the personal-data stage's input, i.e. the chunk output's `OutputHash`; noise-only changes re-key the set, processing design §12), `State` (`writing`/`live`/`held`/`superseded`; only a `live` set's chunks have `DeletedAt` null), `PiiFlagged bool`, `PiiCounts jsonb` (from this set's personal-data stage, preview and re-processing included), `ChunkCount`, `StageKeys jsonb NULL` (the keys and `OutputHash`es of the stage outputs the set was built from, engine design §3.3), `ParseRuleKey varchar(300) NULL` (the key's one salt: NULL \| `rule:{candidates}` \| `force:{executionId}` \| `force:{executionId};rule:{candidates}`; the admin parse rules the parse took and a forced reprocess, engine design §14), `PiiFlaggedAt timestamptz NULL` (the first transition to flagged, kept on a re-flag; processing design §9.5), `CreatedAt`, `LiveAt`, `SupersededAt` (purged 14 days later; one-click rollback until then) | U (`DocumentId`, `PipelineVersionId`, `SiteProfileVersionId`, `EmbeddingProfileId`, `ContentHash`, `ParseRuleKey`) (`IX_ChunkSets_Key`, nulls not distinct); IX (`State`, `CreatedAt`) `WHERE "State" = 'writing'` (orphan sweep); IX `SupersededAt` |
| `Chunks` | **Collection index**: flat chunks (`Kind = child`) in P1; parents only if parent-child is adopted (processing design §5.3) | `Id`, `ChunkSetId`, `DocumentId`, `CollectionId uuid NOT NULL` (the source's collection), `Classification`, `Kind` (`parent`/`child`), `ParentId`, `Ordinal int`, `HeadingPath text`, `PageEnd int NULL`, `Text text`, `SearchText text` (text plus the compound parts from `SwedishDecompounder`, §3.1), `OverlapChars smallint NOT NULL DEFAULT 0` (leading characters of `Text` repeated from the previous chunk; section expansion strips them, knowledge design §9.3), `TextHash bytea`, `TokenCount int`, `Page int`, `Tags text[]`, `DocumentType varchar(30)`, `ContentKind varchar(16) NOT NULL DEFAULT 'text'` (`text`/`table`/`list`, CHECK `CK_Chunks_ContentKind`) and `LargeTable bool NOT NULL DEFAULT false` (true on the second and later row groups of a table over 2,000 rows; both read by `generate-set`, processing design §13.1), `SourceModifiedAt`, `EmbeddingProfileId uuid` (children only), `Embedding vector` (children only, untyped so profiles may differ in size), `SearchVector tsvector` (generated, §3.2), `DeletedAt` (a chunk is searchable iff null: set while its set is `writing`, `held` or `superseded`, and on soft delete) | PK `Id`; FK `ChunkSetId` → `ChunkSets` Cascade; FK `DocumentId` → `Documents`; **U (`ChunkSetId`, `Ordinal`)** `IX_Chunks_ChunkSetId_Ordinal`; B-tree `IX_Chunks_CollectionId`; IX `ParentId`; HNSW and GIN per §3.2. `Kind`/`ParentId` stay although every P1 row is `child`; dropped if P2 does not adopt parent windows (processing design §5.3) |
| `EmbeddingCache` | (profile, text hash) → embedding, so re-chunking and every per-source copy after the first embed only new text | `Classification`, `EmbeddingProfileId`, `TextHash bytea`, `Embedding vector`, `LastUsedAt` | PK (`Classification`, `EmbeddingProfileId`, `TextHash`). P2, behind its trigger (cache > 20 % of the database): look up `Chunks` first via `IX_Chunks_Profile_TextHash` on `Chunks` (`EmbeddingProfileId`, `TextHash`) `WHERE "Kind" = 'child'`, cache only texts not in a live set, cache vectors as `halfvec` ([`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.2) |
| `Runs` | One per sync/reprocess | `Id`, `SourceId`, `Classification`, `Kind` (`sync`/`retry-failed`/`reprocess`), `Trigger` (`schedule`/`manual`/`upload`/`publish`/`rollback`/`site-profile` (engine design §10.1)/`retry`/`config-change`; P2 `notification`), `PipelineVersionId`, `ScheduledFor timestamptz NULL`, `Phase varchar(20)` (`list`/`fetch`/`reconcile`/`done`), `Report jsonb`, `SettingsHashes jsonb`, `State` (`running`/`succeeded`/`degraded`/`failed`/`awaiting-deletion-confirmation`/`cancelled`), `Added/Updated/Unchanged/Deleted/Failed int`, `ProposedDeletions int`, `CostUsd numeric(18,8)`, `StartedAt`, `FinishedAt` | IX (`SourceId`, `StartedAt` DESC) |
| `RunItems` | One per document per run, partitioned by day (§4.4) | `Id bigint identity`, `CreatedAt` (partition key), `RunId`, `Classification`, `DocumentId`, `Identity text`, `Outcome` (engine design §10.2), `Reason varchar(500)`, `Strategy`, `PipelineVersionId uuid`, `ParseProfileId`, `ExecutionId uuid`, `DurationMs`, `CostUsd`, `ParserBuildHash bytea` (parser `/v1/info` build; full info in `content."ParserBuilds"` (`BuildHash` PK, `Info jsonb`, `FirstSeenAt`)) | PK (`CreatedAt`, `Id`); IX (`RunId`) |
| `ParseObservations` | Parse quality observations (input for P2 profile proposals) | `Id`, `Classification`, `DocumentId`, `Domain`, `PathPattern`, `DocumentType`, `Strategy`, `Score numeric(5,4)`, `Metrics jsonb`, `ObservedAt` | IX (`Domain`, `PathPattern`, `DocumentType`) |
| `ParseProfiles` | Parse rules: built-in and admin-made; from P2 learned proposals that need platform-admin approval | `Id`, `Classification`, `DepartmentId` (scope), `Match jsonb`, `Strategy jsonb`, `CleanupRules jsonb`, `Origin` (`builtin`/`manual`; P2 `learned`), `State` (`active`/`disabled`; P2 `proposed`), P2: `SampleCount`, `ApprovedBy`, `ApprovedAt` | IX (`State`) |
| `EvaluationSets` | One per collection | `Id`, `CollectionId` (no FK), `Classification`, `Round int NOT NULL DEFAULT 1` (the round the next generation uses; processing design §13.1), `CreatedAt` | U `CollectionId` |
| `EvaluationQuestions` | Layers generated (judge-filtered) / human / feedback / skill; a `human` question with empty `ExpectedDocumentIds` expects "not found" (pilot gold set, weakness review §2) | `Id`, `SetId`, `Classification`, `Layer`, `Question text`, `ExpectedAnswer text`, `ExpectedDocumentIds uuid[]`, `SourceContentHash bytea`, `Origin jsonb`, `State` (`active`/`stale`/`retired`), `SourceChunkId`, `SkillVersionId`, `CreatedBy`, `RetiredAt` | FK `SetId` → `EvaluationSets` Cascade; IX (`SetId`) `WHERE "RetiredAt" IS NULL` |
| `EvaluationRuns` | One advisory run over the full collection, or over an assistant's test questions; no decision or override (the report advises, the owner keeps or rolls back) | `Id`, `SetId uuid NULL` (null for `assistant-tests`), `Classification`, `Kind` (`publish-report`/`nightly`/`on-demand` = retrieval runs by the worker; `answers` = answer-level judge, nightly from P2 (weakness review §1), by `rag-admin`; `assistant-tests` (P2) = an assistant's test questions after each publish and on demand, by `rag-admin`, `SubjectId` = assistant, `SubjectVersion` = version number; P2 `skill-gate`, by `rag-admin`), `SubjectId`, `SubjectVersion`, `ComparedRunId`, `QuestionCount`, `K int`, `Found int`, `Wins int`, `Losses int` (per-question changes against the compared run), `RecallAtK numeric(5,4)`, `Mrr numeric(5,4)`, `Faithfulness numeric(5,4)`, `CitationPrecision numeric(5,4)` (`answers` runs), `Reranked bool`, `Variant` (retrieval runs: `hybrid` default, `vector`, `hybrid-no-decompounder`; the P1a three-way gold-set run, weakness review §2; the job's `hybrid-rerank` and `hybrid-rerank-gateway` variants are stored as `hybrid` with `Reranked = true`), `PromptVersion`, `Seed` (processing design §14), `Candidate bool NOT NULL DEFAULT false` and `ThresholdProposal jsonb NULL` (`on-demand` runs: the pass under candidate settings and the coverage threshold proposal, processing design §13.6), `StartedAt`, `FinishedAt` | FK `SetId` Cascade; IX (`SetId`, `StartedAt` DESC); IX (`SubjectId`, `StartedAt` DESC) `WHERE "Kind" = 'assistant-tests'` |
| `EvaluationResults` | Per question per run | `RunId`, `QuestionId` (`assistant-tests`: → `config."AssistantTestQuestions"`, no FK), `Classification`, `ExpectedRank int` (null = not found), `Hit bool` (`assistant-tests`: expected source cited and phrase present, where set), `Coverage` (`ok`/`low`), `BestDistance real`, `BestKeywordScore real` (retrieval runs; for coverage calibration from answerable and unanswerable `human` questions, weakness review §2), `Faithfulness numeric(5,4)`, `CitationPrecision numeric(5,4)`; `assistant-tests` only: `Verdict varchar(10)` (`pass`/`fail`/`unsure`, the judge against `ExpectedBehaviour`), `Question text` (as asked, so later edits do not rewrite history), `Answer text` (generated from collection content, the assistant's level), `JudgeNote varchar(1000)` | PK (`RunId`, `QuestionId`); FK `RunId` Cascade |
| `RetrievalStats` | Counters only, never query text (knowledge design §4.7) | `Day date`, `DocumentId`, `TextHash bytea`, `CollectionId`, `Classification`, `Retrieved int`, `Cited int` (keyed by text hash, not chunk id, so counts survive re-chunking, processing design §6.5) | PK (`Day`, `DocumentId`, `TextHash`, `CollectionId`) |

**Writing a chunk set** (short transactions, simplification pass §9; engine design §14). A chunk is searchable iff
`Chunks."DeletedAt" IS NULL`. The index writer inserts the `ChunkSets` row as `writing`, then the chunks **with
`DeletedAt` set** (invisible to K and V) by `INSERT … SELECT FROM unnest(…)` with a text-to-`vector` cast (engine
design §14) in batches of ≤ 1,000 rows, each batch its own transaction (well inside the worker's 60 s
`statement_timeout` and idle-in-transaction limit);
then **one short swap transaction per document**: fence (`Documents.CurrentExecutionId`), `Documents.LiveChunkSetId`
= new set, `DeletedAt` cleared on the new set's chunks and set on the previous live set's chunks (tens to hundreds of
rows), new set → `live`, previous set → `superseded`. Superseded sets are kept 14 days; rollback is the same swap back.
A `public`-collection source whose new set is `PiiFlagged` and not covered by the document's clearance gets the set as
`held` (its chunks stay deleted): the document leaves the collection until a holder of the `Manage` right in the document's department (department
admin) or a platform admin clears the document with a reason of ≥ 10 characters (`command` `clear-pii-flag`, admin
design §3.3, V26; processing design §9.5), which releases the set by the same swap. An orphan sweep deletes sets left
`writing` for more than 24 hours (engine design §14). Hydration may still re-check `LiveChunkSetId` as a safety net
(§3.3), but no candidates are lost to non-live sets.

**Site profiles** live in `content` because the worker learns and applies them; platform-admin decisions are `command` jobs
(`approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile`; §12.2), audited by the admin when enqueued and by the worker
(`system:worker`) when it applies a learned version automatically (P2; in P1 every version is `manual`, weakness review §1).

**Schema details of the built `content` tables** (where they are narrower or more specific than the table above):
- **CHECKs hold P1 values only**: no `sharepoint` in `Documents.IdentityKind`, no `notification` in `Runs.Trigger`;
  `SiteProfileVersions.Origin` `manual`/`preset` and `State` `applied`/`superseded`; `ParseProfiles.Origin`
  `builtin`/`manual` and `State` `active`/`disabled`; P2 widens them with its features. `Runs.State` is `varchar(40)`
  (`awaiting-deletion-confirmation` is 30 characters); the `Runs` counters (`Added` … `ProposedDeletions`) and
  `CostUsd` default to 0, so a writer may leave them out.
- **Keys**: `Chunks.DocumentId` cascades, so a purge removes sets and chunks (§8); `SiteProfileVersions` →
  `SiteProfiles` cascades; `SiteProfiles.AppliedVersionId` has no FK (it would be a cycle). `ConnectorCheckpoints` has
  PK (`SourceId`, `Kind`). `CrawlFrontier.ETag`/`LastModified` are strings, as received (connectors design §4).
  `DeletionProposals.Decision` has no CHECK (no value set is designed).
- **Evaluation tables, P2 columns not created**: `EvaluationQuestions.SkillVersionId` and the layers
  `feedback`/`skill`; `EvaluationRuns` `Faithfulness`/`CitationPrecision`, the kinds
  `answers`/`assistant-tests`/`skill-gate` and the (`SubjectId`, `StartedAt`) index; `EvaluationResults`
  `Faithfulness`/`CitationPrecision`/`Verdict`/`Question`/`Answer`/`JudgeNote`.
- **`EvaluationRuns`**: `SetId` is NOT NULL (only P2 `assistant-tests` runs have no set); counts and metrics are
  nullable until the run finishes; `Seed` is `bytea` (SHA-256, processing design §13); strata are not a column.
  `QuestionCount` counts answerable questions (the recall denominator).
- **`EvaluationResults`**: `Hit` is nullable and set only for answerable questions (null for unanswerable ones);
  CHECKs on `Coverage` and `ExpectedRank ≥ 1`; no FK on `QuestionId`, because P2 results point elsewhere.
- **`rag_count_retrievals`** (§12 row 3): checks its input (arrays of equal length, counts ≥ 0, known levels), caps a
  call at 10,000 keys, folds duplicate keys (a key reported at two levels in one call keeps the higher) and sorts them
  before the upsert; on conflict with a stored row it keeps the higher of the stored and the new level; it returns the
  number of rows written (`int`); `REVOKE ALL FROM PUBLIC`, execute for the api only.

### 2.3 `chat` (owner: Chat API)

| Table | Purpose | Key columns (ciphertext in **bold**) | Keys and indexes |
|---|---|---|---|
| `Conversations` | One conversation | `Id`, `Classification` (effective level from the first message), `AssistantId`, `ClientId`, `SubjectKind` (`entra-user`/`owui-user`/`anonymous`/`application`), `SubjectId varchar(200)` (the provider's user id: Keycloak `sub` in P1, Entra `oid` from P2; for `owui-user` the Open WebUI user id from its JWT `sub`, identity design §5.1, §5.4; or HMAC of the widget session) (identity design §1.1, §11.2, 2026-10-31), `ExternalChatId varchar(200)` (e.g. `X-OpenWebUI-Chat-Id`), `HistoryMode`, `RetentionDays`, **`TitleCiphertext bytea`** (re-encrypted under the newest message's bucket), `TitleDataKeyId`, `CreatedAt`, `LastMessageAt`, `ExpiresAt` (= expiry of its newest message); title = the first question (≤ 120 characters) | PK `Id`; U (`ClientId`, `SubjectId`, `ExternalChatId`, `AssistantId`) (a model switch in one Open WebUI chat = new conversation, chat design §11.1); IX (`SubjectId`, `LastMessageAt` DESC); IX `ExpiresAt` |
| `DataKeys` | One wrapped data key per (level, expiry month) **bucket** (§7) | `Id`, `Classification`, `ExpiryMonth date` (first day of the month), `WrappedKey bytea`, `KeyProvider`, `MasterKeyName varchar(100)` (`rag-chat-<level>`, or a shred key once shredding is on, §7), `MasterKeyVersion varchar(100)`, `CreatedAt` | PK `Id`; U (`Classification`, `ExpiryMonth`) |
| `Messages` | User/assistant turns | `Id`, `ConversationId`, `Classification`, `Seq int`, `Role`, **`ContentCiphertext bytea`** (null in mode `none`), `DataKeyId` (bucket of `ExpiresAt`), `ExpiresAt` (`CreatedAt` + retention), `ExternalMessageId varchar(100) NULL` (Open WebUI message id, chat design §11.4), `ParentMessageId uuid NULL`, `AssistantVersionId uuid` (the published version that answered, no FK; feedback and review show its number), `GatewayModel`, `GatewayRequestId`, `ObservedResidency`, `InputTokens`, `OutputTokens`, `CostUsd numeric(18,8)`, `LatencyMs`, `TimeToFirstTokenMs` (token, cost and TTFT columns nullable), `Coverage varchar(10)` (`ok`/`low`/`unknown`/`skipped`; `skipped` = conversational message, chat design §6.2), `Degraded varchar(40)`, `Language char(2)`, `CondenseUsed bool`, `Outcome` (`answered`/`answered-general` (coverage `low`, answered from general knowledge, chat design §10.4)/`not-found`/`unavailable`/`refused`/`error`/`cancelled`), `CreatedAt` | PK; U (`ConversationId`, `Seq`); U (`ConversationId`, `ExternalMessageId`) `WHERE "ExternalMessageId" IS NOT NULL` (regeneration dedup); FK `ConversationId` Cascade; FK `DataKeyId` → `DataKeys` Restrict; IX `ExpiresAt` |
| `MessageSources` | Citations with a snapshot of the cited text | `MessageId`, `Rank int`, `Classification`, `DocumentId`, `ChunkId`, `CollectionId` (ids only, no FK), **`SnippetCiphertext`**, `DataKeyId`, `Number smallint NULL` (display number, null if not cited; chat design §9.2), `Page`, `PageEnd`, `Used bool` (= cited) | PK (`MessageId`, `Rank`); FK `MessageId` Cascade |
| `Feedback` | Thumbs + comment | `Id`, `MessageId`, `ConversationId`, `Classification`, `ClientId`, `Source varchar(20)` (`api`/`owui-import`), `ExternalId varchar(100) NULL` (Open WebUI feedback id), `Rating smallint` (−1/1), `Score smallint NULL` (1–10), `ReasonCategory varchar(40) NULL` (plaintext: one of the fixed Open WebUI reason keys or `other`, never free text; admin design §8.1), **`CommentCiphertext`** (`{reason, comment}`, under its message's bucket), `DataKeyId`, `ReviewState`, `CreatedAt`, `UpdatedAt` | FK `MessageId` Cascade; U (`MessageId`, `Source`) `WHERE "Source" = 'api'`; U (`Source`, `ClientId`, `ExternalId`); IX (`ReviewState`) `WHERE "ReviewState" = 'open'` (chat design §14) |
| `ToolCalls` | Full tool arguments and results (tools §3: only here, under conversation retention) | `Id`, `MessageId`, `Classification`, `CallIndex smallint`, `ToolServerId` (null for built-ins), `ToolName` (model name, or `builtin:<name>`), `ToolId`, `DefinitionId`, `SkillVersionId`, **`ArgumentsCiphertext`**, **`ResultCiphertext`** (the normalised text the model saw), `DataKeyId`, `ArgumentsHmac bytea`, `ArgumentsBytes`, `ResultBytes`, `DurationMs`, `Outcome`, `CreatedAt` (tools design §6.11) | FK `MessageId` Cascade |
| `AssistantDailyTotals` | Statistics that outlive conversations (§8) | `Day date` (Europe/Stockholm), `AssistantId`, `ClientId`, `DepartmentId`, `Classification`, `Conversations`, `Messages`, `NotFound`, `GeneralAnswers`, `Unavailable`, `Degraded`, `Errors`, `Cancelled`, `UncitedAnswers`, `ThumbsUp`, `ThumbsDown`, `ToolCalls`, `ToolErrors`, `AgentLimitHits` (tools design §6.11), `InputTokens bigint`, `OutputTokens bigint`, `CostUsd numeric(18,8)` | PK (`Day`, `AssistantId`, `ClientId`, `Classification`) |
| `FeedbackImportState` | Watermark of the Open WebUI feedback import over its HTTP API (chat design §14.2); no `Classification` | `ClientId` (config id, no FK), `WatermarkUpdatedAt bigint`, `LastRunAt`, `LastOutcome`, `Unmapped int`, `FailedRuns int` (failed runs in a row, for the alert after 4, chat design §14.3; #487) | PK `ClientId` |
| `Attachments` (P2) | Chat-scoped attachment, conversation retention (chat design §15.2) | `Id`, `ConversationId`, `Classification`, `DataKeyId`, **`FileNameCiphertext`**, `MediaType`, `Bytes`, `Sha256`, `State` (`received`/`scanning`/`clean`/`parsed`/`ready`/`malware`/`refused`/`failed`), `Reason`, `PiiTypes text[]`, `ChunkCount`, `CreatedAt` | FK `ConversationId` Cascade |
| `AttachmentChunks` (P2) | Parsed chunks of an attachment, searched only within the conversation | `Id`, `AttachmentId`, `ConversationId`, `Classification`, `Ordinal`, `Page`, `PageEnd`, `DataKeyId`, **`TextCiphertext`**, **`EmbeddingCiphertext`** (1024 float32), `TokenCount` | FK `AttachmentId` Cascade; FK `ConversationId` Cascade |

**P1: metadata and ratings only** ([weakness review §4](../archive/passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)).
The pilot assistant runs history mode `none` until A1 is answered, so P1 writes no ciphertext column, no `DataKeyId`
and no `DataKeys` row. The tables, `DataKeys` and the nullable ciphertext and `DataKeyId` columns exist in the
schema; they are first written when A1 allows stored content.

The background writer upserts `AssistantDailyTotals` in the transaction that inserts the message, so statistics never
depend on rows that retention later removes. The worker never writes `chat`: P2 attachment chunks reach these tables
through the API's internal route (chat design §15.2).

### 2.4 `audit` (all services append)

| Table | Purpose | Key columns | Indexes |
|---|---|---|---|
| `AuditLog` | Changes and privileged reads | gateway `AuditLogEntry` columns (`Id bigint identity`, `Timestamp`, `Actor` (P1: Keycloak `sub`; from P2 Entra `oid`, fallback `sub`; `system:<job>` for jobs; identity design §1.1, 2026-10-31), `Action`, `EntityType`, `EntityId`, `Details jsonb` masked, `ActorName`, `ActorRoles`, `ActorDepartments`, `SourceAddress`, `SessionId`, `RequestId`, `UserAgent`, `RecordedAt`) + `Service varchar(30)`, `Reason varchar(1000)`, `ConfigVersion bigint`, `Classification`, `DepartmentId uuid NULL` (department admins see their departments' entries, admin design §9.1) | IX `Timestamp`; IX (`EntityType`, `EntityId`); IX `DepartmentId` |
| `ConversationAccess` | Written only once A1 allows stored chat content (weakness review §4). Admin review workflow as append-only events: `requested` (reason), `approved` (second approver, `sensitive`), `decrypted` | `Id bigint identity`, `RequestId uuid`, `Event`, `Scope varchar(20)` (`comment`/`conversation`), `ConversationId`, `FeedbackId uuid NULL` (comment reveals), `AssistantId uuid`, `DepartmentId uuid`, `Classification`, `Actor`, `Reason`, `ExpiresAt timestamptz` (access window, admin design §8.3), `Timestamp` | IX `RequestId`; `CK_` approver ≠ requester enforced by app + test |
| `ToolCallEvents` | Tool hub audit (tools design §9.1): metadata and keyed HMAC of arguments | `Id bigint identity`, `Timestamp`, `AssistantId`, `ToolServerId`, `ToolId`, `DefinitionId`, `SkillVersionId`, `SubjectId`, `ClientId`, `ConversationId`, `RunId` (message id), `CallIndex`, `Classification` (effective level), `ResultLevel`, `DurationMs`, `Outcome`, `ErrorCode`, `Truncated`, `ArgumentsBytes`, `ResultBytes`, `ArgumentsHmac bytea` | IX `Timestamp`; IX (`ToolId`, `Timestamp`) |
| `AuthFailures` | Refused credentials, aggregated per 10 s bucket (identity design §6.3) | gateway `AuthFailure` columns + `Credential varchar(20)`, `ClientId uuid`; never the presented value | IX `FirstSeen` |
| `ToolDefinitionObservations` | Tool hub saw a definition differing from the pinned one (tool disabled in memory at once, re-approval shown in admin) | `Id bigint identity`, `ToolServerId`, `ToolId` (null for `added`/`instructions-changed`), `ToolName`, `Kind` (`changed`/`removed`/`added`/`instructions-changed`, tools design §4.4), `ObservedHash bytea`, `Definition jsonb`, `ObservedAt` | IX (`ToolId`, `ObservedAt`) |

Security events are not stored: like the gateway (ADR 015) they go to OpenTelemetry in `Ume.Rag.Security`.

## 3. Search indexes and the retrieval statements

### 3.1 Swedish text search: .NET decompounder + built-in `swedish`

Compounds are split in .NET by `SwedishDecompounder` (`Ume.Rag.Retrieval`, used by the worker and the query side):
driven by the `sv_SE` Hunspell word list (LibreOffice dictionaries, pinned commit, LGPL-3.0; separate,
replaceable files next to the application with their licence texts, repo-and-deployment §5.2). Linking morphemes
(`s`, vowel elision) come from the list's own suffix rules and compound-only stems, not
from separate hand rules; longest match and a minimum part length. The worker stores `Chunks."SearchText"` = text plus the
parts of the text's and heading path's compounds; Postgres uses only the **built-in `swedish`** configuration
(Snowball), so no dictionary loads into Postgres backends. The query side runs the same decompounder before building
the query (§3.3).

Tests: .NET unit tests for the compound cases (`bygglovsansökan` → `bygglov`, `ansökan`; `förskoleplats`,
`avfallstaxa`, `parkeringstillstånd`, `äldreomsorgen`); one integration test that a part matches through Postgres; a
P0 comparison of the decompounder against Postgres `ispell` with the same word list on a word sample, recording any
recall difference (§14).

### 3.2 Columns and indexes on `content."Chunks"`

```sql
ALTER TABLE content."Chunks" ADD COLUMN "SearchVector" tsvector GENERATED ALWAYS AS (
    CASE WHEN "Kind" = 'child' THEN
        setweight(to_tsvector('swedish'::regconfig, coalesce("HeadingPath", '')), 'A') ||
        setweight(to_tsvector('swedish'::regconfig, "SearchText"), 'B')
    END) STORED;

CREATE INDEX "IX_Chunks_SearchVector" ON content."Chunks" USING gin ("SearchVector")
    WHERE "Kind" = 'child' AND "DeletedAt" IS NULL;
CREATE INDEX "IX_Chunks_CollectionId" ON content."Chunks" ("CollectionId") WHERE "DeletedAt" IS NULL;
CREATE INDEX "IX_Chunks_Tags" ON content."Chunks" USING gin ("Tags") WHERE "DeletedAt" IS NULL;
-- Section expansion (knowledge design §6.3, §9); the chunker also writes "OverlapChars".
CREATE UNIQUE INDEX "IX_Chunks_ChunkSetId_Ordinal" ON content."Chunks" ("ChunkSetId", "Ordinal");

-- One partial HNSW index per embedding profile, created only by content.rag_ensure_profile_index (below); no
-- migration creates one, so a profile's model and size stay deployment choices. Example for a 1024-dimension profile:
-- half precision in the index only (the column stays vector; exact scans use full precision).
CREATE INDEX "IX_Chunks_Embedding_<profile id, 32 hex digits>" ON content."Chunks"
    USING hnsw (("Embedding"::halfvec(1024)) halfvec_cosine_ops) WITH (m = 16, ef_construction = 64)
    WHERE "EmbeddingProfileId" = '<profile id>' AND "DeletedAt" IS NULL;
```

The swap makes chunks visible by an `UPDATE` of `DeletedAt`, so every entry of the HNSW index comes from an update and
re-processing leaves dead ones; the worker's index-health check measures each profile index's recall against exact
search and rebuilds it concurrently below the floor (knowledge design §6.4).

`CollectionId` is a scalar that never changes for a chunk, so the B-tree index costs nothing on membership changes
(there are none: a source belongs to one collection).

A new profile gets its index from `content.rag_ensure_profile_index(profile_id uuid, dims int)` (`SECURITY DEFINER`,
owner migrator, called by the worker) **before** any row of that profile exists, so the build is instant and needs no
`CONCURRENTLY`; it builds the `halfvec` form (≈ 2.7 KB per element instead of 4.3–8 KB, so 10M chunks fit a 64 GB
database host; reasoning: [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.3). The column stays `vector`, so
exact scans, the evaluator and a profile migration use full precision; the fp16 recall cost (HNSW against exact) is
measured with the evaluation sets before the first large collection (§12.16). A second profile migrated into a large
corpus gets its index **after** the shadow load (`CREATE INDEX CONCURRENTLY`); partitioning `Chunks` by profile would
not help, and per-collection partial indexes are the fallback if filtered recall falls short (performance pass §6).
Every profile's index, the first one included, comes from this function; the index is named `IX_Chunks_Embedding_` plus
the profile id without hyphens. The function refuses `dims` outside 1–4,000 (the `halfvec` limit), an unknown profile and a `dims` other
than the profile's `Dimensions`; it takes a transaction advisory lock, returns `false` when the index already exists
and `true` when it built it, and is granted `EXECUTE` to the worker only (§1.2). A change of the decompounder or its word list
requires recomputing `SearchText` (worker command `reindex-fts`, in batches; a release-notes item).

### 3.3 Retrieval statements (three short statements, knowledge design §6)

The statements are defined once, in knowledge design §6.2–§6.3 (owned by `Ume.Rag.Retrieval`'s `PostgresSearchIndex`
and the knowledge role); this section only summarises the rules this schema must support, so two copies of the SQL
cannot drift:

- **K** (keyword candidates): the decompounded query is turned into lexemes (`to_tsvector('swedish', $query)`), and
  `content.rag_or_query(tsvector) → tsquery` ORs that lexeme array (each lexeme quoted through
  `plainto_tsquery('simple', l)`, combined by the aggregate `content.rag_tsquery_or_agg(tsquery)` over the built-in
  `tsquery_or`; `NULL` for a query of stop words only), so user input never reaches `to_tsquery` syntax and the text
  form of `plainto_tsquery` is never rewritten (both created by a migration, knowledge design §6.2); `ts_rank_cd`, top 100, on the partial GIN index of §3.2 (`"Kind" = 'child' AND
  "DeletedAt" IS NULL`). Runs on its own connection **in parallel with the query embedding**.
- **V** (vector candidates): `"Embedding"::vector(1024) <=> $vector`, top 100, approximate through the per-profile
  partial HNSW index, or **exact** (`ORDER BY (… <=> $1) + 0`: `IX_Chunks_CollectionId` + top-N sort). Exact for
  scopes of ≤ `Settings.Retrieval.exactScanMaxChunks` (10,000) chunks; above that HNSW when the scope's share of the
  profile's chunks is ≥ `minHnswSelectivity` (0.2 %), else exact again (metric `rag.retrieval.exact_large`; knowledge
  design §6.4). The evaluator scopes by collection over the live sets, like a chat search.
- Both filter `"CollectionId" = ANY ($scope)` and
  `"Classification" = ANY ($levels)` (defence in depth), return ids and numbers only (`ChunkSetId`, `Ordinal`,
  `TextHash`, `CollectionId`, `DocumentType`, `SourceModifiedAt`, the document's `IdentityKind`/`IdentityHash`, and
  its `SourceId` and `Url` for the age decay and `demote` multipliers of weakness review §3), never chunk text. Optional filters (tags,
  document types, modified after) are **appended only when present**, not `($x IS NULL OR …)`. The profile id is
  **inlined as a literal** (from config, never user input), so the planner matches the partial indexes under a generic
  plan; the dimensions and the candidate depth are inlined too.
- **H+D** (hydrate, one `NpgsqlBatch`): the final hits with their section neighbours by (`ChunkSetId`, `Ordinal`)
  range (the unique index of §3.2), re-checking scope, levels, `DeletedAt` and that the chunk's set is its document's
  `LiveChunkSetId` as a safety net (non-live sets are already invisible through `DeletedAt`, §2.2); the documents of
  the hits with their `DisplayName`.
- RRF fusion, age decay and `demote` (knowledge design §6.5), `(DocumentId, TextHash)` dedup, the per-document cap, the optional reranker (`IReranker`), coverage and
  the final top-k run in .NET (`Ume.Rag.Retrieval`, knowledge design §6.5–§8). There is no parent join (flat chunks;
  sections come from neighbouring chunks, knowledge design §9). `hnsw.ef_search`, `hnsw.iterative_scan` and
  `hnsw.max_scan_tuples` are role defaults (§1.1), not per-query `SET LOCAL`.

The coverage signal uses the best cosine **distance** per searched collection against `coverageMaxDistance` together
with the best keyword score against `coverageMinKeywordScore` (§12.15; knowledge design §7). An assistant spanning embedding profiles runs K and V once per profile and fuses in
.NET (knowledge design §6.5). A warm retrieval is 3 Postgres round trips (K, V, H+D).

## 4. Job queue

### 4.1 Table

```sql
CREATE TABLE content."Jobs" (
    "Id"            uuid        PRIMARY KEY,              -- v7
    "EnqueuedAt"    timestamptz NOT NULL DEFAULT now(),
    "Class"         varchar(20) NOT NULL CHECK ("Class" IN ('preview','command','sync','fetch','parse','embed','eval','attachment')),  -- attachment: P2
    "Priority"      smallint    NOT NULL DEFAULT 0,       -- within a class; higher first
    "DepartmentId"  uuid        NOT NULL,                 -- fairness and attribution
    "TeamId"        uuid,
    "Classification" varchar(20) NOT NULL,
    "DedupKey"      varchar(200),
    "Payload"       jsonb       NOT NULL,                 -- ids and parameters only, never content
    "State"         varchar(20) NOT NULL DEFAULT 'queued'
                    CHECK ("State" IN ('queued','running','succeeded','failed','dead','cancelled')),
    "AvailableAt"   timestamptz NOT NULL DEFAULT now(),
    "Attempts"      smallint    NOT NULL DEFAULT 0,
    "MaxAttempts"   smallint    NOT NULL,
    "Reclaims"      smallint    NOT NULL DEFAULT 0,       -- reclaims after an instance died (not attempts)
    "LeaseOwner"    uuid,                                 -- WorkerInstances.InstanceId
    "LeaseUntil"    timestamptz,
    "RunId"         uuid,
    "Result"        jsonb,                                -- small: preview summary, command outcome
    "LastError"     varchar(500),
    "CompletedAt"   timestamptz);

CREATE INDEX "IX_Jobs_Ready" ON content."Jobs" ("Class", "DepartmentId", "Priority" DESC, "AvailableAt")
    WHERE "State" = 'queued';
CREATE INDEX "IX_Jobs_Leases" ON content."Jobs" ("LeaseUntil") WHERE "State" = 'running';
CREATE UNIQUE INDEX "IX_Jobs_DedupKey" ON content."Jobs" ("DedupKey") WHERE "State" IN ('queued','running');
CREATE INDEX "IX_Jobs_Terminal" ON content."Jobs" ("CompletedAt") WHERE "State" IN ('succeeded','failed','dead','cancelled');
CREATE INDEX "IX_Jobs_RunId" ON content."Jobs" ("RunId") WHERE "State" IN ('queued','running');   -- open jobs of a run (cancel, progress)
```

`Jobs` is a plain EF entity. Autovacuum on it: `autovacuum_vacuum_scale_factor = 0.02`,
`autovacuum_vacuum_cost_limit = 2000`.

### 4.2 Classes, concurrency, retries

| Class | Order | Concurrency (per worker) | Lease | Max attempts | Notes |
|---|---|---|---|---|---|
| `preview` | 1 | 2 | 2 min | 1 | Never writes `content` except `Result` and `CrawlHosts.NextAllowedAt`; stage outputs to `previews/` blobs |
| `command` | 2 | 2 | 1 min | 3 | Admin decisions applied by the worker: clear PII flag, confirm/reject deletion, release a held document, edit or disable a parse rule (`edit-parse-rule`, `disable-parse-rule`), approve/reject/edit/roll back a site profile (`approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile`), edit human evaluation questions, cancel a run, reprocess a document, roll back a publish (§12.2; kinds in engine design §9.1) |
| `sync` | 3 | 2 | 5 min | 3 | Listing, change detection, deletion guard |
| `fetch` | 4 | 8 (frontier limits per host) | 2 min | 5 | |
| `parse` | 5 | `min(setting, parser /v1/info convert_slots)` | 15 min | 3 | Heartbeat extends the lease; yields after a 10-min slice; the gated `/v1/ner` call runs inside the processing job (processing design §9.6) |
| `embed` | 6 | 4 | 2 min | 6 | Batches sized for the gateway |
| `eval` | 7 | 1 | 15 min | 2 | Lowest. Question generation and retrieval runs (`publish-report`, `nightly`, `on-demand`) only; answer-level runs (`answers`, `assistant-tests`) and the P2 skill gate run in `rag-admin`, which writes their `EvaluationRuns` (§1.2) |
| `attachment` (P2) | after `preview` | 2 | 2 min | 3 | Chat attachments (chat design §15.2, engine design §9.1): scan → parse → chunk → PII → embed → post chunks to the API's internal route; never writes `chat` |

- **Back-off**: `TransientError` → `AvailableAt = now() + least(10s * 2^Attempts, 1h)` ± 20 % jitter; `FatalError` →
  `failed` at once; attempts exhausted → `dead` (shown in the run).
- **Concurrency** above is per instance (defaults); per-instance values are the deployment setting
  `Worker:Concurrency:<class>` (settings rule: per-process knobs are deployment values), so heterogeneous hosts differ.
  `Settings.WorkerConcurrency` holds fleet-wide run-time values only (shares and caps such as `departmentShare`); the
  P2 fleet-wide embedding cap `Embedding:GlobalMaxInFlight` is a deployment setting too (§11; reasoning:
  [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.4).
- **Capacity refusals** (parser `busy`/`not_ready`, gateway 429 or 503 with `Retry-After`, unconfirmed config) requeue
  with `Attempts − 1`, so they never use up attempts (engine design §7.3).
- **Leases**: dequeue sets `LeaseOwner`, `LeaseUntil`, `Attempts + 1`; a reaper (every 10 s) returns expired leases to
  `queued`, and also jobs of instances without a heartbeat for 30 s (the 10 s heartbeat may miss two beats; a killed worker's jobs are
  back within 40 s). A reclaim (an expired lease or a dead
  instance, engine design §9.4) is not an attempt: it sets `Attempts − 1` and `Reclaims + 1`; a job reclaimed 3 times is dead-lettered as a poison suspect
  (`LastError = 'poison-suspect'`). A worker restart loses no work (worker §9).
- **Dedup**: enqueue is `INSERT … ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING` against
  the partial unique index. Keys in use include `sync:{sourceId}`, `fetch:{sourceId}:{urlHash}` (P1 per-source
  frontier; `fetch:{level}:{urlHash}` with the P2 shared frontier), `fetch:{sourceId}:upload:{uploadId}`,
  `sync-upload:{batchId}`, `parse:{documentId}:{executionId}`, `embed:{documentId}:{executionId}:{pipelineVersionId}`
  and `eval:{collectionId}` (connectors design §6, §9.1; engine design §9.1).
- **Fairness per department** (from P0): the worker keeps running counts per (class, department) in memory, reads the
  departments with ready jobs of the class with the `GROUP BY` below, and tries them in ascending order of its running
  count, then by best priority, then by oldest ready job, with the indexed query below; the first hit wins. Each class has its own dispatcher loop and slots, so preview
  and command never wait behind a busy class. One job per statement; the **batched** dequeue (`LIMIT` = the free slots
  the department may take), buffered non-fenced completions (flushed within 100 ms by one `UPDATE … FROM unnest`;
  fenced completions stay in their transaction) and the recursive-CTE loose index scan in place of the `GROUP BY`
  (to-verify #9) come in P2 behind the same `IJobQueue` seam (performance pass §3.4).
- **`IJobQueue`** (`Ume.Rag.Domain/Ports/IJobQueue.cs`, the code is the reference): enqueue (optionally inside the
  caller's `DbTransaction`), ready departments per class, dequeue of one job, per-instance heartbeat over the running
  leases, complete (optionally in a transaction, for fenced writes), fail, release and cancel a run's open jobs; the wake-ups
  (`JobWakeUps`) live in `Ume.Rag.Infrastructure`.

```sql
SELECT "DepartmentId", max("Priority"), min("AvailableAt")
FROM content."Jobs"
WHERE "State" = 'queued' AND "Class" = $1 AND "AvailableAt" <= now()
GROUP BY "DepartmentId";

WITH next AS (
    SELECT "Id" FROM content."Jobs"
    WHERE "State" = 'queued' AND "Class" = $1 AND "DepartmentId" = $2 AND "AvailableAt" <= now()
    ORDER BY "Priority" DESC, "AvailableAt"
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE content."Jobs" j
SET "State" = 'running', "LeaseOwner" = $3, "LeaseUntil" = now() + $4::interval, "Attempts" = j."Attempts" + 1
FROM next WHERE j."Id" = next."Id"
RETURNING j.*;
```

### 4.3 LISTEN/NOTIFY

| Channel | Payload | Sent by | Listened by |
|---|---|---|---|
| `rag_jobs` | class name (`preview`, …); other payloads are ignored | enqueue transaction (`pg_notify` delivers at commit) | worker (registered by `AddRagWorker` only) |
| `rag_config` | config version (§5) | config trigger | api, worker, admin |

Each instance holds one dedicated listening connection (counted in its role's limit, opened from its own connection
string `ConnectionStrings__ragdb-listen`, so a later pooler never sees it): the config monitor's, which carries
`rag_config` and, in the worker, `rag_jobs` too. It still polls every 2 s (jobs) and 2 s
(config version), so a missed notification costs seconds. From P2 (first multi-host deployment) a process sends
`rag_jobs` for a class at most once per 200 ms (none when it handles the job locally), and receivers coalesce
wake-ups per class (at most one probe per 50 ms, ignored without a free slot; the 2 s poll stays the safety net;
[`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.4).

### 4.4 Partitions and job cleanup

`RunItems` is range-partitioned by day (`content."RunItems_20261029"`) with a `DEFAULT` partition as a safety net.
`content.rag_partition_maintain()` (`SECURITY DEFINER`, owner migrator, run by the migration service on deploy and by
the worker daily at 02:00) creates partitions for today + 7 days and drops partitions older than the run-item
retention (90 days, a constant in the function until it becomes a `Settings` value), so run items never cause vacuum
storms (R1). It returns `(created, dropped, default_rows)`: when maintenance lagged, rows of a day being created are
moved out of `DEFAULT` into it, and `default_rows > 0` (rows of other days still in `DEFAULT`) is the lag signal for a
worker metric. One run at a time under a transaction advisory lock.

Terminal jobs older than 7 days are deleted nightly in batches of 1,000 through `IX_Jobs_Terminal` (worker retention,
§8). Singleton maintenance (partition maintenance, retention, sweeps) runs under `pg_try_advisory_xact_lock` tick
leaders; from P2, when several instances run maintenance, the config singletons (`Worker:Maintenance`,
`Admin:Retention`) become lease leaders in `content."LeaderLeases"` (§2.2) or lock-guarded batches.

## 5. Config outbox and versioning (R7)

```sql
CREATE TABLE config."ConfigVersion" (
    "Id" smallint PRIMARY KEY CHECK ("Id" = 1),
    "Version" bigint NOT NULL DEFAULT 0,
    "LastTransactionId" xid8,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now());
CREATE TABLE config."ConfigOutbox" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Version" bigint NOT NULL,
    "Timestamp" timestamptz NOT NULL DEFAULT now(),
    "EntityType" varchar(100) NOT NULL,
    "EntityId" varchar(100),
    "Operation" char(1) NOT NULL,          -- I, U, D
    "SecurityRelevant" boolean NOT NULL);
CREATE INDEX "IX_ConfigOutbox_Version" ON config."ConfigOutbox" ("Version");

CREATE FUNCTION config.rag_config_changed() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE v bigint;
BEGIN
    UPDATE config."ConfigVersion"
       SET "Version" = CASE WHEN "LastTransactionId" = pg_current_xact_id() THEN "Version" ELSE "Version" + 1 END,
           "LastTransactionId" = pg_current_xact_id(), "UpdatedAt" = now()
     WHERE "Id" = 1
    RETURNING "Version" INTO v;
    INSERT INTO config."ConfigOutbox" ("Version", "EntityType", "EntityId", "Operation", "SecurityRelevant")
    VALUES (v, TG_TABLE_NAME,
            CASE TG_OP WHEN 'DELETE' THEN to_jsonb(OLD) ->> 'Id' ELSE to_jsonb(NEW) ->> 'Id' END,
            left(TG_OP, 1), TG_ARGV[0]::boolean);
    PERFORM pg_notify('rag_config', v::text);
    RETURN NULL;
END $$;

CREATE TRIGGER "TR_Keys_ConfigChanged" AFTER INSERT OR UPDATE OR DELETE ON config."Keys"
    FOR EACH ROW EXECUTE FUNCTION config.rag_config_changed('true');
```

- **Every** config table except `KeyLastUse`, `DataProtectionKeys`, `AdminSessions`, `ConfigVersion`, `ConfigOutbox`
  and the admin-only `AssistantTestQuestions` and `AssistantTemplates` gets the trigger, so no code path can forget the
  outbox. `AssistantVersions` skips drafts, so editing a draft never bumps the config version: its triggers carry
  `WHEN (NEW."State" <> 'draft')` (insert, update) and `WHEN (OLD."State" <> 'draft')` (delete); publish and restore
  fire them. `SecurityRelevant = true` for `Keys`, `Clients`, `Collections`, `CollectionShares`, `Sources`,
  `Assistants`, `AssistantVersions`, `AssistantAudiences`, `Skills`, `SkillShares`, `SkillVersions` (state changes),
  `SkillVersionLinks`, `ToolServers`, `Tools`, `ToolApprovals`, `ToolDefinitions`, `ToolDepartmentGrants`,
  `DataProtectionPolicies`, `Departments`, `Teams`.
- `content."ParseProfiles"` gets the trigger too (`TR_ParseProfiles_ConfigChanged`,
  `rag_config_changed('false')`), so a parse-rule change moves the config version (processing design §3.5).
- **Serialised config writes**: every admin config transaction first takes
  `SELECT … FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE`, then runs `ConfigRules` (the cross-rules) and
  writes, so two concurrent saves cannot each pass a cross-rule (write skew).
- **Monotonic in commit order**: the single-row lock is held until commit, so a later transaction always gets a higher
  version and commits after it. (A sequence would not: version 6 could commit before 5, and a reader at 6 would never
  see 5.) Config writes are rare, so serialising them costs nothing.
- One version per transaction; identical `NOTIFY` payloads in one transaction are folded by Postgres.
- **Readers** (`ConfigVersionMonitor` in `Ume.Rag.Infrastructure`, on every instance of every service): load a snapshot tagged with its version; on
  `NOTIFY` or a 2 s poll of `SELECT "Version" FROM config."ConfigVersion"`, read `ConfigOutbox WHERE "Version" > @cached`,
  reload the affected entity types and swap the snapshot atomically. If the cached version is older than the oldest
  outbox row, reload everything. A per-instance config-lag metric (cached against current version) is still to
  build (performance pass §3.5).
- **Grace window**: a security-relevant decision uses the last confirmed snapshot while
  `now − lastConfirmedAt ≤ Config:MaxUnconfirmedSeconds` (deployment setting, default 90 s); beyond that, keyed
  requests get `503 config_unconfirmed` and a security event. Notifications still apply changes (revocations included)
  at once when they arrive. `/health/ready` does not fail on staleness within the window, so one stalled database does
  not drain every instance at the same moment. Other settings may serve from the last snapshot.
- The config version is in every log scope (plan §10) and in `audit."AuditLog"."ConfigVersion"`.

## 6. Classification of rows (no tiers)

There are no data tiers (§13). Rows of
`content` and `chat` carry a plain `Classification` column (a chunk's level is its document's, i.e. its source's);
retrieval filters `"Classification" = ANY ($levels)` as defence in depth; master keys stay per level
(`rag-chat-<level>`, `rag-blob-internal`). If `sensitive` ever needs isolation it becomes a **separate deployment**
reached over the existing HTTP seam (`Retrieval:Mode=Http`; postponed T1/K2), not a second database of this one.

## 7. Envelope encryption

**Chat: built when A1 allows stored content** ([weakness review §4](../archive/passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)).
In P1 the pilot assistant runs history mode `none`: no bucket keys, no chat master keys `rag-chat-<level>` (neither
created nor escrowed), no content encryption, no shredding. `DataProtectionPolicies.AllowedHistoryModes` holds only
`none` until this is built. The chat design below applies from that point. **Blob encryption** (below) is built: the
worker writes `internal` raw, parse and stage blobs as `URB2`, the admin writes `internal` uploads as `URB2`
(connectors design §9.1), and rag-api reads `URB2` for citation downloads (knowledge design §3.5).

**Chat** (all ciphertext columns are `bytea`: `0x01` format byte ‖ 12-byte nonce ‖ AES-256-GCM ciphertext ‖ 16-byte
tag; AAD = `"<table>:<row id>:<column>:<conversation id>"`, so ciphertext cannot be moved between rows):

| Ciphertext | Plaintext metadata (stays readable) |
|---|---|
| `Conversations.TitleCiphertext`, `Messages.ContentCiphertext`, `MessageSources.SnippetCiphertext`, `Feedback.CommentCiphertext` (reason + comment), `ToolCalls.ArgumentsCiphertext`, `ToolCalls.ResultCiphertext`; P2 `Attachments.FileNameCiphertext`, `AttachmentChunks.TextCiphertext`, `AttachmentChunks.EmbeddingCiphertext` | ids, times, assistant, client, subject id (oid), classification, history mode, token counts, cost, latency, outcome, residency, rating, tool name, sizes, HMAC of arguments, cited document/chunk ids |

- **Bucket keys**: one AES-256 data key per (level, expiry month) in `chat."DataKeys"`, created on first use
  (`INSERT … ON CONFLICT DO NOTHING` on the unique key, so concurrent writers converge), wrapped by `IKeyProvider`
  (Key Vault / Managed HSM `wrapKey`, OpenBao transit, key files: the **file key provider in the pilot**, plan §8a,
  postponed decision K1; also in development). A message, its sources, feedback and tool calls are encrypted under the
  bucket of the message's `ExpiresAt` month; the title is re-encrypted under the newest message's bucket. Unwrapped
  bucket keys live in process memory for at most 5 minutes, never in Redis.
- **Who unwraps**: the Chat API (own history) and the admin review path after a `ConversationAccess` `approved` event
  (second approver for `sensitive`). DB roles alone see ciphertext.
- **Wrapping keys and shredding** (§12.5): while `Settings.ChatShreddingEnabled` is false (until postponed A1 allows
  deletion), buckets are wrapped by the level's long-lived master key `rag-chat-<level>`, which is escrowed
  (repo-and-deployment §7.4), and retention deletes rows only. When the setting is switched on, new buckets are wrapped
  by a per-month **shred key** `rag-chat-<level>-<yyyy-mm>` in the key service (existing buckets are re-wrapped by the
  `rewrap` command); once a month's last expiry plus 7 days' grace has passed, the admin retention job destroys that
  shred key, which makes every backup copy of the bucket unwrappable. Shred keys are never escrowed beyond that rule.
- **Master-key rotation** (compromise or policy): new version of the wrapping key; a background `rewrap` command
  unwraps and re-wraps each `DataKeys` row (no message re-encryption), then the old version is disabled. Audited.
- **History modes**: `stored` as above; `session` sets `ExpiresAt` = 24 h; `none` writes metadata only (ciphertext
  columns null, no data key). Public level with encryption switched off writes format byte `0x00` (plaintext); such
  rows are only deleted (§12.10).

**Blobs**: `public` blobs are **not envelope-encrypted** (encrypted disk only). `internal` blobs are encrypted with a
per-blob data key wrapped by `rag-blob-internal` and stored **in the blob's header**, so content-addressed blobs shared
by several documents need no key table and a blob is self-contained for backup and restore. Format: header `URB2` ‖
master key name and version ‖ wrapped data key ‖ base nonce, then 64 KiB segments with AES-256-GCM (STREAM
construction). Unwrapped by the worker and, for citation downloads (`GET /v1/documents/{id}/content`, raw blob only,
read-only mount), by the knowledge role (knowledge design §3.5); downloads stream through the api with range
requests, and no service keeps a decrypted copy on disk. `sensitive` content is not stored in this deployment (§6).
The blob master keys are long-lived keys, escrowed offline with the other long-lived keys (repo-and-deployment §7.4);
only chat shred keys (above) follow the shred rule instead.

**Blob envelope, implementation** (`Ume.Rag.Infrastructure/Blobs/`):
- `IBlobEnvelope` over the plain store and an `EnvelopeBlobStore` that replaces `IBlobStore` (`AddRagBlobEnvelope`),
  with `IKeyProvider` and the pilot's `FileKeyProvider` in `Infrastructure/Blobs/`. `KeyProvider:Directory` names the
  key directory, default `/run/keys` (the Compose mount). A key file holds the base64 of 32 bytes, as `init-deployment.sh` writes it; file keys have version `1` only.
- The data key is wrapped with AES-256-GCM, with the master key name and version as associated data. Each segment's
  associated data is the header; the blob path is not bound, so a blob can be migrated or restored under another key.
  A failing tag, a changed header, swapped segments or a missing final segment throw `BlobIntegrityException` (an
  `IOException`) before the segment's bytes are returned.
- The master key per level comes from `DataProtectionPolicies.BlobMasterKeyName`; without a row a non-`public` level
  uses `rag-blob-<level>`, and a row cannot switch encryption off for a non-`public` level.
- Reads of an encrypted level require `URB2`: a plaintext `internal` blob cannot be read. Quarantine and preview
  outputs are encrypted by the source's level (`ForLevel`); access without a level throws.
- Range reads decrypt and verify the earlier segments instead of seeking (the worker uses no ranges, and knowledge
  §3.5 has none in P1).

## 8. Retention jobs

Nightly, batched step functions in the gateway style (ADR 016): `SECURITY DEFINER`, owner migrator,
`SET search_path = pg_catalog, pg_temp`, minimums enforced inside the function, each run logged and written to
`audit."AuditLog"` as actor `system:retention`. Admin API runs the `config`/`chat`/`audit` steps; the worker runs the
`content` steps and partition maintenance (it owns content and blobs).

| Schema / table | Rule (platform §8 unless marked *proposed*) | Mechanism |
|---|---|---|
| `chat."Messages"` + `MessageSources`, `Feedback`, `ToolCalls` (cascade) | `ExpiresAt` = `CreatedAt` + assistant retention (default 90 d, `sensitive` 30 d, max per level policy; `session` 24 h); in P1 metadata rows only (history mode `none`, weakness review §4) | `chat.rag_retention_chat_step(batch)` |
| `chat."Conversations"` (+ P2 `Attachments`, `AttachmentChunks`) | deleted when no message remains and `ExpiresAt` passed; user delete: rows deleted at once | same function / Chat API `DELETE` |
| `chat."DataKeys"` | when A1 allows stored content (none in P1, weakness review §4): bucket row deleted when its month has passed and no row references it; shred key destroyed only when `Settings.ChatShreddingEnabled` (§7) | same function; then shred-key destruction |
| `chat."AssistantDailyTotals"` | 3 years *proposed* | same function |
| `content."Documents"`, `"ChunkSets"`, `"Chunks"` | removed from source → soft delete, purge after 14 days | worker; cascade from `Documents` |
| `content."ChunkSets"` (`superseded`) + chunks | 14 days after `SupersededAt` (rollback window) | worker |
| `content."ChunkSets"` (`writing`) | orphans older than 24 hours (engine design §14) | worker sweep |
| `content."NodeOutputs"` (+ blobs) | unused 30 days (`Settings`); a parse output referenced by a live document is kept | worker, `LastUsedAt` |
| uploads (blobs) | while the source exists; a replaced or deleted upload is tombstoned; uncommitted batches after 24 h | worker on source/upload deletion; admin sweep for uncommitted batches |
| blob tombstones (`content."BlobTombstones"`, P1, §9) | purged `Settings.BlobPurgeDelayDays` (35, = Postgres backup retention) after deletion, so every restore point finds its blobs | worker sweep: `IBlobStore.DeleteAsync` per row older than the delay, then the row is deleted, in batches |
| `content."QuarantinedFiles"` (+ blobs) | `Settings.QuarantineRetentionDays` (30) | worker |
| `tmp/` and `previews/` blobs | 24 hours | worker sweep |
| `content."EvaluationSets"` and children | until the collection is removed | worker on collection deletion |
| `content."EvaluationRuns"` of kind `assistant-tests` (+ results) | the last 20 runs per assistant *proposed*; all on assistant deletion | worker |
| `content."EmbeddingCache"` | unused 30 days *proposed* | worker |
| `content."Jobs"` | terminal jobs 7 days *proposed* | worker, batched `DELETE` (§4.4) |
| `content."RunItems"` | 90 days *proposed* | partition drop |
| `content."Runs"` | 13 months *proposed* | worker |
| `content."ParseObservations"` | 180 days *proposed* | worker |
| `content."RetrievalStats"` | 13 months *proposed* | worker |
| `content."SiteProfileVersions"` | `superseded`/`rejected` versions older than 1 year, keeping the last 10 per host *proposed* | worker |
| `config."ConfigOutbox"` | 30 days *proposed* | `config.rag_retention_config_step` |
| `config."AssistantVersions"` | `superseded` versions older than 1 year, keeping the last 20 per assistant *proposed* (longer than any message that names its version) | `config.rag_retention_config_step` |
| `config."AdminSessions"` | rows past `ExpiresAt` (sign-out deletes at once) | `config.rag_retention_config_step`, run by the admin's `RetentionService` |
| `audit.*` | 2 years (`Settings.AuditRetentionDays`, minimum 90, may be "forever") | `audit.rag_retention_audit_step` |
| Open WebUI's own database | client data-handling profile (90 days): feedback pruned through its HTTP API; chat copies of other users cannot be pruned at v0.12.5 without `ENABLE_ADMIN_CHAT_ACCESS=true`, so they stay unpruned in the pilot (stated in its data-handling profile) until register item W2 is decided within the DPIA (G1). In the pilot this store is the **primary copy** of every chat and feedback snapshot with content (weakness review §4) | Open WebUI's HTTP API, never SQL against its database |

**HNSW and vacuum.** Superseded sets are hard-deleted after their 14 days in weekly batches, so vacuum cleans the HNSW
indexes in bulk; `autovacuum_work_mem` is 2 GB (repo-and-deployment §6.2); `Chunks` gets
`autovacuum_vacuum_scale_factor = 0.05` (not set yet). If a vacuum of `Chunks` takes more than 6 hours, the HNSW
indexes are rebuilt monthly with `REINDEX CONCURRENTLY` (reasoning: performance pass §3.2).

## 9. Blob layout (`IBlobStore`)

One container (file system: `BlobStore:Root`, default `/data/blobs/rag/`; S3: one bucket, `BlobStore:S3:Bucket`,
default `rag`). Paths below the container; ids in `D` format, lower case (below); `internal` objects
envelope-encrypted per §7, `public` objects not (no SSE; the object store's disks are encrypted).

**Contract** (S3-safe on both implementations, from P0; reasoning: [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md)
§3.13): **write-once**: a key is never overwritten (new content = new key); `PutAsync` is atomic (visible only when complete; FS: a staging file under `{root}/.staging/`, fsync, then a
no-overwrite POSIX `link(2)` to the key's path, because .NET's `File.Move(overwrite: false)` on Unix checks with
`lstat` and then `rename`s over a file that appeared in between; S3: PUT or completed multipart) with `IfAbsent`
semantics (S3 `If-None-Match: *`, 412 = already there = success, which is correct for content-addressed keys) and
returns `BlobWriteResult` (`Created` or `AlreadyExisted`); `OpenReadAsync` (null = absent; a range is `offset` plus an
optional length, clamped to the end, one that starts at or past the end reads as empty, an S3 store maps HTTP 416 to
empty; no suffix ranges); `ExistsAsync`; `PromoteAsync` (FS link then unlink, S3 conditional server-side copy +
delete; only from `tmp/`; returns `BlobWriteResult`, `AlreadyExisted` when the target exists, also on a retry);
`DeleteAsync` is the idempotent **purge** (an absent key is success); `DeletePrefixAsync` only for `tmp/` and
`previews/` sweeps; `ListAsync` only for those sweeps and the weekly orphan audit, never on hot paths. The file system
implementation serves dev and the single-host pilot; beyond one host an S3-compatible object store: the
municipality's S3 service if IT offers one, else SeaweedFS (never MinIO, which is archived), decided with IT before
the first multi-host deployment (postponed decision S2). The S3 implementation (`S3BlobStore`, not built yet; due in
P1 with its contract tests) uses `AWSSDK.S3` (not the `Minio` client) with `ForcePathStyle = true`,
`RequestChecksumCalculation`/`ResponseChecksumValidation` `WHEN_REQUIRED`, a 30 s timeout and `MaxErrorRetry` 2; it
sends one PUT for a known length (≤ 5 GB) and multipart (8 MiB parts) only for an unknown length over 64 MiB; the
contract tests run against each server in CI. Settings: `BlobStore:Kind`, `BlobStore:S3:ServiceUrl`,
`BlobStore:S3:Bucket` (one per deployment). Access keys **per service**
(`BlobStore__S3__AccessKey`/`SecretKey`) with bucket policies mirroring today's mounts: worker read/write, admin write
on `uploads/` and read, api read-only (P2: write on `chat-tmp/`).

**Tombstones are database rows, not store markers.** A user's or a retention rule's deletion inserts a row into
`content."BlobTombstones"` in the transaction of its own change (`INSERT … ON CONFLICT DO NOTHING`); the worker's
purge sweep takes rows older than `Settings.BlobPurgeDelayDays` (§8) oldest first, calls `DeleteAsync(key)` for each
and then deletes the row, in batches. Table: `Key varchar(1024)` PK (a `BlobKey`), `DeletedAt timestamptz NOT NULL DEFAULT now()` with an index for the sweep, `Reason
varchar(100) NULL`. Writers: the admin (S I) for the upload blobs it replaces or deletes; the worker (S I U D) for
stage outputs, quarantine and the sweep (§1.2). The weekly orphan audit (`ListAsync`) compares the listing with the
live references plus the tombstones; orphan detection is otherwise database-driven, and nothing lists the store on
a hot path. A marker in the store would survive a
point-in-time restore to T and purge, 35 days later, a blob the restored database still references; a row rolls back
with the reference. Scratch (`tmp/`) and preview blobs are deleted directly.

**Keys** (`BlobKey`): the first segment is one of the areas in the table below; a key that names a blob has at least
two segments (a one-segment key is a prefix only); segments are 1–255 characters of **lower-case** `a–z`, digits,
`-`, `_` and `.`, neither starting nor ending with `.`; no leading, trailing or doubled `/`; at most 1,024 characters
(the S3 limit). Lower case only, so two keys never collide on a case-insensitive file system. So the path parts
below are written in that alphabet (the callers format them):
- ids are lower-case GUIDs (`D` format);
- hashes and cache keys are lower-case hex;
- `{level}` is the classification value (`public`, `internal`, `sensitive`);
- `{nodeType}` is the node type's lower-case name;
- `{host}` is the lower-case ASCII (punycode) host, and a port becomes `_{port}` (`example.se_8443`);
- `{fetchedAt}` is UTC Unix seconds (`robots-1793395200.txt`).

**File system limits (P0):** the parent directory is not fsynced after the link; only Linux is run (Windows and
macOS paths are untested); the store needs a file system with hard links (the pilot uses a local Docker volume); the
staging directory sits at the root, so P2's read-only api mount with a writable `chat-tmp/` needs a staging directory
per writable mount.

| Kind | Path | Retention | Backup |
|---|---|---|---|
| Upload | `uploads/{sourceId}/{slot}/{sha256}`; batch manifest `uploads/{sourceId}/batches/{batchId}.json` (names, hashes; connectors design §9.1) | while the source exists | copied to the backup target within 15 minutes (the only content that cannot be fetched again) |
| Quarantined file (ClamAV) | `quarantine/{sourceId}/{sha256}` | `Settings.QuarantineRetentionDays` (30) | none |
| Raw fetch | `raw/{level}/{contentHash}`; extracted links `raw/{level}/{contentHash}.links.json.gz` (connectors design §5) | while a document references it | nightly or none (re-creatable) |
| Parse output | prefix `parse/{level}/{parseCacheKey}/` with `result.json`, `docling/{from}-{to}.json.gz` and `ranges/{from}-{to}.{strategy}.result.json` (intermediate; the strategy is part of the name) (parser design §7.6) | while a live document references it; else unused 30 days | nightly or none (re-creatable) |
| Other stage output | `stages/{level}/{nodeType}/{cacheKey}.{ext}` | unused 30 days | none |
| Job scratch | `tmp/{jobId}/…`; the admin stages upload batches under `tmp/upload-batches/{batchId}/…`. The worker streams a fetched file to a local temp file (≤ 60 MB), scans it with ClamAV, then makes one `PutAsync`, so no `tmp/` object is written for it (performance pass §3.13) | 24 hours (sweep) | none |
| Chat attachment upload (P2) | `chat-tmp/{attachmentId}` (written by the API; chat design §15.2) | deleted after parsing; orphans swept after 24 hours | none |
| Preview output | `previews/{jobId}/{n}/{stage}.json` plus `keys/` and the redirected connector paths (engine design §11) | 24 hours | none |
| robots.txt | `crawl/{host}/robots-{fetchedAt}.txt` | refreshed daily | none |

**Cache keys** (per stage, no whole-pipeline hashes): `CacheKey` = SHA-256 of `inputHash ‖ nodeType ‖ nodeVersion ‖
paramsHash`, where `inputHash` is the hash of the stage's input (for the first stage: the raw content hash),
`nodeVersion` the stage code version and `paramsHash` the SHA-256 of the canonical JSON of the stage's settings. The
parse key is (content hash, parser settings hash, parser build). Two sources (of any department) that fetch the same
bytes share the raw blob and every stage output up to the first stage whose settings differ; embeddings are shared by
(text hash, profile).

## 10. Migrations

- **One `RagDbContext`** (as the gateway's one context), entity configurations in folders per schema
  (`Persistence/Config`, `Content`, `Chat`; the audit tables are configured in `RagDbContext`), `ToTable("Chunks", "content")`, history table
  `config."__EFMigrationsHistory"`. Raw SQL inside migrations for what EF cannot express: the partitioned `RunItems`
  (`ExcludeFromMigrations()` on its entity), HNSW expression indexes, generated `tsvector`, partial unique indexes,
  triggers and step functions (`REVOKE ALL ON FUNCTION … FROM PUBLIC`, as `DataRetention.cs`).
- **Before production, any schema change may be made in one step** (Decisions "No backward compatibility before
  production"): no expand/contract steps and no fallbacks for old rows; development data is rebuilt or re-crawled
  instead. **From production**: expand/contract only, so rolling updates (P2) stay possible (gateway ADR 017); every
  release notes *Rolling update: yes/no*.
- **Migration service** (copy of `MigrationWorker.cs`): `MigrateAsync`, apply `app-roles.sql`, run
  `content.rag_partition_maintain()`, then the dev seeder (development only, `Seed:Enabled`; insert-if-absent: the
  `dev-embedding` profile with its fixed id `01929b6e-0000-7000-8000-00000000b6e3`, model `bge-m3` of FakeGateway,
  1024 dimensions, ceiling `Eu`, and its index through `content.rag_ensure_profile_index`). The platform profile and
  its canary are seeded from the `EmbeddingProfile:*` knobs (§2.1). Services wait for it as in the gateway compose files.

## 11. Redis (P2, first multi-host deployment; mirrors `RedisKeys.cs`)

**No Redis in the pilot** (reasoning: simplification pass §14). Each P1 use lives in Postgres or in process instead.
The seams are `IRateLimitStore` and `ISharedCache` (in-memory and Redis implementations). A deployment enables Redis by
configuring `ConnectionStrings:redis` (P2, where rate limits, in-flight leases and the cache must be shared across
instances; production is enabled by the connection string, and `Redis:Enabled` is the dev AppHost switch whose P2 path
sets it, repo-and-deployment §4.1); without it the in-memory implementations are used. With several instances there
are no sticky sessions: the config snapshot is per instance (90 s grace, then 503), admin sessions are in
`config."AdminSessions"`, Data Protection keys in `config."DataProtectionKeys"`, ids and grants are signed, MCP is
stateless, and the L1 cache and the stats writer queue are per instance. Redis runs on one host in P2 (one instance
is enough at 10× load; `mem_limit` 256 MB, no persistence; Sentinel or a replica by a trigger, to build with the
Redis deployment).

| Use | P1 (single host, one slot, no Redis) | From P2 (`ConnectionStrings:redis` set) |
|---|---|---|
| Admin sessions (BFF ticket store, `IAdminSessionStore`) | `config."AdminSessions"` (§2.1) through `PostgresAdminSessionStore`, owned by `rag-admin`; expired rows deleted by the admin's `RetentionService` (§8) | unchanged: Postgres serves every admin instance |
| Data Protection key ring | `config."DataProtectionKeys"` (as the gateway's `PersistKeysToDbContext`, `InfrastructureExtensions.cs:60-63`) | unchanged |
| Rate limits | `IRateLimitStore`, in-memory per instance (exact with one slot), same keys and windows as below | Redis keys below |
| Query-embedding cache | `ISharedCache`, in-memory in process, same HMAC key, value format and size cap, every level (knowledge design §5.4) | Redis L2 for `public`/`internal` below; the in-process L1 stays |
| Crawl and embedding in-flight leases | not needed: one `fetch` instance keeps the per-host cap exact; per-instance AIMD for embedding | Redis keys below |

Redis keys (P2): prefix `ume-rag:`, ACL user limited to `~ume-rag:*`; no publish channels (change events come from
Postgres). Every use states its behaviour when Redis is down; rate limits fall back to per-instance in-memory limits.

| Key (P2) | Lifetime | Redis down |
|---|---|---|
| `ume-rag:rl:req:{keyId}:{minute}`, `ume-rag:rl:user:{subjectHash}:{minute}`, `ume-rag:rl:tool:{toolId}:{minute}`, `ume-rag:rl:tooluser:{toolId}:{subjectHash}:{minute}`, `ume-rag:rl:tools:{subjectHash}:{minute}`, `ume-rag:rl:mcp:{subjectHash}:{minute}` (tools design §4.9, §7.6), `ume-rag:rl:ip:{ipHash}:{minute}` (P2 widget, identity design §6.5), `ume-rag:rl:dl:{ipHash}:{minute}` (citation downloads, `Knowledge:DownloadsPerMinutePerAddress`; its own key so the two per-address limits never share a counter) | 120 s (`RateWindowSeconds`, as gateway) | per-instance in-memory limits with the same values |
| `ume-rag:qemb:{profileId:N}:{queryMac}` (query embedding only, value 1 version byte ‖ 1024 little-endian float32; `queryMac` = hex of the first 16 bytes of HMAC-SHA256(`Security:CacheKeyPepper`, normalised query), never a plain hash; `public`/`internal` effective level only, §12.14; an in-process L1 serves all levels; knowledge design §5.4) | 5 min | L1 only; misses embed through the gateway |
| `ume-rag:crawl:inflight:{hostMac}` (P2, sorted set of in-flight fetch leases per crawl host, cap `MaxConnections`; `hostMac` = HMAC of the host name with `Security__CrawlKeyPepper`), `ume-rag:embed:inflight` (P2, sorted set, fleet-wide cap `Embedding:GlobalMaxInFlight`, default 2 × provider replicas) ([`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.6, §3.9). A refused crawl lease requeues the fetch as `rate_limited`; the lease pattern is the gateway's `RedisStores.cs` | TTL = lease (60 s for fetch) | crawl: ⌈`MaxConnections` ÷ k⌉ per instance, k = live fetch instances (heartbeat < 45 s); embedding: per-instance AIMD; `CrawlHosts.NextAllowedAt` keeps host politeness in Postgres |

## 12. Open decisions and contradictions found

**Review (2026-10-29):** recommendations 1–22 accepted and carried into the plans (platform §3, §8, §8a; worker §2,
§4; knowledge §3; key prefixes everywhere). 23 is resolved by the Chat API detail design (§15.2 there). Rows 5–8, 10,
13, 16, 18 and 19 are restated in the decided form of the simplification pass (2026-10-30); 24–25 come from its §15
(many assistants per department).

| # | Topic | Finding | Recommendation |
|---|---|---|---|
| 1 | Identifier casing | Plans write `content.node_manifest` (snake case); the gateway uses EF's quoted PascalCase | PascalCase as here (`content."NodeManifest"`); update worker §4 wording |
| 2 | Admin writes to `content` | Plan §3 says `content` is written by the worker, but the admin enqueues preview/sync/eval, clears PII flags, confirms deletions, approves parse rules and site profiles, edits human questions | Admin gets `INSERT` on `Jobs`; decisions are `command` jobs applied by the worker (adds class `command` to worker §2). The one exception: `rag-admin` runs the answer-level judge and the P2 skill gate itself (it holds `Ume.Rag.Answering` and the keys of every level), so it gets `INSERT`/`UPDATE` on `EvaluationRuns` and `EvaluationResults` (§1.2) |
| 3 | Retrieval statistics | Knowledge API counts retrievals but has read-only `content` | `content.rag_count_retrievals(day, document ids, text hashes, collection ids, levels, retrieved counts, cited counts)` `SECURITY DEFINER` upsert of aggregated counters, flushed every 30 s off the request path (knowledge design §4.7; keyed by text hash so counts survive re-chunking) |
| 4 | Tool hub writes | Tool-call audit and definition drift need a writer | `audit."ToolCallEvents"` and `audit."ToolDefinitionObservations"`, `INSERT` for `api` (chat) and `admin` (skill gate, discovery, drift polling), where the in-process hub runs in P2; not the worker |
| 5 | Crypto-shredding vs backups | Deleting a wrapped key in the live database does not reach backups | Bucket keys per (level, expiry month); destroying per-month shred keys reaches backups, switched on only after A1 (§7); check Key Vault purge protection (7–90 days) and OpenBao key deletion settings then |
| 6 | Retention granularity | 90 days per conversation from last activity would keep old messages of long conversations | Per-message `ExpiresAt`; each message under the bucket of its own expiry month |
| 7 | Shared chunks vs dedup | Plan §3 "one set of chunks" vs knowledge §3 "deduplicated by document and chunk text hash" | Chunk sets per source; reuse by caches (§9); retrieval still dedups by (`DocumentId`, `TextHash`) |
| 8 | Chunk level | "Classification lives on collections" vs isolation | A chunk carries its source's level; access by collection; isolation of `sensitive` = separate deployment (§6) |
| 9 | Organisation sync | Data API `catalog.read` has departments and teams but no member groups | Gateway release 2 (P2, weakness review §5) adds `memberGroups` (strings) to `/v1/catalog/departments`; admin syncs every 5 min with a gateway Data API key (`ume-dk-`, `catalog.read` only; identity design §2.1); in `gateway-changes` |
| 10 | Encryption below `sensitive` | §8a requires encryption for `internal`/`sensitive`, optional for `public` | Chat encrypted at every level by default; blobs: `internal` encrypted, `public` not (encrypted disk only) |
| 11 | Retention values missing in §8 | Jobs, run items, runs, observations, caches, outbox, stats, quarantine | Values marked *proposed* in §8–9; add to `Settings` and platform §8 |
| 12 | Job classes | Worker §2 lists six classes | Add `command` (§12.2) |
| 13 | Cache key "node id" | Pipeline-local node ids would prevent sharing | Stage type id + input hash (§9); stage outputs are content-addressed and shared across documents and sources |
| 14 | Query-embedding cache | Plan §8a allows embeddings in Redis; embeddings partly reveal text | No Redis cache for `sensitive` queries (in-process only); resolved by knowledge design §5.4 (L1 in process for all levels with an HMAC key; from P2 an L2 in Redis for `public`/`internal` only, §11; no Redis in P1) |
| 15 | Coverage signal | Knowledge §3 uses "best fused score"; RRF scores are rank-only | Coverage on best cosine distance per collection together with the keyword list (`low` only when the distance is above the threshold and the keyword list is empty or below its floor; knowledge design §7) |
| 16 | Vector storage | `vector` (4 KB per 1024-dim chunk) vs `halfvec` (2 KB) | **Decided (performance pass, [`../archive/passes/performance-pass.md`](../archive/passes/performance-pass.md) §3.3)**: HNSW index on `("Embedding"::halfvec(1024))` from P1, column stays `vector`; recall measured with the evaluation sets (HNSW vs exact) before the first large collection |
| 17 | Run items, frontier, profiles placement | Hold URLs and patterns of possibly `sensitive` sources | Carry `Classification`; parse rules and site profiles apply only within their level |
| 18 | Swedish compounds | Postgres `ispell` per backend costs memory and does not support every Hunspell compound rule | **Decided (simplification pass §3)**: decompound in .NET, built-in `swedish` in Postgres (§3.1); postponed H1 closed |
| 19 | One context | Alternative: a second context per tier | One context, one database (no tiers) |
| 20 | Pool caps | Starting values only | api 12 (`MaxConcurrentSearches` 4), admin 5, worker 10 per instance; measured in P0 and confirmed in the R1 release test (P1); adjust `app-roles.sql` |
| 21 | Conversation review state | No schema owner for a workflow with two approvers | Append-only `audit."ConversationAccess"` events |
| 22 | Key prefixes | Plans say `sk_`/`pk_`, gateway uses `ume-sk-` | `ume-rag-sk-` / `ume-rag-pk-` (gateway shape, cannot be mistaken for a gateway key) |
| 23 | Chat attachments (P2) | Temporary per-conversation collections would put chat-scoped content in `content` | **Resolved** (chat design §15.2, 2026-10-30): chat-scoped tables `chat."Attachments"`/`"AttachmentChunks"` under the conversation's bucket keys; the worker parses but never writes `chat` (chunks posted to the API's internal route) |
| 24 | Assistant links under versioning (simplification pass §15) | `AssistantCollections`/`AssistantSkills` with FKs describe only the current state; a version needs its links frozen, and FKs from old versions would block deleting a collection | **Decided**: links are id arrays in `AssistantVersions` (snapshot, no FK); `ConfigRules` protect what drafts and published versions link; restore re-runs the V-rules (§2.1) |
| 25 | P2 owner-team edit scoping (simplification pass §15) | Needs to know who belongs to an owner team and whether a department wants the limit | Membership from the admin's `teamCodes` claim matched to `Teams.Code` (identity design §1.3), as team-scoped roles already do, so nothing new there; the only new data is the department switch `DepartmentSettings.EditOwnerTeamOnly` (P2) and `Skills.OwnerTeamId` (§2.1) |

## 13. Deferred or removed (simplification pass)

See [`../archive/passes/simplification-pass.md`](../archive/passes/simplification-pass.md) (section numbers there).

| Item | Where it went |
|---|---|
| Platform-wide document store, `DocumentSources`, shared-chunk rule, `CollectionIds` arrays + GIN, set revival, follow runs, `Jobs.RunIds`, trigger `shared-document` (§2) | Removed: per-source documents and chunk sets, scalar `Chunks.CollectionId` + B-tree, reuse by caches |
| Merkle stage hashes, the graph form of `PipelineVersions.Definition` (nodes, edges, locked nodes), the `publishing`/gating state and `PlanJobId` of `PipelineVersions`, `PipelineCanvasLayouts`, `NodeManifest.Ports`, `Pipelines.IsTemplate`/`CopiedFromId` (§2, §8) | Removed: per-stage cache keys; pipeline version = (template id, parameters); plan and report are job results |
| PII flag and counts on `Documents` (§2) | Moved to `ChunkSets`; clearance stays on `Documents` |
| `Variant`, `RenderRecommendedAt`, render leases, the `:r` dedup suffix (§1) | Removed; the renderer (2026-11-07) adds only `Documents.FetchMode` |
| `Documents.ETag`/`LastModified` (§2) | Moved to `CrawlFrontier` (one request serves every source) |
| Hydration keeping only live-set chunks plus a batched follow-up that marks superseded chunks deleted (§9) | Replaced: chunks written with `DeletedAt` set and flipped in the swap transaction (§2.2) |
| `RetrievalSettings.efSearch` and per-query `SET LOCAL` (§9) | Replaced: role defaults `hnsw.ef_search`, `iterative_scan`, `max_scan_tuples` (§1.1) |
| Email attachment columns on `Documents` (§1) | P2 with email formats |
| `content.rag_swedish`, Hunspell in Postgres, custom image (§3) | Removed: .NET decompounder, built-in `swedish`; H1 closed |
| Tiers: resolver, `DataTiers`, `TierName`, main-only/tier-local placement, composite keys and FKs, tier SQL files, move-tier, cross-tier fusion (§5) | Removed: separate deployment if ever needed (T1/K2) |
| Per-conversation keys, epochs, `DeletedByUserAt` tombstone, key-to-message cascades, always-on shred buckets (§5) | Removed: bucket keys per (level, expiry month); shredding behind A1 |
| `DocumentKeys`, per-upload and per-job keys, `BlobPutMode.Overwrite`, per-tier containers (§4) | Removed: key in the blob header, `public` unencrypted, write-once keys, one container |
| Partitioned `Jobs`, advisory-lock dedup, queued-job moves; class `ner` (§9, §1) | Removed: unpartitioned `Jobs` with a partial unique index; NER inside the processing job |
| Gate columns on `Collections`, `EvaluationRuns` and `Settings`; parse auto-apply columns and states (§8, §2) | Removed: advisory harness; P2 proposals with approval |
| `GatewayKeyLinks`; `Clients.TaskModel`/`TaskModelResidency`; `ResolveUsersByEmail` and the `owui-oid` key; GIN on `MemberGroups` (§1, §6, §7) | Removed or dropped |
| Redis `ume-rag:groups:{oid}` (§7) | P2 with the Graph member-group lookup (identity design) |
| Redis in the pilot: `ume-rag:admin-session:{sessionId}`, rate-limit and query-embedding keys (§14) | Admin sessions in `config."AdminSessions"` (also in P2); rate limits and the query-embedding cache in process in P1; Redis from the first multi-host deployment (P2) |
| Skill pin/follow columns (`AssistantSkills.PinnedVersionId`, `PinnedReason`), `SkillVersions` states `gating`/`gate-failed`, `Settings.Tools.SkillRoutingFloor`; `SkillAudiences`; `Assistants.RiskAcceptance` (§10) | Removed (P2 sharing through `CollectionShares`/`SkillShares`) |
| Tool hosting `municipal`/`external`, auth `none`/`api-key`/`obo`, `ToolServers.EncryptedCredential`, `PerCallConnection`; role `ume_rag_tools` (§10) | P3 (with `rag-tools`, the first external, OBO or write tool) |
| `ume_rag_monitor`, `LeaderLeases`, connection-budget script, batched dequeue, debounced `NOTIFY`, Redis crawl and embedding leases (§1) | P2 (first multi-host deployment) |
| 10-second fail-closed config rule (§9) | Replaced by the grace window (§5) |
| `Assistants.Mode` (`rag`/`agent`) as an editable column (§15) | Removed: derived from the published version's tool-bearing skills (§2.1) |
| `AssistantCollections`, `AssistantSkills`; assistant settings edited in place (§15) | Replaced: `AssistantVersions` (draft, published, superseded) with linked collections and skills as a snapshot, the view `PublishedAssistantVersions` for the api |
| `SkillVersionLinks` kind `collection` and the skill level rule over collections (§15) | Removed: skills carry know-how and tools only; an assistant's knowledge is its direct collections |

## 14. To verify (simplification pass)

| Item | Phase | How |
|---|---|---|
| `pgvector/pgvector` tag for pgvector 0.8.7 on Postgres 17 (digest), and that it ships `pg_stat_statements` | P0 | source |
| Recall difference between `SwedishDecompounder` and Postgres `ispell` with the same `sv_SE` word list on a word sample | P0 | measure |
| `INSERT … ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING` infers the partial unique index under concurrent enqueues | P0 | test |
| Pool sizes and role limits of §1.1 (api 12 with `MaxConcurrentSearches` 4, admin 5, worker 10; `max_connections` 100) under the R1 load | P0 (measure), P1 (R1) | measure |
| Swap transaction duration: flipping `DeletedAt` on a set's chunks is a non-HOT update that inserts every newly visible chunk into the partial HNSW and GIN indexes inside the swap; p95 per document (sets of 50–500 chunks) during a large publish, and whether it stays short beside chat traffic | P1 | measure |
| `content.rag_tsquery_or_agg` over the built-in `tsquery_or` on the stock image; `rag_or_query` returns an OR query and `NULL` for stop words only | P0 | test |
| `ALTER ROLE … SET hnsw.*` accepted before the `vector` library is loaded in the session, and visible via `SHOW` on each role's connections | P0 | test |
| Bucket-key creation race: one `DataKeys` row per (level, month) under concurrent first writes | when A1 allows stored content (weakness review §4) | test |
| `PostgresAdminSessionStore` with the copied `AdminTicketStore`: sign-out ends every copy of the cookie, an expired row is not returned, extension stops at 12 h, the retention step deletes expired rows | P0 | test |
| Config snapshot with many assistants: build time and memory of the api snapshot with 300 assistants' published versions (instructions up to 20 KB each), reloaded whole on any assistant change; if a reload exceeds 50 ms, reload by the outbox `EntityId` instead; draft saves do not bump the config version (`WHEN` clauses, §5) | P1 | measure + test |

## 15. Deferred or removed (weakness review)

Per [weakness review §1](../archive/passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer). The schema stays as designed; these tables and columns are built (or first written) in P2:

| Item | Where it went |
|---|---|
| `AssistantTestQuestions`, `AssistantTemplates`; `EvaluationRuns` kinds `answers` and `assistant-tests` | Built in P2 |
| `CrawlClaims` and the shared (level, URL) key of `CrawlFrontier` | Built in P2; P1 keys the frontier per source |
| Learning columns of `SiteProfiles`/`SiteProfileVersions` (`LastLearnedAt`, `NextLearnAfter`, `LearnedFromRunId`, `Sample`, states `proposed`/`rejected`, origin `learned`) | Written from P2; P1 stores `manual` versions only |

Per [weakness review §4](../archive/passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected). The schema stays as designed; these are built (or first written) when A1 allows stored chat content:

| Item | Where it went |
|---|---|
| `chat."DataKeys"`, every chat ciphertext column and its `DataKeyId`/`TitleDataKeyId` | Built when A1 allows stored content; P1 writes metadata and ratings only (history mode `none`, §2.3, §7) |
| `DataProtectionPolicies` history modes `stored`/`session`, `ChatMasterKeyName` keys `rag-chat-<level>` | Same; P1 allows only `none` |
| `audit."ConversationAccess"` (conversation review, comment reveals) | Same |
| `Settings.ChatShreddingEnabled` and shred keys | Same (still also behind A1's answer on deletion) |

Per [weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `Departments.MemberGroups` and `Teams.Code` filled in P1 | P2, from gateway release 2; the columns stay and are empty (or null) until then |
