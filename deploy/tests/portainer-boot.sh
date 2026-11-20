#!/usr/bin/env bash
# Boot test of the Portainer stack (release.yml; the deploy-configuration plan's step C0): starts a
# compose.portainer.yaml, the gateway and Källa as one stack, the way Portainer does (the stack file, variables, no
# scripts and no host files but the proxy's certificate), with the profiles clamav, openwebui and proxy, then checks:
#
#   - every long-running service up (healthy where it has a health check), both bootstraps and both migrations
#     completed. Not `up --wait` over the whole stack: it counts a one-shot that exits as a failure, even with code 0,
#     unless a service waits for it, and nothing waits for kalla-openwebui-setup (Portainer doesn't use --wait)
#   - no container restarted or exited with an error; every image is pinned by digest (*_IMAGE variables are unset)
#   - kalla-bootstrap's log ends with its checklist, and the checklist is empty: nothing to paste or type
#   - the gateway credential keys (M-17 C7): neither variables file sets FORS_INTEGRATION_MANIFEST, so the gateway's
#     bootstrap gets the stack file's default (deploy/portainer/integration-manifest.json, compared as JSON); the
#     gateway's migrations created the integration Källa from it, each service has its key and the gateway accepts it;
#     the context names the Integration API, no admin_url, no oidc_authority (nobody completed the gateway's wizard)
#     and the feature oidc-authority
#   - kalla-api: /health/ready = 200 (TLS verified against kalla-bootstrap's CA), /version (with --version), /v1/models =
#     401 without a key and 200 with Open WebUI's key, which the stack generated and registered by itself
#   - all five host names come from FORS_DOMAIN alone (llm., llm-admin., rag., rag-admin. and chat.boot.test; no
#     FORS_*_HOST): adminapi's Gateway__BaseUrl is https://llm.boot.test; kalla-api's public URL is https://rag.boot.test
#     for citation links and for kalla-admin; Open WebUI's WEBUI_URL is https://chat.boot.test
#   - kalla-admin: / = 200 with the admin UI's index.html
#   - the gateway's setup wizard (its docs/identity-and-roles.md, "Setup mode"): the stack sets no sign-in value
#     (M-20's done-when), so adminapi starts in setup mode. The gateway bootstrap's last lines point at
#     https://llm-admin.boot.test/setup; adminapi's output has its setup code; through kalla-proxy, its /bff/user says
#     sign-in is not configured and setup is open; POST /bff/setup/session with its BFF's antiforgery refuses a wrong
#     code (400 setup_code_invalid) and gives a setup session for the right one; GET /bff/setup/state answers first_run,
#     the redirect URI for llm-admin.boot.test and every sign-in value from the code's defaults (no authority)
#   - then kalla-admin's setup wizard (identity design §19): no Oidc value either, so setup is open. kalla-bootstrap's last
#     lines point at https://rag-admin.boot.test:<proxy port>/setup; kalla-admin's output has the setup code; through kalla-proxy,
#     /bff/user says sign-in is not configured and setup is open; the setup session as for the gateway; GET
#     /bff/setup/state answers first_run, the redirect URI for rag-admin.boot.test, every sign-in value from the code's
#     defaults (no authority), the gateway's block once kalla-admin has read its context (no suggested authority, since
#     completing the gateway's wizard needs a real provider; the gateway admin's address https://llm-admin.boot.test
#     from Admin__GatewayAdminUrl) and Open WebUI's block without a provider. Neither setup code is ever printed:
#     masked in GitHub Actions and cut from the logs a failure prints
#   - the gateway from kalla-worker: /health/ready over HTTPS, so the llm network and the gateway's CA work
#   - the parser from kalla-worker: /v1/info over HTTPS
#   - kalla-clamav runs and the worker's secret file ClamAv__Mode is on; kalla-api's OpenWebUi__BaseUrl is kalla-openwebui
#   - the TLS proxy (M-17 C8): each of the five host names answers through kalla-proxy with the test's certificate,
#     and it still reaches kalla-api after kalla-api is recreated on another address (its resolver)
#   - Open WebUI: /health = 200 on its published port, not restarted; password sign-in refused; without a provider
#     anywhere, kalla-api's internal route (401 without a key) answers configured=false, the start wrapper starts it
#     without OIDC sign-in and /api/config offers none, until kalla-admin's wizard saves a provider;
#     kalla-openwebui-setup completed and the
#     feedback key it minted (M-17 C6) answers the feedback list (200), is refused elsewhere (403), and is the file kalla-api
#     reads
#
#   deploy/tests/portainer-boot.sh STACK.yaml [options]
#
# The variables are deploy/portainer/stack.env.example as it is, then this test's FORS_DOMAIN (its one CHANGE line), the
# ports, the binds (127.0.0.1) and the proxy's values, so a variable the stack requires but the example lacks fails the
# test. No sign-in value, secret or host name besides the domain is set.
#
# Options:
#   --version X.Y.Z         kalla-api's /version must report it
#   --env-example FILE      the variables file [../portainer/stack.env.example]
#   --port-offset N         added to every default port [10000]: gateway 18443, its admin 19443 and Data API 20443,
#                           kalla-api 18543, kalla-admin 19543, Open WebUI 13000
#   --compose-file FILE     an extra Compose file after the stack (repeatable), for example fixed subnets on a developer
#                           machine whose Docker address pools are used up
#   --project NAME          the Compose project, which is the stack's name [kalla-portainer-boot]
#   --wait-timeout SECONDS  for the start, for the health checks and for each later wait [900]: the parser warms up and
#                           Open WebUI migrates its database first
#   --keep                  leave the stack running
#
# Needs Docker with Compose v2 (Engine 26 or later: the stack mounts volume subpaths), curl and jq, and pull access to
# the stack's images. An earlier run's stack of the same project is removed first, volumes included.
# Exit 0 when every check passed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack=""; version=""; example="$here/../portainer/stack.env.example"; offset=10000; project=kalla-portainer-boot
wait_timeout=900; keep=0; files=()
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --version) version="$2"; shift 2;;
    --env-example) example="$2"; shift 2;;
    --port-offset) offset="$2"; shift 2;;
    --compose-file) files+=(-f "$2"); shift 2;;
    --project) project="$2"; shift 2;;
    --wait-timeout) wait_timeout="$2"; shift 2;;
    --keep) keep=1; shift;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) [ -z "$stack" ] || usage 1; stack="$1"; shift;;
  esac
