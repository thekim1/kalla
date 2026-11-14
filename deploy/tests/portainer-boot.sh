#!/usr/bin/env bash
# Boot test of the Portainer stack (release.yml; the deploy-configuration plan's step C0): starts a
# compose.portainer.yaml, the gateway and Ume.Rag as one stack, the way Portainer does (the stack file, variables, no
# scripts and no host files but the proxy's certificate), with the profiles clamav, openwebui and proxy, then checks:
#
#   - `up --wait`: every service with a health check healthy, both bootstraps and both migrations completed
#   - no container restarted or exited with an error; every image is pinned by digest (*_IMAGE variables are unset)
#   - rag-bootstrap's log ends with its checklist, and the checklist names exactly what this test leaves out: the three
#     gateway credential keys
#   - rag-api: /health/ready = 200 (TLS verified against rag-bootstrap's CA), /version (with --version), /v1/models =
#     401 without a key and 200 with Open WebUI's key, which the stack generated and registered by itself
#   - rag-api's public URL, which no variable sets, is https://<UME_RAG_API_HOST> for citation links and for rag-admin
#   - rag-admin: / = 200 with the admin UI's index.html
#   - the gateway from rag-worker: /health/ready over HTTPS, so the llm network and the gateway's CA work
#   - the parser from rag-worker: /v1/info over HTTPS
#   - rag-clamav runs and the worker's secret file ClamAv__Mode is on; rag-api's OpenWebUi__BaseUrl is rag-openwebui
#   - the TLS proxy (M-17 C8): each of the five host names answers through rag-proxy with the test's certificate
#   - Open WebUI: /health = 200 on its published port; password sign-in refused; rag-openwebui-setup completed and the
#     feedback key it minted (M-17 C6) answers the feedback list (200), is refused elsewhere (403), and is the file rag-api
#     reads
#
#   deploy/tests/portainer-boot.sh STACK.yaml [options]
#
# The variables are deploy/portainer/stack.env.example as it is, then this test's values for every CHANGE line, the
# ports and the binds (127.0.0.1), so a variable the stack requires but the example lacks fails the test.
#
# Options:
#   --version X.Y.Z         rag-api's /version must report it
#   --env-example FILE      the variables file [../portainer/stack.env.example]
#   --port-offset N         added to every default port [10000]: gateway 18443, its admin 19443 and Data API 20443,
#                           rag-api 18543, rag-admin 19543, Open WebUI 13000
#   --compose-file FILE     an extra Compose file after the stack (repeatable), for example fixed subnets on a developer
#                           machine whose Docker address pools are used up
#   --project NAME          the Compose project, which is the stack's name [ume-rag-portainer-boot]
#   --wait-timeout SECONDS  for `up --wait` and for each service without a health check [900]: the parser warms up and
#                           Open WebUI migrates its database first
#   --keep                  leave the stack running
#
# Needs Docker with Compose v2 (Engine 26 or later: the stack mounts volume subpaths), curl and jq, and pull access to
# the stack's images. An earlier run's stack of the same project is removed first, volumes included.
# Exit 0 when every check passed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack=""; version=""; example="$here/../portainer/stack.env.example"; offset=10000; project=ume-rag-portainer-boot
wait_timeout=900; keep=0; files=()
usage() { sed -n '2,38p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
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
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '_IMAGE$|^UME_|^COMPOSE_PROFILES$' || true)

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
UME_RAG_PROXY_CERTS_DIR=$work/proxy-certs
UME_RAG_PROXY_PORT=$proxy_port
UME_RAG_PROXY_BIND=127.0.0.1
UME_OIDC_AUTHORITY=https://keycloak.boot.test/realms/ume
UME_GATEWAY_HOST=llm.boot.test
UME_GATEWAY_PUBLIC_URL=https://llm.boot.test
UME_ADMIN_HOST=llm-admin.boot.test
UME_RAG_API_HOST=rag.boot.test
UME_RAG_ADMIN_HOST=rag-admin.boot.test
UME_RAG_OIDC_CLIENT_SECRET=boot-test-admin-client-secret
UME_RAG_OPENWEBUI_HOST=chat.boot.test
UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=boot-test-openwebui-client-secret
UME_GATEWAY_PORT=$gateway_port
UME_ADMIN_PORT=$gateway_admin_port
UME_DATA_PORT=$gateway_data_port
UME_RAG_API_PORT=$api_port
UME_RAG_ADMIN_PORT=$admin_port
UME_RAG_OPENWEBUI_PORT=$openwebui_port
UME_GATEWAY_BIND=127.0.0.1
UME_ADMIN_BIND=127.0.0.1
UME_DATA_BIND=127.0.0.1
UME_RAG_API_BIND=127.0.0.1
UME_RAG_ADMIN_BIND=127.0.0.1
UME_RAG_OPENWEBUI_BIND=127.0.0.1
EOF

