#!/usr/bin/env bash
# Pilot stack checks (plans/p1b/README.md §3 item 17; plans/platform/repo-and-deployment.md §6.2; chat-api/design.md
# §4.3, §14.2), offline with `docker compose config` against placeholder settings:
#
#   - the base files (compose.prod.yaml + compose.ingestion.yaml) keep every cpus cap at 2 or less (decisions table,
#     "Low CPU during development") and carry the 16 GB limits of §6.2;
#   - with compose.host-24g.yaml (and compose.clamav-conf.yaml, as init-deployment.sh --host-memory 24 lists them) every
#     container has the pilot caps and the 24 GB limits of §6.2, the parser runs 2 slots x 2 threads and postgres the
#     24 GB memory settings; deploy/host/install.sh --host-memory 24 writes the slice's MemoryMax=11G;
#   - compose.openwebui.yaml: the §6.2 row for open-webui, API keys only for the feedback routes, the admin's chat
#     access and export off, password sign-in off unless UME_RAG_OPENWEBUI_PASSWORD_AUTH=true, OIDC users as role user;
#   - init-deployment.sh --openwebui-host writes the feedback service account once (a re-run keeps its password).
#
#   deploy/tests/pilot-stack.sh
#
# Needs Docker with Compose v2, jq and openssl. Prints "ok: ..." per check and PASS, or "FAIL: ..." and exits 1.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
d="$repo/deploy"
for tool in docker jq openssl; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

# Placeholder settings, as verify.yml's "Production Compose files are valid".
export UME_RAG_CERTS_DIR=/certs UME_RAG_SECRETS_DIR="$work/secrets"
export UME_RAG_OIDC_AUTHORITY=https://keycloak.example.se/realms/ume UME_RAG_GATEWAY_URL=https://llm.example.se
export UME_RAG_API_PUBLIC_URL=https://rag-api.example.se UME_RAG_OPENWEBUI_HOST=chat.example.se
export UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=open-webui UME_RAG_OPENWEBUI_DATA_DIR=/openwebui-data
export UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL=https://keycloak.example.se/realms/ume/.well-known/openid-configuration
unset UME_RAG_OPENWEBUI_PASSWORD_AUTH
mkdir -p "$UME_RAG_SECRETS_DIR" && touch "$UME_RAG_SECRETS_DIR/openwebui.env"

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
render() { # render FILE... — the merged configuration as JSON
  local args=() f
  for f in "$@"; do args+=(-f "$d/$f"); done
  docker compose "${args[@]}" config --format json
}
bytes() { # bytes SIZE — Compose's byte count for 1.5g, 384m, ...
  awk -v s="$1" 'BEGIN { n = s + 0; u = substr(s, length(s));
    printf "%d", n * (u == "g" ? 1073741824 : u == "m" ? 1048576 : 1) }'
}
# expect JSON TABLE — TABLE rows: service cpus cpu_shares mem_reservation mem_limit pids_limit (- = not set)
expect() {
  local json="$1" svc cpus shares res limit pids got want res_json
  while read -r svc cpus shares res limit pids; do
    [ -n "$svc" ] || continue
    if [ "$res" = - ]; then res_json=null; else res_json="\"$(bytes "$res")\""; fi
    want="$cpus $shares $res_json \"$(bytes "$limit")\" $pids"
    got="$(jq -r --arg s "$svc" '.services[$s] | "\(.cpus) \(.cpu_shares) \(.mem_reservation | tojson) \(.mem_limit | tojson) \(.pids_limit)"' <<< "$json")"
    if [ "$got" = "$want" ]; then ok "$svc: cpus $cpus, cpu_shares $shares, mem $res/$limit, pids $pids"
    else fail "$svc: expected '$want' (cpus cpu_shares mem_reservation mem_limit pids_limit), got '$got'"; fi
  done <<< "$2"
}
env_is() { # env_is JSON SERVICE KEY VALUE
  local got
  got="$(jq -r --arg s "$2" --arg k "$3" '.services[$s].environment[$k] // "<unset>"' <<< "$1")"
  if [ "$got" = "$4" ]; then ok "$2: $3=$4"; else fail "$2: $3 is '$got', expected '$4'"; fi
}

