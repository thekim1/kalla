#!/usr/bin/env bash
# Smoke test of a release bundle (plans/platform/repo-and-deployment.md §8.1, release.yml): unpacks the bundle, runs its
# init-deployment.sh non-interactively with test-host options, starts the production Compose files with the images the
# bundle pins (`up --wait`: every service with a health check healthy, rag-migrate completed), then checks:
#
#   - api, admin and worker each have their gateway credential key (placeholders, secrets/<service>/Gateway__Key)
#   - every running container's image is pinned by digest (the bundle's defaults; *_IMAGE variables are unset first)
#   - rag-api: /health/ready = 200, /version reports the bundle's version
#   - rag-api: /v1/models = 401 without a key           (P1: rag-api has no /v1 routes yet; a 404 is reported as
#                                                        not built, any other answer than 401 fails)
#   - rag-admin: / = 200 with the admin UI's index.html
#   - a Swedish compound query through the API          (P1: no query endpoint exists yet; reported, not run)
#   - pgbackrest stanza-create and check in the postgres container
#   - parser /v1/info from the worker container (the worker image's own probe pointed at the parser: HTTPS through the
#     internal CA, 2xx; the image has no shell or curl)
#   - the renderer: with --render, rag-renderer runs healthy (COMPOSE_PROFILES=render), and then switching it off as
#     the upgrade guide says (no profile, no endpoint, rm rag-renderer, restart rag-worker) leaves a healthy stack
#     without it; without --render it never runs (#505)
#   - the malware scan: rag-clamav runs healthy by default (COMPOSE_PROFILES has clamav, UME_RAG_CLAMAV_MODE=on), and
#     then switching it off as init-deployment.sh --clamav off does (no clamav profile, UME_RAG_CLAMAV_MODE=off, rm
#     rag-clamav) leaves the worker healthy with ClamAv__Mode=off and no rag-clamav container (#535)
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
#   --render                init-deployment.sh --render (the Compose profile `render`), then switched off again (#505)
#   --keep                  leave the stack running and the files in place
#
# Needs Docker with Compose v2, curl, jq and openssl; the containers read their secrets as their own users, so
# init-deployment.sh needs root or passwordless sudo (GitHub runners have it) unless --chown-with-docker is given.
# Exit 0 when every check that exists in this phase passed; the steps not built yet are listed at the end.
set -euo pipefail

bundle=""; work=""; extra_files=(); api_port=8543; admin_port=9543; wait_timeout=900; docker_chown=0; keep=0; render=""
usage() { sed -n '2,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --work-dir) work="$2"; shift 2;;
    --compose-file) extra_files+=("$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"); shift 2;;
    --api-port) api_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --chown-with-docker) docker_chown=1; shift;;
    --render) render=1; shift;;
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

# One placeholder credential key per service, as a gateway admin issues them (production-setup.md, Gateway credential
# keys): the stack boots as a real one does. No check needs the gateway, which llm.smoke.test does not reach.
mkdir -p "$work/keys"
for svc in api admin worker; do printf 'smoke-%s-key\n' "$svc" > "$work/keys/$svc"; done

# init-deployment.sh with the test host's options plus "$@", then this test's own .env lines, which a re-run (it rewrites
# .env) needs again.
init_deployment() {
  PATH="$init_path" "$deploy/init-deployment.sh" \
    --oidc-authority https://keycloak.smoke.test/realms/ume \
    --gateway-url https://llm.smoke.test \
    --gateway-credential-key-file api="$work/keys/api" --gateway-credential-key-file admin="$work/keys/admin" \
    --gateway-credential-key-file worker="$work/keys/worker" \
    --api-host rag-api.smoke.test --admin-host rag-admin.smoke.test \
    --api-port "$api_port" --admin-port "$admin_port" \
    --backup-target local --allow-file-keys "$@" --yes > "$work/init.log" \
    || { cat "$work/init.log" >&2; fail "init-deployment.sh failed"; }
  grep -q 'Run as root first' "$work/init.log" && { cat "$work/init.log" >&2; fail "init-deployment.sh could not chown"; }
  {
    # No rag-ingestion.slice on a test runner (deploy/host/install.sh is not run here).
    echo "UME_RAG_INGESTION_SLICE="
  } >> "$deploy/.env"
  for f in ${extra_files[@]+"${extra_files[@]}"}; do sed -i "s#^COMPOSE_FILE=.*#&:$f#" "$deploy/.env"; done
}

echo "== init-deployment.sh ($name)"
init_deployment ${render:+--render}
ok "init-deployment.sh wrote $(basename "$deploy")/.env, certs and secrets"
# The files now belong to the containers' users, so read them as root.
for svc in api admin worker; do
  key="$("${as_root[@]}" cat "$deploy/secrets/$svc/Gateway__Key")" || fail "no secrets/$svc/Gateway__Key"
  [ "$key" = "smoke-$svc-key" ] || fail "secrets/$svc/Gateway__Key is not $svc's credential key"
