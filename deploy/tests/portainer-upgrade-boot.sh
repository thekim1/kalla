#!/usr/bin/env bash
# Boot test of an upgraded Portainer stack (release.yml): starts an earlier release's compose.portainer.yaml with the
# variables an operator had for it, then applies the new stack file over it the way Portainer's "Update the stack" does
# (the same stack name, the same variables, `up -d` with the new file), and checks that the upgrade is that one step:
#
#   - the earlier release boots: both migrations completed, kalla-api and kalla-admin healthy
#   - the new file starts over the old volumes with no variable added, changed or removed, and nothing stopped by hand:
#     both bootstraps and both migrations complete, every long-running service is up (healthy where it has a health
#     check) and none restarted
#   - every container runs the image the new file names (nothing of the earlier release is left running)
#   - the database is the same one, moved forward: kalla-migrate's log names its migration, the migration history is
#     longer than before, and the embedding profiles and the settings row of the earlier release are still there
#   - kalla-bootstrap kept what it had generated: the CA certificate, the pepper and the Data Protection certificate
#     are the files of the earlier release (compared by checksum, never printed)
#   - kalla-api: /health/ready = 200 and /version (with --version); kalla-admin: / = 200
#   - with --expect-fenced ROLE: kalla-migrate's log names that service role as stopped for a migration the earlier
#     version can't run beside (data-model §10), so the operator stops nothing
#   - the end of kalla-bootstrap's log (its checklist) is printed, as the upgrade guide tells the operator to read it
#
# With --rename-0.9 (an earlier release before 0.9.0, the rename to Källa and Fors 0.12) the upgrade is the steps of
# 0.9.0's docs/portainer.md, "Upgrade", instead of that one step: the services are stopped and the roles renamed in the
# two old databases (deploy/upgrade/0.9-rename-roles.sql in rag-postgres, Fors's statement in postgres), then the stack
# is stopped, its variables renamed (rename-0.9.sh; the test's ports and binds too) and the new file applied once with
# the old containers pruned (both bootstraps copy the old volumes). The checks after the upgrade are the same; nothing
# is fenced, as the old services were stopped.
#
#   deploy/tests/portainer-upgrade-boot.sh OLD-STACK.yaml NEW-STACK.yaml --from-env FILE [options]
#
# OLD-STACK.yaml is the earlier release's asset (its images pinned by digest), NEW-STACK.yaml the one under test.
# --from-env is the earlier release's variables as an operator filled them in (fixtures/stack-<version>.env); this
# test adds only its ports and binds (127.0.0.1) and keeps them through the upgrade.
#
# Options:
#   --version X.Y.Z         kalla-api's /version must report it after the upgrade
#   --expect-fenced ROLE    a service role kalla-migrate must report as stopped (repeatable), e.g. kalla_admin
#   --rename-0.9            the earlier release is from before 0.9.0: upgrade by 0.9.0's steps (above)
#   --port-offset N         added to every default port [12000]
#   --compose-file FILE     an extra Compose file after either stack (repeatable), e.g. fixed subnets
#   --project NAME          the Compose project, which is the stack's name [kalla-portainer-upgrade]
#   --wait-timeout SECONDS  for each start and each wait [900]
#   --keep                  leave the stack running
#
# Needs Docker with Compose v2 (Engine 26 or later), curl and jq, and pull access to both stacks' images. An earlier
# run's stack of the same project is removed first, volumes included. Exit 0 when every check passed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

old=""; new=""; from_env=""; version=""; offset=12000; project=kalla-portainer-upgrade
wait_timeout=900; keep=0; files=(); fenced=(); rename=0
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --from-env) from_env="$2"; shift 2;;
    --version) version="$2"; shift 2;;
    --expect-fenced) fenced+=("$2"); shift 2;;
    --rename-0.9) rename=1; shift;;
    --port-offset) offset="$2"; shift 2;;
    --compose-file) files+=(-f "$2"); shift 2;;
    --project) project="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --keep) keep=1; shift;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) if [ -z "$old" ]; then old="$1"; elif [ -z "$new" ]; then new="$1"; else usage 1; fi; shift;;
  esac