done
if [ -z "$stack" ] || [ ! -r "$stack" ]; then usage 1; fi
[ -r "$example" ] || { echo "error: no variables file at $example" >&2; exit 1; }
for tool in docker curl jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done

# The stack's pins and the variables file are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$|^KALLA_|^FORS_|^UME_|^COMPOSE_PROFILES$' || true)

work="$(mktemp -d)"
gateway_port=$((8443 + offset)); gateway_admin_port=$((9443 + offset)); gateway_data_port=$((10443 + offset))
api_port=$((8543 + offset)); admin_port=$((9543 + offset)); openwebui_port=$((3000 + offset))
proxy_port=$((11443 + offset))
# The operator's certificate for the proxy: one self-signed certificate for the five names, readable by nginx (uid 101).
mkdir -p "$work/proxy-certs"
docker run --rm --network none -v "$work/proxy-certs:/c" --entrypoint sh \
  docker.io/alpine/openssl@sha256:59c5cb51e536d40587667229468b007a2a6cae1705397e59e5f7774c08b74029 -c \
  'openssl req -x509 -newkey rsa:2048 -nodes -keyout /c/tls.key -out /c/tls.crt -days 2 -subj /CN=boot.test \
     -addext "subjectAltName=DNS:llm.boot.test,DNS:llm-admin.boot.test,DNS:rag.boot.test,DNS:rag-admin.boot.test,DNS:chat.boot.test" \
     >/dev/null 2>&1 && chmod 644 /c/tls.key /c/tls.crt'
cat > "$work/test.env" <<EOF
COMPOSE_PROFILES=clamav,openwebui,proxy
KALLA_PROXY_CERTS_DIR=$work/proxy-certs
KALLA_PROXY_PORT=$proxy_port
KALLA_PROXY_BIND=127.0.0.1
FORS_DOMAIN=boot.test
FORS_GATEWAY_PORT=$gateway_port
FORS_ADMIN_PORT=$gateway_admin_port
FORS_DATA_PORT=$gateway_data_port
KALLA_API_PORT=$api_port
KALLA_ADMIN_PORT=$admin_port
KALLA_OPENWEBUI_PORT=$openwebui_port
FORS_GATEWAY_BIND=127.0.0.1
FORS_ADMIN_BIND=127.0.0.1
FORS_DATA_BIND=127.0.0.1
KALLA_API_BIND=127.0.0.1
KALLA_ADMIN_BIND=127.0.0.1
KALLA_OPENWEBUI_BIND=127.0.0.1
EOF

