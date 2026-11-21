-- Källa 0.9.0: renames the database roles from before the rename to Källa (ume_rag_*) to kalla_* (docs/upgrade-guide.md,
-- "0.9.0"), as the Postgres superuser. The one-shot kalla-roles runs it before every kalla-migrate (Compose and the
-- Portainer stack, deploy/compose.rag.yaml), and deploy/upgrade/upgrade-to-0.9.sh in the old rag-postgres; by hand it
-- is the fallback (Kubernetes, or a kalla-roles that failed). Idempotent: a role already renamed is skipped, so on a
-- database with the new names it changes nothing. A session still connected under an old name is ended first, waiting
-- up to 10 s for it: Postgres renames a role beneath its other sessions, which would go on under the new name. Nothing
-- should be connected, as the old services use the old database or are stopped. SCRAM passwords survive the rename,
-- so the services' passwords stay the same; grants, ownership, the database ragdb and the per-role settings follow the
-- role. The schema-admission table and function (public.kalla_service_fences, public.kalla_admit) are created anew by
-- kalla-migrate; the old ones (ume_rag_*) are dropped here.
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['migrator', 'api', 'admin', 'worker'] LOOP
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_rag_' || r)
       AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'kalla_' || r) THEN
      PERFORM pg_terminate_backend(pid, 10000) FROM pg_stat_activity WHERE usename = 'ume_rag_' || r;
      EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_rag_' || r, 'kalla_' || r);
      RAISE NOTICE 'renamed role ume_rag_% to kalla_%', r, r;
    END IF;
  END LOOP;
END $$;
\connect ragdb
-- Without the notices of objects that are gone, which every run after the first would print.
SET client_min_messages = warning;
DROP FUNCTION IF EXISTS public.ume_rag_admit(text);
DROP TABLE IF EXISTS public.ume_rag_service_fences;