dc() { docker compose -p "$project" -f "$stack" ${files[@]+"${files[@]}"} --env-file "$example" --env-file "$work/test.env" "$@"; }
ok() { echo "ok: $*"; }
fail() {
  echo "error: $*" >&2
  dc ps -a >&2 || true
  dc logs --no-color --tail 60 >&2 || true
  exit 1
}
cleanup() {
  if [ "$keep" = 1 ]; then
    echo "Kept: docker compose -p $project -f $stack --env-file $example --env-file $work/test.env ..."
    return
  fi
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

echo "== up --wait (profiles clamav, openwebui, proxy)"
start=$SECONDS
dc up -d --quiet-pull --wait --wait-timeout "$wait_timeout" || fail "the stack did not start and become healthy within ${wait_timeout}s"
ok "every service with a health check healthy ($((SECONDS - start)) s)"
dc ps -a --format 'table {{.Service}}\t{{.Status}}' | sed 's/^/  /'

echo "== containers"
# Every service of the stack with these profiles has its container (the gateway's own profiles stay off).
expected="$(dc config --services | sort | tr '\n' ' ' | sed 's/ $//')"
running="$(dc ps -a --format '{{.Service}}' | sort | tr '\n' ' ' | sed 's/ $//')"
[ "$running" = "$expected" ] || fail "the stack's containers are '$running', expected '$expected'"
for svc in gateway adminapi rag-api rag-admin rag-worker rag-parser rag-clamav rag-openwebui rag-proxy; do
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

echo "== rag-bootstrap's checklist"
log="$(dc logs --no-color --no-log-prefix rag-bootstrap)"
grep -q '^rag-bootstrap: certificates and secrets ready$' <<< "$log" || fail "rag-bootstrap did not finish its secrets"
checklist="$(sed -n '/^rag-bootstrap: still to do/,$p' <<< "$log")"
[ -n "$checklist" ] || fail "rag-bootstrap printed no checklist, though this test sets no gateway credential key"
awk '{print "  " $0}' <<< "$checklist"
items="$(grep -c '^  - ' <<< "$checklist" || true)"
for var in UME_RAG_API_GATEWAY_KEY UME_RAG_ADMIN_GATEWAY_KEY UME_RAG_WORKER_GATEWAY_KEY; do
  grep -q "^  - $var" <<< "$checklist" || fail "the checklist does not name $var"
done
[ "$items" = 3 ] || fail "the checklist has $items items, expected the three gateway credential keys"
ok "the checklist names the three gateway credential keys, and nothing else (Open WebUI's client is set up by the stack)"

echo "== rag-api"
helper="$(dc config --images | grep -m1 '/alpine/openssl@')"
docker run --rm --network none -v "${project}_rag-certs:/c:ro" --entrypoint cat "$helper" /c/ca.pem > "$work/ca.pem" \
  || fail "could not read rag-bootstrap's CA from the volume ${project}_rag-certs"
https() { curl -sS --cacert "$work/ca.pem" --max-time 15 "$@"; }
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/health/ready")"
[ "$status" = 200 ] || fail "rag-api /health/ready answered $status"
ok "rag-api /health/ready = 200 (TLS verified against rag-bootstrap's CA)"
reported="$(https "https://localhost:$api_port/version" | jq -r '.informationalVersion')"
if [ -n "$version" ]; then
  case "$reported" in "$version"|"$version+"*) ;; *) fail "rag-api /version is $reported, expected $version";; esac