compose=(docker compose -p "$project" -f "$stack" ${files[@]+"${files[@]}"} --env-file "$example" --env-file "$work/test.env")
dc() { "${compose[@]}" "$@"; }
ok() { echo "ok: $*"; }
# The two setup codes once read (kalla-admin's and adminapi's), for fail() to cut.
admin_code=""; gateway_code=""
fail() {
  echo "error: $*" >&2
  dc ps -a >&2 || true
  # Each setup code is worth platform admin or gateway admin while setup is open: cut from the logs printed here, after
  # "Setup code: " on either service's line, and as itself wherever else it might appear (the codes are [0-9A-Z-]).
  dc logs --no-color --tail 60 2>&1 | sed -E -e 's/(Setup code: )[0-9A-Za-z-]+/\1(hidden)/' \
    ${admin_code:+-e "s/$admin_code/(hidden)/g"} ${gateway_code:+-e "s/$gateway_code/(hidden)/g"} >&2 || true
  exit 1
}
cleanup() {
  if [ "$keep" = 1 ]; then
    echo "Kept: docker compose -p $project -f $stack --env-file $example --env-file $work/test.env ..."
    return
  fi
  docker rm -f "$project-placeholder" >/dev/null 2>&1 || true
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
# wait_for SECONDS COMMAND...: the command once a second until it succeeds.
wait_for() {
  local deadline=$((SECONDS + $1)); shift
  until "$@" >/dev/null 2>&1; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 1
  done
}

dc down -v --remove-orphans >/dev/null 2>&1 || true

echo "== images"
unpinned="$(dc config --images | grep -vE '@sha256:[0-9a-f]{64}$' || true)"
[ -z "$unpinned" ] || fail "images not pinned by digest: $unpinned"
dc config --images | sort -u | sed 's/^/  /'

echo "== up (profiles clamav, openwebui, proxy)"
dc pull --quiet || fail "could not pull the stack's images"
start=$SECONDS
# Plain `up -d`, the way Portainer starts the stack: Compose still holds each dependent until its one-shot has completed
# (service_completed_successfully) and its dependencies are healthy. Bounded, since a hanging one-shot would block it.
timeout "$wait_timeout" "${compose[@]}" up -d || fail "the stack did not start within ${wait_timeout}s"
# Then --wait on the long-running services only (restart is not "no"). --no-deps keeps Compose from starting the
# one-shots again; kalla-openwebui-setup may still be running and is checked below.
mapfile -t long_running < <(dc config --format json | jq -r '.services | to_entries[] | select((.value.restart // "no") != "no") | .key')
[ "${#long_running[@]}" -gt 0 ] || fail "the stack has no long-running services"
dc up -d --no-deps --no-recreate --wait --wait-timeout "$wait_timeout" "${long_running[@]}" \
  || fail "the long-running services did not become healthy within ${wait_timeout}s"
ok "every long-running service up and every one with a health check healthy ($((SECONDS - start)) s)"
dc ps -a --format 'table {{.Service}}\t{{.Status}}' | sed 's/^/  /'
# setup_code / gateway_setup_code: the last setup code on kalla-admin's / adminapi's output (empty while there is none).
# adminapi's line is "adminapi: setup mode is open until <yyyy-MM-dd HH:mm UTC>. Setup code: <code>" (the gateway's
# src/Fors.AdminApi/Setup/SetupStartup.cs). In GitHub Actions each is masked as soon as it is read, so no later
# step can show it.
setup_code() {
  dc logs --no-color --no-log-prefix kalla-admin 2>/dev/null | sed -n 's/^kalla-admin: setup mode is open until .*\. Setup code: //p' | tail -n 1
}
gateway_setup_code() {
  dc logs --no-color --no-log-prefix adminapi 2>/dev/null | sed -n 's/^adminapi: setup mode is open until .*\. Setup code: //p' | tail -n 1
}
mask() { if [ -n "$1" ] && [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::add-mask::$1"; fi; }
admin_code="$(setup_code)"; mask "$admin_code"
gateway_code="$(gateway_setup_code)"; mask "$gateway_code"

echo "== containers"
# Every service of the stack with these profiles has its container (the gateway's own profiles stay off).
expected="$(dc config --services | sort | tr '\n' ' ' | sed 's/ $//')"
running="$(dc ps -a --format '{{.Service}}' | sort | tr '\n' ' ' | sed 's/ $//')"
[ "$running" = "$expected" ] || fail "the stack's containers are '$running', expected '$expected'"
for svc in gateway adminapi kalla-api kalla-admin kalla-worker kalla-parser kalla-clamav kalla-openwebui kalla-proxy; do
  case " $running " in *" $svc "*) ;; *) fail "the stack has no $svc";; esac
done
while read -r name state exit_code restarts; do
  case "$name" in
    *-bootstrap-1|*-migrations-1|*-rag-migrate-1)
      [ "$state $exit_code" = "exited 0" ] || fail "$name is $state (exit $exit_code), expected to have completed";;
    *-rag-openwebui-setup-1)
      # One-shot that waits for Open WebUI itself; checked below.
      [ "$state" = running ] || [ "$state $exit_code" = "exited 0" ] || fail "$name is $state (exit $exit_code)";;
    *)
      [ "$state" = running ] || fail "$name is $state (exit $exit_code), expected running"
      [ "$restarts" = 0 ] || fail "$name restarted $restarts times";;
  esac
done < <(dc ps -a -q | xargs docker inspect --format '{{.Name}} {{.State.Status}} {{.State.ExitCode}} {{.RestartCount}}')
ok "both bootstraps and both migrations completed; every other container runs and none restarted"

echo "== kalla-bootstrap's checklist"
log="$(dc logs --no-color --no-log-prefix kalla-bootstrap)"
grep -q '^kalla-bootstrap: certificates and secrets ready$' <<< "$log" || fail "kalla-bootstrap did not finish its secrets"
grep -q '^kalla-bootstrap: every setting it checks is in place$' <<< "$log" \
  || { sed -n '/^kalla-bootstrap: still to do/,$p' <<< "$log" | sed 's/^/  /'; fail "kalla-bootstrap's checklist is not empty"; }
ok "kalla-bootstrap's checklist is empty: the gateway credential keys came from the manifest (M-17 C7), nothing pasted"
# No client secret in the stack: kalla-bootstrap can't see the database, so it points at the wizard on every run.
grep -qF "kalla-bootstrap: sign-in is set in the setup wizard: https://rag-admin.boot.test:$proxy_port/setup, " <<< "$log" \
  || fail "kalla-bootstrap's log does not point at the setup wizard on https://rag-admin.boot.test:$proxy_port/setup"
ok "kalla-bootstrap points at the setup wizard (https://rag-admin.boot.test:$proxy_port/setup, through kalla-proxy); no client secret was typed into the stack"

echo "== the gateway credential keys from the manifest (M-17 C7)"
# Neither variables file sets FORS_INTEGRATION_MANIFEST, so the gateway's bootstrap got the stack file's default.
env_of() { docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$(dc ps -a -q "$1")"; }
if grep -q '^FORS_INTEGRATION_MANIFEST=' "$example" "$work/test.env"; then fail "the variables set FORS_INTEGRATION_MANIFEST"; fi
used="$(env_of bootstrap | sed -n 's/^FORS_INTEGRATION_MANIFEST=//p')"
if ! jq -e . >/dev/null 2>&1 <<< "$used" \
  || [ "$(jq -S -c . <<< "$used")" != "$(jq -S -c . "$here/../portainer/integration-manifest.json")" ]; then
  fail "the gateway's bootstrap did not get the default manifest (deploy/portainer/integration-manifest.json)"
fi
ok "with no FORS_INTEGRATION_MANIFEST set, the gateway's bootstrap got the stack file's default manifest"
# The gateway's migrations created the integration Källa from it and wrote each key to the integration-secrets volume;
# kalla-bootstrap copied them to each service. The gateway accepts each one on its context.
gw_helper="$(grep -m1 '/alpine/openssl@' <<< "$(dc config --images)")"
docker run --rm --network none -v "${project}_fors-certs:/c:ro" --entrypoint cat "$gw_helper" /c/ca.pem > "$work/gateway-ca.pem" \
  || fail "could not read the gateway's CA from the volume ${project}_fors-certs"
for svc in api admin worker; do
  key="$(docker run --rm --network none -v "${project}_kalla-secrets:/s:ro" --entrypoint cat "$gw_helper" "/s/$svc/Gateway__Key" 2>/dev/null)" \
    || fail "kalla-secrets/$svc/Gateway__Key is missing"
  manifest_key="$(docker run --rm --network none -v "${project}_integration-secrets:/i:ro" --entrypoint cat "$gw_helper" "/i/Kalla/rag-$svc" 2>/dev/null)" \
    || fail "integration-secrets/Kalla/rag-$svc is missing: the gateway did not provision the manifest"
  [ "$key" = "$manifest_key" ] || fail "kalla-$svc's Gateway__Key is not the manifest's key"
  status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 --cacert "$work/gateway-ca.pem" \
    --resolve "gateway:$gateway_port:127.0.0.1" -H "Authorization: Bearer $key" "https://gateway:$gateway_port/v1/fors/context")"
  [ "$status" = 200 ] || fail "the gateway's /v1/fors/context answered $status to kalla-$svc's key, expected 200"
