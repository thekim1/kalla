#!/usr/bin/env bash
# The upgrade of a Compose host that differs from the bundle in every way at once (Decisions "Compose host layout and
# upgrades", #1101 D7; release.yml, the dry run only): an earlier release's bundle is started with
#
#   - a project of its own (kalla-odd, not kalla), and published ports of its own (18543, 19543)
#   - the database on a bind-backed volume and the blobs on a direct bind, both from an overlay of the operator's own
#     outside the bundles (its COMPOSE_FILE names it), so the data is on host paths, not in Docker's volumes
#   - its env file in the old bundle's deploy/.env, from before the state directory
#
# then a marker table, a blob and the key pepper are noted, and the host is upgraded as docs/upgrade-guide.md says
# (*Upgrade procedure (Compose host)*): the new bundle's doctor.sh against the live env file, the env file copied into
# the new deploy/ and the new init-deployment.sh run without options (it moves the file to the state directory and
# points COMPOSE_FILE at the new bundle, keeping the site overlay), `up --wait`, and the doctor again. Checked after:
#
#   - the same project, the same published ports, the new version on /version
#   - the same host paths: the bind-backed volume's device and the direct bind, and nothing copied into Docker's volumes
#   - the marker's 123 rows, the blob and the pepper unchanged
#   - both doctor runs exit 0, and name the same host paths
#
#   deploy/tests/compose-upgrade-odd.sh FROM.tar.gz NEW.tar.gz [--work-dir DIR] [--wait-timeout SECONDS] [--keep]
#                                       [--expect-version X.Y.Z]   (the images' version, when not the new bundle's name:
#                                                                   a bundle built locally pins the last release's)
#
# Needs Docker with Compose v2.33.1 or later, curl and tar; no root (the data directories are made in a container).
set -euo pipefail

from_bundle=""; new_bundle=""; work=""; wait_timeout=900; keep=0; expect=""
usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --work-dir) work="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --keep) keep=1; shift;;
    --expect-version) expect="$2"; shift 2;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) if [ -z "$from_bundle" ]; then from_bundle="$1"; elif [ -z "$new_bundle" ]; then new_bundle="$1"; else usage 1; fi; shift;;
  esac
done
if [ ! -r "$from_bundle" ] || [ ! -r "$new_bundle" ]; then usage 1; fi
for tool in docker curl tar; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done

# The bundles' pins and env files are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$|^(UME|FORS|KALLA)_|^COMPOSE_' || true)

if [ -z "$work" ]; then work="$(mktemp -d)"; else mkdir -p "$work"; work="$(cd "$work" && pwd)"; fi
unpack() { # unpack BUNDLE DIR: the bundle's deploy/ directory
  mkdir -p "$2"; tar -xzf "$1" -C "$2"
  local name; name="$(tar -tzf "$1" | sed -n 1p)"; echo "$2/${name%%/*}/deploy"
}
from="$(unpack "$from_bundle" "$work/from")"; to="$(unpack "$new_bundle" "$work/to")"
version="$(basename "$(dirname "$to")")"; version="${expect:-${version#kalla-}}"
project=kalla-odd; api_port=18543; admin_port=19543
state="$work/state"; data="$work/data"; site="$work/site/odd.yaml"
dc_old() { docker compose --env-file "$from/.env" "$@"; }
dc_new() { docker compose --env-file "$state/.env" "$@"; }
ok() { echo "ok: $*"; }
fail() {
  echo "error: $*" >&2
  { dc_new ps -a || dc_old ps -a; } >&2 2>/dev/null || true
  { dc_new logs --no-color --tail 80 || dc_old logs --no-color --tail 80; } 2>&1 | sed -E 's/(Setup code: )[0-9A-Za-z-]+/\1(hidden)/' >&2 || true
  exit 1
}
image=""
cleanup() {
  if [ "$keep" = 1 ]; then echo "Kept: $work (docker compose --env-file $state/.env ...)"; return; fi
  dc_new down -v --remove-orphans >/dev/null 2>&1 || dc_old down -v --remove-orphans >/dev/null 2>&1 || true
  # The data directories belong to the services' users: emptied in a container.
  [ -z "$image" ] || docker run --rm --user 0 --network none --mount "type=bind,src=$data,dst=/d" --entrypoint sh "$image" \
    -c 'rm -rf /d/pg /d/blobs' >/dev/null 2>&1 || true
  rm -rf "$work" 2>/dev/null || echo "warning: could not remove $work" >&2
}
trap cleanup EXIT

