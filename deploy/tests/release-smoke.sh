#!/usr/bin/env bash
# Smoke test of a release bundle (plans/platform/repo-and-deployment.md §8.1, release.yml): unpacks the bundle, runs its
# init-deployment.sh non-interactively with test-host options (deploy/compose.rag.yaml with compose.backup.yaml, host
# names without --domain as in the Compose quick start, no sign-in value), starts it with the images the bundle pins
# (`up --wait`: kalla-bootstrap and kalla-migrate completed, every service with a health check healthy), then checks:
#
#   - api, admin and worker each have their gateway credential key (placeholders, set as variables; kalla-bootstrap
#     writes them to kalla-secrets/<service>/Gateway__Key)
#   - every running container's image is pinned by digest (the bundle's defaults; *_IMAGE variables are unset first)
#   - kalla-api: /health/ready = 200, /version reports the bundle's version
#   - kalla-api: /v1/models = 401 without a key           (a 404 is reported as not built, any other answer fails)
#   - kalla-admin: / = 200 with the admin UI's index.html
#   - the setup wizard (Decisions "Setup wizard"), as deploy/tests/portainer-boot.sh checks it: kalla-bootstrap's log
#     points at https://rag-admin.smoke.test:<admin port>/setup, kalla-admin's output has a setup code (never printed: masked in GitHub
#     Actions and cut from the logs a failure prints), and /bff/user says sign-in is not configured and setup is open
#   - a Swedish compound query through the API          (no query endpoint exists yet; reported, not run)
#   - pgbackrest stanza-create and check in kalla-postgres
#   - the blob directories 1654:1654 0750 (kalla-bootstrap), read by rag-upload-backup (uid 1654, read-only)
#   - parser /v1/info from the worker container (the worker image's own probe pointed at the parser: HTTPS through the
#     internal CA, 2xx; the image has no shell or curl)
#   - the renderer: with --render, kalla-renderer runs healthy (the profile render), and then switching it off (the
#     profile out, rm kalla-renderer, up: kalla-bootstrap removes the worker's endpoint and the worker is recreated) leaves
#     a healthy stack without it; without --render it never runs (#505)
#   - the malware scan: kalla-clamav runs healthy by default (the profile clamav), and then switching it off the same way
#     leaves the worker healthy with ClamAv__Mode off and no kalla-clamav container (#535)
#
#   deploy/tests/release-smoke.sh BUNDLE.tar.gz [options]
#
# Options:
#   --work-dir DIR          where the bundle is unpacked [a new temporary directory]; removed afterwards
#   --compose-file FILE     an extra Compose file appended to the deployment's COMPOSE_FILE (repeatable)
#   --api-port N, --admin-port N   host ports of the two listeners [8543, 9543]
#   --wait-timeout SECONDS  for `up --wait` [900]: the parser warms up and freshclam downloads the signatures first
#   --render                the profile render, then switched off again (#505)
#   --keep                  leave the stack running and the files in place
#
# Needs Docker with Compose v2.33.1 or later, curl, jq and tar; no root (kalla-bootstrap gives the files to the services'
# users inside the volumes). Exit 0 when every check that exists in this phase passed; the steps not built yet are
# listed at the end.
set -euo pipefail

bundle=""; work=""; extra_files=(); api_port=8543; admin_port=9543; wait_timeout=900; keep=0; render=""
usage() { sed -n '2,39p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --work-dir) work="$2"; shift 2;;
    --compose-file) extra_files+=("$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"); shift 2;;
    --api-port) api_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --render) render=1; shift;;
    --keep) keep=1; shift;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) [ -z "$bundle" ] || usage 1; bundle="$1"; shift;;
  esac
done
if [ -z "$bundle" ] || [ ! -r "$bundle" ]; then usage 1; fi
for tool in docker curl jq tar; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done

# The bundle's pins and deploy/.env are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$|^KALLA_|^FORS_|^UME_|^COMPOSE_' || true)