done
ok "kalla-api, kalla-admin and kalla-worker each have the manifest's key, and the gateway accepts it (/v1/fors/context = 200)"
context="$(docker run --rm --network none -v "${project}_kalla-secrets:/s:ro" --entrypoint cat "$gw_helper" /s/admin/Gateway__Key)"
gateway_context="$(curl -sS --max-time 15 --cacert "$work/gateway-ca.pem" --resolve "gateway:$gateway_port:127.0.0.1" \
  -H "Authorization: Bearer $context" "https://gateway:$gateway_port/v1/fors/context" | jq -c '.gateway')"
api_url="$(jq -r '.integration_api_url' <<< "$gateway_context")"
[ "$api_url" = https://adminapi:8443/integration/v1 ] || fail "the context's integration_api_url is '$api_url'"
ok "the context names the Integration API at https://adminapi:8443/integration/v1"
# Nothing declares the gateway's sign-in and its wizard is not completed, so it hands out no provider; no
# FORS_ADMIN_PUBLIC_URL, so no admin_url either (kalla-admin takes Admin__GatewayAdminUrl). The gateway's
# src/Fors.Gateway/Requests/ContextEndpoint.cs.
jq -e '.oidc_authority == null and .admin_url == null and (.features | index("oidc-authority")) != null' >/dev/null <<< "$gateway_context" \
  || fail "the context's gateway block: $(jq -c '{oidc_authority, admin_url, features}' <<< "$gateway_context")"
ok "the context announces oidc-authority with none set, and no admin_url"
probe_admin() { dc exec -T -e "FORS_HEALTHCHECK_URL=$1" kalla-admin dotnet Kalla.AdminApi.dll --health-check; }
probe_admin https://adminapi:8443/health/live || fail "kalla-admin could not reach the gateway's adminapi (the edge network, the gateway's CA)"
ok "kalla-admin reaches https://adminapi:8443 (the gateway's edge network)"

echo "== kalla-api"
helper="$(grep -m1 '/alpine/openssl@' <<< "$(dc config --images)")"
docker run --rm --network none -v "${project}_kalla-certs:/c:ro" --entrypoint cat "$helper" /c/ca.pem > "$work/ca.pem" \
  || fail "could not read kalla-bootstrap's CA from the volume ${project}_kalla-certs"
https() { curl -sS --cacert "$work/ca.pem" --max-time 15 "$@"; }
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/health/ready")"
[ "$status" = 200 ] || fail "kalla-api /health/ready answered $status"
ok "kalla-api /health/ready = 200 (TLS verified against kalla-bootstrap's CA)"
reported="$(https "https://localhost:$api_port/version" | jq -r '.informationalVersion')"
if [ -n "$version" ]; then
  case "$reported" in "$version"|"$version+"*) ;; *) fail "kalla-api /version is $reported, expected $version";; esac
fi
ok "kalla-api /version = $reported"
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/v1/models")"
[ "$status" = 401 ] || fail "kalla-api /v1/models answered $status without a key, expected 401"
ok "kalla-api /v1/models = 401 without a key"
# Open WebUI's key, generated by kalla-bootstrap and registered by kalla-migrate as the deployment's own client (M-17 C4):
# kalla-api accepts it with no client made by hand. Read from the volume, never printed.
owui_key="$(docker run --rm --network none -v "${project}_kalla-secrets:/s:ro" --entrypoint cat "$helper" /s/.openwebui-rag-api-key)"
status="$(https -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $owui_key" "https://localhost:$api_port/v1/models")"
[ "$status" = 200 ] || fail "kalla-api /v1/models answered $status with Open WebUI's generated key, expected 200"
ok "kalla-api /v1/models = 200 with Open WebUI's generated key (the deployment's own client, no client made by hand)"

env_of adminapi | grep -qx 'Gateway__BaseUrl=https://llm.boot.test' \
  || fail "adminapi's Gateway__BaseUrl is not https://llm.boot.test with only FORS_DOMAIN set"
env_of adminapi | grep -qx 'Oidc__Authority=' || fail "adminapi has an Oidc__Authority with none set"
ok "the gateway's public URL defaults to https://llm.boot.test under FORS_DOMAIN; adminapi has no authority"
env_of kalla-api | grep -qx 'Knowledge__PublicBaseUrl=https://rag.boot.test' \
  || fail "kalla-api's Knowledge__PublicBaseUrl is not https://rag.boot.test with KALLA_API_PUBLIC_URL unset"
env_of kalla-admin | grep -qx 'Admin__ApiPublicUrl=https://rag.boot.test' \
  || fail "kalla-admin's Admin__ApiPublicUrl is not https://rag.boot.test with KALLA_API_PUBLIC_URL unset"