done
ok "one gateway credential key per service (secrets/{api,admin,worker}/Gateway__Key)"

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
reported="$(https "https://localhost:$api_port/version" | jq -r '.informationalVersion')"
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

echo "== renderer"
renderer="$(dc ps --format '{{.Service}} {{.Health}}' | awk '$1 == "rag-renderer" {print $2}')"
if [ -n "$render" ]; then
  [ "$renderer" = healthy ] || fail "rag-renderer is '${renderer:-not running}' with --render, expected healthy"
  grep -qx 'COMPOSE_PROFILES=clamav,render' "$deploy/.env" || fail "--render did not set COMPOSE_PROFILES=clamav,render in .env"
  ok "rag-renderer healthy with COMPOSE_PROFILES=clamav,render"

  # The other half without a second boot: switch the renderer off as docs/upgrade-guide.md tells operators to. Its
  # first step, init-deployment.sh again without --render, needs root here (the files now belong to the containers'
  # users) and is checked on its own in verify.yml's deploy-checks; this applies its result (no profile, no endpoint)
  # and checks that the stack runs without the renderer.
  echo "== renderer off (no profile, no endpoint, rm rag-renderer, restart rag-worker)"
  sed -i 's/^COMPOSE_PROFILES=.*/COMPOSE_PROFILES=clamav/' "$deploy/.env"
  "${as_root[@]}" rm -f "$deploy/secrets/worker/Render__Endpoint"
  dc rm -sf rag-renderer >/dev/null
  dc restart rag-worker >/dev/null
  dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not become healthy again without the renderer"
  [ -z "$(dc ps --format '{{.Service}}' | grep -x rag-renderer || true)" ] || fail "rag-renderer still runs after switching it off"
  ok "renderer switched off: no rag-renderer, every service healthy again (browser sources: render.not_configured)"
else
  [ -z "$renderer" ] || fail "rag-renderer runs without --render"
  grep -qx 'COMPOSE_PROFILES=clamav' "$deploy/.env" || fail "COMPOSE_PROFILES is not just clamav in .env without --render"
  ok "no rag-renderer without --render (browser sources: render.not_configured)"
fi

echo "== malware scan"
clamd="$(dc ps --format '{{.Service}} {{.Health}}' | awk '$1 == "rag-clamav" {print $2}')"
[ "$clamd" = healthy ] || fail "rag-clamav is '${clamd:-not running}' by default, expected healthy"
grep -qx 'UME_RAG_CLAMAV_MODE=on' "$deploy/.env" || fail "init-deployment.sh did not set UME_RAG_CLAMAV_MODE=on by default"
ok "rag-clamav healthy by default (the clamav profile, UME_RAG_CLAMAV_MODE=on)"

# The other mode without a second boot from scratch, as for the renderer: init-deployment.sh --clamav off needs root
# here and is checked on its own in verify.yml's deploy-checks; this applies its result to deploy/.env (the clamav
# profile out, UME_RAG_CLAMAV_MODE=off), removes rag-clamav and lets `up` recreate the worker with ClamAv__Mode=off.
echo "== malware scan off (no clamav profile, UME_RAG_CLAMAV_MODE=off, rm rag-clamav)"
sed -i -e '/^COMPOSE_PROFILES=/{s/=/=,/; s/,clamav\(,\|$\)/\1/; s/=,/=/}' \
  -e 's/^UME_RAG_CLAMAV_MODE=.*/UME_RAG_CLAMAV_MODE=off/' "$deploy/.env"
if grep -E '^COMPOSE_PROFILES=' "$deploy/.env" | grep -qw clamav; then fail "could not take clamav out of COMPOSE_PROFILES"; fi
dc rm -sf rag-clamav >/dev/null
dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not become healthy with the malware scan off"
[ -z "$(dc ps -a --format '{{.Service}}' | grep -x rag-clamav || true)" ] || fail "a rag-clamav container exists with the scan off"
worker="$(dc ps -q rag-worker)"
[ "$(docker inspect -f '{{.State.Health.Status}}' "$worker")" = healthy ] || fail "rag-worker is not healthy with the scan off"
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$worker" | grep -qx 'ClamAv__Mode=off' \
  || fail "rag-worker does not run with ClamAv__Mode=off"
ok "malware scan off: no rag-clamav, rag-worker healthy with ClamAv__Mode=off (files stored unscanned)"

echo
echo "Smoke test passed for $name."
if [ ${#pending[@]} -gt 0 ]; then
  echo "Not built yet, so not checked:"
  printf '  - %s\n' "${pending[@]}"
fi
