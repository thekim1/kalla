-- Dev seed: a public URL-list source in the local AppHost database (P1a item 18, milestone 1), until the admin's set-up
-- endpoints (J7) exist. Run it through seed_url_list.py, which refuses a non-local database and sets the parameters;
-- tests/Ume.Rag.Worker.Tests/Engine/DevSeedScriptTests.cs runs this same file against TestPostgres.
--
-- Parameters, as session settings (set_config) before this file runs:
--   ume_seed.urls             JSON array of absolute http(s) URLs (required)
--   ume_seed.profile          embedding profile name (required): pilot-embedding (EmbeddingProfile:*) or dev-embedding
--   ume_seed.department_id    an existing department to use (optional; else ume_seed.department_name)
--   ume_seed.department_name  department found by name, created when missing
--   ume_seed.collection_slug  collection found by slug, created when missing (public, published, on the profile)
--   ume_seed.collection_name  the new collection's name
--   ume_seed.source_name      URL-list source found by name in the collection: created, or its settings replaced
--   ume_seed.pii_mode         the source's piiMode (contracts/sources/v1/url-list.schema.json): report (default; the
--                             municipality's own published site, Decisions table 2026-11-01) or hold
--   ume_seed.enqueue          true: enqueue a manual sync as the admin's "sync now" does; false: leave it to the scheduler
--   ume_seed.key_hash         with a key (--api-key): hex HMAC-SHA256 of the new key under Security:KeyPepper (KeyHasher.Hash);
--                             empty: no client and no key
--   ume_seed.key_prefix       the new key's display prefix (its first 15 characters, KeyHasher.Generate)
--   ume_seed.client_name      the internal client found by name, created when missing (ceiling public)
--
-- Rows as the engine reads them (EngineTestKit.SeedUrlListAsync, SourceContexts): the source and its collection at level
-- public, the collection on the profile, the source on the platform url-list pipeline that the migrator seeds and
-- publishes (PipelineTemplateSeeder). The config triggers bump config."ConfigVersion" and write the outbox for every
-- row; the version row is locked first, as an admin config write does (ConfigWrites), so the change is one version.
--
-- With a key, what rag-api's knowledge routes check (KnowledgeCallers, AccessPolicy steps 1-9, KeyChecks): an internal client
-- (Knowledge:OpenToClientKinds; the internal kind reaches any live collection directly, there is no link row in P1), ceiling
-- public, no history store; a secret key of it scoped to the collection (Keys.Scopes), enabled, expiring after 30 days; and
-- the public level's DataProtectionPolicies row, without which every public collection is refused (fail closed). The row
-- holds the plan §8a values for public with history mode none (P1a item 14's admin endpoint owns them later). A usable key of
-- the client named for the collection is kept: no second key. The Keys and Clients triggers bump the config version, so the
-- api's key store and snapshot pick the change up.

BEGIN;

SET LOCAL lock_timeout = '5s';

CREATE TEMP TABLE seed_result (
    "Key" text NOT NULL,
    "Value" text,
    "Ordinal" int NOT NULL
) ON COMMIT DROP;

DO $seed$
DECLARE
    actor constant text := 'dev:seed-url-list';
    urls jsonb := nullif(current_setting('ume_seed.urls', true), '')::jsonb;
    profile_name text := nullif(current_setting('ume_seed.profile', true), '');
    department_param text := nullif(current_setting('ume_seed.department_id', true), '');
    department_name text := coalesce(nullif(current_setting('ume_seed.department_name', true), ''), 'Pilot (dev seed)');
    collection_slug text := coalesce(nullif(current_setting('ume_seed.collection_slug', true), ''), 'pilot-public');
    collection_name text := coalesce(nullif(current_setting('ume_seed.collection_name', true), ''), 'Pilot: umea.se (public)');
    source_name text := coalesce(nullif(current_setting('ume_seed.source_name', true), ''), 'Pilot URL list');
    pii_mode text := coalesce(nullif(current_setting('ume_seed.pii_mode', true), ''), 'report');
    enqueue boolean := coalesce(nullif(current_setting('ume_seed.enqueue', true), '')::boolean, true);
    key_hash text := nullif(current_setting('ume_seed.key_hash', true), '');
    key_prefix text := nullif(current_setting('ume_seed.key_prefix', true), '');
    client_name text := coalesce(nullif(current_setting('ume_seed.client_name', true), ''), 'Pilot retrieval (dev seed)');
    settings jsonb;
    profile_id uuid;
    department_id uuid;
    department_state text := 'existing';
    collection record;
    collection_id uuid;
    collection_state text := 'existing';
    pipeline_id uuid;
    version_id uuid;
    source record;
    source_id uuid;
    source_state text;
    job_id uuid;
    policy_state text;
    client record;
    client_id uuid;
    client_state text;
    key_name text;
    key_id uuid;
    key_state text;
    key_expires timestamptz;
    config_version bigint;
BEGIN
    IF urls IS NULL OR jsonb_typeof(urls) <> 'array' OR jsonb_array_length(urls) = 0 THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.urls must be a non-empty JSON array of URLs';
    END IF;
    IF pii_mode NOT IN ('hold', 'report') THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.pii_mode must be hold or report, not %', pii_mode;
    END IF;
    settings := jsonb_build_object('urls', urls, 'piiMode', pii_mode);

    IF profile_name IS NULL THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.profile (the embedding profile name) is required';
    END IF;

    IF key_hash IS NOT NULL AND (key_hash !~ '^[0-9a-f]{64}$' OR key_prefix IS NULL OR key_prefix !~ '^ume-rag-sk-[0-9A-Za-z]{4}$') THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.key_hash must be 64 lower-case hex digits and ume_seed.key_prefix a secret key''s 15-character prefix';
    END IF;

    -- The admin's config write: the version lock first.
    PERFORM 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE;

    SELECT p."Id" INTO profile_id FROM config."EmbeddingProfiles" p WHERE p."Name" = profile_name AND p."State" = 'active';
    IF profile_id IS NULL THEN
        RAISE EXCEPTION 'seed-url-list: no active embedding profile %; profiles: %', profile_name,
            (SELECT coalesce(string_agg("Name" || ' (' || "State" || ')', ', ' ORDER BY "Name"), 'none') FROM config."EmbeddingProfiles");
    END IF;

    SELECT p."Id", p."PublishedVersionId" INTO pipeline_id, version_id
    FROM config."Pipelines" p
    JOIN config."PipelineVersions" v ON v."Id" = p."PublishedVersionId" AND v."State" = 'published'
    WHERE p."DepartmentId" IS NULL AND p."Name" = 'url-list' AND p."TemplateId" = 'url-list';
    IF pipeline_id IS NULL THEN
        RAISE EXCEPTION 'seed-url-list: no published platform pipeline url-list; start the AppHost once so the migrator seeds it';
    END IF;

    IF department_param IS NOT NULL THEN
        SELECT d."Id" INTO department_id FROM config."Departments" d WHERE d."Id" = department_param::uuid;
        IF department_id IS NULL THEN
            RAISE EXCEPTION 'seed-url-list: department % does not exist', department_param;
        END IF;
    ELSE
        SELECT d."Id" INTO department_id FROM config."Departments" d WHERE d."Name" = department_name ORDER BY d."Id" LIMIT 1;
        IF department_id IS NULL THEN
            department_id := gen_random_uuid();
            INSERT INTO config."Departments" ("Id", "Name", "CostCenterCode", "MemberGroups", "IsActive", "SyncedAt")
            VALUES (department_id, department_name, 'dev-seed-' || left(replace(department_id::text, '-', ''), 12), '{}', true, now());
            department_state := 'created';
        END IF;
    END IF;

    SELECT c."Id", c."DepartmentId", c."Classification", c."EmbeddingProfileId", c."DeletedAt" INTO collection
    FROM config."Collections" c WHERE c."Slug" = collection_slug;
    IF NOT FOUND THEN
        collection_id := gen_random_uuid();
        INSERT INTO config."Collections" ("Id", "Slug", "Name", "DepartmentId", "Classification", "EmbeddingProfileId", "RetrievalSettings",
                                          "PublishedAt", "UpdatedAt", "UpdatedBy")
        VALUES (collection_id, collection_slug, collection_name, department_id, 'public', profile_id, '{}', now(), now(), actor);
        collection_state := 'created';
    ELSIF collection."DeletedAt" IS NOT NULL OR collection."Classification" <> 'public' OR collection."EmbeddingProfileId" <> profile_id
          OR collection."DepartmentId" <> department_id THEN
        RAISE EXCEPTION 'seed-url-list: collection % exists but is not a live public collection of this department on profile %; use another slug',
            collection_slug, profile_name;
    ELSE
        collection_id := collection."Id";
    END IF;

    SELECT s."Id", s."Settings", s."State", s."PipelineId" INTO source
    FROM config."Sources" s
    WHERE s."CollectionId" = collection_id AND s."Name" = source_name AND s."Type" = 'url-list' AND s."DeletedAt" IS NULL
    ORDER BY s."Id" LIMIT 1;
    IF NOT FOUND THEN
        source_id := gen_random_uuid();
        INSERT INTO config."Sources" ("Id", "CollectionId", "Type", "Name", "Classification", "Settings", "PipelineId", "State", "UpdatedAt", "UpdatedBy")
        VALUES (source_id, collection_id, 'url-list', source_name, 'public', settings, pipeline_id, 'active', now(), actor);
        source_state := 'created';
    ELSE
        source_id := source."Id";
        IF source."Settings" = settings AND source."State" = 'active' AND source."PipelineId" = pipeline_id THEN
            source_state := 'unchanged';
        ELSE
            UPDATE config."Sources" SET "Settings" = settings, "State" = 'active', "PipelineId" = pipeline_id, "UpdatedAt" = now(), "UpdatedBy" = actor
            WHERE "Id" = source_id;
            source_state := 'updated';
        END IF;
    END IF;

    -- "Sync now" as the admin enqueues it (contracts/jobs/v1/sync.payload.schema.json; PostgresJobQueue's insert): dedup
    -- sync:{sourceId}, priority 10, the sync class's 3 attempts; the run's id is the job's. Null when a sync of the source is
    -- already queued or running.
    IF enqueue THEN
        INSERT INTO content."Jobs" ("Id", "Class", "Priority", "DepartmentId", "Classification", "DedupKey", "Payload", "MaxAttempts")
        VALUES (gen_random_uuid(), 'sync', 10, department_id, 'public', 'sync:' || source_id::text,
                jsonb_build_object('sourceId', source_id, 'kind', 'sync', 'trigger', 'manual'), 3)
        ON CONFLICT ("DedupKey") WHERE "State" IN ('queued', 'running') DO NOTHING
        RETURNING "Id" INTO job_id;
        IF job_id IS NOT NULL THEN
            PERFORM pg_notify('rag_jobs', 'sync');
        END IF;
    END IF;

    IF key_hash IS NOT NULL THEN
        INSERT INTO config."DataProtectionPolicies" ("Classification", "AllowedHistoryModes", "DefaultHistoryMode", "RetentionDefaultDays",
                                                     "RetentionMaxDays", "EncryptionRequired", "ReviewSecondApprover", "ClientsWithoutHistoryOnly",
                                                     "BlobMasterKeyName", "UpdatedAt", "UpdatedBy")
        VALUES ('public', ARRAY['none']::varchar(20)[], 'none', 90, 90, false, false, false, NULL, now(), actor)
        ON CONFLICT ("Classification") DO NOTHING;
        policy_state := CASE WHEN FOUND THEN 'created' ELSE 'existing' END;

        SELECT c."Id", c."Kind", c."ClassificationCeiling", c."StoresHistory", c."IsEnabled" INTO client
        FROM config."Clients" c WHERE c."Name" = client_name ORDER BY c."Id" LIMIT 1;
        IF NOT FOUND THEN
            client_id := gen_random_uuid();
            INSERT INTO config."Clients" ("Id", "Name", "Kind", "ClassificationCeiling", "StoresHistory", "AllowedOrigins", "ToolPolicy", "IsEnabled",
                                          "UpdatedAt", "UpdatedBy")
            VALUES (client_id, client_name, 'internal', 'public', false, '{}', 'none', true, now(), actor);
            client_state := 'created';
        ELSIF client."Kind" <> 'internal' OR client."ClassificationCeiling" <> 'public' OR client."StoresHistory" OR NOT client."IsEnabled" THEN
            RAISE EXCEPTION 'seed-url-list: client % exists but is not an enabled internal client with ceiling public and no history; use another --client-name',
                client_name;
        ELSE
            client_id := client."Id";
            client_state := 'existing';
        END IF;

        -- A usable key (ApiKey.GetStatus active: not revoked, enabled, not expired, not rotated) of the client for this collection.
        key_name := 'dev seed: ' || collection_slug;
        SELECT k."Id", k."ExpiresAt" INTO key_id, key_expires
        FROM config."Keys" k
        WHERE k."ClientId" = client_id AND k."Name" = key_name AND k."Kind" = 'sk' AND k."IsEnabled" AND k."RevokedAt" IS NULL
              AND k."GraceUntil" IS NULL AND (k."ExpiresAt" IS NULL OR k."ExpiresAt" > now())
              AND k."Scopes" = jsonb_build_object('assistantIds', '[]'::jsonb, 'collectionIds', jsonb_build_array(collection_id))
        ORDER BY k."CreatedAt" DESC, k."Id" LIMIT 1;
        IF key_id IS NULL THEN
            key_id := gen_random_uuid();
            key_expires := now() + interval '30 days';
            INSERT INTO config."Keys" ("Id", "ClientId", "Kind", "Name", "Description", "Prefix", "KeyHash", "Scopes", "AllowedOrigins", "AllowedIpRanges",
                                       "IsEnabled", "CreatedAt", "CreatedBy", "ExpiresAt")
            VALUES (key_id, client_id, 'sk', key_name, 'Dev seed key for /v1/retrieve on ' || collection_slug, key_prefix, key_hash,
                    jsonb_build_object('assistantIds', '[]'::jsonb, 'collectionIds', jsonb_build_array(collection_id)), '{}', '{}',
                    true, now(), actor, key_expires);
            key_state := 'created';
        ELSE
            key_state := 'existing';
        END IF;
    END IF;

    SELECT "Version" INTO config_version FROM config."ConfigVersion" WHERE "Id" = 1;

    INSERT INTO seed_result ("Key", "Value", "Ordinal") VALUES
        ('department', department_id::text || ' (' || department_state || ')', 1),
        ('collection', collection_id::text || ' ' || collection_slug || ' (' || collection_state || ', public)', 2),
        ('embedding_profile', profile_id::text || ' ' || profile_name, 3),
        ('pipeline', pipeline_id::text || ' url-list, published version ' || version_id::text, 4),
        ('source', source_id::text || ' ' || source_name || ' (' || source_state || ', ' || jsonb_array_length(urls) || ' URLs, piiMode '
                    || pii_mode || ')', 5),
        ('sync_job', CASE WHEN NOT enqueue THEN 'not enqueued (the scheduler runs it on the url-list schedule)'
                          WHEN job_id IS NULL THEN 'not enqueued (a sync of the source is already queued or running)'
                          ELSE job_id::text || ' (manual; the run id is the job id)' END, 6),
        ('config_version', config_version::text, 7),
        ('source_id', source_id::text, 8),
        ('collection_id', collection_id::text, 9),
        ('department_id', department_id::text, 10),
        ('sync_job_id', job_id::text, 11);
    IF key_hash IS NOT NULL THEN
        INSERT INTO seed_result ("Key", "Value", "Ordinal") VALUES
            ('level_policy', 'public (' || policy_state || ', history mode none)', 12),
            ('client', client_id::text || ' ' || client_name || ' (' || client_state || ', internal, ceiling public)', 13),
            ('api_key', key_id::text || ' ' || key_name || ' (' || key_state || ', scoped to ' || collection_slug || ', expires '
                        || to_char(key_expires AT TIME ZONE 'UTC', 'YYYY-MM-DD') || CASE WHEN key_state = 'existing'
                        THEN '; its plaintext was shown when it was created and is not printed again)' ELSE ')' END, 14),
            ('client_id', client_id::text, 15),
            ('api_key_id', key_id::text, 16),
            ('api_key_state', key_state, 17);
    END IF;
END
$seed$;

SELECT "Key", "Value" FROM seed_result ORDER BY "Ordinal";

COMMIT;