# --- Base files: development caps, 16 GB limits ---------------------------------------------------------------------
base="$(render compose.prod.yaml compose.ingestion.yaml)"
expect "$base" '
postgres    2   1024 2g   3g   512
rag-api     2   1024 384m 1g   512
rag-admin   1   512  256m 512m 256
rag-migrate 1   512  -    512m 128
rag-worker  1.5 256  512m 1.5g 512
rag-parser  2   128  2g   3.5g 256
rag-clamav  1   128  1.2g 1.5g 128'
over="$(jq -r '.services | to_entries[] | select((.value.cpus // 0) > 2) | .key' <<< "$base")"
if [ -z "$over" ]; then ok "base files: every cpus cap is 2 or less"; else fail "base files: cpus above 2: $over"; fi
env_is "$base" rag-parser PARSER_WORKERS 1
env_is "$base" rag-parser PARSER_THREADS 2
env_is "$base" rag-api OpenWebUi__BaseUrl ""
env_is "$base" rag-api OpenWebUi__FeedbackImportInterval 00:15:00

# --- 24 GB pilot host: pilot caps and limits ------------------------------------------------------------------------
pilot="$(render compose.prod.yaml compose.ingestion.yaml compose.host-24g.yaml compose.clamav-conf.yaml)"
expect "$pilot" '
postgres    3   1024 2g   5g   512
rag-api     2   1024 384m 1.5g 512
rag-admin   1   512  256m 768m 256
rag-migrate 1   512  -    512m 128
rag-worker  1.5 256  512m 2g   512
rag-parser  4   128  2g   6g   256
rag-clamav  1   128  1.2g 3g   128'
env_is "$pilot" rag-parser PARSER_WORKERS 2
env_is "$pilot" rag-parser PARSER_THREADS 2
env_is "$pilot" rag-parser PARSER_SLOT_MAX_RSS_MB 2600
for setting in shared_buffers=1536MB effective_cache_size=3500MB maintenance_work_mem=1GB; do
  if jq -e --arg s "$setting" '.services.postgres.command | index($s)' <<< "$pilot" >/dev/null; then
    ok "postgres: -c $setting"
  else fail "postgres: -c $setting missing"; fi
done
bash "$d/host/install.sh" --unit-dir "$work/units" --host-memory 24 --no-enable >/dev/null
if grep -qx 'MemoryMax=11G' "$work/units/rag-ingestion.slice.d/50-host-memory.conf"; then
  ok "rag-ingestion.slice: MemoryMax=11G on a 24 GB host"
else fail "rag-ingestion.slice: no MemoryMax=11G drop-in for a 24 GB host"; fi

# --- Open WebUI -----------------------------------------------------------------------------------------------------
owui="$(render compose.openwebui.yaml)"
expect "$owui" 'open-webui 1 512 512m 1.5g 256'
env_is "$owui" open-webui ENABLE_API_KEYS true
env_is "$owui" open-webui ENABLE_API_KEYS_ENDPOINT_RESTRICTIONS true
env_is "$owui" open-webui API_KEYS_ALLOWED_ENDPOINTS /api/v1/evaluations/feedbacks/list,/api/v1/evaluations/feedback
env_is "$owui" open-webui USER_PERMISSIONS_FEATURES_API_KEYS false
env_is "$owui" open-webui ENABLE_ADMIN_CHAT_ACCESS false
env_is "$owui" open-webui ENABLE_ADMIN_EXPORT false
env_is "$owui" open-webui ENABLE_LOGIN_FORM false
env_is "$owui" open-webui ENABLE_PASSWORD_AUTH false
env_is "$owui" open-webui DEFAULT_USER_ROLE user
env_is "$owui" open-webui ENABLE_PERSISTENT_CONFIG false
env_is "$owui" open-webui DEFAULT_MODEL_METADATA '{"capabilities":{"builtin_tools":false}}'
env_is "$(UME_RAG_OPENWEBUI_PASSWORD_AUTH=true render compose.openwebui.yaml)" open-webui ENABLE_PASSWORD_AUTH true

