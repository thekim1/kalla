-- Källa 0.9.0: renames the database roles from before the rename to Källa (ume_rag_*) to kalla_* (docs/upgrade-guide.md,
-- "0.9.0"). Run once as the Postgres superuser, with kalla-api, kalla-admin, kalla-worker and kalla-migrate stopped (a
-- role that is connected cannot be renamed). Idempotent: a role already renamed is skipped. SCRAM passwords survive the
-- rename, so the services' passwords stay the same; grants, ownership, the database ragdb and the per-role settings follow
-- the role. The schema-admission table and function (public.kalla_service_fences, public.kalla_admit) are created anew
-- by kalla-migrate; the old ones (ume_rag_*) are dropped here.
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['migrator', 'api', 'admin', 'worker'] LOOP
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_rag_' || r)
       AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'kalla_' || r) THEN
      EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_rag_' || r, 'kalla_' || r);
      RAISE NOTICE 'renamed role ume_rag_% to kalla_%', r, r;
    END IF;
  END LOOP;
END $$;
\connect ragdb
DROP FUNCTION IF EXISTS public.ume_rag_admit(text);
DROP TABLE IF EXISTS public.ume_rag_service_fences;
