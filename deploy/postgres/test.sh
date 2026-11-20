#!/usr/bin/env bash
# Image test for kalla/postgres (repo-and-deployment §5.2; CI job `images` in .github/workflows/verify.yml).
# Runs the image hardened as in Compose (uid 999, read-only root file system, tmpfs /tmp and /var/run/postgresql, no
# capabilities) with deploy/postgres/postgresql.conf, then checks pgvector, an HNSW build on 1024-dim vectors,
# pg_stat_statements, wal_compression=lz4, TLS, WAL archiving with pgBackRest from inside the container (stanza-create,
# check), a full backup through `docker exec`, and a restore in a one-off container of the same image.
# init.sql and pg_hba.conf are stream B's (P0 #6), so the test uses the bootstrap superuser and writes its own minimal
# pg_hba.conf.
#
# Usage: deploy/postgres/test.sh [image]   (default: kalla/postgres:latest)
set -euo pipefail

image="${1:-kalla/postgres:latest}"
expected_vector=0.8.7
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
id="kalla-pgtest-$$"
work="$(mktemp -d)"
password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
main="$id-main"
restored="$id-restored"

cleanup() {
  docker rm -f "$main" "$restored" >/dev/null 2>&1 || true
  docker volume rm "$id-data" "$id-restored-data" "$id-repo" "$id-certs" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  for c in "$main" "$restored"; do
    docker ps -a --format '{{.Names}}' | grep -qx "$c" && { echo "--- logs of $c" >&2; docker logs --tail 40 "$c" >&2; }
  done
  exit 1
}
ok() { echo "ok: $*"; }

# psql as the bootstrap superuser over the Unix socket (peer), inside the given container.
sql() { local c="$1"; shift; docker exec -i "$c" psql -X -q -t -A -v ON_ERROR_STOP=1 -U postgres -d ragdb "$@"; }

wait_ready() {
  local c="$1"
  for _ in $(seq 1 120); do
    # TCP only: the entrypoint's temporary init server listens on the socket alone.
    docker exec "$c" pg_isready -q -h 127.0.0.1 -d ragdb 2>/dev/null && return 0
    [ "$(docker inspect -f '{{.State.Running}}' "$c" 2>/dev/null)" = true ] || fail "$c exited before it was ready"
    sleep 1
  done
  fail "$c not ready after 120 s"
}

# Test-only pg_hba.conf: the superuser over the socket by peer (as pgBackRest connects) and over TLS by password.
chmod 0755 "$work"
cat > "$work/pg_hba.conf" <<'EOF'
local   all  postgres                 peer
hostssl all  postgres  127.0.0.1/32   scram-sha-256
host    all  all       all            reject
EOF
# A local, encrypted test repository on a volume at pgBackRest's default repository path.
cat > "$work/repo.conf" <<EOF
[global]
repo1-type=posix
repo1-path=/var/lib/pgbackrest
repo1-cipher-type=aes-256-cbc
repo1-cipher-pass=$password
EOF
chmod 0644 "$work/pg_hba.conf" "$work/repo.conf"

for v in data restored-data repo certs; do docker volume create "$id-$v" >/dev/null; done