done
if [ -z "$old" ] || [ ! -r "$old" ] || [ -z "$new" ] || [ ! -r "$new" ]; then usage 1; fi
[ -n "$from_env" ] && [ -r "$from_env" ] || { echo "error: --from-env FILE is required" >&2; exit 1; }
for tool in docker curl jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done

# The stacks' pins and the variables file are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$|^(UME|FORS|KALLA)_|^COMPOSE_PROFILES$' || true)

work="$(mktemp -d)"
api_port=$((8543 + offset)); admin_port=$((9543 + offset))
cat > "$work/test.env" <<EOF
FORS_GATEWAY_PORT=$((8443 + offset))
FORS_ADMIN_PORT=$((9443 + offset))
FORS_DATA_PORT=$((10443 + offset))
KALLA_API_PORT=$api_port
KALLA_ADMIN_PORT=$admin_port
KALLA_OPENWEBUI_PORT=$((3000 + offset))
FORS_GATEWAY_BIND=127.0.0.1
FORS_ADMIN_BIND=127.0.0.1
FORS_DATA_BIND=127.0.0.1
KALLA_API_BIND=127.0.0.1
KALLA_ADMIN_BIND=127.0.0.1
KALLA_OPENWEBUI_BIND=127.0.0.1
EOF
# The variables the stack runs with: the earlier release's until the upgrade renames them (--rename-0.9). Before 0.9.0
# the test's own are UME_* and UME_RAG_*, and Källa's services and volumes are rag-*.
env_file="$from_env"; test_env="$work/test.env"; pg=kalla-postgres; migrate=kalla-migrate; volume_prefix=kalla
if [ "$rename" = 1 ]; then
  sed -E -e 's/^KALLA_/UME_RAG_/' -e 's/^FORS_/UME_/' "$work/test.env" > "$work/test-old.env"
  test_env="$work/test-old.env"; pg=rag-postgres; migrate=rag-migrate; volume_prefix=rag
fi

stack="$old"
dc() { docker compose -p "$project" -f "$stack" ${files[@]+"${files[@]}"} --env-file "$env_file" --env-file "$test_env" "$@"; }
ok() { echo "ok: $*"; }
fail() {
  echo "error: $*" >&2
  dc ps -a >&2 || true
  # A setup code is worth platform admin while setup is open: cut it from the logs printed here.
  dc logs --no-color --tail 60 2>&1 | sed -E 's/(Setup code: )[0-9A-Za-z-]+/\1(hidden)/' >&2 || true
  exit 1
}
cleanup() {
  if [ "$keep" = 1 ]; then
    echo "Kept: docker compose -p $project -f $stack --env-file $env_file --env-file $test_env ..."
    return
  fi
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# start WHAT: `up -d` of $stack the way Portainer does it (--remove-orphans: "Prune services"), then --wait on the
# long-running services only (a one-shot that exits counts as a failure for --wait over the whole stack;
# portainer-boot.sh has the reasoning).
start() {
  local began=$SECONDS
  dc pull --quiet || fail "could not pull the images of $1"
  timeout "$wait_timeout" docker compose -p "$project" -f "$stack" ${files[@]+"${files[@]}"} --env-file "$env_file" \
    --env-file "$test_env" up -d --remove-orphans || fail "$1 did not start within ${wait_timeout}s"
  mapfile -t long_running < <(dc config --format json | jq -r '.services | to_entries[] | select((.value.restart // "no") != "no") | .key')
  [ "${#long_running[@]}" -gt 0 ] || fail "$1 has no long-running services"
  dc up -d --no-deps --no-recreate --wait --wait-timeout "$wait_timeout" "${long_running[@]}" \
    || fail "the long-running services of $1 did not become healthy within ${wait_timeout}s"
  ok "$1: every long-running service up, every one with a health check healthy ($((SECONDS - began)) s)"
}
# completed: both bootstraps and both migrations exited 0; every other container runs and none restarted.
completed() {
  local name state exit_code restarts
  while read -r name state exit_code restarts; do
    case "$name" in
      *-bootstrap-1|*-migrations-1|*-rag-migrate-1|*-kalla-migrate-1)
        [ "$state $exit_code" = "exited 0" ] || fail "$name is $state (exit $exit_code), expected to have completed";;
      *-rag-openwebui-setup-1|*-kalla-openwebui-setup-1)
        [ "$state" = running ] || [ "$state $exit_code" = "exited 0" ] || fail "$name is $state (exit $exit_code)";;
      *)
        [ "$state" = running ] || fail "$name is $state (exit $exit_code), expected running"
        [ "$restarts" = 0 ] || fail "$name restarted $restarts times";;
    esac
  done < <(dc ps -a -q | xargs docker inspect --format '{{.Name}} {{.State.Status}} {{.State.ExitCode}} {{.RestartCount}}')
}
sql() { dc exec -T "$pg" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d ragdb -c "$1"; }
# kept_files: checksums of what kalla-bootstrap generates once and must keep (the CA, the pepper, the Data Protection
# certificate), read from the volumes (rag-* before 0.9.0) through a container; names and checksums only.
kept_files() {
  docker run --rm --network none -v "${project}_${volume_prefix}-certs:/c:ro" \
    -v "${project}_${volume_prefix}-secrets:/s:ro" --entrypoint sh \
    docker.io/alpine/openssl@sha256:59c5cb51e536d40587667229468b007a2a6cae1705397e59e5f7774c08b74029 -c \
    'for f in /c/ca.pem /c/data-protection.pfx /s/.pepper; do [ -e "$f" ] && sha256sum "$f"; done; true'
}
# migration: the migration kalla-migrate's last "Database schema is at migration ..." line names (a JSON log line).
migration() { dc logs --no-color --no-log-prefix "$migrate" 2>/dev/null | grep -F 'Database schema is at migration' | grep -o '"Migration":"[^"]*"' | tail -n 1 | cut -d'"' -f4; }

