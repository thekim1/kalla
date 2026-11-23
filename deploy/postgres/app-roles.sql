-- Applied by the migration service after every migration run (data-model §1.2, as the gateway's file). The service
-- roles' privileges are revoked and granted again in one transaction, so each role holds exactly what this file lists
-- (a privilege removed here is removed from existing installations too) and running services see either the old or the
-- new grants, never none. Tables are named one by one: a new table is readable by no service until it is added here
-- (DatabaseRoleTests checks the effective grants). Partitioned tables are granted on the parent only.
-- No curly braces: the migration service runs this file with EF Core's ExecuteSqlRaw, which reads them as placeholders.
BEGIN;
REVOKE ALL ON ALL TABLES IN SCHEMA config, content, chat, audit FROM kalla_api, kalla_admin, kalla_worker;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA config, content, chat, audit FROM kalla_api, kalla_admin, kalla_worker;
REVOKE ALL ON SCHEMA config, content, chat, audit FROM kalla_api, kalla_admin, kalla_worker;

GRANT USAGE ON SCHEMA config, content, audit TO kalla_api, kalla_admin, kalla_worker;
GRANT USAGE ON SCHEMA chat TO kalla_api, kalla_admin;

-- Sequences in the schemas each role writes.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA config, content, audit TO kalla_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA config, chat, audit TO kalla_api;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA content, audit TO kalla_worker;

-- config: the version row and the outbox are written only by the trigger function (owner's rights). The admin's
-- UPDATE on ConfigVersion exists only for the FOR UPDATE lock that serialises config writes (data-model §5).
GRANT SELECT ON config."ConfigVersion", config."ConfigOutbox" TO kalla_api, kalla_admin, kalla_worker;
GRANT UPDATE ON config."ConfigVersion" TO kalla_admin;
-- The admin creates and rotates the Data Protection keys; the others only decrypt.
GRANT SELECT ON config."DataProtectionKeys" TO kalla_api, kalla_admin, kalla_worker;
GRANT INSERT ON config."DataProtectionKeys" TO kalla_admin;
GRANT SELECT ON config."__EFMigrationsHistory" TO kalla_admin;
-- The gateway mirror (departments, teams): written by the admin's org sync, read by every service (scopes, audiences).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Departments", config."Teams" TO kalla_admin;
GRANT SELECT ON config."Departments", config."Teams" TO kalla_api, kalla_worker;
-- The assigned gateway keys' mirror (data-model §1.2, §2.1): the admin's sync writes it; the api reads an assistant's key
-- status and output cap before a call (chat §4.1, §6.3); the worker never needs it.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."GatewayKeys" TO kalla_admin;
GRANT SELECT ON config."GatewayKeys" TO kalla_api;
-- Clients and keys: the admin manages them, the api authenticates with them; key use goes to KeyLastUse (api), so it
-- never bumps the config version. The worker holds no keys.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Clients", config."Keys" TO kalla_admin;
GRANT SELECT ON config."Clients", config."Keys" TO kalla_api;
GRANT SELECT ON config."KeyLastUse" TO kalla_admin;
GRANT SELECT, INSERT, UPDATE ON config."KeyLastUse" TO kalla_api;
-- Admin BFF sessions: only the admin reads and writes them (data-model §2.1).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."AdminSessions" TO kalla_admin;
-- Setup mode (identity design §19): the setup sessions and test sign-ins, only kalla-admin; they hold the candidate values.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."SetupSessions", config."SetupTests" TO kalla_admin;
-- Ingestion config (data-model §1.2): the admin manages it; the worker reads all of it; the api reads what retrieval
-- needs. The api reads only the listed columns of Sources (knowledge design §6.3, §6.5), never Settings jsonb or
-- schedules, and no pipelines.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles",
  config."Collections", config."Sources", config."Pipelines", config."PipelineVersions" TO kalla_admin;
GRANT SELECT ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles", config."Collections",
  config."Sources", config."Pipelines", config."PipelineVersions" TO kalla_worker;
GRANT SELECT ON config."Settings", config."DataProtectionPolicies", config."EmbeddingProfiles", config."Collections" TO kalla_api;
GRANT SELECT ("Id", "CollectionId", "Type", "Classification", "State", "DeletedAt", "ContainsThirdPartyText", "DemoteRules")
  ON config."Sources" TO kalla_api;
-- Collection shares (data-model §1.2, admin design §3.2): the admin manages them; the api reads who may link a
-- collection; the worker never needs them.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."CollectionShares" TO kalla_admin;
GRANT SELECT ON config."CollectionShares" TO kalla_api;
-- Assistants and the gateway model mirror (data-model §1.2): the admin manages them; the api reads assistants,
-- audiences and models, and versions only through the view of published versions, never AssistantVersions itself, so a
-- draft cannot reach chat. The worker reads the model mirror (the evaluation's residency checks, #130) and, for the
-- index-health check, only which collections and topics active assistants' published versions link (knowledge design
-- §6.4; Decisions "M-2 open design calls": every HNSW-searched scope an assistant link uses is sampled).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."Assistants", config."AssistantVersions", config."AssistantAudiences",
  config."GatewayModels" TO kalla_admin;
-- Explain access (identity design 4.9) builds the api's policy snapshot in kalla-admin, which reads the published view too.
GRANT SELECT ON config."PublishedAssistantVersions" TO kalla_admin;
GRANT SELECT ON config."Assistants", config."AssistantAudiences", config."PublishedAssistantVersions",
  config."GatewayModels" TO kalla_api;
GRANT SELECT ON config."GatewayModels" TO kalla_worker;
GRANT SELECT ("AssistantId", "CollectionLinks") ON config."PublishedAssistantVersions" TO kalla_worker;
GRANT SELECT ("Id", "State") ON config."Assistants" TO kalla_worker;
-- An assistant's test questions (P2, data-model AssistantTestQuestions, admin design §7.4): read and written only by
-- kalla-admin, which runs them (its runs and results are the EvaluationRuns/EvaluationResults writes below).
GRANT SELECT, INSERT, UPDATE, DELETE ON config."AssistantTestQuestions" TO kalla_admin;
-- Assistant templates (P2, data-model AssistantTemplates): starting points copied into a new assistant's first draft,
-- read and written only by kalla-admin.
GRANT SELECT, INSERT, UPDATE, DELETE ON config."AssistantTemplates" TO kalla_admin;

-- content: the worker owns it; the admin reads it and enqueues jobs (decisions are command jobs, data-model §12.2).
GRANT SELECT, INSERT, UPDATE, DELETE ON content."Jobs", content."WorkerInstances", content."RunItems" TO kalla_worker;
GRANT SELECT ON content."Jobs", content."WorkerInstances", content."RunItems" TO kalla_admin;
GRANT INSERT ON content."Jobs" TO kalla_admin;
-- Ingestion content (data-model §1.2, §2.2): the worker writes it all, the admin reads it all (incl. the test panel).
GRANT SELECT, INSERT, UPDATE, DELETE ON content."NodeManifest", content."WorkerNodeSupport", content."CrawlHosts",
  content."SiteProfiles", content."SiteProfileVersions", content."CrawlFrontier", content."CrawlRuns",
  content."ConnectorCheckpoints", content."Uploads", content."DeletionProposals", content."QuarantinedFiles",
  content."Documents", content."NodeOutputs", content."ChunkSets", content."Chunks", content."EmbeddingCache",
  content."Runs", content."ParseObservations", content."ParseProfiles", content."BlobTombstones" TO kalla_worker;
GRANT SELECT ON content."NodeManifest", content."WorkerNodeSupport", content."CrawlHosts", content."SiteProfiles",
  content."SiteProfileVersions", content."CrawlFrontier", content."CrawlRuns", content."ConnectorCheckpoints",
  content."Uploads", content."DeletionProposals", content."QuarantinedFiles", content."Documents", content."NodeOutputs",
  content."ChunkSets", content."Chunks", content."EmbeddingCache", content."Runs", content."ParseObservations",
  content."ParseProfiles", content."BlobTombstones" TO kalla_admin;
-- The admin tombstones the upload blobs it replaces or deletes, in the transaction of its own change (issue #34).
GRANT INSERT ON content."BlobTombstones" TO kalla_admin;
-- The api searches chunks and reads the documents' citation columns only (knowledge design §3.5); "Language" and
-- "FetchedAt" for statement D and the citation fields (knowledge design §6.3, §10.1); "CanonicalIdentity" (the upload
-- slot) and "ContentHash" for the blob key and ETag of the document content route (knowledge design §3.5).
GRANT SELECT ON content."ChunkSets", content."Chunks" TO kalla_api;
-- The keyword stop list's statistics (knowledge design 6.2, #499): the worker keeps them, kalla-api and the admin read them.
GRANT SELECT, INSERT, UPDATE, DELETE ON content."KeywordStatistics" TO kalla_worker;
GRANT SELECT ON content."KeywordStatistics" TO kalla_api, kalla_admin;
GRANT SELECT ("Id", "SourceId", "Classification", "IdentityKind", "IdentityHash", "Title", "Url", "MediaType",
  "DisplayName", "SourceModifiedAt", "PageCount", "LiveChunkSetId", "DeletedAt", "Language", "FetchedAt",
  "CanonicalIdentity", "ContentHash")
  ON content."Documents" TO kalla_api;
-- Evaluation and retrieval counters (data-model §1.2, §12.2, §12.3): the worker writes them; the admin reads them and
-- writes runs and results itself for the answer-level runs it executes (P2 kinds); the api counts retrievals only
-- through content.rag_count_retrievals.
GRANT SELECT, INSERT, UPDATE, DELETE ON content."EvaluationSets", content."EvaluationQuestions", content."EvaluationRuns",
  content."EvaluationResults", content."RetrievalStats" TO kalla_worker;
GRANT SELECT ON content."EvaluationSets", content."EvaluationQuestions", content."EvaluationRuns", content."EvaluationResults",
  content."RetrievalStats" TO kalla_admin;
GRANT INSERT, UPDATE ON content."EvaluationRuns", content."EvaluationResults" TO kalla_admin;
-- Tokens and cost of the worker's model calls per knowledge base and step (data-model §2.2; cost-attribution §5 C3): the
-- worker writes them, the admin reads them for its cost report.
GRANT SELECT, INSERT, UPDATE, DELETE ON content."CollectionDailyUsage" TO kalla_worker;
GRANT SELECT ON content."CollectionDailyUsage" TO kalla_admin;

-- chat (data-model §1.2, §2.3): the api owns it; the admin reads it and marks feedback reviewed (admin design §8.1); the
-- worker has no access to the schema. P1 writes metadata and ratings only (weakness review §4).
GRANT SELECT, INSERT, UPDATE, DELETE ON chat."Conversations", chat."DataKeys", chat."Messages", chat."MessageSources",
  chat."Feedback", chat."ToolCalls", chat."AssistantDailyTotals", chat."FeedbackImportState" TO kalla_api;
GRANT SELECT ON chat."Conversations", chat."DataKeys", chat."Messages", chat."MessageSources", chat."Feedback",
  chat."ToolCalls", chat."AssistantDailyTotals", chat."FeedbackImportState" TO kalla_admin;
GRANT UPDATE ("ReviewState") ON chat."Feedback" TO kalla_admin;
-- Tokens and cost of every model call per assistant, client and step (data-model §2.3; chat design §8.3, §12.2): the api's
-- conversation writer upserts them; the admin upserts the test panel's calls too (admin design §4.9).
GRANT SELECT, INSERT, UPDATE, DELETE ON chat."AssistantDailyUsage" TO kalla_api;
GRANT SELECT, INSERT, UPDATE ON chat."AssistantDailyUsage" TO kalla_admin;

-- audit: every service appends, only the admin reads; no role deletes (retention runs through step functions).
GRANT INSERT ON audit."AuditLog" TO kalla_api, kalla_admin, kalla_worker;
-- The HNSW index-health rebuild (knowledge design §6.4): REINDEX INDEX CONCURRENTLY needs MAINTAIN (Postgres 17).
GRANT MAINTAIN ON content."Chunks" TO kalla_worker;
GRANT SELECT ON audit."AuditLog" TO kalla_admin;

-- Functions (data-model §1.2). The keyword query function calls the aggregate with the caller's rights.
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA config, content, chat, audit FROM kalla_api, kalla_admin, kalla_worker;
GRANT EXECUTE ON FUNCTION content.rag_or_query(tsvector), content.rag_tsquery_or_agg(tsquery)
  TO kalla_api, kalla_admin, kalla_worker;
GRANT EXECUTE ON FUNCTION content.rag_partition_maintain(), content.rag_ensure_profile_index(uuid, integer) TO kalla_worker;
-- The worker's nightly content retention (data-model §8): the ages and minimums live in the function.
GRANT EXECUTE ON FUNCTION content.rag_retention_content_step(integer) TO kalla_worker;
-- Editable embedding profiles (admin design 4.5): the admin resets an unused profile's cache and index after a change;
-- the worker inserts a chunk set only while the profile still has the values it embedded with (row held FOR SHARE).
GRANT EXECUTE ON FUNCTION content.rag_reset_profile(uuid) TO kalla_admin;
GRANT EXECUTE ON FUNCTION content.rag_lock_profile_if_unchanged(uuid, text, integer, boolean) TO kalla_worker;
GRANT EXECUTE ON FUNCTION config.rag_retention_config_step(integer) TO kalla_admin;
GRANT EXECUTE ON FUNCTION chat.rag_retention_chat_step(integer) TO kalla_admin;
GRANT EXECUTE ON FUNCTION content.rag_count_retrievals(date, uuid[], bytea[], uuid[], character varying[], integer[], integer[])
  TO kalla_api;
COMMIT;