fi
ok "rag-api /version = $reported"
status="$(https -o /dev/null -w '%{http_code}' "https://localhost:$api_port/v1/models")"
[ "$status" = 401 ] || fail "rag-api /v1/models answered $status without a key, expected 401"
ok "rag-api /v1/models = 401 without a key"
# Open WebUI's key, generated by rag-bootstrap and registered by rag-migrate as the deployment's own client (M-17 C4):
# rag-api accepts it with no client made by hand. Read from the volume, never printed.
owui_key="$(docker run --rm --network none -v "${project}_rag-secrets:/s:ro" --entrypoint cat "$helper" /s/.openwebui-rag-api-key)"
status="$(https -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $owui_key" "https://localhost:$api_port/v1/models")"
[ "$status" = 200 ] || fail "rag-api /v1/models answered $status with Open WebUI's generated key, expected 200"
ok "rag-api /v1/models = 200 with Open WebUI's generated key (the deployment's own client, no client made by hand)"

env_of() { docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$(dc ps -q "$1")"; }
env_of rag-api | grep -qx 'Knowledge__PublicBaseUrl=https://rag.boot.test' \
  || fail "rag-api's Knowledge__PublicBaseUrl is not https://rag.boot.test with UME_RAG_API_PUBLIC_URL unset"
env_of rag-admin | grep -qx 'Admin__ApiPublicUrl=https://rag.boot.test' \
  || fail "rag-admin's Admin__ApiPublicUrl is not https://rag.boot.test with UME_RAG_API_PUBLIC_URL unset"
ok "rag-api's public URL defaults to https://rag.boot.test (citation links; rag-admin's How to connect)"

echo "== rag-admin"
status="$(https -o "$work/admin-index.html" -w '%{http_code}' "https://localhost:$admin_port/")"
[ "$status" = 200 ] || fail "rag-admin / answered $status"
grep -q '<div id="app">' "$work/admin-index.html" || fail "rag-admin / is not the admin UI's index.html"
ok "rag-admin / = 200 (the admin UI)"

echo "== the gateway and the parser from rag-worker"
probe() { dc exec -T -e "UME_HEALTHCHECK_URL=$1" rag-worker dotnet Ume.Rag.Worker.dll --health-check; }
probe https://gateway:8443/health/ready || fail "rag-worker could not reach the gateway's /health/ready"
ok "the gateway's /health/ready answered 2xx to rag-worker (the llm network, the gateway's CA)"
wait_for "$wait_timeout" probe https://rag-parser:8000/v1/info || fail "rag-worker could not reach the parser's /v1/info within ${wait_timeout}s"
ok "the parser's /v1/info answered 2xx to rag-worker (HTTPS, rag-bootstrap's CA)"

echo "== malware scan and the feedback import's address, from the profiles"
# rag-bootstrap writes both as secret files (M-17 C2, C3); docker cp reads them from images without a shell.
secret_of() { docker cp "$(dc ps -q "$1"):/run/app-secrets/$2" - 2>/dev/null | tar -xO; }
[ "$(secret_of rag-worker ClamAv__Mode)" = on ] || fail "rag-worker's secret file ClamAv__Mode is not on with the clamav profile"
ok "rag-clamav runs (the clamav profile) and rag-worker's ClamAv__Mode is on"
[ "$(secret_of rag-api OpenWebUi__BaseUrl)" = http://rag-openwebui:8080 ] \
  || fail "rag-api's secret file OpenWebUi__BaseUrl is not http://rag-openwebui:8080 with the openwebui profile"
ok "rag-api's OpenWebUi__BaseUrl is the stack's rag-openwebui (the openwebui profile)"

echo "== the TLS proxy (profile proxy, M-17 C8)"
# through HOST PATH: the HTTP status of https://HOST:<proxy port>PATH through rag-proxy, verified with the test's certificate.
through() { curl -sS -o /dev/null -w '%{http_code}' --max-time 10 --cacert "$work/proxy-certs/tls.crt" \
  --resolve "$1:$proxy_port:127.0.0.1" "https://$1:$proxy_port$2"; }
reached() { case "$(through "$1" "$2")" in 2??|3??) return 0;; *) return 1;; esac; }
while read -r host path what; do
  wait_for 60 reached "$host" "$path" || fail "rag-proxy: https://$host$path answered $(through "$host" "$path") ($what)"
