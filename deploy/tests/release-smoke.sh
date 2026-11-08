#!/usr/bin/env bash
# Smoke test of a release bundle (plans/platform/repo-and-deployment.md §8.1, release.yml): unpacks the bundle, runs its
# init-deployment.sh non-interactively with test-host options, starts the production Compose files with the images the
# bundle pins (`up --wait`: every service with a health check healthy, rag-migrate completed), then checks:
#
#   - every running container's image is pinned by digest (the bundle's defaults; *_IMAGE variables are unset first)
#   - rag-api: /health/ready = 200, /version reports the bundle's version
#   - rag-api: /v1/models = 401 without a key           (P1: rag-api has no /v1 routes yet; a 404 is reported as
#                                                        not built, any other answer than 401 fails)
#   - rag-admin: / = 200 with the admin UI's index.html
#   - a Swedish compound query through the API          (P1: no query endpoint exists yet; reported, not run)
#   - pgbackrest stanza-create and check in the postgres container
#   - parser /v1/info from the worker container (the worker image's own probe pointed at the parser: HTTPS through the
#     internal CA, 2xx; the image has no shell or curl)
#
#   deploy/tests/release-smoke.sh BUNDLE.tar.gz [options]
#
# Options:
#   --work-dir DIR          where the bundle is unpacked [a new temporary directory]; removed afterwards
#   --compose-file FILE     an extra Compose file appended to the deployment's COMPOSE_FILE (repeatable), for example
#                           other subnets where 172.30.0.0/16 is taken on a developer machine
#   --api-port N, --admin-port N   host ports of the two listeners [8543, 9543]
#   --wait-timeout SECONDS  for `up --wait` [900]: the parser warms up and freshclam downloads the signatures first
#   --chown-with-docker     without root or passwordless sudo (a developer machine): init-deployment.sh's chown runs
#                           as root in a throwaway container of the bundle's postgres image, and so does the clean-up
#   --keep                  leave the stack running and the files in place
#
# Needs Docker with Compose v2, curl, jq and openssl; the containers read their secrets as their own users, so
# init-deployment.sh needs root or passwordless sudo (GitHub runners have it) unless --chown-with-docker is given.
# Exit 0 when every check that exists in this phase passed; the steps not built yet are listed at the end.
set -euo pipefail

bundle=""; work=""; extra_files=(); api_port=8543; admin_port=9543; wait_timeout=900; docker_chown=0; keep=0
usage() { sed -n '2,31p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --work-dir) work="$2"; shift 2;;
    --compose-file) extra_files+=("$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"); shift 2;;
    --api-port) api_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --chown-with-docker) docker_chown=1; shift;;
    --keep) keep=1; shift;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) [ -z "$bundle" ] || usage 1; bundle="$1"; shift;;
  esac
done
if [ -z "$bundle" ] || [ ! -r "$bundle" ]; then usage 1; fi
for tool in docker curl jq openssl tar; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done

# The bundle's pins are under test: an inherited *_IMAGE variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$' || true)

if [ "$(id -u)" = 0 ]; then
  as_root=()
elif sudo -n true 2>/dev/null; then
  as_root=(sudo -n)
elif [ "$docker_chown" = 1 ]; then
  as_root=(docker)
else
  echo "error: the containers read their files as their own users, so init-deployment.sh must chown them: run as root," \
    "with passwordless sudo, or pass --chown-with-docker" >&2
  exit 1
fi

name="$(tar -tzf "$bundle" | sed -n 1p)"; name="${name%%/*}"
version="${name#ume-rag-}"
[[ "$name" =~ ^ume-rag-[0-9]+\.[0-9]+\.[0-9]+ ]] || { echo "error: $bundle is not an ume-rag-X.Y.Z bundle" >&2; exit 1; }
if [ -z "$work" ]; then work="$(mktemp -d)"; else mkdir -p "$work"; work="$(cd "$work" && pwd)"; fi
tar -xzf "$bundle" -C "$work"
root="$work/$name"
deploy="$root/deploy"
project="ume-rag-smoke"
helper_image="$(grep -m1 -oE 'POSTGRES_IMAGE:-[^}]+' "$deploy/compose.prod.yaml")"
helper_image="${helper_image#POSTGRES_IMAGE:-}"

dc() { docker compose -p "$project" --env-file "$deploy/.env" "$@"; }
pending=()
ok() { echo "ok: $*"; }
fail() {
  echo "error: $*" >&2
  dc ps -a >&2 || true
  dc logs --no-color --tail 100 >&2 || true
  exit 1
}

init_path="$PATH"
if [ "${as_root[*]}" = docker ]; then
  # A stand-in for `sudo -n CMD ARGS` (the only form init-deployment.sh uses): CMD as root in a throwaway container
  # with the work directory mounted at the same path. Test-only; never part of the bundle.
  mkdir -p "$work/.bin"
  cat > "$work/.bin/sudo" <<EOF
#!/bin/sh
[ "\$1" = -n ] && shift
cmd="\$1"; shift
exec docker run --rm --network none --user 0 -v "$work:$work" --entrypoint "\$cmd" "$helper_image" "\$@"
EOF
  chmod +x "$work/.bin/sudo"
  as_root=("$work/.bin/sudo" -n)
  init_path="$work/.bin:$PATH"
fi