name="$(tar -tzf "$bundle" | sed -n 1p)"; name="${name%%/*}"
version="${name#kalla-}"
[[ "$name" =~ ^kalla-[0-9]+\.[0-9]+\.[0-9]+ ]] || { echo "error: $bundle is not a kalla-X.Y.Z bundle" >&2; exit 1; }
if [ -z "$work" ]; then work="$(mktemp -d)"; else mkdir -p "$work"; work="$(cd "$work" && pwd)"; fi
tar -xzf "$bundle" -C "$work"
deploy="$work/$name/deploy"
project="kalla-smoke"

dc() { docker compose -p "$project" --env-file "$deploy/.env" "$@"; }
pending=()
ok() { echo "ok: $*"; }
setup_code_seen=""
fail() {
  echo "error: $*" >&2
  dc ps -a >&2 || true
  # The setup code is worth platform admin while setup is open: cut from the logs printed here.
  dc logs --no-color --tail 100 2>&1 | sed -E -e 's/(Setup code: )[0-9A-Za-z-]+/\1(hidden)/' \
    ${setup_code_seen:+-e "s/$setup_code_seen/(hidden)/g"} >&2 || true
  exit 1
}
cleanup() {
  if [ "$keep" = 1 ]; then echo "Kept: $work/$name (docker compose -p $project --env-file $deploy/.env ...)"; return; fi
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work" 2>/dev/null || echo "warning: could not remove $work" >&2
}
trap cleanup EXIT

# One placeholder credential key per service, as a gateway admin issues them: the stack boots as a real one does. No
# check needs the gateway, which llm.smoke.test does not reach.
mkdir -p "$work/keys"
for svc in api admin worker; do printf 'smoke-%s-key\n' "$svc" > "$work/keys/$svc"; done

# init-deployment.sh with the test host's options plus "$@"; the extra Compose files after its COMPOSE_FILE. Host names
# and no --domain, as the Compose quick start gives them: then the gateway admin's address renders empty, which
# kalla-admin must take as unset (#865); the Compose bootstrap test and the Portainer boot test cover the domain form.
init_deployment() {
  "$deploy/init-deployment.sh" --api-host rag-api.smoke.test --admin-host rag-admin.smoke.test \
    --gateway-url https://llm.smoke.test \
    --gateway-credential-key-file api="$work/keys/api" --gateway-credential-key-file admin="$work/keys/admin" \
    --gateway-credential-key-file worker="$work/keys/worker" \
    --set KALLA_API_PORT="$api_port" --set KALLA_ADMIN_PORT="$admin_port" \
    --set KALLA_API_BIND=127.0.0.1 --set KALLA_ADMIN_BIND=127.0.0.1 \
    --backup-target local "$@" --yes > "$work/init.log" 2>&1 || { cat "$work/init.log" >&2; fail "init-deployment.sh failed"; }
  for f in ${extra_files[@]+"${extra_files[@]}"}; do sed -i "s#^COMPOSE_FILE='\(.*\)'\$#COMPOSE_FILE='\1:$f'#" "$deploy/.env"; done
}
# volume_cat VOLUME PATH: a file of the stack's volume, read-only, with kalla-bootstrap's image.
volume_cat() { docker run --rm --network none -v "${project}_$1:/v:ro" --entrypoint cat "$(dc config --images kalla-bootstrap)" "/v/$2"; }

echo "== init-deployment.sh ($name)"
init_deployment --profiles "clamav${render:+,render}"
grep -q "^COMPOSE_FILE='.*compose.backup.yaml" "$deploy/.env" || fail "init-deployment.sh did not list compose.backup.yaml"
ok "init-deployment.sh wrote $(basename "$deploy")/.env (no certificate or secret)"

echo "== images"
unpinned="$(dc --profile backup config --images | grep -vE '@sha256:[0-9a-f]{64}$' || true)"
[ -z "$unpinned" ] || fail "images not pinned by digest: $unpinned"
dc config --images | sort -u | sed 's/^/  /'

echo "== up --wait"
start=$SECONDS
dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not become healthy within ${wait_timeout}s"
ok "every service healthy, kalla-bootstrap and kalla-migrate completed ($((SECONDS - start)) s)"
dc ps -a --format 'table {{.Service}}\t{{.Status}}' | sed 's/^/  /'
while read -r container image; do
  [[ "$image" =~ @sha256:[0-9a-f]{64}$ ]] || fail "$container runs $image, not a pinned image"
