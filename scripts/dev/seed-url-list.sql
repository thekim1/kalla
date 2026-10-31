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
--   ume_seed.source_name      URL-list source found by name in the collection: created, or its URL list replaced
--   ume_seed.enqueue          true: enqueue a manual sync as the admin's "sync now" does; false: leave it to the scheduler
--
-- Rows as the engine reads them (EngineTestKit.SeedUrlListAsync, SourceContexts): the source and its collection at level
-- public, the collection on the profile, the source on the platform url-list pipeline that the migrator seeds and
-- publishes (PipelineTemplateSeeder). The config triggers bump config."ConfigVersion" and write the outbox for every
-- row; the version row is locked first, as an admin config write does (ConfigWrites), so the change is one version.

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
    enqueue boolean := coalesce(nullif(current_setting('ume_seed.enqueue', true), '')::boolean, true);
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
    config_version bigint;
BEGIN
    IF urls IS NULL OR jsonb_typeof(urls) <> 'array' OR jsonb_array_length(urls) = 0 THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.urls must be a non-empty JSON array of URLs';
    END IF;
    settings := jsonb_build_object('urls', urls);

    IF profile_name IS NULL THEN
        RAISE EXCEPTION 'seed-url-list: ume_seed.profile (the embedding profile name) is required';
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

    SELECT "Version" INTO config_version FROM config."ConfigVersion" WHERE "Id" = 1;

    INSERT INTO seed_result ("Key", "Value", "Ordinal") VALUES
        ('department', department_id::text || ' (' || department_state || ')', 1),
        ('collection', collection_id::text || ' ' || collection_slug || ' (' || collection_state || ', public)', 2),
        ('embedding_profile', profile_id::text || ' ' || profile_name, 3),
        ('pipeline', pipeline_id::text || ' url-list, published version ' || version_id::text, 4),
        ('source', source_id::text || ' ' || source_name || ' (' || source_state || ', ' || jsonb_array_length(urls) || ' URLs)', 5),
        ('sync_job', CASE WHEN NOT enqueue THEN 'not enqueued (the scheduler runs it on the url-list schedule)'
                          WHEN job_id IS NULL THEN 'not enqueued (a sync of the source is already queued or running)'
                          ELSE job_id::text || ' (manual; the run id is the job id)' END, 6),
        ('config_version', config_version::text, 7),
        ('source_id', source_id::text, 8),
        ('collection_id', collection_id::text, 9),
        ('department_id', department_id::text, 10),
        ('sync_job_id', job_id::text, 11);
END
$seed$;

SELECT "Key", "Value" FROM seed_result ORDER BY "Ordinal";

COMMIT;