cleanup() {
  if [ "$keep" = 1 ]; then echo "Kept: $root (docker compose -p $project --env-file $deploy/.env ...)"; return; fi
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  "${as_root[@]}" rm -rf "$work" 2>/dev/null || rm -rf "$work" 2>/dev/null \
    || echo "warning: could not remove $work" >&2
}
trap cleanup EXIT

echo "== init-deployment.sh ($name)"
PATH="$init_path" "$deploy/init-deployment.sh" \
  --oidc-authority https://keycloak.smoke.test/realms/ume \
  --gateway-url https://llm.smoke.test \
  --api-host rag-api.smoke.test --admin-host rag-admin.smoke.test \
  --api-port "$api_port" --admin-port "$admin_port" \
  --backup-target local --allow-file-keys --yes > "$work/init.log" \
  || { cat "$work/init.log" >&2; fail "init-deployment.sh failed"; }
grep -q 'Run as root first' "$work/init.log" && { cat "$work/init.log" >&2; fail "init-deployment.sh could not chown"; }
ok "init-deployment.sh wrote $(basename "$deploy")/.env, certs and secrets"
{
  # No rag-ingestion.slice on a test runner (deploy/host/install.sh is not run here).
  echo "UME_RAG_INGESTION_SLICE="
} >> "$deploy/.env"
for f in ${extra_files[@]+"${extra_files[@]}"}; do sed -i "s#^COMPOSE_FILE=.*#&:$f#" "$deploy/.env"; done

echo "== images"
unpinned="$(dc config --images | grep -vE '@sha256:[0-9a-f]{64}$' || true)"
[ -z "$unpinned" ] || fail "images not pinned by digest: $unpinned"
dc config --images | sort -u | sed 's/^/  /'

echo "== up --wait"
start=$SECONDS
dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not become healthy within ${wait_timeout}s"
ok "every service healthy, rag-migrate completed ($((SECONDS - start)) s)"
dc ps --format 'table {{.Service}}\t{{.Status}}' | sed 's/^/  /'
while read -r container image; do
  [[ "$image" =~ @sha256:[0-9a-f]{64}$ ]] || fail "$container runs $image, not a pinned image"
done < <(dc ps -q | xargs docker inspect --format '{{.Name}} {{.Config.Image}}')
ok "every running container uses a digest-pinned image"

echo "== rag-api"
ca="$deploy/certs/ca.pem"
https() { curl -sS --cacert "$ca" --max-time 15 "$@"; }
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/health/ready")"
[ "$status" = 200 ] || fail "rag-api /health/ready answered $status"
ok "rag-api /health/ready = 200 (TLS verified against the internal CA)"
reported="$(https "https://localhost:$api_port/version" | jq -r .informationalVersion)"
case "$reported" in "$version"|"$version+"*) ok "rag-api /version = $reported";; *) fail "rag-api /version is $reported, expected $version";; esac
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/v1/models")"
case "$status" in
  401) ok "rag-api /v1/models = 401 without a key";;
  404) pending+=("rag-api /v1/models = 401 without a key: the route does not exist yet (answered 404; P1 chat API)");;
  *) fail "rag-api /v1/models answered $status without a key, expected 401";;
esac
pending+=("a Swedish compound query through the API: rag-api has no query endpoint yet (P1)")

echo "== rag-admin"
status="$(https -o "$work/admin-index.html" -w '%{http_code}' "https://localhost:$admin_port/")"
[ "$status" = 200 ] || fail "rag-admin / answered $status"
grep -q '<div id="app">' "$work/admin-index.html" || fail "rag-admin / is not the admin UI's index.html"
ok "rag-admin / = 200 (the admin UI)"

echo "== pgbackrest"
dc exec -T postgres pgbackrest --stanza=ragdb stanza-create >/dev/null || fail "pgbackrest stanza-create failed"
dc exec -T postgres pgbackrest --stanza=ragdb check || fail "pgbackrest check failed"
ok "pgbackrest stanza-create and check in the postgres container"

echo "== blob container (issue #41)"
owner="$(dc run --rm --no-deps rag-upload-backup stat -c '%u:%g %a' /data/blobs/rag)" || fail "could not stat /data/blobs/rag"
[ "$owner" = "1654:1654 750" ] || fail "/data/blobs/rag is $owner, expected 1654:1654 750"
ok "rag-blobs-init made /data/blobs/rag 1654:1654 0750; rag-upload-backup (uid 1654, read-only) can read it"
spool="$(dc run --rm --no-deps rag-upload-backup stat -c '%u:%g %a' /data/blobs/spool)" || fail "could not stat /data/blobs/spool"
[ "$spool" = "1654:1654 750" ] || fail "/data/blobs/spool is $spool, expected 1654:1654 750"
ok "rag-blobs-init made the worker's fetch spool /data/blobs/spool 1654:1654 0750 (TMPDIR, on disk)"

echo "== parser from the worker"
dc exec -T -e UME_HEALTHCHECK_URL=https://rag-parser:8000/v1/info rag-worker dotnet Ume.Rag.Worker.dll --health-check \
  || fail "the worker could not reach the parser's /v1/info"
ok "parser /v1/info answered 2xx to the worker (HTTPS, internal CA)"

echo
echo "Smoke test passed for $name."
if [ ${#pending[@]} -gt 0 ]; then
  echo "Not built yet, so not checked:"
  printf '  - %s\n' "${pending[@]}"
fi