done < <(dc ps -q | xargs docker inspect --format '{{.Name}} {{.Config.Image}}')
ok "every running container uses a digest-pinned image"
for svc in api admin worker; do
  [ "$(volume_cat kalla-secrets "$svc/Gateway__Key")" = "smoke-$svc-key" ] || fail "kalla-secrets/$svc/Gateway__Key is not $svc's credential key"
done
ok "one gateway credential key per service (kalla-secrets/{api,admin,worker}/Gateway__Key)"

echo "== kalla-api"
ca="$work/ca.pem"; volume_cat kalla-certs ca.pem > "$ca" || fail "no ca.pem in kalla-certs"
https() { curl -sS --cacert "$ca" --max-time 15 "$@"; }
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/health/ready")"
[ "$status" = 200 ] || fail "kalla-api /health/ready answered $status"
ok "kalla-api /health/ready = 200 (TLS verified against the internal CA)"
reported="$(https "https://localhost:$api_port/version" | jq -r '.informationalVersion')"
case "$reported" in "$version"|"$version+"*) ok "kalla-api /version = $reported";; *) fail "kalla-api /version is $reported, expected $version";; esac
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/v1/models")"
case "$status" in
  401) ok "kalla-api /v1/models = 401 without a key";;
  404) pending+=("kalla-api /v1/models = 401 without a key: the route does not exist yet (answered 404)");;
  *) fail "kalla-api /v1/models answered $status without a key, expected 401";;
esac
pending+=("a Swedish compound query through the API: kalla-api has no query endpoint yet")

echo "== kalla-admin"
status="$(https -o "$work/admin-index.html" -w '%{http_code}' "https://localhost:$admin_port/")"
[ "$status" = 200 ] || fail "kalla-admin / answered $status"
grep -q '<div id="app">' "$work/admin-index.html" || fail "kalla-admin / is not the admin UI's index.html"
ok "kalla-admin / = 200 (the admin UI)"

echo "== the setup wizard (identity design §19)"
dc logs --no-color --no-log-prefix kalla-bootstrap \
  | grep -qF "kalla-bootstrap: sign-in is set in the setup wizard: https://rag-admin.smoke.test:$admin_port/setup " \
  || fail "kalla-bootstrap's log does not point at the setup wizard on https://rag-admin.smoke.test:$admin_port/setup"