dc down -v --remove-orphans >/dev/null 2>&1 || true

echo "== the earlier release"
dc config --images | grep -E 'kalla/|ume-rag' | sort -u | sed 's/^/  /'
start "the earlier release"
completed
ok "the earlier release: both bootstraps and both migrations completed, nothing restarted"
migration_before="$(migration)"
[ -n "$migration_before" ] || fail "the earlier release's kalla-migrate names no migration in its log"
history_before="$(sql 'SELECT count(*) FROM config."__EFMigrationsHistory"')"
profiles_before="$(sql 'SELECT coalesce(string_agg("Id"::text, chr(44) ORDER BY "Id"), chr(45)) FROM config."EmbeddingProfiles"')"
settings_before="$(sql 'SELECT count(*) FROM config."Settings"')"
files_before="$(kept_files)"
[ "$(wc -l <<< "$files_before")" -eq 3 ] || fail "the earlier release's volumes lack the CA, the pepper or the Data Protection certificate"
echo "  schema at $migration_before ($history_before migrations), embedding profiles: $profiles_before"

if [ "$rename" = 1 ]; then
  echo "== the upgrade to 0.9.0: rename the roles in the old databases, stop the stack, rename its variables, update it"
  # docs/portainer.md, "Upgrade", step 7: the services stopped, the two old databases running for their consoles.
  dc stop >/dev/null || fail "could not stop the earlier release"
  dc up -d --no-deps --wait --wait-timeout "$wait_timeout" postgres rag-postgres || fail "the old databases did not start"
  # The Postgres superuser with the bootstrap password, the statements on stdin, as the step says.
  # shellcheck disable=SC2016 # expanded in the container
  as_superuser='PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q'
  dc exec -T rag-postgres sh -c "$as_superuser" < "$here/../upgrade/0.9-rename-roles.sql" \
    || fail "the roles in rag-postgres could not be renamed"
  # Fors 0.12's guide, "Portainer", step 3 (its deploy/upgrade/0.12-rename-roles.sql).
  dc exec -T postgres sh -c "$as_superuser" <<'SQL' || fail "the roles in the gateway's postgres could not be renamed"
DO $$ DECLARE r text; BEGIN FOREACH r IN ARRAY ARRAY['migrator','gateway','admin','data','keycloak'] LOOP
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_' || r) AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_' || r)
  THEN EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_' || r, 'fors_' || r); END IF; END LOOP; END $$;
SQL
  ok "the roles are renamed in the old rag-postgres and in the gateway's postgres"
  # Steps 8 and 9: the stack stopped, its variables renamed, the new file applied once with "Prune services".
  dc stop >/dev/null || fail "could not stop the old databases"
  "$here/rename-0.9.sh" "$from_env" "$work/renamed.env"
  env_file="$work/renamed.env"; test_env="$work/test.env"; pg=kalla-postgres; migrate=kalla-migrate; volume_prefix=kalla
  stack="$new"
  dc pull --quiet || fail "could not pull the images of the new release"
