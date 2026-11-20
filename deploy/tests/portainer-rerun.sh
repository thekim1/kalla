#!/usr/bin/env bash
# Re-run test of the Portainer stack's kalla-bootstrap (verify.yml deploy-checks, #697): runs kalla-bootstrap alone, twice,
# on the same throwaway volumes, the way a stack update re-runs it, and checks:
#
#   - every secret that existing data depends on is unchanged by the second run: the internal CA, the Data Protection
#     certificate, the database passwords, the key pepper, the api's keys, kalla-admin's setup secret, the worker's crawl
#     key pepper, the renderer's WebSocket path, Open WebUI's secrets and key for kalla-api, and the blob master key
#   - every file and directory in kalla-certs and kalla-secrets has the same owner and mode after both runs, and the ones
#     a service depends on have the expected ones (openwebui-feedback 2750 1000:1654, the blob key 400 1654:1654, ...)
#   - the gateway credential keys: a KALLA_*_GATEWAY_KEY variable wins over the manifest's file in
#     integration-secrets, the file is used without one, and with neither the service has no key
#   - the host names under FORS_DOMAIN: the example's domain is on the checklist, the names derived from it are not
#   - the TLS proxy (profile proxy): a URL where a host name belongs leaves that block out of nginx.conf and names it on
#     the checklist, instead of stopping kalla-bootstrap (#711); the blocks of the gateway, its admin, kalla-api, kalla-admin
#     and Open WebUI carry the names derived from FORS_DOMAIN
#   - retired variables (Decisions "Deployment values across releases"): one the stack still sets is on the checklist
#     with what replaced it and in admin/Deployment__RetiredVariables for kalla-admin; an empty one and none are not
#
# No image but kalla-bootstrap's (alpine/openssl) is pulled and nothing else starts: the gateway's fors-certs volume is
# faked with a CA certificate only, and integration-secrets with one key file.
#
#   deploy/tests/portainer-rerun.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 (Engine 26 or later).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
env_example="$here/../portainer/stack.env.example"
project="kalla-rerun-$$"
work="$(mktemp -d)"

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
cleanup() {
  docker compose -p "$project" -f "$stack" --env-file "$work/stack.env" down -v --remove-orphans >/dev/null 2>&1 || true
  docker volume rm -f "${project}_ume-certs" "${project}_integration-secrets" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# The example's variables with every profile that changes what kalla-bootstrap writes, and one key set by hand.
{ grep -v '^COMPOSE_PROFILES=' "$env_example"
  echo "COMPOSE_PROFILES=clamav,openwebui,render,proxy"
  echo "KALLA_WORKER_GATEWAY_KEY=worker-key-from-variable"; } > "$work/stack.env"
dc() { docker compose -p "$project" -f "$stack" --env-file "$work/stack.env" "$@"; }

# The list first, then the search: grep -m1 on the pipe closes it early, and with pipefail a `config` that is still
# writing then fails the line (seen once on the CI runner).
images="$(dc config --images)" || fail "docker compose config --images failed for $stack"
helper="$(grep -m1 '/alpine/openssl@' <<< "$images")" || fail "no alpine/openssl image in $stack"
in_helper() { docker run --rm --network none --entrypoint /bin/sh "$@"; }

# The gateway's volumes as its bootstrap and migrations leave them: a CA certificate in fors-certs, and the manifest's
# key for kalla-api in integration-secrets (uid 1654, mode 600, as the gateway writes it). kalla-admin has neither a file
# nor a variable.
docker volume create "${project}_ume-certs" >/dev/null
docker volume create "${project}_integration-secrets" >/dev/null
in_helper -v "${project}_ume-certs:/c" -v "${project}_integration-secrets:/i" "$helper" -c '
  openssl req -x509 -newkey rsa:2048 -nodes -keyout /tmp/k -out /c/ca.pem -days 1 -subj /CN=fake-gateway-ca 2>/dev/null
  mkdir -p /i/Kalla && printf api-key-from-manifest > /i/Kalla/rag-api
  chown -R 1654:1654 /i && chmod 700 /i /i/Kalla && chmod 600 /i/Kalla/rag-api'

# The secrets existing data depends on, and every path's owner and mode.
snapshot() {
  in_helper -v "${project}_kalla-certs:/certs:ro" -v "${project}_kalla-secrets:/secrets:ro" "$helper" -c '
    cd / && sha256sum certs/ca.key certs/ca.pem certs/data-protection.pfx secrets/.pepper secrets/postgres/*_password \
      secrets/api/Security__DownloadGrantKeys__k1 secrets/api/Security__CacheKeyPepper \
      secrets/api/Security__ConversationIdKeys__k1 secrets/admin/Setup__Secret \
      secrets/worker/Security__CrawlKeyPepper secrets/.render-ws-path \
      secrets/.openwebui-jwt-secret secrets/openwebui/webui_secret_key secrets/openwebui/admin-password \
      secrets/.openwebui-rag-api-key secrets/keys/rag-blob-internal
    find certs secrets -exec stat -c "%a %u:%g %n" {} + | sort -k3'
}

run_bootstrap() {
  dc run --rm --no-deps kalla-bootstrap > "$work/bootstrap-$1.log" 2>&1 \
    || { cat "$work/bootstrap-$1.log"; fail "kalla-bootstrap run $1 failed"; }
  grep -q '^kalla-bootstrap: certificates and secrets ready$' "$work/bootstrap-$1.log" \
    || { cat "$work/bootstrap-$1.log"; fail "kalla-bootstrap run $1 did not finish its secrets"; }
}

echo "== kalla-bootstrap, first run"
run_bootstrap 1
snapshot > "$work/first"
echo "== kalla-bootstrap, second run on the same volumes"
run_bootstrap 2
snapshot > "$work/second"
grep -q 'issuing certificate' "$work/bootstrap-2.log" && fail "the second run issued certificates again"
diff -u "$work/first" "$work/second" || fail "the second run changed a secret, an owner or a mode"
ok "the second run kept every secret, owner and mode ($(grep -c '^[0-9a-f]\{64\} ' "$work/second") secrets, $(grep -vc '^[0-9a-f]\{64\} ' "$work/second") paths)"

# Owners and modes the services depend on (uid 1654: api, admin, worker, migrations; 999: Postgres; 10001: parser;
# 10002: renderer; 1000: Open WebUI).
expect() {
  grep -qx "$1 $2" "$work/second" || fail "expected '$1 $2', found '$(grep " $2\$" "$work/second" || echo missing)'"
}
expect "755 0:0" certs
expect "755 0:0" secrets
expect "400 1654:1654" secrets/keys/rag-blob-internal
expect "2750 1000:1654" secrets/openwebui-feedback
expect "700 1000:1000" secrets/openwebui
expect "600 999:999" secrets/postgres/api_password
expect "600 10001:10001" certs/parser/parser.key
expect "600 10002:10002" secrets/renderer/ws-path
for d in api admin worker migrations; do expect "700 1654:1654" "secrets/$d"; done
expect "600 0:0" certs/ca.key
ok "owners and modes as the services expect"

echo "== the gateway credential keys"
key() { in_helper -v "${project}_kalla-secrets:/s:ro" "$helper" -c "cat /s/$1/Gateway__Key 2>/dev/null || echo '<none>'"; }
[ "$(key api)" = api-key-from-manifest ] || fail "kalla-api's key is '$(key api)', expected the manifest's file"
[ "$(key worker)" = worker-key-from-variable ] || fail "kalla-worker's key is '$(key worker)', expected the variable"
[ "$(key admin)" = '<none>' ] || fail "kalla-admin has a key without a file or a variable"
grep -q 'KALLA_ADMIN_GATEWAY_KEY: no key for kalla-admin' "$work/bootstrap-2.log" \
  || fail "the checklist does not name kalla-admin's missing key"
grep -qE 'KALLA_(API|WORKER)_GATEWAY_KEY' "$work/bootstrap-2.log" && fail "the checklist names a key that is set"
ok "a variable wins over the manifest's file, the file is used without one, and a missing key is on the checklist"

echo "== the host names under FORS_DOMAIN"
grep -q 'FORS_DOMAIN is still an example value' "$work/bootstrap-2.log" || fail "the checklist does not name the example FORS_DOMAIN"
grep -qE 'KALLA_(API|OPENWEBUI)_HOST' "$work/bootstrap-2.log" && fail "the checklist names a host variable the example does not set"
ok "the example's FORS_DOMAIN is on the checklist once, not as the host names derived from it"
# The gateway's two names come from FORS_DOMAIN too (the example sets neither FORS_GATEWAY_HOST nor FORS_ADMIN_HOST).
conf="$(in_helper -v "${project}_kalla-config:/f:ro" "$helper" -c 'cat /f/proxy/nginx.conf')"
grep -q 'server_name llm\.example\.se;' <<< "$conf" || fail "the proxy has no gateway block for llm.example.se under FORS_DOMAIN"
grep -q 'server_name llm-admin\.example\.se;' <<< "$conf" || fail "the proxy has no gateway admin block for llm-admin.example.se under FORS_DOMAIN"
ok "the proxy's gateway and gateway admin blocks carry llm. and llm-admin. under FORS_DOMAIN"

echo "== the proxy with a URL as a host name"
# A deployer's own FORS_GATEWAY_HOST (the example has it only as a comment), given a URL instead of a host name.
sed -i '/^#*FORS_GATEWAY_HOST=/d' "$work/stack.env"
echo "FORS_GATEWAY_HOST=https://llm.example.se" >> "$work/stack.env"
run_bootstrap 3
grep -q 'FORS_GATEWAY_HOST is not a host name, so the proxy has no gateway block' "$work/bootstrap-3.log" \
  || fail "the checklist does not name the bad FORS_GATEWAY_HOST"
conf="$(in_helper -v "${project}_kalla-config:/f:ro" "$helper" -c 'cat /f/proxy/nginx.conf')"
grep -q 'server_name rag.example.se;' <<< "$conf" || fail "the proxy lost kalla-api's block"
# The example's FORS_DOMAIN gives kalla-admin and Open WebUI their names (no KALLA_*_HOST set).
grep -q 'server_name rag-admin.example.se;' <<< "$conf" || fail "the proxy has no kalla-admin block under FORS_DOMAIN"
grep -q 'server_name chat.example.se;' <<< "$conf" || fail "the proxy has no Open WebUI block under FORS_DOMAIN"
grep -q 'llm.example.se\|@GATEWAY_HOST@' <<< "$conf" && fail "nginx.conf still has a gateway block"
grep -q 'server_name llm-admin\.example\.se;' <<< "$conf" || fail "the proxy lost the gateway admin's block with a bad FORS_GATEWAY_HOST"
ok "a URL as FORS_GATEWAY_HOST leaves the gateway block out and is on the checklist; kalla-bootstrap completes"

echo "== retired variables"
retired_file() { in_helper -v "${project}_kalla-secrets:/s:ro" "$helper" -c 'cat /s/admin/Deployment__RetiredVariables 2>/dev/null || echo "<none>"'; }
# The example sets none: nothing on the checklist, no file for kalla-admin.
grep -q 'no longer read' "$work/bootstrap-3.log" && fail "the checklist names a retired variable the example does not set"
[ "$(retired_file)" = '<none>' ] || fail "kalla-admin has a retired-variables file without one set: $(retired_file)"
# A stack upgraded from 0.5.x that kept two of them (an empty one does not count).
{ echo "KALLA_CLAMAV_MODE=on"; echo "KALLA_EMBEDDING_MODEL=text-embedding-3-large"; echo "FORS_ORGANISATION_NAME="; } >> "$work/stack.env"
run_bootstrap 4
grep -q 'KALLA_CLAMAV_MODE is set and no longer read (the malware scan follows the clamav profile)' "$work/bootstrap-4.log" \
  || fail "the checklist does not name the retired KALLA_CLAMAV_MODE"
grep -q 'KALLA_EMBEDDING_MODEL is set and no longer read' "$work/bootstrap-4.log" || fail "the checklist does not name the retired KALLA_EMBEDDING_MODEL"
grep -q 'FORS_ORGANISATION_NAME' "$work/bootstrap-4.log" && fail "the checklist names a retired variable that is empty"
[ "$(retired_file)" = 'KALLA_CLAMAV_MODE,KALLA_EMBEDDING_MODEL' ] || fail "kalla-admin's retired variables are '$(retired_file)'"
[ "$(in_helper -v "${project}_kalla-secrets:/s:ro" "$helper" -c 'stat -c "%a %u:%g" /s/admin/Deployment__RetiredVariables')" = "600 1654:1654" ] \
  || fail "kalla-admin cannot read its retired-variables file"
# Removed from the stack again: the file goes with them.
sed -i '/^KALLA_CLAMAV_MODE=/d; /^KALLA_EMBEDDING_MODEL=/d' "$work/stack.env"
run_bootstrap 5
[ "$(retired_file)" = '<none>' ] || fail "the retired-variables file stays after the variables are removed"
ok "a retired variable the stack still sets is on the checklist and in kalla-admin's file; without one there is neither"
