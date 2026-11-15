#!/usr/bin/env bash
# Re-run test of the Portainer stack's rag-bootstrap (verify.yml deploy-checks, #697): runs rag-bootstrap alone, twice,
# on the same throwaway volumes, the way a stack update re-runs it, and checks:
#
#   - every secret that existing data depends on is unchanged by the second run: the internal CA, the Data Protection
#     certificate, the database passwords, the key pepper, the api's keys, the worker's crawl key pepper, the
#     renderer's WebSocket path, Open WebUI's secrets and key for rag-api, and the blob master key
#   - every file and directory in rag-certs and rag-secrets has the same owner and mode after both runs, and the ones
#     a service depends on have the expected ones (openwebui-feedback 2750 1000:1654, the blob key 400 1654:1654, ...)
#   - the gateway credential keys: a UME_RAG_*_GATEWAY_KEY variable wins over the manifest's file in
#     integration-secrets, the file is used without one, and with neither the service has no key
#
# No image but rag-bootstrap's (alpine/openssl) is pulled and nothing else starts: the gateway's ume-certs volume is
# faked with a CA certificate only, and integration-secrets with one key file.
#
#   deploy/tests/portainer-rerun.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 (Engine 26 or later).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
env_example="$here/../portainer/stack.env.example"
project="ume-rag-rerun-$$"
work="$(mktemp -d)"

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
cleanup() {
  docker compose -p "$project" -f "$stack" --env-file "$work/stack.env" down -v --remove-orphans >/dev/null 2>&1 || true
  docker volume rm -f "${project}_ume-certs" "${project}_integration-secrets" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# The example's variables with every profile that changes what rag-bootstrap writes, and one key set by hand.
{ grep -v '^COMPOSE_PROFILES=' "$env_example"
  echo "COMPOSE_PROFILES=clamav,openwebui,render,proxy"
  echo "UME_RAG_WORKER_GATEWAY_KEY=worker-key-from-variable"; } > "$work/stack.env"
dc() { docker compose -p "$project" -f "$stack" --env-file "$work/stack.env" "$@"; }

helper="$(dc config --images | grep -m1 '/alpine/openssl@')" || fail "no alpine/openssl image in $stack"
in_helper() { docker run --rm --network none --entrypoint /bin/sh "$@"; }

# The gateway's volumes as its bootstrap and migrations leave them: a CA certificate in ume-certs, and the manifest's
# key for rag-api in integration-secrets (uid 1654, mode 600, as the gateway writes it). rag-admin has neither a file
# nor a variable.
docker volume create "${project}_ume-certs" >/dev/null
docker volume create "${project}_integration-secrets" >/dev/null
in_helper -v "${project}_ume-certs:/c" -v "${project}_integration-secrets:/i" "$helper" -c '
  openssl req -x509 -newkey rsa:2048 -nodes -keyout /tmp/k -out /c/ca.pem -days 1 -subj /CN=fake-gateway-ca 2>/dev/null
  mkdir -p /i/Ume.Rag && printf api-key-from-manifest > /i/Ume.Rag/rag-api
  chown -R 1654:1654 /i && chmod 700 /i /i/Ume.Rag && chmod 600 /i/Ume.Rag/rag-api'

# The secrets existing data depends on, and every path's owner and mode.
snapshot() {
  in_helper -v "${project}_rag-certs:/certs:ro" -v "${project}_rag-secrets:/secrets:ro" "$helper" -c '
    cd / && sha256sum certs/ca.key certs/ca.pem certs/data-protection.pfx secrets/.pepper secrets/postgres/*_password \
      secrets/api/Security__DownloadGrantKeys__k1 secrets/api/Security__CacheKeyPepper \
      secrets/api/Security__ConversationIdKeys__k1 secrets/worker/Security__CrawlKeyPepper secrets/.render-ws-path \
      secrets/.openwebui-jwt-secret secrets/openwebui/webui_secret_key secrets/openwebui/admin-password \
      secrets/.openwebui-rag-api-key secrets/keys/rag-blob-internal
    find certs secrets -exec stat -c "%a %u:%g %n" {} + | sort -k3'
}

run_bootstrap() {
  dc run --rm --no-deps rag-bootstrap > "$work/bootstrap-$1.log" 2>&1 \
    || { cat "$work/bootstrap-$1.log"; fail "rag-bootstrap run $1 failed"; }
  grep -q '^rag-bootstrap: certificates and secrets ready$' "$work/bootstrap-$1.log" \
    || { cat "$work/bootstrap-$1.log"; fail "rag-bootstrap run $1 did not finish its secrets"; }
}

echo "== rag-bootstrap, first run"
run_bootstrap 1
snapshot > "$work/first"
echo "== rag-bootstrap, second run on the same volumes"
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
key() { in_helper -v "${project}_rag-secrets:/s:ro" "$helper" -c "cat /s/$1/Gateway__Key 2>/dev/null || echo '<none>'"; }
[ "$(key api)" = api-key-from-manifest ] || fail "rag-api's key is '$(key api)', expected the manifest's file"
[ "$(key worker)" = worker-key-from-variable ] || fail "rag-worker's key is '$(key worker)', expected the variable"
[ "$(key admin)" = '<none>' ] || fail "rag-admin has a key without a file or a variable"
grep -q 'UME_RAG_ADMIN_GATEWAY_KEY: no key for rag-admin' "$work/bootstrap-2.log" \
  || fail "the checklist does not name rag-admin's missing key"
grep -qE 'UME_RAG_(API|WORKER)_GATEWAY_KEY' "$work/bootstrap-2.log" && fail "the checklist names a key that is set"
ok "a variable wins over the manifest's file, the file is used without one, and a missing key is on the checklist"