mkdir -p "$work/keys" "$data" "$(dirname "$site")"
for svc in api admin worker; do printf 'odd-%s-key\n' "$svc" > "$work/keys/$svc"; done

echo "== the earlier release ($(basename "$(dirname "$from")")), as an odd host runs it"
bash "$from/init-deployment.sh" --api-host rag-api.odd.test --admin-host rag-admin.odd.test --gateway-url https://llm.odd.test \
  --gateway-credential-key-file api="$work/keys/api" --gateway-credential-key-file admin="$work/keys/admin" \
  --gateway-credential-key-file worker="$work/keys/worker" --profiles '' \
  --set COMPOSE_PROJECT_NAME="$project" --set KALLA_API_PORT="$api_port" --set KALLA_ADMIN_PORT="$admin_port" \
  --set KALLA_API_BIND=127.0.0.1 --set KALLA_ADMIN_BIND=127.0.0.1 --yes > "$work/init-old.log" 2>&1 \
  || { cat "$work/init-old.log" >&2; fail "the earlier release's init-deployment.sh failed"; }
image="$(dc_old config --images kalla-postgres | grep "/postgres:" | head -n 1)"  # not its dependencies' images
# The data directories, as the operator made them: Postgres's for its uid 999, the blobs' for kalla-bootstrap (root).
docker run --rm --user 0 --network none --mount "type=bind,src=$data,dst=/d" --entrypoint sh "$image" \
  -c 'mkdir -p /d/pg /d/blobs && chown 999:999 /d/pg && chmod 700 /d/pg' || fail "could not make the data directories"
cat > "$site" <<EOF
# The operator's own overlay, outside the bundles: the database on a bind-backed volume, the blobs on a direct bind.
volumes:
  kalla-postgres-data:
    driver: local
    driver_opts: {type: none, o: bind, device: "$data/pg"}
services:
  kalla-bootstrap:
    volumes: [{type: bind, source: "$data/blobs", target: /blobs}]
  kalla-api:
    volumes: [{type: bind, source: "$data/blobs", target: /data/blobs, read_only: true}]
  kalla-admin:
    volumes: [{type: bind, source: "$data/blobs", target: /data/blobs}]
  kalla-worker:
    volumes: [{type: bind, source: "$data/blobs", target: /data/blobs}]
EOF
sed -i "s#^COMPOSE_FILE='\(.*\)'\$#COMPOSE_FILE='\1:$site'#" "$from/.env"
dc_old up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the earlier release did not become healthy within ${wait_timeout}s"
ok "the earlier release runs as project $project on ports $api_port and $admin_port, its data on $data"

# The marker table, a blob and the pepper, to compare after the upgrade.
psql_old() { dc_old exec -T kalla-postgres psql -h /var/run/postgresql -U postgres -d ragdb -X -q -t -A -v ON_ERROR_STOP=1 -c "$1"; }
psql_old "CREATE TABLE public.d7_marker AS SELECT g AS i FROM generate_series(1, 123) g" >/dev/null || fail "could not write the marker"
docker run --rm --user 0 --network none --mount "type=bind,src=$data/blobs,dst=/b" --entrypoint sh "$image" \
  -c 'echo d7 > /b/d7-marker' || fail "could not write the blob marker"
pepper() { docker run --rm --user 0 --network none -v "${project}_kalla-secrets:/s:ro" --entrypoint sha256sum "$image" /s/.pepper | cut -d' ' -f1; }
pepper_before="$(pepper)"; [ -n "$pepper_before" ] || fail "no pepper in ${project}_kalla-secrets"
ports() { docker ps --filter "label=com.docker.compose.project=$project" --filter label=com.docker.compose.service=kalla-api --format '{{.Ports}}'; }
ports_before="$(ports)"
grep -q "127.0.0.1:$api_port->8443/tcp" <<< "$ports_before" || fail "kalla-api isn't published on $api_port: $ports_before"
ok "noted: 123 marker rows, a blob, the pepper, kalla-api on $ports_before"

# doctor.sh's lines for the two host paths, as one block to compare.
paths() { grep -E "(bind-backed volume ${project}_kalla-postgres-data → $data/pg|bind → $data/blobs):" "$1" | sed 's/:.*//' | sort; }
doctor() { # doctor LOG [options]: the new bundle's doctor; must exit 0
  KALLA_STATE_DIR="$state" bash "$to/doctor.sh" --no-backups-ok --unit-dir "$work/no-units" --state-dir "$work/prom" "${@:2}" > "$1" 2>&1 \
    || { cat "$1"; fail "doctor.sh exited non-zero"; }
}