done <<'CHECKS'
llm.boot.test /health/ready the gateway
llm-admin.boot.test / the gateway's admin
rag.boot.test /health/ready rag-api
rag-admin.boot.test / rag-admin
chat.boot.test /health Open WebUI
CHECKS
ok "rag-proxy answers each of the five host names with the operator's certificate (verified upstream TLS)"

echo "== Open WebUI"
owui() { [ "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$openwebui_port/health")" = 200 ]; }
wait_for "$wait_timeout" owui || fail "Open WebUI's /health did not answer 200 within ${wait_timeout}s"
ok "Open WebUI /health = 200 on 127.0.0.1:$openwebui_port"
[ "$(docker inspect -f '{{.RestartCount}}' "$(dc ps -q rag-openwebui)")" = 0 ] || fail "rag-openwebui restarted while starting"
code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H 'Content-Type: application/json' \
  -d '{"email":"feedback-import@localhost","password":"x"}' "http://127.0.0.1:$openwebui_port/api/v1/auths/signin")"
[ "$code" = 403 ] || fail "Open WebUI's password sign-in answered $code, expected 403 (off)"
ok "Open WebUI's password sign-in is off (403)"

echo "== the feedback key (rag-openwebui-setup, M-17 C6)"
setup="$(dc ps -a -q rag-openwebui-setup)"
[ -n "$setup" ] || fail "the stack has no rag-openwebui-setup"
setup_done() { [ "$(docker inspect -f '{{.State.Status}}' "$setup")" = exited ]; }
wait_for "$wait_timeout" setup_done || fail "rag-openwebui-setup did not complete within ${wait_timeout}s"
setup_exit="$(docker inspect -f '{{.State.ExitCode}}' "$setup")"
dc logs --no-color --no-log-prefix rag-openwebui-setup | sed 's/^/  /'
[ "$setup_exit" = 0 ] || fail "rag-openwebui-setup exited $setup_exit"
feedback_key="$(docker run --rm --network none -v "${project}_rag-secrets:/s:ro" --entrypoint cat "$helper" /s/openwebui-feedback/OpenWebUi__AdminApiKey)"
case "$feedback_key" in sk-*) ;; *) fail "rag-secrets/openwebui-feedback/OpenWebUi__AdminApiKey holds no sk- key";; esac
owui_status() { curl -sS -o /dev/null -w '%{http_code}' --max-time 10 -H "Authorization: Bearer $feedback_key" "http://127.0.0.1:$openwebui_port$1"; }
[ "$(owui_status /api/v1/evaluations/feedbacks/list)" = 200 ] || fail "the feedback key is refused on the feedback list"
[ "$(owui_status /api/v1/users/)" = 403 ] || fail "the feedback key is not limited to the feedback routes"
read_by_api="$(docker cp "$(dc ps -q rag-api):/run/openwebui-feedback/OpenWebUi__AdminApiKey" - 2>/dev/null | tar -xO)"
[ "$read_by_api" = "$feedback_key" ] || fail "rag-api's /run/openwebui-feedback does not hold the minted key"
ok "rag-openwebui-setup minted the feedback key: 200 on the feedback list, 403 elsewhere, and rag-api reads it"

echo
echo "The Portainer stack booted: $(basename "$stack")${version:+, Ume.Rag $version}."