ok "kalla-bootstrap points at the setup wizard (https://rag-admin.smoke.test:$admin_port/setup); no sign-in value was given"
setup_code() {
  dc logs --no-color --no-log-prefix kalla-admin 2>/dev/null | sed -n 's/^kalla-admin: setup mode is open until .*\. Setup code: //p' | tail -n 1
}
for _ in $(seq 60); do [ -z "$(setup_code)" ] || break; sleep 1; done
setup_code_seen="$(setup_code)"
if [ -n "$setup_code_seen" ] && [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::add-mask::$setup_code_seen"; fi
[ -n "$setup_code_seen" ] || fail "kalla-admin's output has no 'setup mode is open until … Setup code:' line"
grep -qE '^[0-9A-Z]{5}(-[0-9A-Z]{5}){3}-[0-9A-Z]{6}$' <<< "$setup_code_seen" || fail "kalla-admin's setup code is not in the form XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX"
user="$(https --resolve "rag-admin.smoke.test:$admin_port:127.0.0.1" -H 'Accept: application/json' \
  "https://rag-admin.smoke.test:$admin_port/bff/user")" || fail "kalla-admin's /bff/user did not answer"
jq -e '.isAuthenticated == false and .signIn.configured == false and .setup.open == true' >/dev/null <<< "$user" \
  || fail "kalla-admin's /bff/user is not signed out, sign-in not configured and setup open: $(jq -c '{isAuthenticated, signIn, setup}' <<< "$user")"
ok "kalla-admin's output has a setup code (not shown); /bff/user: sign-in not configured and setup open"

echo "== pgbackrest"
dc exec -T kalla-postgres pgbackrest --stanza=ragdb stanza-create >/dev/null || fail "pgbackrest stanza-create failed"
dc exec -T kalla-postgres pgbackrest --stanza=ragdb check || fail "pgbackrest check failed"
ok "pgbackrest stanza-create and check in kalla-postgres"

echo "== blob container (issue #41)"
for dir in rag spool; do
  owner="$(dc run --rm --no-deps -T --entrypoint stat rag-upload-backup -c '%u:%g %a' "/data/blobs/$dir")" || fail "could not stat /data/blobs/$dir"
  [ "$owner" = "1654:1654 750" ] || fail "/data/blobs/$dir is $owner, expected 1654:1654 750"
done
ok "kalla-bootstrap made /data/blobs/rag and the worker's spool 1654:1654 0750; rag-upload-backup (uid 1654) reads them"

echo "== parser from the worker"
dc exec -T -e FORS_HEALTHCHECK_URL=https://kalla-parser:8000/v1/info kalla-worker dotnet Kalla.Worker.dll --health-check \
  || fail "the worker could not reach the parser's /v1/info"
ok "parser /v1/info answered 2xx to the worker (HTTPS, internal CA)"

# switch_off SERVICE PROFILES: take SERVICE's profile out as an operator does (init-deployment.sh --profiles), remove
# its container, and `up` again: kalla-bootstrap runs with the new profiles and the worker is recreated with them.
switch_off() {
  dc rm -sf "$1" >/dev/null
  init_deployment --profiles "$2"
  dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not become healthy again without $1"
  [ -z "$(dc ps -a --format '{{.Service}}' | grep -x "$1" || true)" ] || fail "$1 still exists after switching it off"
}

echo "== renderer"
renderer="$(dc ps --format '{{.Service}} {{.Health}}' | awk '$1 == "kalla-renderer" {print $2}')"
if [ -n "$render" ]; then
  [ "$renderer" = healthy ] || fail "kalla-renderer is '${renderer:-not running}' with --render, expected healthy"
  volume_cat kalla-secrets worker/Render__Endpoint | grep -q '^ws://kalla-renderer:3000/' || fail "the worker has no Render__Endpoint"
  ok "kalla-renderer healthy with the profile render, and the worker has its endpoint"
  echo "== renderer off (the profile out, rm kalla-renderer, up)"
  switch_off kalla-renderer clamav
  if volume_cat kalla-secrets worker/Render__Endpoint >/dev/null 2>&1; then fail "the worker still has Render__Endpoint"; fi
  ok "renderer switched off: no kalla-renderer, no endpoint, every service healthy again (browser sources: render.not_configured)"
else
  [ -z "$renderer" ] || fail "kalla-renderer runs without --render"
  ok "no kalla-renderer without --render (browser sources: render.not_configured)"
fi

echo "== malware scan"
clamd="$(dc ps --format '{{.Service}} {{.Health}}' | awk '$1 == "kalla-clamav" {print $2}')"
[ "$clamd" = healthy ] || fail "kalla-clamav is '${clamd:-not running}' by default, expected healthy"
[ "$(volume_cat kalla-secrets worker/ClamAv__Mode)" = on ] || fail "the worker's ClamAv__Mode is not on with the profile clamav"
ok "kalla-clamav healthy by default (the profile clamav, the worker's ClamAv__Mode on)"
echo "== malware scan off (the profile out, rm kalla-clamav, up)"
switch_off kalla-clamav ''
[ "$(volume_cat kalla-secrets worker/ClamAv__Mode)" = off ] || fail "the worker's ClamAv__Mode is not off without the profile"
worker="$(dc ps -q kalla-worker)"
[ "$(docker inspect -f '{{.State.Health.Status}}' "$worker")" = healthy ] || fail "kalla-worker is not healthy with the scan off"
ok "malware scan off: no kalla-clamav, kalla-worker healthy with ClamAv__Mode off (files stored unscanned)"

echo
echo "Smoke test passed for $name."
if [ ${#pending[@]} -gt 0 ]; then
  echo "Not built yet, so not checked:"
  printf '  - %s\n' "${pending[@]}"
fi