# A throwaway self-signed certificate owned by uid 999, as the server requires of its key.
docker run --rm --user 0 --entrypoint sh -v "$id-certs:/run/certs" "$image" -c '
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=postgres \
    -keyout /run/certs/postgres.key -out /run/certs/postgres.crt 2>/dev/null
  cp /run/certs/postgres.crt /run/certs/ca.pem
  chown 999:999 /run/certs/*
  chmod 0600 /run/certs/postgres.key' || fail "could not create the test certificate"

# The Compose hardening (x-hardened, repo-and-deployment §6.2, §6.3) and mounts; $1 is the data volume.
run_flags() {
  printf '%s\n' --read-only --user 999:999 --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp --tmpfs /var/run/postgresql:uid=999,gid=999,mode=0700 --shm-size 1g \
    -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=ragdb \
    -v "$1:/var/lib/postgresql/data" -v "$id-repo:/var/lib/pgbackrest" -v "$id-certs:/run/certs:ro" \
    -v "$here/postgresql.conf:/etc/postgresql/postgresql.conf:ro" \
    -v "$work/pg_hba.conf:/run/config/pg_hba.conf:ro" \
    -v "$work/repo.conf:/etc/pgbackrest/conf.d/repo.conf:ro"
}
mapfile -t main_flags < <(run_flags "$id-data")
mapfile -t restored_flags < <(run_flags "$id-restored-data")
start() { docker run -d --name "$1" "${@:2}" "$image" postgres -c config_file=/etc/postgresql/postgresql.conf >/dev/null; }

echo "image: $image"
start "$main" "${main_flags[@]}" || fail "could not start $main"
wait_ready "$main"
ok "started with postgresql.conf on a read-only root file system"

# User, read-only root, every process as 999.
[ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$main")" = true ] || fail "root file system is not read-only"
# /var/lib/postgresql is owned by 999 and not a volume, so only the read-only root stops the write.
docker exec "$main" touch /var/lib/postgresql/x 2>/dev/null && fail "the root file system is writable"
[ "$(docker exec "$main" id -u)" = 999 ] || fail "container user is not 999"
others="$(docker top "$main" -eo pid,uid,args | awk 'NR > 1 && $2 != 999')"
[ -z "$others" ] || fail "processes not running as 999: $others"
ok "uid 999 for every process, root file system read-only"
backrest="$(docker exec "$main" pgbackrest version)" || fail "pgbackrest does not run in the image"
echo "   $backrest"

# pgvector
sql "$main" -c 'CREATE EXTENSION IF NOT EXISTS vector' >/dev/null
v="$(sql "$main" -c "SELECT extversion FROM pg_extension WHERE extname = 'vector'")"
[ "$v" = "$expected_vector" ] || fail "vector extversion is '$v', expected $expected_vector"
ok "vector $v"

# HNSW on 1024-dim vectors (the BGE-M3 dimension). The reference to i makes each row's vector its own random draw.
sql "$main" <<'EOF' || fail "HNSW build failed"
CREATE TABLE image_test (id bigint PRIMARY KEY, embedding vector(1024) NOT NULL);
INSERT INTO image_test
SELECT i, v.embedding
FROM generate_series(1, 2000) AS i,
     LATERAL (SELECT array_agg(random())::vector(1024) AS embedding FROM generate_series(1, 1024) WHERE i > 0) AS v;
CREATE INDEX image_test_hnsw ON image_test USING hnsw (embedding vector_cosine_ops);
EOF
plan="$(sql "$main" -c 'SET enable_seqscan = off' -c \
  'EXPLAIN SELECT id FROM image_test ORDER BY embedding <=> (SELECT embedding FROM image_test WHERE id = 1) LIMIT 5')"
grep -q 'image_test_hnsw' <<<"$plan" || fail "the nearest-neighbour query does not use the HNSW index: $plan"
# HNSW is approximate: on 2000 random 1024-dim vectors the default ef_search (40) can miss even an exact match, so the
# self-match check widens the search for this one query (it proves the index answers, not its recall).
nearest="$(sql "$main" -c 'SET hnsw.ef_search = 1000' -c \
  'SELECT id FROM image_test ORDER BY embedding <=> (SELECT embedding FROM image_test WHERE id = 1) LIMIT 1')"
[ "$nearest" = 1 ] || fail "nearest neighbour of row 1 is '$nearest'"
ok "HNSW index built on 2000 x vector(1024), used by the nearest-neighbour query"

# pg_stat_statements
grep -q pg_stat_statements <<<"$(sql "$main" -c 'SHOW shared_preload_libraries')" \
  || fail "pg_stat_statements is not preloaded"
sql "$main" -c 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements' >/dev/null
[ "$(sql "$main" -c 'SELECT count(*) > 0 FROM pg_stat_statements')" = t ] || fail "pg_stat_statements is empty"
ok "pg_stat_statements preloaded and collecting"

# wal_compression
w="$(sql "$main" -c 'SHOW wal_compression')"
[ "$w" = lz4 ] || fail "wal_compression is '$w'"
ok "wal_compression = lz4"

# TLS
s="$(docker exec -e PGPASSWORD="$password" "$main" psql -X -t -A \
  "host=127.0.0.1 dbname=ragdb user=postgres sslmode=verify-ca sslrootcert=/run/certs/ca.pem" \
  -c 'SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()')" || fail "TLS connection failed"
[ "$s" = t ] || fail "the TCP connection is not TLS"
ok "ssl = on (verify-ca connection)"

# WAL archiving with pgBackRest from inside the container
docker exec "$main" pgbackrest --stanza=ragdb stanza-create || fail "stanza-create failed"
docker exec "$main" pgbackrest --stanza=ragdb check || fail "pgbackrest check failed"
a="$(sql "$main" -c 'SELECT archived_count > 0 AND last_archived_wal IS NOT NULL FROM pg_stat_archiver')"
[ "$a" = t ] || fail "pg_stat_archiver shows no archived WAL"
ok "archive_command pushed WAL to the test repository (pgbackrest check)"

# A full backup as rag-backup.timer runs it (compose exec), then a row written after it that only WAL can restore.
docker exec "$main" pgbackrest --stanza=ragdb --type=full --start-fast backup || fail "full backup failed"
sql "$main" -c "CREATE TABLE after_backup AS SELECT 'archived' AS marker" >/dev/null
docker exec "$main" pgbackrest --stanza=ragdb check || fail "pgbackrest check after the backup failed"
docker exec "$main" pgbackrest --stanza=ragdb info | sed 's/^/   /'
ok "full backup taken inside the running container"
docker stop "$main" >/dev/null

# Restore in a one-off container of the same image onto an empty volume, then start Postgres on it.
docker run --rm "${restored_flags[@]}" --entrypoint pgbackrest "$image" --stanza=ragdb restore \
  || fail "restore in a one-off container failed"
ok "restored in a one-off container (read-only root, uid 999)"
start "$restored" "${restored_flags[@]}" || fail "could not start $restored"
wait_ready "$restored"
for _ in $(seq 1 60); do
  [ "$(sql "$restored" -c 'SELECT pg_is_in_recovery()')" = f ] && break
  sleep 1
done
[ "$(sql "$restored" -c 'SELECT pg_is_in_recovery()')" = f ] || fail "the restored server did not leave recovery"
[ "$(sql "$restored" -c 'SELECT count(*) FROM image_test')" = 2000 ] || fail "restored image_test is incomplete"
[ "$(sql "$restored" -c 'SELECT marker FROM after_backup')" = archived ] || fail "the post-backup row was not replayed"
ok "restored server replayed the archived WAL and was promoted"

echo "PASS: $image"
