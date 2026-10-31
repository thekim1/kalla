-- Applied by the migration service after every migration run (data-model §1.2, as the gateway's file). The service
-- roles' privileges are revoked and granted again in one transaction, so each role holds exactly what this file lists
-- (a privilege removed here is removed from existing installations too) and running services see either the old or the
-- new grants, never none. Tables are named one by one: a new table is readable by no service until it is added here
-- (DatabaseRoleTests checks the effective grants). Partitioned tables are granted on the parent only.
-- No curly braces: the migration service runs this file with EF Core's ExecuteSqlRaw, which reads them as placeholders.
BEGIN;
REVOKE ALL ON ALL TABLES IN SCHEMA config, content, chat, audit FROM ume_rag_api, ume_rag_admin, ume_rag_worker;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA config, content, chat, audit FROM ume_rag_api, ume_rag_admin, ume_rag_worker;
REVOKE ALL ON SCHEMA config, content, chat, audit FROM ume_rag_api, ume_rag_admin, ume_rag_worker;

GRANT USAGE ON SCHEMA config, content, audit TO ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT USAGE ON SCHEMA chat TO ume_rag_api, ume_rag_admin;

-- Sequences in the schemas each role writes.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA config, content, audit TO ume_rag_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA config, chat, audit TO ume_rag_api;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA content, audit TO ume_rag_worker;

-- config: the version row and the outbox are written only by the trigger function (owner's rights). The admin's
-- UPDATE on ConfigVersion exists only for the FOR UPDATE lock that serialises config writes (data-model §5).
GRANT SELECT ON config."ConfigVersion", config."ConfigOutbox" TO ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT UPDATE ON config."ConfigVersion" TO ume_rag_admin;
-- The admin creates and rotates the Data Protection keys; the others only decrypt.
GRANT SELECT ON config."DataProtectionKeys" TO ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT INSERT ON config."DataProtectionKeys" TO ume_rag_admin;
GRANT SELECT ON config."__EFMigrationsHistory" TO ume_rag_admin;
-- The gateway mirror (departments, teams): written by the admin's org sync, read by every service (scopes, audiences).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Departments", config."Teams" TO ume_rag_admin;
GRANT SELECT ON config."Departments", config."Teams" TO ume_rag_api, ume_rag_worker;
-- Clients and keys: the admin manages them, the api authenticates with them; key use goes to KeyLastUse (api), so it
-- never bumps the config version. The worker holds no keys.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Clients", config."Keys" TO ume_rag_admin;
GRANT SELECT ON config."Clients", config."Keys" TO ume_rag_api;
GRANT SELECT ON config."KeyLastUse" TO ume_rag_admin;
GRANT SELECT, INSERT, UPDATE ON config."KeyLastUse" TO ume_rag_api;
-- Admin BFF sessions: only the admin reads and writes them (data-model §2.1).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."AdminSessions" TO ume_rag_admin;
-- Ingestion config (data-model §1.2): the admin manages it; the worker reads all of it; the api reads what retrieval
-- needs. The api reads only the listed columns of Sources (knowledge design §6.3, §6.5), never Settings jsonb or
-- schedules, and no pipelines.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles",
  config."Collections", config."Sources", config."Pipelines", config."PipelineVersions" TO ume_rag_admin;
GRANT SELECT ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles", config."Collections",
  config."Sources", config."Pipelines", config."PipelineVersions" TO ume_rag_worker;
GRANT SELECT ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles", config."Collections" TO ume_rag_api;
GRANT SELECT ("Id", "CollectionId", "Type", "Classification", "State", "DeletedAt", "ContainsThirdPartyText")
  ON config."Sources" TO ume_rag_api;
-- Assistants and the gateway model mirror (data-model §1.2): the admin manages them; the api reads assistants,
-- audiences and models, and versions only through the view of published versions, never AssistantVersions itself, so a
-- draft cannot reach chat. The worker reads no assistant tables.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Assistants", config."AssistantVersions", config."AssistantAudiences",
  config."GatewayModels" TO ume_rag_admin;
GRANT SELECT ON config."Assistants", config."AssistantAudiences", config."PublishedAssistantVersions",
  config."GatewayModels" TO ume_rag_api;

-- content: the worker owns it; the admin reads it and enqueues jobs (decisions are command jobs, data-model §12.2).
GRANT SELECT, INSERT, UPDATE, DELETE ON content."Jobs", content."WorkerInstances", content."RunItems" TO ume_rag_worker;
GRANT SELECT ON content."Jobs", content."WorkerInstances", content."RunItems" TO ume_rag_admin;
GRANT INSERT ON content."Jobs" TO ume_rag_admin;
-- Ingestion content (data-model §1.2, §2.2): the worker writes it all, the admin reads it all (incl. the test panel).
GRANT SELECT, INSERT, UPDATE, DELETE ON content."NodeManifest", content."WorkerNodeSupport", content."CrawlHosts",
  content."SiteProfiles", content."SiteProfileVersions", content."CrawlFrontier", content."CrawlRuns",
  content."ConnectorCheckpoints", content."Uploads", content."DeletionProposals", content."QuarantinedFiles",
  content."Documents", content."NodeOutputs", content."ChunkSets", content."Chunks", content."EmbeddingCache",
  content."Runs", content."ParseObservations", content."ParseProfiles", content."BlobTombstones" TO ume_rag_worker;
GRANT SELECT ON content."NodeManifest", content."WorkerNodeSupport", content."CrawlHosts", content."SiteProfiles",
  content."SiteProfileVersions", content."CrawlFrontier", content."CrawlRuns", content."ConnectorCheckpoints",
  content."Uploads", content."DeletionProposals", content."QuarantinedFiles", content."Documents", content."NodeOutputs",
  content."ChunkSets", content."Chunks", content."EmbeddingCache", content."Runs", content."ParseObservations",
  content."ParseProfiles", content."BlobTombstones" TO ume_rag_admin;
-- The admin tombstones the upload blobs it replaces or deletes, in the transaction of its own change (issue #34).
GRANT INSERT ON content."BlobTombstones" TO ume_rag_admin;
-- The api searches chunks and reads the documents' citation columns only (knowledge design §3.5).
GRANT SELECT ON content."ChunkSets", content."Chunks" TO ume_rag_api;
GRANT SELECT ("Id", "SourceId", "Classification", "IdentityKind", "IdentityHash", "Title", "Url", "MediaType",
  "DisplayName", "SourceModifiedAt", "PageCount", "LiveChunkSetId", "DeletedAt") ON content."Documents" TO ume_rag_api;
-- Evaluation and retrieval counters (data-model §1.2, §12.2, §12.3): the worker writes them; the admin reads them and
-- writes runs and results itself for the answer-level runs it executes (P2 kinds); the api counts retrievals only
-- through content.rag_count_retrievals.
GRANT SELECT, INSERT, UPDATE, DELETE ON content."EvaluationSets", content."EvaluationQuestions", content."EvaluationRuns",
  content."EvaluationResults", content."RetrievalStats" TO ume_rag_worker;
GRANT SELECT ON content."EvaluationSets", content."EvaluationQuestions", content."EvaluationRuns", content."EvaluationResults",
  content."RetrievalStats" TO ume_rag_admin;
GRANT INSERT, UPDATE ON content."EvaluationRuns", content."EvaluationResults" TO ume_rag_admin;

-- audit: every service appends, only the admin reads; no role deletes (retention runs through step functions).
GRANT INSERT ON audit."AuditLog" TO ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT SELECT ON audit."AuditLog" TO ume_rag_admin;

-- Functions (data-model §1.2). The keyword query function calls the aggregate with the caller's rights.
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA config, content, chat, audit FROM ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT EXECUTE ON FUNCTION content.rag_or_query(tsvector), content.rag_tsquery_or_agg(tsquery)
  TO ume_rag_api, ume_rag_admin, ume_rag_worker;
GRANT EXECUTE ON FUNCTION content.rag_partition_maintain(), content.rag_ensure_profile_index(uuid, integer) TO ume_rag_worker;
GRANT EXECUTE ON FUNCTION config.rag_retention_config_step(integer) TO ume_rag_admin;
GRANT EXECUTE ON FUNCTION content.rag_count_retrievals(date, uuid[], bytea[], uuid[], character varying[], integer[], integer[])
  TO ume_rag_api;
COMMIT;
