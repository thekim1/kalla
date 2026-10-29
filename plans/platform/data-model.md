# Platform data model and storage: detailed design

Detail round, step 1. Covers every table, role, index, queue, key and blob path of Ume.Rag. Builds on
[`plan.md`](plan.md) §2–4, §6, §8, §8a and §9 and the data parts of every service plan. Where this document had to choose
something the plans leave open, or found two plans disagreeing, the choice is listed in §12 with a recommendation.

**Gateway conventions mirrored** (`~/git/llmgateway`, user decision: be as compatible as possible):

| Convention | Gateway source | Here |
|---|---|---|
| One EF Core context, Npgsql, `AddNpgsqlDbContext`, production refuses superuser or non-`VerifyFull` TLS | `src/Ume.LlmGateway.Infrastructure/Persistence/GatewayDbContext.cs`, `InfrastructureExtensions.cs` (`AddGatewayDatabase`) | `RagDbContext`, `AddRagDatabase`, same checks |
| Identifiers: EF defaults, **PascalCase, quoted** (`"UsageRecords"."TeamId"`), plural table names, `IX_`/`CK_` names | `GatewayDbContext.cs`, `deploy/postgres/app-roles.sql` | Same, inside lowercase schemas (`content."Chunks"`) |
| Ids `Guid.CreateVersion7()`; append-only logs use `bigint` identity | `Domain/Entities/Entities.cs` (`Department`, `VirtualKey`, `AuditLogEntry`) | Same |
| `DateTimeOffset` stored UTC (`UtcDateTimeOffsetConverter`) → `timestamptz` | `GatewayDbContext.cs` | Same |
| Enums as strings with max length (`HasConversion<string>()`); policies as `jsonb` | `GatewayDbContext.cs` (`PolicyColumn`) | Same; classification is `varchar(20)` `public`/`internal`/`sensitive` |
| One-shot migrator: `MigrateAsync`, then roles SQL from `Migration:RolesSqlPath`, then dev seeder | `src/Ume.LlmGateway.MigrationService/MigrationWorker.cs` | Same, looped over data tiers (§10) |
| Roles: `init.sql` creates login roles from secret files; DDL role owns the database; `app-roles.sql` revokes and re-grants **table by table** in one transaction on every migration run; `DatabaseRoleTests` checks effective grants | `deploy/postgres/init.sql`, `deploy/postgres/app-roles.sql`, `tests/.../DatabaseRoleTests.cs`, `docs/runbook.md` (DB section), `docs/architecture.md` (DDL vs DML roles) | Same files under `deploy/postgres/`, same test |
| Retention only through `SECURITY DEFINER` step functions owned by the DDL role, called nightly by the admin API in batches; DML roles never get `DELETE` on audit | ADR 016 in `docs/adr.md`, `Migrations/20261012060353_DataRetention.cs` (`ume_retention_step`) | §8 |
| Keys: random secret shown once, `KeyHash` = hex HMAC-SHA256 with a pepper outside the DB, display `Prefix`, rotation with `GraceUntil`/`RotatedToKeyId`, Data-Protection-encrypted `EncryptedSecret` for audited reveal | `Domain/Services/VirtualKeyHasher.cs`, `VirtualKey` entity, `Infrastructure/Security/Security.cs`, ADR 004 / 007 | `config."Keys"`, prefixes `ume-rag-sk-` / `ume-rag-pk-` |
| Audit entry columns (`Actor`, `ActorName`, `ActorRoles`, `ActorDepartments`, `SourceAddress`, `SessionId`, `RequestId`, `UserAgent`, masked `Details`) | `AuditLogEntry`, `GatewayDbContext.cs` | `audit."AuditLog"` plus `Reason`, `Service`, `ConfigVersion` |
| Data Protection key ring in the database (`DataProtectionKeys`) | `GatewayDbContext` implements `IDataProtectionKeyContext` | `config."DataProtectionKeys"` |
| Redis: one `RedisKeys` class, all keys under one prefix, ACL user limited to it | `Infrastructure/Stores/RedisKeys.cs`, `deploy/redis/users.acl.example` | `ume-rag:` prefix (§11) |

The gateway has no connection caps, `statement_timeout`, partitioning, schemas or `LISTEN/NOTIFY`; those are new here
(R1, R7) and follow Postgres defaults where nothing better is needed.

## 1. Database layout

One Postgres 17 instance (our image: pgvector 0.8.x + Hunspell `sv_SE`), database **`ragdb`** (gateway: `gatewaydb`),
owned by the DDL role. Extension `vector` is created by `init.sql` as the bootstrap superuser (it is not a trusted
extension). The `public` schema keeps only the extension; `REVOKE ALL ON SCHEMA public FROM PUBLIC` and `USAGE` for the
service roles, as in the gateway.

| Schema | Owner role | Written by | Read by | Tier |
|---|---|---|---|---|
| `config` | `ume_rag_migrator` | admin (plus `KeyLastUse` by api) | api, tools, worker (table subsets) | main only |
| `content` | `ume_rag_migrator` | worker (plus `Jobs` inserts by admin, counters by api via a function) | api (read-only, subset), admin (read) | tier-local, except the main-only tables marked **M** |
| `chat` | `ume_rag_migrator` | api | admin (read; decryption audited) | tier-local |
| `audit` | `ume_rag_migrator` | all (append-only `INSERT`) | admin | main only |

### 1.1 Roles, caps and timeouts

Created by `deploy/postgres/init.sql` from `/run/postgres-secrets/<role>_password` (gateway pattern); `pg_hba.conf`
allows only `hostssl ragdb ume_rag_migrator,ume_rag_admin,ume_rag_api,ume_rag_tools,ume_rag_worker all scram-sha-256`.

| Role | Used by | `CONNECTION LIMIT` | Npgsql `Maximum Pool Size` per instance | `statement_timeout` | `lock_timeout` | `idle_in_transaction_session_timeout` |
|---|---|---|---|---|---|---|
| `ume_rag_migrator` | `rag-migrate` (DDL, owner of everything) | 3 | 2 | 0 (none; index builds) | 10s | 0 |
| `ume_rag_api` | `rag-api` (knowledge, chat, MCP) | 60 | 25 (+1 LISTEN) | 5s | 1s | 10s |
| `ume_rag_tools` | `rag-tools` | 12 | 5 (+1 LISTEN) per slot (two rolling slots fit) | 5s | 1s | 10s |
| `ume_rag_admin` | `rag-admin` | 15 | 6 (+1 LISTEN) per slot (two rolling slots fit) | 30s | 5s | 60s |
| `ume_rag_worker` | `rag-worker` | 14 | 10 (+1 LISTEN) | 60s | 5s | 60s |

`max_connections = 150`; the role limits sum to 104, so the data plane can always connect (R1). Pool sizes are
starting values for the R1 release test. Applied in `init.sql` and re-applied by `app-roles.sql`:

```sql
ALTER ROLE ume_rag_worker CONNECTION LIMIT 14;
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET statement_timeout = '60s';
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET lock_timeout = '5s';
ALTER ROLE ume_rag_worker IN DATABASE ragdb SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE ume_rag_migrator IN DATABASE ragdb SET maintenance_work_mem = '1GB';   -- HNSW builds
```

### 1.2 Grants (`deploy/postgres/app-roles.sql`)