echo "== the upgrade ($version), as the upgrade guide says"
doctor "$work/doctor-before.log" --env-file "$from/.env"
grep -q "ok     project $project" "$work/doctor-before.log" || { cat "$work/doctor-before.log"; fail "the doctor didn't find project $project"; }
[ "$(paths "$work/doctor-before.log" | wc -l)" = 2 ] || { cat "$work/doctor-before.log"; fail "the doctor doesn't list both host paths"; }
ok "step 1: the new doctor.sh against the live env file: project $project, both host paths, exit 0"

cp -p "$from/.env" "$to/.env"
KALLA_STATE_DIR="$state" bash "$to/init-deployment.sh" --yes > "$work/init-new.log" 2>&1 \
  || { cat "$work/init-new.log" >&2; fail "the new init-deployment.sh failed"; }
if [ ! -f "$state/.env" ] || [ -e "$to/.env" ]; then fail "the env file wasn't moved to the state directory"; fi
grep -qx "COMPOSE_FILE='$to/compose.rag.yaml:$site'" "$state/.env" || fail "COMPOSE_FILE isn't the new bundle's with the site overlay: $(grep '^COMPOSE_FILE' "$state/.env")"
grep -qx "COMPOSE_PROJECT_NAME='$project'" "$state/.env" || fail "the project name was lost"
ok "step 5: the env file moved to $state, COMPOSE_FILE the new bundle's with the site overlay kept"

dc_new up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the new release did not become healthy within ${wait_timeout}s"
ok "step 7: up --wait"

echo "== after the upgrade"
[ "$(ports)" = "$ports_before" ] || fail "kalla-api's ports changed: $ports_before -> $(ports)"
[ -z "$(docker ps -a --filter label=com.docker.compose.project=kalla --format '{{.Names}}')" ] || fail "a second project kalla was started"
ca="$work/ca.pem"
# kalla-bootstrap's image reads the volumes, as release-smoke.sh does.
boot_image="$(dc_new config --images kalla-bootstrap | head -n 1)"
docker run --rm --network none -v "${project}_kalla-certs:/c:ro" --entrypoint cat "$boot_image" /c/ca.pem > "$ca" || fail "no ca.pem"
status="$(curl -sS --cacert "$ca" --max-time 15 -o /dev/null -w '%{http_code}' "https://localhost:$api_port/health/ready")"
[ "$status" = 200 ] || fail "kalla-api /health/ready answered $status on port $api_port"
reported="$(curl -sS --cacert "$ca" --max-time 15 "https://localhost:$api_port/version" | sed -n 's/.*"informationalVersion":"\([^"]*\)".*/\1/p')"
reported="${reported//\\u002B/+}"  # JSON's escape of the build metadata's +
case "$reported" in "$version"|"$version+"*) ;; *) fail "kalla-api /version is $reported, expected $version";; esac
ok "the same project and ports; kalla-api ready on $api_port at $reported"

[ "$(docker volume inspect -f '{{index .Options "device"}}' "${project}_kalla-postgres-data")" = "$data/pg" ] \
  || fail "${project}_kalla-postgres-data is no longer bound to $data/pg"
if docker volume inspect "${project}_kalla-blobs" >/dev/null 2>&1 \
  && [ -n "$(docker run --rm --network none -v "${project}_kalla-blobs:/v:ro" --entrypoint sh "$image" -c 'ls -A /v')" ]; then
  fail "something was copied into the named volume ${project}_kalla-blobs"
fi
rows="$(dc_new exec -T kalla-postgres psql -h /var/run/postgresql -U postgres -d ragdb -X -q -t -A -c 'SELECT count(*) FROM public.d7_marker')"
[ "$rows" = 123 ] || fail "the marker table has ${rows:-no} rows, not 123"
docker run --rm --network none --mount "type=bind,src=$data/blobs,dst=/b,readonly" --entrypoint cat "$image" /b/d7-marker | grep -qx d7 \
  || fail "the blob marker is gone"
[ "$(pepper)" = "$pepper_before" ] || fail "the key pepper changed"
ok "the same host paths (nothing copied), the marker's 123 rows, the blob and the pepper"

doctor "$work/doctor-after.log"
grep -qE "kalla-api +$version " "$work/doctor-after.log" || { cat "$work/doctor-after.log"; fail "the doctor doesn't show kalla-api at $version"; }
[ "$(paths "$work/doctor-after.log")" = "$(paths "$work/doctor-before.log")" ] || fail "the doctor's host paths differ before and after"
ok "step 9: the doctor again: the new version, the same host paths, exit 0"

echo
echo "The odd-host upgrade from $(basename "$(dirname "$from")") to $version passed."