ok "kalla-api's public URL defaults to https://rag.boot.test (citation links; kalla-admin's How to connect)"
env_of kalla-openwebui | grep -qx 'WEBUI_URL=https://chat.boot.test' \
  || fail "kalla-openwebui's WEBUI_URL is not https://chat.boot.test with only FORS_DOMAIN set"
ok "Open WebUI's address defaults to https://chat.boot.test under FORS_DOMAIN"

echo "== kalla-admin"
status="$(https -o "$work/admin-index.html" -w '%{http_code}' "https://localhost:$admin_port/")"
[ "$status" = 200 ] || fail "kalla-admin / answered $status"
grep -q '<div id="app">' "$work/admin-index.html" || fail "kalla-admin / is not the admin UI's index.html"
ok "kalla-admin / = 200 (the admin UI)"

echo "== the gateway and the parser from kalla-worker"
probe() { dc exec -T -e "FORS_HEALTHCHECK_URL=$1" kalla-worker dotnet Kalla.Worker.dll --health-check; }
probe https://gateway:8443/health/ready || fail "kalla-worker could not reach the gateway's /health/ready"
ok "the gateway's /health/ready answered 2xx to kalla-worker (the llm network, the gateway's CA)"
wait_for "$wait_timeout" probe https://kalla-parser:8000/v1/info || fail "kalla-worker could not reach the parser's /v1/info within ${wait_timeout}s"
ok "the parser's /v1/info answered 2xx to kalla-worker (HTTPS, kalla-bootstrap's CA)"

echo "== malware scan and the feedback import's address, from the profiles"
# kalla-bootstrap writes both as secret files (M-17 C2, C3); docker cp reads them from images without a shell.
secret_of() { docker cp "$(dc ps -q "$1"):/run/app-secrets/$2" - 2>/dev/null | tar -xO; }
[ "$(secret_of kalla-worker ClamAv__Mode)" = on ] || fail "kalla-worker's secret file ClamAv__Mode is not on with the clamav profile"
ok "kalla-clamav runs (the clamav profile) and kalla-worker's ClamAv__Mode is on"
[ "$(secret_of kalla-api OpenWebUi__BaseUrl)" = http://kalla-openwebui:8080 ] \
  || fail "kalla-api's secret file OpenWebUi__BaseUrl is not http://kalla-openwebui:8080 with the openwebui profile"
ok "kalla-api's OpenWebUi__BaseUrl is the stack's kalla-openwebui (the openwebui profile)"

echo "== the TLS proxy (profile proxy, M-17 C8)"
# through HOST PATH: the HTTP status of https://HOST:<proxy port>PATH through kalla-proxy, verified with the test's certificate.
through() { curl -sS -o /dev/null -w '%{http_code}' --max-time 10 --cacert "$work/proxy-certs/tls.crt" \
  --resolve "$1:$proxy_port:127.0.0.1" "https://$1:$proxy_port$2"; }
reached() { case "$(through "$1" "$2")" in 2??|3??) return 0;; *) return 1;; esac; }
while read -r host path what; do
  wait_for 60 reached "$host" "$path" || fail "kalla-proxy: https://$host$path answered $(through "$host" "$path") ($what)"
done <<'CHECKS'
llm.boot.test /health/ready the gateway
llm-admin.boot.test / the gateway's admin
rag.boot.test /health/ready kalla-api
rag-admin.boot.test / kalla-admin
chat.boot.test /health Open WebUI
CHECKS
ok "kalla-proxy answers each of the five host names, all from FORS_DOMAIN, with the operator's certificate (verified upstream TLS)"
# A stack update recreates services while kalla-proxy keeps running. Docker gives a new container the lowest free address,
# so a placeholder takes kalla-api's address first, and the recreated kalla-api must still be reached by name (the proxy's
# resolver, not an address kept from its start).
api_net="${project}_kalla-edge"
api_ip() { docker inspect -f "{{(index .NetworkSettings.Networks \"$api_net\").IPAddress}}" "$(dc ps -q kalla-api)"; }
old_ip="$(api_ip)"
dc rm -sf kalla-api >/dev/null 2>&1 || fail "could not remove kalla-api"
docker run -d --rm --name "$project-placeholder" --network "$api_net" --entrypoint sleep \
  "$(docker inspect -f '{{.Config.Image}}' "$(dc ps -q kalla-proxy)")" 600 >/dev/null || fail "could not start the placeholder"
dc up -d --no-deps --wait --wait-timeout "$wait_timeout" kalla-api >/dev/null 2>&1 || fail "kalla-api did not come back healthy"
new_ip="$(api_ip)"
docker rm -f "$project-placeholder" >/dev/null 2>&1 || true
[ "$new_ip" != "$old_ip" ] || fail "kalla-api came back on its old address $old_ip; the check below would prove nothing"
wait_for 30 reached rag.boot.test /health/ready \
  || fail "kalla-proxy: kalla-api recreated at $new_ip (was $old_ip), https://rag.boot.test/health/ready answered $(through rag.boot.test /health/ready)"
ok "kalla-proxy follows a recreated kalla-api to its new address ($old_ip → $new_ip) without a restart"