Revoke-and-grant in one transaction on every migration run, tables named one by one, so a new table is unreadable until
listed here (copied from the gateway's file header). `S` select, `I` insert, `U` update, `D` delete, `X` execute.

| Object | admin | api | tools | worker |
|---|---|---|---|---|
| `config` tables except below | S I U D | S on: `Departments`, `Teams`, `Settings`, `DataTiers`, `DataProtectionPolicies`, `EmbeddingProfiles`, `Collections`, `Assistants`, `Assistant*`, `Skills`, `SkillVersions`, `SkillVersionLinks`, `Tools`, `ToolDefinitions`, `ToolApprovals`, `Clients`, `Keys`, `GatewayKeyLinks` | S on: `Settings`, `DataProtectionPolicies`, `Assistants`, `AssistantSkills`, `Skills`, `SkillVersions`, `SkillVersionLinks`, `ToolServers`, `Tools`, `ToolDefinitions`, `ToolApprovals`, `Clients` | S on: `Departments`, `Teams`, `Settings`, `DataTiers`, `DataProtectionPolicies`, `EmbeddingProfiles`, `Collections`, `Sources`, `Pipelines`, `PipelineVersions`, `Assistants`, `AssistantCollections`, `SkillVersions`, `GatewayKeyLinks` |
| `config."ToolServers"` | S I U D | S on every column **except** `EncryptedCredential` | S (all columns) | – |
| `config."PipelineCanvasLayouts"` | S I U D | – | – | – (the worker never reads layouts, enforced) |
| `config."KeyLastUse"` | S | S I U | – | – |
| `config."DataProtectionKeys"` | S I | S | S | S |
| `config."ConfigVersion"`, `"ConfigOutbox"` | S (writes only via trigger, §5) | S | S | S |
| `config."__EFMigrationsHistory"` | S | – | – | – |
| `content` tables | S | S on `Documents`, `ChunkSets`, `Chunks` | – | S I U D (all) |
| `content."Jobs"` | S I | – | – | S I U D |
| `chat` tables | S | S I U D | – | – |
| `audit."AuditLog"` | S I | I | I | I |
| `audit."ConversationAccess"` | S I | – | – | – |
| `audit."ToolCallEvents"`, `"ToolDefinitionObservations"` | S | – | I | – |
| `audit."AuthFailures"` | S I | I | – | – |
| Functions (§4, §5, §8) | X `chat.rag_retention_chat_step`, `audit.rag_retention_audit_step`, `config.rag_retention_config_step` | X `content.rag_count_retrievals` | – | X `content.rag_partition_maintain`, `content.rag_ensure_profile_index` |
| Sequences | `USAGE, SELECT ON ALL SEQUENCES` in the schemas each role writes | | | |

Partitioned tables are only accessed through their parent, so grants on the parent suffice. In a separate `sensitive`
tier database (§6) the same file is applied with the main-only tables left out.

## 2. Tables

Notation: `PK`, `FK`, `U` unique, `IX` index. All `Id uuid` are UUID v7 unless noted. **L** = tier-local, carries
`"Classification" varchar(20) NOT NULL CHECK ("Classification" IN ('public','internal','sensitive'))`, and every FK
between L tables includes `Classification` (§6). Config rows referenced from L tables are referenced **by id without a
FK** (config is main-only).

### 2.1 `config` (owner: admin API)

| Table | Purpose | Key columns | Keys and indexes | Classification rules |
|---|---|---|---|---|
| `Departments` | Mirror of gateway departments, incl. feature C member groups | `Id uuid` (= gateway id), `Name varchar(200)`, `CostCenterCode varchar(50)`, `MemberGroups varchar(256)[]` (group claim values: Entra object ids, Keycloak/AD FS names; identity design §2.2), `IsActive bool`, `SyncedAt` | PK `Id`; U `CostCenterCode`; GIN `MemberGroups` | – |
| `Teams` | Mirror of gateway teams (attribution `x-ume-on-behalf-of` sends `Id`) | `Id uuid` (= gateway), `DepartmentId`, `Name`, `Code varchar(100)` (from `/v1/catalog/teams`, gateway release C; display only), `IsActive`, `SyncedAt` | FK `DepartmentId` → `Departments` (Restrict, as gateway); U `Code` | – |
| `Settings` | Singleton: platform defaults (deletion guard 20 %, crawl politeness, gate threshold 3 points, auto-apply ≥ 0.9 / ≥ 20 docs / 10 % trial, **parse auto-apply kill switch**, retention values from §8, `AuditRetentionDays` 730, `RetentionLastRun*`) | `Id smallint` | `CK_Settings_Singleton` (`"Id" = 1`), range checks like `CK_GatewaySettings_*` | – |
| `DataTiers` | Data tiers known to the tier resolver | `Name varchar(30)` (`main`, `sensitive`), `ConnectionName varchar(50)` (`ragdb`, `ragdb-sensitive`: names of connection strings in env, never secrets), `BlobContainer varchar(63)`, `State` (`active`/`migrating`) | PK `Name` | – |
| `DataProtectionPolicies` | §8a matrix, one row per level | `Classification` PK, `AllowedHistoryModes varchar(20)[]`, `DefaultHistoryMode`, `RetentionDefaultDays int`, `RetentionMaxDays int`, `EncryptionRequired bool`, `ReviewSecondApprover bool`, `ClientsWithoutHistoryOnly bool`, `TierName` FK → `DataTiers`, `ChatMasterKeyName`, `BlobMasterKeyName` | `CK_…_Encryption` (`internal`/`sensitive` ⇒ `EncryptionRequired`); `CK_…_Retention` (default ≤ max ≤ 90) | Defines them |
| `EmbeddingProfiles` | R3 registry | `Id`, `Name`, `GatewayModel varchar(200)` (`bge-m3`), `Dimensions int` (1024), `Distance` (`cosine`), `MaxInputTokens` (8192), `IsDefault`, `State` (`active`/`building`/`retired`) | U `Name`; partial U `IsDefault WHERE "IsDefault"` | Always on-prem (R3) |
| `Collections` | Search scope, access and classification | `Id`, `Slug varchar(100)`, `Name`, `DepartmentId`, `OwnerTeamId`, `Classification`, `EmbeddingProfileId` FK, `RetrievalSettings jsonb` (weights, top-k, coverage threshold), `GateSettings jsonb`, `DeletedAt` | U `Slug`; IX `DepartmentId` | ≥ every source's level (validated on save) |
| `Sources` | One connector instance; belongs to one collection | `Id`, `CollectionId` FK, `Type` (`url-list`/`crawl`/`upload`/`sharepoint`), `Name`, `Classification`, `Settings jsonb` (fields tagged identity/scope/behaviour/cosmetic), `Schedule varchar(100)` (cron), `PipelineId` FK, `State` (`active`/`paused`), `DeletedAt` | IX `CollectionId`; IX `PipelineId` | ≤ collection level |
| `Pipelines` | Pipeline or template | `Id`, `DepartmentId` (null = platform template), `Name`, `IsTemplate`, `CopiedFromId`, `PublishedVersionId` FK → `PipelineVersions` (nullable) | U (`DepartmentId`, `Name`) | – |
| `PipelineVersions` | Immutable once published | `Id`, `PipelineId` FK, `Number int`, `State` (`draft`/`published`/`superseded`), `Definition jsonb` (nodes `id`/`type`/`typeVersion`/`params`/`locked`, edges), `DocumentStageHash bytea`, `CollectionStageHash bytea`, `CreatedBy`, `PublishedAt`, `GateRunId uuid` (→ content, no FK) | U (`PipelineId`, `Number`) | – |
| `PipelineCanvasLayouts` | Canvas positions, stored apart from the definition | `PipelineVersionId` PK/FK (Cascade), `Layout jsonb`, `UpdatedAt` | – | – |
| `Assistants` | Assistant = OpenAI model | `Id`, `Slug varchar(100)` (model name), `Name`, `DepartmentId`, `OwnerTeamId`, `Mode` (`rag`/`agent`), `Classification`, `GatewayModel`, `SystemPrompt text`, `QuerySteps jsonb`, `HistoryMode`, `RetentionDays int`, `AllowedOrigins text[]`, `NotFoundText`, `RiskAcceptance jsonb` (tools §6 rule 2), `State` | U `Slug` | ≥ linked collections, skills, tool servers; history/retention within the level's policy |
| `AssistantCollections` | Direct collections | `AssistantId`, `CollectionId` | PK both; FKs Cascade/Restrict | as above |
| `AssistantSkills` | Skills of an assistant | `AssistantId`, `SkillId`, `PinnedVersionId` (null = follow served) | PK (`AssistantId`, `SkillId`) | as above |
| `AssistantAudiences` | Who may use it | `AssistantId`, `Kind` (`client`/`department`/`group`), `SubjectId uuid` (client or department id; null for `group`), `GroupValue varchar(256)` (`group` only; identity design §2.2) | U (`AssistantId`, `Kind`, `coalesce(SubjectId::text, GroupValue)`); `CK_` exactly one of `SubjectId`/`GroupValue` | P1 uses `client` only |
| `Skills` | Skill identity and **served pointer** | `Id`, `Slug`, `Name`, `DepartmentId` (null = platform), `Classification`, `ServedVersionId` FK → `SkillVersions` | U `Slug` | – |
| `SkillVersions` | Immutable SemVer versions | `Id`, `SkillId` FK, `Version varchar(50)`, `VersionMajor/Minor/Patch int`, `Description`, `Instructions text`, `Manifest jsonb`, `ReferenceBlobPrefix`, `TestQuestions jsonb`, `State`, `PublishedAt`, `GateRunId` | U (`SkillId`, `Version`); `CK_` increasing via app | – |
| `SkillVersionLinks` | Linked collections and tools | `SkillVersionId`, `Kind` (`collection`/`tool`), `TargetId` | PK all three | skill level ≥ links |
| `ToolServers` | Registered MCP servers | `Id`, `Name`, `Url varchar(500)`, `Hosting` (`external`/`hosted`), `AuthMode` (`none`/`api-key`/`client-credentials`/`obo`), `EncryptedCredential varchar(4000)` (Data Protection, like gateway `ProviderAccount`), `Classification`, `Residency` (`OnPrem`/`EU`/`Any`), `TimeoutMs`, `MaxResponseBytes`, `OwnerDepartmentId`, `SystemOwner`, `InstructionsSnapshot text` (admin only), `State` | U `Name` | assistant level ≤ server level; residency per §6 |
| `Tools` | One tool | `Id`, `ToolServerId` FK, `Name`, `RiskLevel` (`read`/`write`), `IsAllowed`, `RunsWithoutConfirmation`, `PinnedDefinitionId` FK, `State` (`active`/`pending-approval`/`disabled-changed`) | U (`ToolServerId`, `Name`) | `write` refused while write tools are off |
| `ToolDefinitions` | Pinned definitions | `Id`, `ToolId` FK, `DefinitionHash bytea` (SHA-256 of canonical JSON), `Description text`, `InputSchema jsonb`, `OutputSchema jsonb`, `CapturedAt` | U (`ToolId`, `DefinitionHash`) | – |
| `ToolApprovals` | Two approvals per definition (tools §9.2) | `ToolDefinitionId` FK, `ApproverKind` (`platform-admin`/`system-owner`), `Approver` (`sub`), `ApprovedAt` | PK (`ToolDefinitionId`, `ApproverKind`) | tool usable only with both rows |
| `Clients` | Client registry (R5) | `Id`, `Name`, `Kind` (`open-webui`/`widget`/`integration`/`entra-app`/`mcp-client`/`own-client`/`teams`/`internal`), `OAuthClientId varchar(200)` (`azp`/`appid`/`client_id`; strings for Keycloak), `ClassificationCeiling`, `StoresHistory bool`, `ClientRetentionDays int`, `TaskModel`, `TaskModelResidency`, `AllowedOrigins text[]`, `ResolveUsersByEmail bool` (P2, identity design §5.4), `IsEnabled` | U `OAuthClientId` (nulls distinct); `CK_Clients_History` (`StoresHistory` ⇒ ceiling ≠ `sensitive`); `CK_Clients_Mcp` (`mcp-client` ⇒ ceiling ≠ `sensitive`) | ceiling caps every assistant it may use |
| `Keys` | Gateway-style keys | as gateway `VirtualKey`: `Id`, `ClientId` FK, `TeamId`, `Kind` (`sk`/`pk`), `Name`, `Prefix varchar(20)` (`ume-rag-sk-AbC1`), `KeyHash varchar(64)` (hex HMAC-SHA256, pepper `Security__KeyPepper`), `EncryptedSecret`, `Scopes jsonb` (assistants/collections), `AllowedOrigins`, `AllowedIpRanges`, `RequestsPerMinute`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `ExpiresAt`, `RevokedAt`, `GraceUntil`, `RotatedToKeyId` | U `KeyHash`; concurrency tokens `RotatedToKeyId`, `RevokedAt` (as gateway) | key's client ceiling applies |
| `KeyLastUse` | `LastUsedAt` split out so key use never bumps the config version | `KeyId` PK, `LastUsedAt` | – | – |
| `GatewayKeyLinks` | Interim per-team gateway keys (R4 interim) | `Id`, `TeamId`, `Purpose` (`default`/`onprem`), `EncryptedKey` (Data Protection), `Prefix`, `VerifiedAt` | U (`TeamId`, `Purpose`) | `onprem` used for `sensitive` until gateway release B (ceiling header); table unused after release A (platform service key) and dropped one release later (gateway design §13) |
| `DataProtectionKeys` | ASP.NET Data Protection key ring | EF standard | – | – |
| `ConfigVersion`, `ConfigOutbox` | R7 (§5) | | | |

**Organisation mirror.** The admin API syncs `Departments`/`Teams` from the gateway Data API (`catalog.read`,
`GET /v1/catalog/departments`, `/teams`; `docs/data-access.md`) every 5 minutes and on demand; only changed rows are
written, so a sync without changes does not bump the config version. Member groups need a catalogue field (§12.9).

### 2.2 `content` (owner: worker)

| Table | Tier | Purpose | Key columns | Keys and indexes |
|---|---|---|---|---|
| `WorkerInstances` | **M** | Running workers (heartbeat 15 s) | `InstanceId uuid`, `BuildVersion`, `StartedAt`, `LastHeartbeatAt` | PK |
| `NodeManifest` | **M** | Registered node types (R7) | `NodeType varchar(100)`, `TypeVersion int`, `Ports jsonb`, `ParamsSchema jsonb`, `FirstRegisteredAt` | PK (`NodeType`, `TypeVersion`) |
| `WorkerNodeSupport` | **M** | Which live instance supports which version; publish refused unless a row exists with heartbeat < 60 s | `InstanceId` FK (Cascade), `NodeType`, `TypeVersion` | PK all three |
| `Jobs` | **M** | Queue, partitioned by day (§4) | see §4 | |
| `CrawlHosts` | **M** | Shared politeness budget per host | `Host varchar(253)` PK, `RequestsPerSecond numeric(5,2)`, `MaxConnections`, `NextAllowedAt`, `RobotsBlobPath`, `RobotsFetchedAt` | – |
| `CrawlFrontier` | L | Shared crawl frontier | `Classification`, `UrlHash bytea`, `Url text`, `Host`, `Depth`, `SourceIds uuid[]`, `State`, `NextFetchAt` | PK (`Classification`, `UrlHash`); IX (`Host`, `NextFetchAt`) `WHERE "State"='pending'` |
| `Documents` | L | **Document store**: one row per (level, canonical identity) | `Id`, `Classification`, `IdentityKind` (`url`/`sharepoint`/`file`/`attachment`), `ParentDocumentId uuid NULL` (email attachments, parser design §5.4), `AttachmentOrdinal smallint NULL`, `Depth smallint NOT NULL DEFAULT 0` (≤ 2), `CanonicalIdentity text` (attachment: `<parent identity>#att/<ordinal>/<sha256>`), `IdentityHash bytea` (SHA-256), `ContentHash bytea`, `Title`, `Url`, `MediaType`, `Language`, `SourceModifiedAt`, `FetchedAt`, `ETag`, `LastModified`, `ParseStrategy`, `ParseProfileId`, `ParseOutcome`, `ParseCacheKey bytea`, `PageCount`, `PiiCounts jsonb`, `PiiFlaggedAt`, `PiiClearedAt`, `PiiClearedBy`, `DeletedAt`, `PurgeAfter` | PK `Id`; **U (`Id`, `Classification`)** (FK target); **U (`Classification`, `IdentityKind`, `IdentityHash`)** = "shared only within the same level"; IX `PurgeAfter WHERE "DeletedAt" IS NOT NULL`; IX `PiiFlaggedAt WHERE "PiiClearedAt" IS NULL`; FK (`ParentDocumentId`, `Classification`) → `Documents` **Cascade**; children mirror the parent's `DocumentSources` rows |
| `DocumentKeys` | L | Wrapped blob data key per document (§7) | `DocumentId`, `Classification`, `WrappedKey bytea`, `MasterKeyName`, `MasterKeyVersion`, `CreatedAt` | PK `DocumentId`; FK (`DocumentId`, `Classification`) → `Documents` Cascade |
| `DocumentSources` | L | Which sources reference a document and which chunk set their pipeline produced | `DocumentId`, `SourceId` (no FK), `Classification`, `ChunkSetId`, `PipelineVersionId`, `FirstSeenAt`, `LastSeenAt`, `DeletedAt`, `PurgeAfter` | PK (`DocumentId`, `SourceId`); FK (`DocumentId`, `Classification`) → `Documents`; FK (`ChunkSetId`, `Classification`) → `ChunkSets`; IX (`SourceId`) `WHERE "DeletedAt" IS NULL` |
| `NodeOutputs` | L | Output cache index (blobs hold the data, §9) | `CacheKey bytea` PK, `Classification`, `DocumentId`, `NodeType`, `NodeVersion`, `ParamsHash bytea`, `InputHash bytea`, `BlobPath` (a prefix for multi-blob outputs such as the parse output: `result.json`, `docling/*.json.gz`), `IsParseOutput bool`, `CreatedAt`, `ExpiresAt` (null for the current parse output) | FK (`DocumentId`, `Classification`) → `Documents` Cascade; IX `ExpiresAt` |
| `ChunkSets` | L | One collection-stage output per (document, collection-stage hash, profile); shared by every collection whose pipeline matches (R2) | `Id`, `DocumentId`, `Classification`, `CollectionStageHash bytea`, `EmbeddingProfileId`, `ContentHash`, `CollectionIds uuid[]` (authoritative), `ChunkCount`, `CreatedAt`, `DeletedAt` | U (`Id`, `Classification`); U (`DocumentId`, `CollectionStageHash`, `EmbeddingProfileId`, `ContentHash`); FK (`DocumentId`, `Classification`) → `Documents` Cascade |
| `Chunks` | L | **Collection index**: parents and children | `Id`, `ChunkSetId`, `DocumentId`, `Classification`, `Kind` (`parent`/`child`), `ParentId`, `Ordinal int`, `HeadingPath text`, `Text text`, `TextHash bytea`, `TokenCount int`, `Page int`, `Tags text[]`, `DocumentType varchar(30)`, `SourceModifiedAt`, `CollectionIds uuid[]` (copy of the set's), `EmbeddingProfileId uuid` (children only), `Embedding vector` (children only, untyped so profiles may differ in size), `SearchVector tsvector` (generated, §3), `DeletedAt` | PK `Id`; FK (`ChunkSetId`, `Classification`) → `ChunkSets` Cascade; FK (`DocumentId`, `Classification`) → `Documents`; IX `ParentId`; HNSW and GIN per §3 |
| `EmbeddingCache` | L | (profile, text hash) → embedding, so re-chunking embeds only new text | `Classification`, `EmbeddingProfileId`, `TextHash bytea`, `Embedding vector`, `LastUsedAt` | PK (`Classification`, `EmbeddingProfileId`, `TextHash`) |
| `Runs` | L | One per sync/reprocess | `Id`, `SourceId`, `Classification`, `Kind` (`sync`/`retry-failed`/`reprocess`), `Trigger`, `PipelineVersionId`, `State` (`running`/`succeeded`/`degraded`/`failed`/`awaiting-deletion-confirmation`), `Added/Updated/Unchanged/Deleted/Failed int`, `ProposedDeletions int`, `CostUsd numeric(18,8)`, `StartedAt`, `FinishedAt` | IX (`SourceId`, `StartedAt` DESC) |
| `RunItems` | L | One per document per run, partitioned by day (§4) | `Id bigint identity`, `CreatedAt` (partition key), `RunId`, `Classification`, `DocumentId`, `Identity text`, `Outcome`, `Reason varchar(500)`, `Strategy`, `ParseProfileId`, `ExecutionId uuid`, `DurationMs`, `CostUsd`, `ParserBuildHash bytea` (parser `/v1/info` build; full info in `content."ParserBuilds"` (`BuildHash` PK, `Info jsonb`, `FirstSeenAt`), main tier) | PK (`CreatedAt`, `Id`); IX (`RunId`) |
| `ParseObservations` | L | Learning input | `Id`, `Classification`, `DocumentId`, `Domain`, `PathPattern`, `DocumentType`, `Strategy`, `Score numeric(5,4)`, `Metrics jsonb`, `ObservedAt` | IX (`Domain`, `PathPattern`, `DocumentType`) |
| `ParseProfiles` | L | Learned or manual profiles | `Id`, `Classification`, `DepartmentId` (scope), `Match jsonb`, `Strategy jsonb`, `CleanupRules jsonb`, `Origin` (`learned`/`manual`), `State` (`proposed`/`trial`/`active`/`rolled-back`/`disabled`), `Confidence numeric(4,3)`, `SampleCount`, `TrialResult jsonb`, `GateRunId`, `AppliedAt`, `RolledBackAt` | IX (`State`) |
| `EvaluationSets` | L | One per collection | `Id`, `CollectionId` (no FK), `Classification`, `CreatedAt` | U `CollectionId`; U (`Id`, `Classification`) |
| `EvaluationQuestions` | L | Layers generated / skill / human | `Id`, `SetId`, `Classification`, `Layer`, `Question text`, `ExpectedAnswer text`, `ExpectedDocumentIds uuid[]`, `SourceChunkId`, `SkillVersionId`, `CreatedBy`, `RetiredAt` | FK (`SetId`, `Classification`) → `EvaluationSets` Cascade; IX (`SetId`) `WHERE "RetiredAt" IS NULL` |
| `EvaluationRuns` | L | One gate or scheduled run | `Id`, `SetId`, `Classification`, `Trigger` (`pipeline-publish`/`profile-auto-apply`/`retrieval-change`/`embedding-change`/`skill-publish`/`scheduled`), `SubjectId`, `SubjectVersion`, `BaselineRunId`, `K int`, `SampleSize`, `RecallAtK numeric(5,4)`, `Mrr numeric(5,4)`, `Faithfulness numeric(5,4)`, `ThresholdPoints numeric(4,2)`, `GatePassed bool`, `StartedAt`, `FinishedAt` | FK as above; IX (`SetId`, `StartedAt` DESC) |
| `EvaluationResults` | L | Per question per run | `RunId`, `QuestionId`, `Classification`, `ExpectedRank int` (null = not found), `Hit bool`, `Faithfulness numeric(5,4)` | PK (`RunId`, `QuestionId`); FK (`RunId`, `Classification`) Cascade |
| `RetrievalStats` | L | Counters only, never query text (knowledge §6.2) | `Day date`, `ChunkId`, `CollectionId`, `Classification`, `Retrieved int`, `Cited int` | PK (`Day`, `ChunkId`, `CollectionId`) |

Shared-chunk rule: the worker keeps `ChunkSets."CollectionIds"` = distinct collections of the live `DocumentSources`
pointing at the set, **minus `public` collections while the document's PII flag is not cleared** (worker §4), and
copies it to `Chunks."CollectionIds"` in the same transaction. A set whose array becomes empty gets `DeletedAt`
(14-day grace, restorable without re-processing).

**Known cost (review):** `Chunks."CollectionIds"` has a GIN index, so changing it is never a HOT update: every chunk
of the set gets a new tuple and a new HNSW entry. Adding a large shared site to another department's collection
therefore re-inserts all its vectors. Acceptable at pilot scale (< 100k chunks); the worker applies such changes in
`command` jobs in batches of 1,000 rows, outside office hours for sets above 10,000 chunks. If it becomes a problem,
the alternative is a `ChunkSetCollections` mapping table with the collection filter moved after the HNSW scan.

### 2.3 `chat` (owner: Chat API; all L)

| Table | Purpose | Key columns (ciphertext in **bold**) | Keys and indexes |
|---|---|---|---|
| `Conversations` | One conversation | `Id`, `Classification` (assistant level at creation), `AssistantId`, `ClientId`, `SubjectKind` (`entra-user`/`owui-user`/`anonymous`/`application`), `SubjectId varchar(200)` (oid, Open WebUI `sub`, or HMAC of the widget session), `ExternalChatId varchar(200)` (e.g. `X-OpenWebUI-Chat-Id`), `HistoryMode`, `RetentionDays`, **`TitleCiphertext bytea`**, `TitleDataKeyId`, `CreatedAt`, `LastMessageAt`, `ExpiresAt`, `DeletedByUserAt` | U (`Id`, `Classification`); U (`ClientId`, `SubjectId`, `ExternalChatId`) (chat §8.1); IX (`SubjectId`, `LastMessageAt` DESC); IX `ExpiresAt` |
| `DataKeys` | Wrapped per-conversation data keys, one per 7-day **key epoch** (§7) | `Id`, `ConversationId`, `Classification`, `WrappedKey bytea`, `KeyProvider`, `MasterKeyName varchar(100)` (shred bucket, §7), `MasterKeyVersion varchar(100)`, `CreatedAt`, `LastUsedAt`, `ExpiresAt` | U (`Id`, `Classification`); FK (`ConversationId`, `Classification`) Cascade; IX `ExpiresAt`; IX `MasterKeyName` |
| `Messages` | User/assistant turns | `Id`, `ConversationId`, `Classification`, `Seq int`, `Role`, **`ContentCiphertext bytea`** (null in mode `none`), `DataKeyId`, `GatewayModel`, `GatewayRequestId`, `ObservedResidency`, `InputTokens`, `OutputTokens`, `CostUsd numeric(18,8)`, `LatencyMs`, `TimeToFirstTokenMs`, `Outcome` (`answered`/`not-found`/`refused`/`error`), `CreatedAt` | PK; U (`ConversationId`, `Seq`); FK (`ConversationId`, `Classification`) Cascade; FK (`DataKeyId`, `Classification`) → `DataKeys` **Cascade** (shredding a key deletes what it encrypted) |
| `MessageSources` | Citations with a snapshot of the cited text | `MessageId`, `Rank int`, `Classification`, `DocumentId`, `ChunkId`, `CollectionId` (ids only, no FK), **`SnippetCiphertext`**, `DataKeyId`, `Used bool` | PK (`MessageId`, `Rank`); FKs Cascade |
| `Feedback` | Thumbs + comment | `Id`, `MessageId`, `ConversationId`, `Classification`, `Rating smallint` (−1/1), **`CommentCiphertext`**, `DataKeyId`, `ReviewState`, `CreatedAt` | FK (`MessageId`, `Classification`) Cascade; IX (`ReviewState`) `WHERE "ReviewState" = 'open'` |
| `ToolCalls` | Full tool arguments and results (tools §3: only here, under conversation retention) | `Id`, `MessageId`, `Classification`, `ToolServerId`, `ToolName`, **`ArgumentsCiphertext`**, **`ResultCiphertext`**, `DataKeyId`, `ArgumentsHmac bytea`, `ArgumentsBytes`, `ResultBytes`, `DurationMs`, `Outcome`, `CreatedAt` | FK (`MessageId`, `Classification`) Cascade |
| `AssistantDailyTotals` | Statistics that outlive conversations (§8) | `Day date` (Europe/Stockholm), `AssistantId`, `ClientId`, `DepartmentId`, `Classification`, `Conversations`, `Messages`, `NotFound`, `ThumbsUp`, `ThumbsDown`, `InputTokens bigint`, `OutputTokens bigint`, `CostUsd numeric(18,8)` | PK (`Day`, `AssistantId`, `ClientId`, `Classification`) |

The background writer upserts `AssistantDailyTotals` in the transaction that inserts the message, so statistics never
depend on rows that retention later removes.

### 2.4 `audit` (all services append; main only)

| Table | Purpose | Key columns | Indexes |
|---|---|---|---|
| `AuditLog` | Changes and privileged reads | gateway `AuditLogEntry` columns (`Id bigint identity`, `Timestamp`, `Actor`, `Action`, `EntityType`, `EntityId`, `Details jsonb` masked, `ActorName`, `ActorRoles`, `ActorDepartments`, `SourceAddress`, `SessionId`, `RequestId`, `UserAgent`, `RecordedAt`) + `Service varchar(30)`, `Reason varchar(1000)`, `ConfigVersion bigint`, `Classification` | IX `Timestamp`; IX (`EntityType`, `EntityId`) |
| `ConversationAccess` | Admin review workflow as append-only events: `requested` (reason), `approved` (second approver, `sensitive`), `decrypted` | `Id bigint identity`, `RequestId uuid`, `Event`, `ConversationId`, `Classification`, `Actor`, `Reason`, `Timestamp` | IX `RequestId`; `CK_` approver ≠ requester enforced by app + test |
| `ToolCallEvents` | Tool hub audit (tools §3): metadata and HMAC of arguments | `Id bigint identity`, `Timestamp`, `AssistantId`, `ToolId`, `SubjectId`, `ClientId`, `ConversationId`, `DurationMs`, `Outcome`, `ArgumentsBytes`, `ResultBytes`, `ArgumentsHmac bytea` | IX `Timestamp`; IX (`ToolId`, `Timestamp`) |
| `AuthFailures` | Refused credentials, aggregated per 10 s bucket (identity design §6.3) | gateway `AuthFailure` columns + `Credential varchar(20)`, `ClientId uuid`; never the presented value | IX `FirstSeen` |
| `ToolDefinitionObservations` | Tool hub saw a definition differing from the pinned one (tool disabled in memory at once, re-approval shown in admin) | `Id bigint identity`, `ToolId`, `ObservedHash bytea`, `Definition jsonb`, `ObservedAt` | IX (`ToolId`, `ObservedAt`) |

Security events are not stored: like the gateway (ADR 015) they go to OpenTelemetry in `Ume.Rag.Security`.

## 3. Search indexes and the hybrid query

### 3.1 Swedish text search configuration `content.rag_swedish`

The image installs `sv_se.dict` / `sv_se.affix` (LibreOffice `sv_SE` Hunspell, converted to UTF-8) into
`$SHAREDIR/tsearch_data/`. Created by a migration (the migrator may create dictionaries from built-in templates):

```sql
CREATE TEXT SEARCH DICTIONARY content.rag_swedish_hunspell (
    TEMPLATE = ispell, DictFile = sv_se, AffFile = sv_se, StopWords = swedish);
CREATE TEXT SEARCH DICTIONARY content.rag_swedish_stem (
    TEMPLATE = snowball, Language = swedish, StopWords = swedish);
CREATE TEXT SEARCH CONFIGURATION content.rag_swedish (COPY = pg_catalog.swedish);
ALTER TEXT SEARCH CONFIGURATION content.rag_swedish
    ALTER MAPPING FOR asciiword, word, hword, hword_part, hword_asciipart, asciihword
    WITH content.rag_swedish_hunspell, content.rag_swedish_stem;
```

Hunspell splits compounds (`bygglovsansökan` → `bygglov`, `ansökan`); unknown words fall through to the Snowball
stemmer. P0 test: `SELECT to_tsvector('content.rag_swedish', 'bygglovsansökan')` contains `bygglov`. Ispell
dictionaries load per backend on first use (tens of MB, a few hundred ms), so pools are warmed at startup and
connections are long-lived.

### 3.2 Columns and indexes on `content."Chunks"`

```sql
ALTER TABLE content."Chunks" ADD COLUMN "SearchVector" tsvector GENERATED ALWAYS AS (
    CASE WHEN "Kind" = 'child' THEN
        setweight(to_tsvector('content.rag_swedish'::regconfig, coalesce("HeadingPath", '')), 'A') ||
        setweight(to_tsvector('content.rag_swedish'::regconfig, "Text"), 'B')
    END) STORED;

CREATE INDEX "IX_Chunks_SearchVector" ON content."Chunks" USING gin ("SearchVector")
    WHERE "Kind" = 'child' AND "DeletedAt" IS NULL;
CREATE INDEX "IX_Chunks_CollectionIds" ON content."Chunks" USING gin ("CollectionIds")
    WHERE "DeletedAt" IS NULL;
CREATE INDEX "IX_Chunks_Tags" ON content."Chunks" USING gin ("Tags") WHERE "DeletedAt" IS NULL;

-- One partial HNSW index per embedding profile; the platform profile BGE-M3 (1024 dims) is seeded with a fixed id.
CREATE INDEX "IX_Chunks_Embedding_bge_m3" ON content."Chunks"
    USING hnsw (("Embedding"::vector(1024)) vector_cosine_ops) WITH (m = 16, ef_construction = 64)
    WHERE "EmbeddingProfileId" = '01929b6e-0000-7000-8000-00000000b6e3' AND "DeletedAt" IS NULL;
```

A new profile gets its index from `content.rag_ensure_profile_index(profile_id uuid, dims int)` (`SECURITY DEFINER`,
owner migrator, called by the worker) **before** any row of that profile exists, so the build is instant and needs no
`CONCURRENTLY`. A dictionary change requires regenerating `SearchVector` (`UPDATE … SET "Text" = "Text"` in batches, a
release-notes item).

### 3.3 The hybrid retrieval query (one round trip)

Parameters: `$1 vector(1024)` query embedding, `$2 text` query, `$3 uuid[]` collection ids (already access- and
ceiling-checked in memory), `$4 text[]` allowed levels (defence in depth), `$5 text[]` tags or null, `$6 text[]`
document types or null, `$7 timestamptz` modified after or null, `$8 int` top-k, `$9`/`$10 float8` vector/keyword
weights. The profile id is **inlined as a literal** (from config, never user input), so the planner can match the
partial index under a generic plan.

```sql
BEGIN READ ONLY;
SET LOCAL hnsw.ef_search = 100;
SET LOCAL hnsw.iterative_scan = relaxed_order;   -- pgvector 0.8: keep scanning until filters yield enough rows
WITH vec AS (
    SELECT v."Id", v."DocumentId", v."TextHash", v.distance,
           row_number() OVER (ORDER BY v.distance) AS rnk
    FROM (
        SELECT c."Id", c."DocumentId", c."TextHash",
               c."Embedding"::vector(1024) <=> $1 AS distance
        FROM content."Chunks" c
        WHERE c."EmbeddingProfileId" = '01929b6e-0000-7000-8000-00000000b6e3'
          AND c."DeletedAt" IS NULL
          AND c."CollectionIds" && $3
          AND c."Classification" = ANY ($4)
          AND ($5::text[] IS NULL OR c."Tags" && $5)
          AND ($6::text[] IS NULL OR c."DocumentType" = ANY ($6))
          AND ($7::timestamptz IS NULL OR c."SourceModifiedAt" >= $7)
        ORDER BY c."Embedding"::vector(1024) <=> $1
        LIMIT 100
    ) v
),
kw_query AS (   -- OR semantics: plainto_tsquery ANDs lexemes, so the operators are swapped
    SELECT replace(plainto_tsquery('content.rag_swedish', $2)::text, ' & ', ' | ')::tsquery AS tsq
),
kw AS (
    SELECT k."Id", k."DocumentId", k."TextHash",
           row_number() OVER (ORDER BY k.score DESC) AS rnk
    FROM (
        SELECT c."Id", c."DocumentId", c."TextHash", ts_rank_cd(c."SearchVector", q.tsq) AS score
        FROM content."Chunks" c, kw_query q
        WHERE c."SearchVector" @@ q.tsq
          AND c."Kind" = 'child' AND c."DeletedAt" IS NULL
          AND c."EmbeddingProfileId" = '01929b6e-0000-7000-8000-00000000b6e3'
          AND c."CollectionIds" && $3
          AND c."Classification" = ANY ($4)
          AND ($5::text[] IS NULL OR c."Tags" && $5)
          AND ($6::text[] IS NULL OR c."DocumentType" = ANY ($6))
          AND ($7::timestamptz IS NULL OR c."SourceModifiedAt" >= $7)
        ORDER BY score DESC
        LIMIT 100
    ) k
),
fused AS (
    SELECT COALESCE(v."Id", k."Id") AS "Id",
           COALESCE(v."DocumentId", k."DocumentId") AS "DocumentId",
           COALESCE(v."TextHash", k."TextHash") AS "TextHash",
           v.distance, v.rnk AS vec_rank, k.rnk AS kw_rank,
           COALESCE($9 / (60 + v.rnk), 0) + COALESCE($10 / (60 + k.rnk), 0) AS rrf
    FROM vec v FULL OUTER JOIN kw k ON k."Id" = v."Id"
),
dedup AS (      -- same page indexed by two pipelines appears once (knowledge §3)
    SELECT DISTINCT ON ("DocumentId", "TextHash") *
    FROM fused
    ORDER BY "DocumentId", "TextHash", rrf DESC
)
SELECT d."Id", d.rrf, d.distance, d.vec_rank, d.kw_rank,
       c."Text", c."HeadingPath", c."Page", c."ParentId", p."Text" AS "ParentText",
       doc."Id" AS "DocumentId", doc."Title", doc."Url", doc."IdentityKind", doc."SourceModifiedAt"
FROM dedup d
JOIN content."Chunks" c ON c."Id" = d."Id"
JOIN content."Documents" doc ON doc."Id" = c."DocumentId" AND doc."Classification" = c."Classification"
LEFT JOIN content."Chunks" p ON p."Id" = c."ParentId" AND p."Classification" = c."Classification"
ORDER BY d.rrf DESC
LIMIT $8;
COMMIT;
```

`expand=none` drops the parent join; the API returns each parent text once. The coverage signal uses `distance` of the
best hit, not the RRF score (§12.15). An assistant spanning tiers or profiles runs this once per (tier, profile) and
fuses the lists with RRF in the API (plan §8a, knowledge §3).

## 4. Job queue

### 4.1 Table

```sql
CREATE TABLE content."Jobs" (
    "Id"            uuid        NOT NULL,                 -- v7
    "EnqueuedAt"    timestamptz NOT NULL DEFAULT now(),   -- partition key
    "Class"         varchar(20) NOT NULL CHECK ("Class" IN ('preview','command','sync','fetch','parse','embed','eval')),
    "Priority"      smallint    NOT NULL DEFAULT 0,       -- within a class; higher first
    "DepartmentId"  uuid        NOT NULL,                 -- fairness and attribution
    "TeamId"        uuid,
    "Classification" varchar(20) NOT NULL,                -- selects the tier the job works on
    "DedupKey"      varchar(200),
    "Payload"       jsonb       NOT NULL,                 -- ids and parameters only, never content
    "State"         varchar(20) NOT NULL DEFAULT 'queued'
                    CHECK ("State" IN ('queued','running','succeeded','failed','dead','cancelled')),
    "AvailableAt"   timestamptz NOT NULL DEFAULT now(),
    "Attempts"      smallint    NOT NULL DEFAULT 0,
    "MaxAttempts"   smallint    NOT NULL,
    "LeaseOwner"    uuid,                                 -- WorkerInstances.InstanceId
    "LeaseUntil"    timestamptz,
    "RunId"         uuid,
    "Result"        jsonb,                                -- small: preview summary, command outcome
    "LastError"     varchar(500),
    "CompletedAt"   timestamptz,
    PRIMARY KEY ("EnqueuedAt", "Id")
) PARTITION BY RANGE ("EnqueuedAt");

CREATE INDEX "IX_Jobs_Ready" ON content."Jobs" ("Class", "DepartmentId", "Priority" DESC, "AvailableAt")
    WHERE "State" = 'queued';
CREATE INDEX "IX_Jobs_Leases" ON content."Jobs" ("LeaseUntil") WHERE "State" = 'running';
CREATE INDEX "IX_Jobs_DedupKey" ON content."Jobs" ("DedupKey") WHERE "State" IN ('queued','running');
```

EF Core maps `Jobs` with `ExcludeFromMigrations()`; the table is created by raw SQL in the migration.

### 4.2 Classes, concurrency, retries

| Class | Order | Concurrency (per worker) | Lease | Max attempts | Notes |
|---|---|---|---|---|---|
| `preview` | 1 | 2 | 2 min | 1 | Never writes `content` except `Result`; node outputs to `previews/` blobs |
| `command` | 2 | 2 | 1 min | 3 | Admin decisions applied by the worker: clear PII flag, confirm/reject deletion, approve/rollback profile, edit human evaluation questions (§12.2) |
| `sync` | 3 | 2 | 5 min | 3 | Listing, change detection, deletion guard |
| `fetch` | 4 | 8 (frontier limits per host) | 2 min | 5 | |
| `parse` | 5 | = parser sidecar capacity | 15 min | 3 | Heartbeat extends the lease |
| `embed` | 6 | 4 | 2 min | 6 | Batches sized for the gateway |
| `eval` | 7 | 1 | 15 min | 2 | Lowest |

- **Back-off**: `TransientError` → `AvailableAt = now() + least(10s * 2^Attempts, 1h)` ± 20 % jitter; `FatalError` →
  `failed` at once; attempts exhausted → `dead` (shown in the run).
- **Leases**: dequeue sets `LeaseOwner`, `LeaseUntil`, `Attempts + 1`; a reaper (every 30 s) returns expired leases to
  `queued`. A worker restart loses no work (worker §9).
- **Dedup**: enqueue takes `pg_advisory_xact_lock(hashtextextended("DedupKey", 0))` and inserts only if no
  `queued`/`running` job has the key (a unique index cannot span partitions without the partition key).
- **Fairness per department**: the worker keeps running counts per (class, department) in memory and tries
  departments in ascending order with the indexed query below; the first hit wins. Preview and command are tried first.

```sql
WITH next AS (
    SELECT "EnqueuedAt", "Id" FROM content."Jobs"
    WHERE "State" = 'queued' AND "Class" = $1 AND "DepartmentId" = $2 AND "AvailableAt" <= now()
    ORDER BY "Priority" DESC, "AvailableAt"
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE content."Jobs" j
SET "State" = 'running', "LeaseOwner" = $3, "LeaseUntil" = now() + $4::interval, "Attempts" = j."Attempts" + 1
FROM next WHERE j."EnqueuedAt" = next."EnqueuedAt" AND j."Id" = next."Id"
RETURNING j.*;
```

### 4.3 LISTEN/NOTIFY

| Channel | Payload | Sent by | Listened by |
|---|---|---|---|
| `rag_jobs` | class name (`preview`, …) | enqueue transaction (`pg_notify` delivers at commit) | worker |
| `rag_config` | config version (§5) | config trigger | api, tools, worker, admin |

Each instance holds one dedicated listening connection (counted in its role's limit) and still polls every 2 s (jobs)
and 2 s (config version), so a missed notification costs seconds.

### 4.4 Partitions

`Jobs` and `RunItems` are range-partitioned by day (`content."Jobs_20261029"`, `content."RunItems_20261029"`) with a
`DEFAULT` partition as a safety net. `content.rag_partition_maintain()` (`SECURITY DEFINER`, owner migrator, run by the
migration service on deploy and by the worker daily at 02:00):

1. creates partitions for today + 7 days;
2. for `Jobs` partitions older than the job retention (§8): moves rows still `queued` into today's partition
   (delete + re-insert with `"EnqueuedAt" = now()` in one transaction, `FOR UPDATE SKIP LOCKED` so a job being
   dequeued at that moment is left alone). A partition that still has `running` rows is **skipped** until their leases
   end, because moving a running job changes its primary key while a worker holds the lease. Then `DETACH PARTITION`
   and `DROP TABLE` with `SET LOCAL lock_timeout = '2s'` (retried next night if it cannot get the lock);
3. drops `RunItems` partitions older than the run-item retention.

Dropping replaces `DELETE`, so the queue tables never cause vacuum storms (R1). Autovacuum on the live partitions:
`autovacuum_vacuum_scale_factor = 0.02`, `autovacuum_vacuum_cost_limit = 2000`.

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

- **Every** config table except `KeyLastUse`, `DataProtectionKeys`, `ConfigVersion` and `ConfigOutbox` gets the
  trigger, so no code path can forget the outbox. `SecurityRelevant = true` for `Keys`, `Clients`, `Collections`,
  `Sources`, `Assistants`, `Assistant*`, `Skills`, `SkillVersionLinks`, `ToolServers`, `Tools`, `ToolApprovals`,
  `ToolDefinitions`, `DataProtectionPolicies`, `DataTiers`, `Departments`, `Teams`, `GatewayKeyLinks` (credentials).
- **Monotonic in commit order**: the single-row `UPDATE` holds a row lock until commit, so a later transaction always
  gets a higher version and commits after it. (A sequence would not: version 6 could commit before 5, and a reader at
  6 would never see 5.) Config writes are rare, so serialising them costs nothing.
- One version per transaction; identical `NOTIFY` payloads in one transaction are folded by Postgres.
- **Readers** (`Ume.Rag.Infrastructure` config-version library): load a snapshot tagged with its version; on
  `NOTIFY` or a 2 s poll of `SELECT "Version" FROM config."ConfigVersion"`, read `ConfigOutbox WHERE "Version" > @cached`,
  reload the affected entity types and swap the snapshot atomically. If the cached version is older than the oldest
  outbox row, reload everything.
- **Fail closed**: a security-relevant decision requires `snapshot.Version == lastConfirmedVersion` and
  `now − lastConfirmedAt ≤ 10 s`; otherwise the request gets `503 config_unconfirmed` and a security event. Other
  settings may serve from the last snapshot.
- The config version is in every log scope (plan §10) and in `audit."AuditLog"."ConfigVersion"`.

## 6. Tier resolver (plan §8a)

`ITierResolver.Resolve(Classification) → TierBinding(ConnectionName, BlobContainer, ChatMasterKeyName,
BlobMasterKeyName)`, built from `config."DataProtectionPolicies"` + `config."DataTiers"` (security-relevant, so at the
current config version). Default: every level → tier `main` (`ragdb`, container `rag-main`); master keys are always
per level, even in one tier (`rag-chat-public`, `rag-chat-internal`, `rag-chat-sensitive`, `rag-blob-<level>`).

| Main only | Tier-local (rows carry `Classification`) |
|---|---|
| all of `config` and `audit`; `content."Jobs"`, `"WorkerInstances"`, `"NodeManifest"`, `"WorkerNodeSupport"`, `"CrawlHosts"` | rest of `content`; all of `chat` |

How the "no cross-level foreign keys or joins" rule is upheld:

1. Every tier-local table has a `Classification` column; every FK between tier-local tables is **composite**
   (`(X, Classification) → (Id, Classification)`), so a row can only reference a row of the same level.
2. Tier-local tables have **no FK to `config` or other main-only tables**; they hold config ids only, resolved in
   memory from the config snapshot (which every service already has, R7).
3. No SQL joins between `config` and tier-local tables (e.g. the admin Documents page joins sources in memory).
   Retrieval filters `Classification = ANY(levels)` within one tier and fuses tiers in the API.
4. Chunk level = **document level** (the source's level), not the collection's: a `sensitive` collection with a
   `public` source keeps that source's chunks in the main tier, and retrieval for that collection spans both tiers.
5. Repositories take a `Classification` (or a tier) as a required argument; an integration-test matrix runs the suite
   with the `sensitive` tier as a separate database, which proves the rule in CI from P1.

## 7. Envelope encryption

**Chat** (all ciphertext columns are `bytea`: `0x01` format byte ‖ 12-byte nonce ‖ AES-256-GCM ciphertext ‖ 16-byte
tag; AAD = `"<table>:<row id>:<column>:<conversation id>"`, so ciphertext cannot be moved between rows):

| Ciphertext | Plaintext metadata (stays readable) |
|---|---|
| `Conversations.TitleCiphertext`, `Messages.ContentCiphertext`, `MessageSources.SnippetCiphertext`, `Feedback.CommentCiphertext`, `ToolCalls.ArgumentsCiphertext`, `ToolCalls.ResultCiphertext` | ids, times, assistant, client, subject id (oid), classification, history mode, token counts, cost, latency, outcome, residency, rating, tool name, sizes, HMAC of arguments, cited document/chunk ids |

- **Data keys**: AES-256 key per conversation per **7-day epoch** in `chat."DataKeys"`, wrapped by `IKeyProvider`
  (Key Vault / Managed HSM `wrapKey`, OpenBao transit, local key file in development). New messages use the newest
  epoch key; the title is re-encrypted with it. `KeyProvider`, `MasterKeyName`, `MasterKeyVersion` identify the
  wrapping key. Unwrapped keys live in process memory for at most 5 minutes, never in Redis.
- **Who unwraps**: the Chat API (own history) and the admin review path after a `ConversationAccess` `approved` event
  (second approver for `sensitive`). DB roles alone see ciphertext.
- **Shred buckets** (§12.5): the wrapping master key is chosen by level **and month of latest possible expiry**
  (`rag-chat-internal-2027-01`, latest expiry = epoch start + 7 days + retention days). Retention deletes key rows
  (live database); when a month's last expiry plus 7 days' grace has passed, the admin retention job **destroys that
  bucket key** in the key service, which makes every backup copy of those data keys unwrappable. A later increase of
  an assistant's retention applies to new epochs only.
- **Master-key rotation** (compromise or policy): new version of the bucket key; a background `rewrap` command
  unwraps and re-wraps each `DataKeys` row (no message re-encryption), then the old version is disabled. Audited.
- **History modes**: `stored` as above; `session` sets `ExpiresAt` = 24 h; `none` writes metadata only (ciphertext
  columns null, no data key). Public level with encryption switched off writes format byte `0x00` (plaintext); such
  rows are only deleted, not shredded (§12.10).

**Blobs**: per-document data key in `content."DocumentKeys"` (wrapped by `rag-blob-<level>`); uploads before their
document exists use a per-upload key stored with the upload job and moved to `DocumentKeys` on first fetch. Format:
header `URB1` ‖ key id ‖ base nonce, then 64 KiB segments with AES-256-GCM (STREAM construction). Purging a document
deletes its `DocumentKeys` row, then its blob prefix.

## 8. Retention jobs

Nightly, batched step functions in the gateway style (ADR 016): `SECURITY DEFINER`, owner migrator,
`SET search_path = pg_catalog, pg_temp`, minimums enforced inside the function, each run logged and written to
`audit."AuditLog"` as actor `system:retention`. Admin API runs the `config`/`chat`/`audit` steps (it already holds
connections to every tier); the worker runs the `content` steps and partition maintenance (it owns content and blobs).

| Schema / table | Rule (platform §8 unless marked *proposed*) | Mechanism |
|---|---|---|
| `chat."DataKeys"` + everything encrypted under them (cascade) | key `ExpiresAt` = `LastUsedAt` + assistant retention (default 90 d, `sensitive` 30 d, max per level policy; `session` 24 h) | `chat.rag_retention_chat_step(batch)`; then bucket-key destruction (§7) |
| `chat."Conversations"` | deleted when no data key remains and `ExpiresAt` passed; user delete: immediate shred of all its keys | same function / Chat API `DELETE` |
| `chat."Feedback"`, `"MessageSources"`, `"ToolCalls"` | follow their conversation (cascade) | FK cascade |
| `chat."AssistantDailyTotals"` | 3 years *proposed* | same function |
| `content."DocumentSources"` | removed from source → `DeletedAt`, purge after 14 days | worker, `DELETE` |
| `content."Documents"`, `"ChunkSets"`, `"Chunks"`, `"DocumentKeys"`, `"NodeOutputs"` (+ blobs) | no live `DocumentSources` → soft delete, purge after 14 days | worker; cascade from `Documents` |
| `content."NodeOutputs"` (+ blobs) | intermediate outputs 30 days (`Settings`); current parse output kept while the document lives | worker, `ExpiresAt` |
| uploads (blobs) | while the source exists | worker on source deletion |
| `content."EvaluationSets"` and children | until the collection is removed | worker on collection deletion |
| `content."EmbeddingCache"` | unused 30 days *proposed* | worker |
| `content."Jobs"` | terminal jobs 7 days *proposed* | partition drop |
| `content."RunItems"` | 90 days *proposed* | partition drop |
| `content."Runs"` | 13 months *proposed* | worker |
| `content."ParseObservations"` | 180 days *proposed* | worker |
| `content."RetrievalStats"` | 13 months *proposed* | worker |
| `config."ConfigOutbox"` | 30 days *proposed* | `config.rag_retention_config_step` |
| `audit.*` | 2 years (`Settings.AuditRetentionDays`, minimum 90, may be "forever") | `audit.rag_retention_audit_step` |
| Open WebUI's own database | client data-handling profile (90 days) | separate scheduled job against Open WebUI, not our schema |

## 9. Blob layout (`IBlobStore`)

One container (file system: `/data/blobs/<container>/`) per tier: `rag-main`, `rag-sensitive`. Paths below the
container; ids in `N` format; every object envelope-encrypted with its document's (or upload's) key per §7.

| Kind | Path | Retention |
|---|---|---|
| Upload | `uploads/{sourceId}/{uploadId}/{sha256}` | while the source exists |
| Quarantined file (ClamAV) | `quarantine/{sourceId}/{sha256}` | 30 days *proposed* |
| Raw fetch | `documents/{documentId}/raw/{contentHash}` | current version while the document lives |
| Node output | `documents/{documentId}/nodes/{nodeType}/{cacheKey}.{ext}`; the parse output is a prefix `…/nodes/docling-parse/{cacheKey}/` with `result.json`, `docling/{from}-{to}.json.gz`, `pictures/{n}.png` (on request) and `ranges/{from}-{to}.result.json` (intermediate, 30 days) (parser design §7.6) | 30 days; parse output while the document lives |
| Preview output | `previews/{jobId}/{nodeId}.json` | 24 hours |
| robots.txt | `crawl/{host}/robots.txt` (main) | refreshed daily |
| Skill reference files | `skills/{skillId}/{skillVersionId}/{relativePath}` (main) | while the version exists |

**Cache key** = SHA-256 of `inputHash ‖ nodeType ‖ nodeVersion ‖ paramsHash`, where `inputHash` is the hash of the
node's input port data (for the first node: the raw content hash), `nodeType` the catalogue type id, and `paramsHash`
the SHA-256 of the canonical JSON params. Two collections whose pipelines agree up to a node share its output for the
same document (R2).

## 10. Migrations

- **One `RagDbContext`** (as the gateway's one context), entity configurations in folders per schema
  (`Persistence/Config`, `Content`, `Chat`, `Audit`), `ToTable("Chunks", "content")`, history table
  `config."__EFMigrationsHistory"`. Raw SQL inside migrations for what EF cannot express: text search configuration,
  partitioned tables (`ExcludeFromMigrations()` on their entities), HNSW expression indexes, generated `tsvector`,
  triggers and step functions (`REVOKE ALL ON FUNCTION … FROM PUBLIC`, as `DataRetention.cs`).
- **Expand/contract only**, so rolling updates stay possible (gateway ADR 017); every release notes *Rolling update:
  yes/no*.
- **Migration service** (copy of `MigrationWorker.cs`): for each tier in configuration (`ConnectionStrings__ragdb`,
  optionally `ConnectionStrings__ragdb-sensitive`): `MigrateAsync`, apply `app-roles.sql` (main) or
  `app-roles-tier.sql` (other tiers), run `content.rag_partition_maintain()`, then the dev seeder (main, development
  only; seeds the BGE-M3 profile with its fixed id). Services wait for it as in the gateway compose files.
- **Every tier database gets the same schema** (one migration chain); main-only tables stay empty there and have no
  grants. One model, one history, no drift.
- **Turning on a `sensitive` tier** (decision point before the first `sensitive` source):
  1. provision the on-prem instance from our image with `init-tier.sql` (same roles, no `config`/`audit` grants), its
     own volume encryption and credentials; add `ConnectionStrings__ragdb-sensitive`;
  2. deploy: the migration service creates the schema there;
  3. admin sets `DataTiers.sensitive.State = 'migrating'`; sensitive sources pause and sensitive assistants answer
     `503 maintenance` (expected to be empty or tiny at this point);
  4. a `move-tier` command copies every tier-local table `WHERE "Classification" = 'sensitive'` in FK order
     (`COPY … TO STDOUT (FORMAT binary)` → `COPY … FROM STDIN`), copies blob prefixes to `rag-sensitive`, verifies
     row counts and hashes;
  5. a config change maps `sensitive → sensitive` (`State = 'active'`), services re-resolve at the new version;
  6. the moved rows and blobs are deleted from main (crypto-shredding for chat: the main copies' keys are dropped).

## 11. Redis (mirrors `RedisKeys.cs`)

Prefix `ume-rag:`, ACL user limited to `~ume-rag:*`; no publish channels (change events come from Postgres).

| Key | Lifetime |
|---|---|
| `ume-rag:admin-session:{sessionId}` | session |
| `ume-rag:rl:req:{keyId}:{minute}`, `ume-rag:rl:user:{subjectHash}:{minute}`, `ume-rag:rl:tool:{toolId}:{minute}`, `ume-rag:rl:ip:{ipHash}:{minute}` | 120 s (`RateWindowSeconds`, as gateway) |
| `ume-rag:qemb:{profileId}:{queryHash}` (embedding only; `public`/`internal` queries only, §12.14) | 5 min |
| `ume-rag:groups:{oid}` (feature C `checkMemberGroups` result `{checked, member}`, ids only) | 10 min |
| `ume-rag:owui-oid:{hmac(email)}` (P2, Open WebUI email → Entra `oid`, identity design §5.4) | 1 h |

## 12. Open decisions and contradictions found

**Review (2026-10-29):** recommendations 1–22 accepted and carried into the plans (platform §3, §8, §8a; worker §2,
§4; knowledge §3; gateway-changes feature C; key prefixes everywhere). 23 is decided in the Chat API detail round.
Review fixes in this document: partition maintenance no longer moves `running` jobs (§4.4); the write cost of shared
chunk sets is stated (§2.2); `GatewayKeyLinks` is security-relevant (§5).

| # | Topic | Finding | Recommendation |
|---|---|---|---|
| 1 | Identifier casing | Plans write `content.node_manifest` (snake case); the gateway uses EF's quoted PascalCase | PascalCase as here (`content."NodeManifest"`); update worker §4 wording |
| 2 | Admin writes to `content` | Plan §3 says `content` is written by the worker, but the admin enqueues preview/sync/eval, clears PII flags, confirms deletions, approves/rolls back profiles, edits human questions | Admin gets `INSERT` on `Jobs` only; decisions are `command` jobs applied by the worker (adds class `command` to worker §2) |
| 3 | Retrieval statistics | Knowledge API counts retrievals but has read-only `content` | `content.rag_count_retrievals(day, chunk ids, collection ids, cited ids)` `SECURITY DEFINER` upsert, called off the request path |
| 4 | Tool hub writes | Tool-call audit and definition drift need a writer that is not admin | `audit."ToolCallEvents"` and `audit."ToolDefinitionObservations"`, `INSERT` for tools |
| 5 | Crypto-shredding vs backups | Plan §8a says deleting the wrapped key reaches backups; it does not, because backups contain the key rows and the master key still exists | Monthly shred-bucket master keys destroyed after expiry (§7); check Key Vault purge protection (it delays purge by 7–90 days) and OpenBao key deletion settings |
| 6 | Retention granularity | 90 days per conversation from last activity would keep old messages of long conversations | 7-day key epochs per conversation; every message gone ≤ retention + 7 days |
| 7 | Shared chunks vs dedup | Plan §3 "one set of chunks" vs knowledge §3 "deduplicated by document and chunk text hash" | Chunk sets shared via `CollectionIds uuid[]`; dedup still needed when two pipelines differ |
| 8 | Chunk tier | "Classification lives on collections" vs tier split | Tier placement by document (source) level; access by collection (§6.4) |
| 9 | Organisation sync | Data API `catalog.read` has departments and teams but no member groups | Gateway feature C adds `memberGroups` (strings) to `/v1/catalog/departments`; admin syncs every 5 min with a gateway Data API key (`ume-dk-`, `catalog.read` only; identity design §2.1); in `gateway-changes` |
| 10 | Encryption below `sensitive` | §8a requires encryption for `internal`/`sensitive`, optional for `public`; blob encryption only stated for `sensitive` | Encrypt chat at every level by default and all blobs at every level (same code path, negligible cost) |
| 11 | Retention values missing in §8 | Jobs, run items, runs, observations, caches, outbox, stats, quarantine | Values marked *proposed* in §8–9; add to `Settings` and platform §8 |
| 12 | Job classes | Worker §2 lists six classes | Add `command` (§12.2) |
| 13 | Cache key "node id" | Pipeline-local node ids would prevent sharing; per-document blob keys prevent cross-document sharing | Node type id + input hash (§9); namespace node outputs per document; accept no sharing between two URLs with identical bytes |
| 14 | Query-embedding cache | Plan §8a allows embeddings in Redis; embeddings partly reveal text | No Redis cache for `sensitive` queries (in-process only) |
| 15 | Coverage signal | Knowledge §3 uses "best fused score"; RRF scores are rank-only | Coverage threshold on best cosine distance per collection |
| 16 | Vector storage | `vector` (4 KB per 1024-dim chunk) vs `halfvec` (2 KB) | Start with `vector`; evaluate `halfvec` with the evaluation sets in P2 |
| 17 | Run items, frontier, profiles placement | Hold URLs and patterns of possibly `sensitive` sources | Tier-local (as here); learned profiles apply only within their level |
| 18 | Hunspell compounds | Postgres `ispell` supports `COMPOUNDFLAG`-style affixes but not every Hunspell compound rule | P0 test decides; fallback: decompound in the worker before indexing, same configuration name |
| 19 | One context, all tiers | Alternative: a second `TierDbContext` without main-only tables | One context and identical schemas (simpler, gateway-like) |
| 20 | Pool caps | Starting values only | Confirm in the R1 release test; adjust `app-roles.sql` |
| 21 | Conversation review state | No schema owner for a workflow with two approvers | Append-only `audit."ConversationAccess"` events |
| 22 | Key prefixes | Plans say `sk_`/`pk_`, gateway uses `ume-sk-` | `ume-rag-sk-` / `ume-rag-pk-` (gateway shape, cannot be mistaken for a gateway key) |
| 23 | Chat attachments (P2) | Temporary per-conversation collections would put chat-scoped content in `content` | Decide in the chat detail round; keep them tier-local with conversation retention |