else
  echo "== the upgrade: the new stack file over the same stack, variables unchanged"
  stack="$new"
fi
unpinned="$(dc config --images | grep -vE '@sha256:[0-9a-f]{64}$' || true)"
[ -z "$unpinned" ] || fail "images not pinned by digest: $unpinned"
dc config --images | grep kalla/ | sort -u | sed 's/^/  /'
start "the new release"
completed
ok "the new release: both bootstraps and both migrations completed, every other container runs and none restarted"
if [ "$rename" = 1 ]; then
  dc logs --no-color --no-log-prefix kalla-bootstrap 2>/dev/null | grep -F 'copied the database' >/dev/null \
    || fail "kalla-bootstrap did not copy the old volumes (no 'copied the database' line)"
  ok "kalla-bootstrap copied the rag-* volumes into the kalla-* volumes, in one update"
fi

# Every service's container runs the image the new file names.
while read -r svc want; do
  id="$(dc ps -a -q "$svc")"
  [ -n "$id" ] || fail "the new stack has no container for $svc"
  got="$(docker inspect --format '{{.Config.Image}}' "$id")"
  [ "$got" = "$want" ] || fail "$svc still runs $got, the new file names $want"
done < <(dc config --format json | jq -r '.services | to_entries[] | "\(.key) \(.value.image)"')
ok "every container runs the image the new file names"

migration_after="$(migration)"
history_after="$(sql 'SELECT count(*) FROM config."__EFMigrationsHistory"')"
[ -n "$migration_after" ] || fail "kalla-migrate names no migration in its log"
[ "$history_after" -ge "$history_before" ] || fail "the migration history shrank: $history_before before, $history_after after"
[ "$migration_after" != "$migration_before" ] || [ "$history_after" = "$history_before" ] \
  || fail "the history grew but kalla-migrate still names $migration_before"
ok "the schema moved from $migration_before to $migration_after ($history_before to $history_after migrations)"
profiles_after="$(sql 'SELECT coalesce(string_agg("Id"::text, chr(44) ORDER BY "Id"), chr(45)) FROM config."EmbeddingProfiles"')"
[ "$profiles_after" = "$profiles_before" ] || fail "the embedding profiles changed: '$profiles_before' before, '$profiles_after' after"
[ "$(sql 'SELECT count(*) FROM config."Settings"')" = "$settings_before" ] || fail "the settings row changed in number"
ok "the embedding profiles and the settings row are the earlier release's"
[ "$(kept_files)" = "$files_before" ] || fail "kalla-bootstrap replaced the CA, the pepper or the Data Protection certificate"
ok "the CA, the pepper and the Data Protection certificate are the earlier release's"

for role in ${fenced[@]+"${fenced[@]}"}; do
  dc logs --no-color --no-log-prefix kalla-migrate 2>/dev/null | grep -F "stops the services of role $role" >/dev/null \
    || fail "kalla-migrate's log has no 'stops the services of role $role' line: the earlier version's service was not fenced"
  ok "kalla-migrate stopped $role itself for a migration the earlier version can't run beside"
done

echo "== the services"
ready="$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:$api_port/health/ready" || true)"
[ "$ready" = 200 ] || fail "kalla-api's /health/ready answers $ready"
ok "kalla-api is ready"
if [ -n "$version" ]; then
  got="$(curl -sk "https://127.0.0.1:$api_port/version" | jq -r '.informationalVersion // empty' 2>/dev/null || true)"
  case "$got" in "$version"|"$version+"*) ;; *) fail "kalla-api's /version reports '$got', expected $version";; esac
  ok "kalla-api reports version $version"
fi
admin="$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:$admin_port/" || true)"
[ "$admin" = 200 ] || fail "kalla-admin's / answers $admin"
ok "kalla-admin serves the admin UI"

echo "== the end of kalla-bootstrap's log (what the upgrade guide tells the operator to read)"
dc logs --no-color --no-log-prefix --tail 25 kalla-bootstrap 2>/dev/null | sed 's/^/  /'
echo PASS