echo "== the gateway's setup wizard (the gateway's docs/identity-and-roles.md, \"Setup mode\")"
# No FORS_OIDC_AUTHORITY, so adminapi opened setup mode at its first start and wrote its setup code to its standard output;
# read from the log and never printed. Routes, cookies and headers as the gateway's own deploy/tests/setup-mode-boot.sh
# and its admin API: /bff/user (Security/AdminAuthentication.cs) sets the XSRF-TOKEN cookie, sent back as X-XSRF-TOKEN
# with X-Requested-With; /bff/setup/session, /bff/setup/state and the cookie __Secure-ume-setup (Setup/SetupEndpoints.cs,
# Setup/SetupSessions.cs); the problem code setup_code_invalid (Setup/SetupContracts.cs).
grep -qF "bootstrap: sign-in to the admin console is set in the setup wizard: https://llm-admin.boot.test:$gateway_admin_port/setup (behind a reverse proxy https://llm-admin.boot.test/setup)" \
  <<< "$(dc logs --no-color --no-log-prefix bootstrap)" \
  || fail "the gateway's bootstrap does not point at the setup wizard on https://llm-admin.boot.test/setup"
ok "the gateway's bootstrap points at its setup wizard (https://llm-admin.boot.test/setup)"
has_gateway_code() { [ -n "$(gateway_setup_code)" ]; }
wait_for 60 has_gateway_code || true
gateway_code="$(gateway_setup_code)"
[ -n "$gateway_code" ] || fail "adminapi's output has no 'setup mode is open until … Setup code:' line"
mask "$gateway_code"
grep -qE '^[0-9A-Z]{5}(-[0-9A-Z]{5}){3}-[0-9A-Z]{6}$' <<< "$gateway_code" || fail "adminapi's setup code is not in the form XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX"
ok "adminapi's output says setup mode is open and has a setup code (not shown)"
gw_jar="$work/gateway-setup-cookies"; : > "$gw_jar"
gw_setup_url="https://llm-admin.boot.test:$proxy_port"
gateway_ui() { curl -sS --max-time 15 --cacert "$work/proxy-certs/tls.crt" --resolve "llm-admin.boot.test:$proxy_port:127.0.0.1" \
  -c "$gw_jar" -b "$gw_jar" "$@"; }
user="$(gateway_ui -H 'Accept: application/json' "$gw_setup_url/bff/user")" || fail "the gateway's /bff/user did not answer through kalla-proxy"
jq -e '.isAuthenticated == false and .signIn.configured == false and .setup.open == true' >/dev/null <<< "$user" \
  || fail "the gateway's /bff/user is not signed out, sign-in not configured and setup open: $(jq -c '{isAuthenticated, signIn, setup}' <<< "$user")"
ok "the gateway's /bff/user: sign-in not configured (no authority anywhere) and setup open"
gw_xsrf="$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$gw_jar")"
[ -n "$gw_xsrf" ] || fail "the gateway's /bff/user set no XSRF-TOKEN cookie"
# gateway_post PATH: as post below, for the gateway's admin.
gateway_post() { gateway_ui -X POST -H 'Content-Type: application/json' -H 'Accept: application/json' -H 'X-Requested-With: XMLHttpRequest' \
  -H "X-XSRF-TOKEN: $gw_xsrf" --data-binary @- -w '\n%{http_code}' "$gw_setup_url$1"; }
answer="$(printf '{"code":"%s"}' 00000-00000-00000-00000-000000 | gateway_post /bff/setup/session)"
status="$(tail -n 1 <<< "$answer")"; body="$(sed '$d' <<< "$answer")"
[ "$status" = 400 ] && [ "$(jq -r '.code // empty' <<< "$body")" = setup_code_invalid ] \
  || fail "a wrong setup code answered $status $(jq -c '{code, detail}' <<< "$body" 2>/dev/null) at the gateway, expected 400 setup_code_invalid"
ok "the gateway's POST /bff/setup/session refuses a wrong code (400 setup_code_invalid)"
answer="$(printf '{"code":"%s"}' "$gateway_code" | gateway_post /bff/setup/session)"
status="$(tail -n 1 <<< "$answer")"
[ "$status" = 200 ] || fail "the setup code from adminapi's output answered $status on the gateway's POST /bff/setup/session, expected 200"
grep -q $'\t__Secure-ume-setup\t' "$gw_jar" || fail "the gateway's POST /bff/setup/session gave no setup session cookie"
ok "the gateway's POST /bff/setup/session takes the code from adminapi's output: 200 and a setup session"
state="$(gateway_ui -H 'Accept: application/json' -w '\n%{http_code}' "$gw_setup_url/bff/setup/state")"
status="$(tail -n 1 <<< "$state")"; state="$(sed '$d' <<< "$state")"
[ "$status" = 200 ] || fail "the gateway's GET /bff/setup/state answered $status with the setup session, expected 200"
jq -e '.kind == "first_run" and .redirectUris.signIn == "https://llm-admin.boot.test/signin-oidc"
    and .signIn.authority.value == null and .signIn.authority.source == "default" and .signIn.clientSecret.set == false
    and .gatewayAdminGroups == []' >/dev/null <<< "$state" \
  || fail "the gateway's GET /bff/setup/state: $(jq -c '{kind, redirectUris, authority: .signIn.authority, clientSecret: .signIn.clientSecret, gatewayAdminGroups}' <<< "$state")"
declared="$(jq -r '.signIn | to_entries[] | select((.value | type) == "object" and .value.source != "default") | .key' <<< "$state")"
[ -z "$declared" ] || fail "the stack sets gateway sign-in values a deployer didn't: $(tr '\n' ' ' <<< "$declared")"
ok "the gateway's GET /bff/setup/state: first_run, redirect URI https://llm-admin.boot.test/signin-oidc, nothing declared (every value the code's default, no authority)"

