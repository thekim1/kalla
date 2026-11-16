#!/usr/bin/env bash
# Pilot stack checks (plans/archive/p1b/README.md §3 item 17; plans/platform/repo-and-deployment.md §6.2; chat-api/design.md
# §4.3, §14.2), offline with `docker compose config` of deploy/compose.rag.yaml and its overlays, against placeholder
# settings:
#
#   - the base file with compose.hardened.yaml (its CPU weights) keeps every cpus cap at 2 or less (decisions table,
#     "Low CPU during development") and carries the 16 GB limits of §6.2;
#   - with compose.host-24g.yaml and compose.clamav-conf.yaml (as init-deployment.sh --host-memory 24 lists them) every
#     container has the pilot caps and the 24 GB limits of §6.2, the parser runs 2 slots x 2 threads and rag-bootstrap
#     writes the 24 GB Postgres settings (UME_RAG_HOST_MEMORY); deploy/host/install.sh --host-memory 24 writes the
#     slice's MemoryMax=11G;
#   - the openwebui profile: the §6.2 row for rag-openwebui, API keys only for the feedback routes, the admin's chat
#     access and export off, password sign-in always off, OIDC users as role user, its OIDC settings from rag-api through
#     start-with-sign-in.py (identity design §19); rag-openwebui-setup (the feedback key, M-17 C6) as Open WebUI's user,
#     read-only, without capabilities, writing only rag-secrets/openwebui-feedback and its store;
#   - the backup overlay's upload copy as the services' uid, read-only, without a network.
#
#   deploy/tests/pilot-stack.sh
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per check and PASS, or "FAIL: ..." and exits 1.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
d="$repo/deploy"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