# --- init-deployment.sh: the feedback service account ---------------------------------------------------------------
# The 24 GB profile copies the ClamAV image's confs from ../images/clamav, as in the repository.
i="$work/init"; mkdir -p "$i/keys" "$i/images"; cp -r "$d" "$i/deploy"; cp -r "$repo/images/clamav" "$i/images/"
for k in eu owui-key owui-secret; do printf '%s-placeholder\n' "$k" > "$i/keys/$k"; done
init() {
  bash "$i/deploy/init-deployment.sh" --oidc-authority https://keycloak.example.se/realms/ume \
    --gateway-url https://llm.example.se --api-host rag-api.example.se --admin-host rag-admin.example.se \
    --gateway-key-file Eu="$i/keys/eu" --openwebui-host chat.example.se \
    --openwebui-api-key-file "$i/keys/owui-key" --openwebui-oidc-client-secret-file "$i/keys/owui-secret" \
    --backup-target local --host-memory 24 --allow-file-keys --no-chown --yes > "$i/init.log"
}
account() { grep -E '^WEBUI_ADMIN_(EMAIL|NAME|PASSWORD)=' "$i/deploy/secrets/openwebui.env"; }
init; account > "$i/first"
init; account > "$i/second"
if grep -qx "WEBUI_ADMIN_EMAIL='feedback-import@localhost'" "$i/first" &&
   grep -qE "^WEBUI_ADMIN_PASSWORD='[0-9a-f]{64}'$" "$i/first"; then
  ok "init-deployment.sh: the feedback service account is in secrets/openwebui.env"
else fail "init-deployment.sh: no feedback service account in secrets/openwebui.env"; fi
if diff -q "$i/first" "$i/second" >/dev/null; then ok "init-deployment.sh: a re-run keeps the service account"
else fail "init-deployment.sh: a re-run changed the service account"; fi
if grep -qx "UME_RAG_OPENWEBUI_BASE_URL=https://chat.example.se" "$i/deploy/.env"; then
  ok "init-deployment.sh --openwebui-host: rag-api's importer reads https://chat.example.se"
else fail "init-deployment.sh --openwebui-host: no UME_RAG_OPENWEBUI_BASE_URL in deploy/.env"; fi
if grep -q "compose.host-24g.yaml" "$i/deploy/.env"; then ok "init-deployment.sh --host-memory 24: COMPOSE_FILE has compose.host-24g.yaml"
else fail "init-deployment.sh --host-memory 24: compose.host-24g.yaml not in COMPOSE_FILE"; fi
if (
  unset UME_RAG_CERTS_DIR UME_RAG_SECRETS_DIR UME_RAG_OIDC_AUTHORITY UME_RAG_GATEWAY_URL UME_RAG_API_PUBLIC_URL \
    UME_RAG_OPENWEBUI_HOST UME_RAG_OPENWEBUI_OIDC_CLIENT_ID UME_RAG_OPENWEBUI_DATA_DIR UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL
  docker compose -f "$i/deploy/compose.openwebui.yaml" --env-file "$i/deploy/.env" config --quiet
  docker compose --env-file "$i/deploy/.env" config --quiet
); then ok "the generated deploy/.env renders the main stack (24 GB) and Open WebUI"
else fail "the generated deploy/.env does not render"; fi

if [ "$failures" = 0 ]; then echo PASS; else echo "$failures check(s) failed"; exit 1; fi