echo "== kalla-admin's setup wizard (identity design §19)"
# No Oidc value either (neither FORS_OIDC_AUTHORITY nor KALLA_OIDC_*), so setup is open on this first run and kalla-admin
# wrote the setup code to its standard output. It is read from the log and never printed.
has_setup_code() { [ -n "$(setup_code)" ]; }
wait_for 60 has_setup_code || true
admin_code="$(setup_code)"
[ -n "$admin_code" ] || fail "kalla-admin's output has no 'setup mode is open until … Setup code:' line"
mask "$admin_code"
grep -qE '^[0-9A-Z]{5}(-[0-9A-Z]{5}){3}-[0-9A-Z]{6}$' <<< "$admin_code" || fail "kalla-admin's setup code is not in the form XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX"
ok "kalla-admin's output says setup mode is open and has a setup code (not shown)"
# Through kalla-proxy, as a browser reaches it, with a cookie jar and the admin UI's antiforgery headers
# (src/admin-ui/src/api/client.ts): X-Requested-With and the RAG-XSRF-TOKEN cookie as X-XSRF-TOKEN.
jar="$work/setup-cookies"; : > "$jar"
setup_url="https://rag-admin.boot.test:$proxy_port"
admin_ui() { curl -sS --max-time 15 --cacert "$work/proxy-certs/tls.crt" --resolve "rag-admin.boot.test:$proxy_port:127.0.0.1" \
  -c "$jar" -b "$jar" "$@"; }
user="$(admin_ui -H 'Accept: application/json' "$setup_url/bff/user")" || fail "kalla-admin's /bff/user did not answer through kalla-proxy"
jq -e '.isAuthenticated == false and .signIn.configured == false and .setup.open == true' >/dev/null <<< "$user" \
  || fail "kalla-admin's /bff/user is not signed out, sign-in not configured and setup open: $(jq -c '{isAuthenticated, signIn, setup}' <<< "$user")"
ok "/bff/user: sign-in not configured (no authority until the wizard saves one) and setup open"
xsrf="$(awk '$6 == "RAG-XSRF-TOKEN" { print $7 }' "$jar")"
[ -n "$xsrf" ] || fail "kalla-admin's /bff/user set no RAG-XSRF-TOKEN cookie"
# post PATH: a JSON body from standard input, as the admin UI posts; prints the body, then the status on a line of its own.
post() { admin_ui -X POST -H 'Content-Type: application/json' -H 'Accept: application/json' -H 'X-Requested-With: XMLHttpRequest' \
  -H "X-XSRF-TOKEN: $xsrf" --data-binary @- -w '\n%{http_code}' "$setup_url$1"; }
answer="$(printf '{"code":"%s"}' 00000-00000-00000-00000-000000 | post /bff/setup/session)"
status="$(tail -n 1 <<< "$answer")"; body="$(sed '$d' <<< "$answer")"
[ "$status" = 400 ] && [ "$(jq -r '.code // empty' <<< "$body")" = setup_code_invalid ] \
  || fail "a wrong setup code answered $status $(jq -c '{code, detail}' <<< "$body" 2>/dev/null), expected 400 setup_code_invalid"
ok "POST /bff/setup/session refuses a wrong code (400 setup_code_invalid)"
answer="$(printf '{"code":"%s"}' "$admin_code" | post /bff/setup/session)"
status="$(tail -n 1 <<< "$answer")"
[ "$status" = 200 ] || fail "the setup code from kalla-admin's output answered $status on POST /bff/setup/session, expected 200"
grep -q $'\t__Secure-kalla-setup\t' "$jar" || fail "POST /bff/setup/session gave no setup session cookie"
ok "POST /bff/setup/session takes the code from kalla-admin's output: 200 and a setup session"
# setup_state: GET /bff/setup/state with the setup session; the body into $state, the status as the result.
setup_state() {
  local answer; answer="$(admin_ui -H 'Accept: application/json' -w '\n%{http_code}' "$setup_url/bff/setup/state")"
  state="$(sed '$d' <<< "$answer")"
  [ "$(tail -n 1 <<< "$answer")" = 200 ]
}
setup_state || fail "GET /bff/setup/state did not answer 200 with the setup session"
jq -e '.kind == "first_run" and .redirectUris.signIn == "https://rag-admin.boot.test/signin-oidc"
    and .signIn.authority.value == null and .signIn.authority.source == "default"
    and .signIn.clientSecret.set == false' >/dev/null <<< "$state" \
  || fail "GET /bff/setup/state: $(jq -c '{kind, redirectUris, authority: .signIn.authority, clientSecret: .signIn.clientSecret}' <<< "$state")"
# Nothing is locked: the stack passes a sign-in value only when a variable sets it.
locked="$(jq -r '.signIn | to_entries[] | select((.value | type) == "object" and .value.source != "default") | .key' <<< "$state")"
[ -z "$locked" ] || fail "the stack sets sign-in values a deployer didn't: $(tr '\n' ' ' <<< "$locked")"
# Open WebUI: no provider (no authority to derive one from, none typed), so not configured.
jq -e '.openWebUi.enabled == true and .openWebUi.signIn.configured == false and .openWebUi.signIn.providerUrl.value == null
    and .openWebUi.signIn.clientSecret.source == "default"
    and .openWebUi.redirectUri == "https://chat.boot.test/oauth/oidc/login/callback"' >/dev/null <<< "$state" \
  || fail "GET /bff/setup/state's Open WebUI block: $(jq -c '.openWebUi | {enabled, redirectUri, configured: .signIn.configured, providerUrl: .signIn.providerUrl, clientSecret: .signIn.clientSecret}' <<< "$state")"