# The files and these placeholders are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^(UME_|RAG_|COMPOSE_|OPENWEBUI_IMAGE$)' || true)
export UME_DOMAIN=example.se UME_RAG_GATEWAY_URL=https://llm.example.se UME_RAG_BACKUP_TARGET=local

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
render() { # render FILE... — the merged configuration as JSON, with the clamav, render and openwebui profiles on
  local args=() f
  for f in "$@"; do args+=(-f "$d/compose.$f.yaml"); done
  docker compose "${args[@]}" --profile clamav --profile render --profile openwebui --profile backup config --format json
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

# --- The base file and its CPU weights: development caps, 16 GB limits --------------------------------------------
base="$(render rag hardened)"
expect "$base" '
rag-postgres  2   1024 2g   3g   512
rag-api       2   1024 384m 1g   512
rag-admin     1   512  256m 512m 256
rag-migrate   1   512  -    512m 128
rag-worker    1.5 256  512m 1.5g 512
rag-parser    2   128  2g   3.5g 256
rag-clamav    1   128  1.2g 1.5g 128
rag-renderer  2   128  256m 2g   512
rag-openwebui 1   512  512m 1.5g 256'
over="$(jq -r '.services | to_entries[] | select((.value.cpus // 0) > 2) | .key' <<< "$base")"
if [ -z "$over" ]; then ok "base file: every cpus cap is 2 or less"; else fail "base file: cpus above 2: $over"; fi
env_is "$base" rag-parser PARSER_WORKERS 1
env_is "$base" rag-parser PARSER_THREADS 2
env_is "$base" rag-bootstrap UME_RAG_HOST_MEMORY '<unset>'

# --- 24 GB pilot host: pilot caps and limits ------------------------------------------------------------------------
pilot="$(render rag hardened host-24g clamav-conf)"
expect "$pilot" '
rag-postgres  3   1024 2g   5g   512
rag-api       2   1024 384m 1.5g 512
rag-admin     1   512  256m 768m 256
rag-migrate   1   512  -    512m 128
rag-worker    1.5 256  512m 2g   512
rag-parser    4   128  2g   6g   256
rag-clamav    1   128  1.2g 3g   128
rag-renderer  2   128  256m 2g   512'
env_is "$pilot" rag-parser PARSER_WORKERS 2
env_is "$pilot" rag-parser PARSER_THREADS 2
env_is "$pilot" rag-parser PARSER_SLOT_MAX_RSS_MB 2600
env_is "$pilot" rag-bootstrap UME_RAG_HOST_MEMORY 24
if jq -e '.services["rag-clamav"].volumes | map(select(.target == "/etc/clamav" and .volume.subpath == "clamav")) | length == 1' \
  <<< "$pilot" >/dev/null; then ok "rag-clamav: its confs from rag-config/clamav"; else fail "rag-clamav: no confs from rag-config/clamav"; fi
bash "$d/host/install.sh" --unit-dir "$work/units" --host-memory 24 --no-enable >/dev/null
if grep -qx 'MemoryMax=11G' "$work/units/rag-ingestion.slice.d/50-host-memory.conf"; then
  ok "rag-ingestion.slice: MemoryMax=11G on a 24 GB host"
else fail "rag-ingestion.slice: no MemoryMax=11G drop-in for a 24 GB host"; fi

# --- Open WebUI (the openwebui profile) -----------------------------------------------------------------------------
owui="$(render rag)"
env_is "$owui" rag-openwebui ENABLE_API_KEYS true
env_is "$owui" rag-openwebui ENABLE_API_KEYS_ENDPOINT_RESTRICTIONS true
env_is "$owui" rag-openwebui API_KEYS_ALLOWED_ENDPOINTS /api/v1/evaluations/feedbacks/list,/api/v1/evaluations/feedback
env_is "$owui" rag-openwebui USER_PERMISSIONS_FEATURES_API_KEYS false
env_is "$owui" rag-openwebui ENABLE_ADMIN_CHAT_ACCESS false
env_is "$owui" rag-openwebui ENABLE_ADMIN_EXPORT false
env_is "$owui" rag-openwebui ENABLE_LOGIN_FORM false
env_is "$owui" rag-openwebui ENABLE_PASSWORD_AUTH false
env_is "$owui" rag-openwebui DEFAULT_USER_ROLE user
env_is "$owui" rag-openwebui ENABLE_PERSISTENT_CONFIG false
env_is "$owui" rag-openwebui DEFAULT_MODEL_METADATA '{"capabilities":{"builtin_tools":false}}'
env_is "$(UME_RAG_OPENWEBUI_PASSWORD_AUTH=true render rag)" rag-openwebui ENABLE_PASSWORD_AUTH false
# Its OIDC settings come from rag-api through the start wrapper (identity design §19), inside the stack.
env_is "$owui" rag-openwebui UME_RAG_SIGN_IN_URL https://rag-api:8443/internal/openwebui/sign-in
env_is "$owui" rag-openwebui WEBUI_URL https://chat.example.se
want='["python3","-I","/config/start-with-sign-in.py","--secrets-dir","/run/owui-secrets","--","bash","start.sh"]'
if [ "$(jq -c '.services["rag-openwebui"].entrypoint' <<< "$owui")" = "$want" ]; then
  ok "rag-openwebui: starts through start-with-sign-in.py"
else fail "rag-openwebui: entrypoint is $(jq -c '.services["rag-openwebui"].entrypoint' <<< "$owui")"; fi
setup_ok="$(jq -r '.services["rag-openwebui-setup"] | [.user, (.read_only | tostring), (.cap_drop | join(",")),
  ([.volumes[] | select(.read_only != true) | .target] | sort | join(","))] | join(" ")' <<< "$owui")"
if [ "$setup_ok" = "1000:1000 true ALL /data,/run/feedback" ]; then
  ok "rag-openwebui-setup: uid 1000, read-only, no capabilities, writes only /data and /run/feedback"
else fail "rag-openwebui-setup: expected '1000:1000 true ALL /data,/run/feedback', got '$setup_ok'"; fi

# --- The upload copy (compose.backup.yaml) --------------------------------------------------------------------------
copy="$(render rag backup | jq -r '.services["rag-upload-backup"] | [.user, (.read_only | tostring), .network_mode,
  ([.volumes[] | select(.target == "/data/blobs") | .read_only | tostring] | join(","))] | join(" ")')"
if [ "$copy" = "1654:1654 true none true" ]; then ok "rag-upload-backup: uid 1654, read-only, no network, the blobs read-only"
else fail "rag-upload-backup: expected '1654:1654 true none true', got '$copy'"; fi

if [ "$failures" = 0 ]; then echo PASS; else echo "$failures check(s) failed"; exit 1; fi
