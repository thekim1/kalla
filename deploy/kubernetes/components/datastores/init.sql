-- Bootstrap, run once by the image entrypoint as the bootstrap superuser (mounted as
-- /docker-entrypoint-initdb.d/10-roles.sql; repo-and-deployment §5.2). Creates the login roles from secret files, the
-- database ragdb owned by the DDL role, the vector extension (not a trusted extension) and the role caps and defaults of
-- data-model §1.1. Schemas, tables and grants come from the migration service (migrations, then app-roles.sql).
SET log_statement = 'none';
DO $$
BEGIN
  EXECUTE format('CREATE ROLE kalla_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/migrator_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE kalla_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/api_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE kalla_admin LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/admin_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE kalla_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/worker_password'), E'\r\n '));
END $$;
CREATE DATABASE ragdb OWNER kalla_migrator;
REVOKE ALL ON DATABASE ragdb FROM PUBLIC;
GRANT CONNECT ON DATABASE ragdb TO kalla_api, kalla_admin, kalla_worker;

-- Caps and timeouts (data-model §1.1). Pool sizes per instance: api 12, admin 5, worker 10, migrator 2, each + 1 LISTEN
-- connection; the limits leave room for a second api and admin instance and the worker's stop-then-start.
ALTER ROLE kalla_migrator CONNECTION LIMIT 3;
ALTER ROLE kalla_migrator IN DATABASE ragdb SET lock_timeout = '10s';
ALTER ROLE kalla_migrator IN DATABASE ragdb SET maintenance_work_mem = '1GB';   -- HNSW builds
ALTER ROLE kalla_api CONNECTION LIMIT 26;
ALTER ROLE kalla_api IN DATABASE ragdb SET statement_timeout = '5s';
ALTER ROLE kalla_api IN DATABASE ragdb SET lock_timeout = '1s';
ALTER ROLE kalla_api IN DATABASE ragdb SET idle_in_transaction_session_timeout = '10s';
ALTER ROLE kalla_admin CONNECTION LIMIT 12;
ALTER ROLE kalla_admin IN DATABASE ragdb SET statement_timeout = '30s';
ALTER ROLE kalla_admin IN DATABASE ragdb SET lock_timeout = '5s';
ALTER ROLE kalla_admin IN DATABASE ragdb SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE kalla_worker CONNECTION LIMIT 12;
ALTER ROLE kalla_worker IN DATABASE ragdb SET statement_timeout = '60s';
ALTER ROLE kalla_worker IN DATABASE ragdb SET lock_timeout = '5s';
ALTER ROLE kalla_worker IN DATABASE ragdb SET idle_in_transaction_session_timeout = '60s';

-- Retrieval defaults (knowledge design §6.4): no retrieval statement runs SET LOCAL. The api searches, the worker runs
-- the retrieval evaluator, the admin runs the test panel.
ALTER ROLE kalla_api IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE kalla_api IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE kalla_api IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
ALTER ROLE kalla_worker IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE kalla_worker IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE kalla_worker IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
ALTER ROLE kalla_admin IN DATABASE ragdb SET hnsw.ef_search = 100;
ALTER ROLE kalla_admin IN DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER ROLE kalla_admin IN DATABASE ragdb SET hnsw.max_scan_tuples = 100000;

\connect ragdb
-- The public schema keeps only the extension (data-model §1).
CREATE EXTENSION vector;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO kalla_migrator;
GRANT USAGE ON SCHEMA public TO kalla_api, kalla_admin, kalla_worker;