ok "GET /bff/setup/state: first_run, redirect URI https://rag-admin.boot.test/signin-oidc, every value the code's default (no authority); Open WebUI without a provider"
# The gateway's block (SetupModeEndpoints.cs, SetupGatewaySignIn) is null until kalla-admin has read its own gateway context
# with the feature oidc-authority: at its start, or ContextInterval (5 minutes) after a first read that found the gateway
# not up yet. Then no suggested authority (the gateway's wizard is not completed: that needs a real provider) and the
# gateway admin's address from Admin__GatewayAdminUrl, since the context has no admin_url.
gateway_block() { setup_state && jq -e '.gateway != null' >/dev/null <<< "$state"; }
wait_for "$wait_timeout" gateway_block || fail "GET /bff/setup/state has no gateway block within ${wait_timeout}s: $(jq -c '.gateway' <<< "$state")"
jq -e '.gateway.suggestedAuthority == null and .gateway.adminUrl == "https://llm-admin.boot.test"' >/dev/null <<< "$state" \
  || fail "GET /bff/setup/state's gateway block: $(jq -c '.gateway' <<< "$state")"
ok "GET /bff/setup/state: the gateway suggests no authority (its wizard not completed) and links to https://llm-admin.boot.test"

echo "== Open WebUI"
owui() { [ "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$openwebui_port/health")" = 200 ]; }
wait_for "$wait_timeout" owui || fail "Open WebUI's /health did not answer 200 within ${wait_timeout}s"
ok "Open WebUI /health = 200 on 127.0.0.1:$openwebui_port"
[ "$(docker inspect -f '{{.RestartCount}}' "$(dc ps -q kalla-openwebui)")" = 0 ] || fail "kalla-openwebui restarted while starting"
code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H 'Content-Type: application/json' \
  -d '{"email":"feedback-import@localhost","password":"x"}' "http://127.0.0.1:$openwebui_port/api/v1/auths/signin")"
[ "$code" = 403 ] || fail "Open WebUI's password sign-in answered $code, expected 403 (off)"
ok "Open WebUI's password sign-in is off (403)"
# Its sign-in settings (identity design §19): kalla-api's provider address is OpenWebUi:Oidc:ProviderUrl, else derived from
# kalla-admin's authority (the deployment's or the wizard's); this stack has neither, so the route answers configured=false
# (OpenWebUiSignIn.Configured needs a provider address) and the start wrapper (deploy/openwebui/start-with-sign-in.py)
# starts Open WebUI without OIDC sign-in: nobody can sign in until kalla-admin's wizard saves a provider, and the wrapper
# then restarts it with one. Its line is "no sign-in settings yet …", or "kalla-api did not answer within …" when kalla-api
# was slower than the wrapper's wait; both say "starting without OIDC sign-in". Nothing of the route's answer is printed.
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/internal/openwebui/sign-in")"
[ "$status" = 401 ] || fail "kalla-api's Open WebUI sign-in route answered $status without a key, expected 401"
configured="$(https -H "Authorization: Bearer $owui_key" "https://localhost:$api_port/internal/openwebui/sign-in" | jq -r '.configured')"
[ "$configured" = false ] || fail "kalla-api's Open WebUI sign-in route says configured=$configured with no provider anywhere"
owui_log="$(dc logs --no-color --no-log-prefix kalla-openwebui)"
grep -q '^kalla-openwebui: .*starting without OIDC sign-in' <<< "$owui_log" \
  || fail "kalla-openwebui's start wrapper did not say it starts Open WebUI without OIDC sign-in"
if grep -q '^kalla-openwebui: starting with OIDC sign-in' <<< "$owui_log"; then fail "kalla-openwebui started with OIDC sign-in, with no provider anywhere"; fi
provider="$(curl -sS --max-time 10 "http://127.0.0.1:$openwebui_port/api/config" | jq -r '.oauth.providers.oidc // empty')"
[ -z "$provider" ] || fail "Open WebUI offers an OIDC sign-in ('$provider') with no provider anywhere"
ok "kalla-api answers Open WebUI's sign-in route to its own client only (401 without a key): not configured; Open WebUI starts without OIDC sign-in"

echo "== the feedback key (kalla-openwebui-setup, M-17 C6)"
setup="$(dc ps -a -q kalla-openwebui-setup)"
[ -n "$setup" ] || fail "the stack has no kalla-openwebui-setup"
setup_done() { [ "$(docker inspect -f '{{.State.Status}}' "$setup")" = exited ]; }
wait_for "$wait_timeout" setup_done || fail "kalla-openwebui-setup did not complete within ${wait_timeout}s"
setup_exit="$(docker inspect -f '{{.State.ExitCode}}' "$setup")"
dc logs --no-color --no-log-prefix kalla-openwebui-setup | sed 's/^/  /'
[ "$setup_exit" = 0 ] || fail "kalla-openwebui-setup exited $setup_exit"
feedback_key="$(docker run --rm --network none -v "${project}_kalla-secrets:/s:ro" --entrypoint cat "$helper" /s/openwebui-feedback/OpenWebUi__AdminApiKey)"
case "$feedback_key" in sk-*) ;; *) fail "kalla-secrets/openwebui-feedback/OpenWebUi__AdminApiKey holds no sk- key";; esac
owui_status() { curl -sS -o /dev/null -w '%{http_code}' --max-time 10 -H "Authorization: Bearer $feedback_key" "http://127.0.0.1:$openwebui_port$1"; }
[ "$(owui_status /api/v1/evaluations/feedbacks/list)" = 200 ] || fail "the feedback key is refused on the feedback list"
[ "$(owui_status /api/v1/users/)" = 403 ] || fail "the feedback key is not limited to the feedback routes"
read_by_api="$(docker cp "$(dc ps -q kalla-api):/run/openwebui-feedback/OpenWebUi__AdminApiKey" - 2>/dev/null | tar -xO)"
[ "$read_by_api" = "$feedback_key" ] || fail "kalla-api's /run/openwebui-feedback does not hold the minted key"
ok "kalla-openwebui-setup minted the feedback key: 200 on the feedback list, 403 elsewhere, and kalla-api reads it"

echo
echo "The Portainer stack booted: $(basename "$stack")${version:+, Källa $version}."
