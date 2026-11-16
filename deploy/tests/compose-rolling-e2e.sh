#!/usr/bin/env bash
# End-to-end test of updates without downtime on Docker Compose (docs/zero-downtime-updates.md), adapted from the
# gateway's: boots compose.rag.yaml + compose.rolling.yaml from a copy of deploy/, puts nginx in front with
# deploy/proxy/nginx.conf.example (both slots of each service), and replaces every slot with rolling-update.sh --force
# while clients send POST and GET requests to rag-api and load the admin UI. Fails if a single request is lost.
#
#   deploy/tests/compose-rolling-e2e.sh      # compose.rag.yaml's pinned images, or set RAG_API_IMAGE, RAG_ADMIN_IMAGE,
#                                            # RAG_MIGRATIONS_IMAGE and RAG_POSTGRES_IMAGE (local builds, for example)
#
# Only rag-api, rag-admin, their second slots and what they need start (rag-bootstrap, rag-postgres, rag-migrate):
# rolling-update.sh then has no worker, parser, ClamAV or renderer running to update, and the test needs no gateway,
# ClamAV signatures or cgroup slice. Needs Docker (Linux host), python3 and openssl; no sudo.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project=ume-rag-rolling-e2e; edge=ume-rag-rolling-e2e-edge; port=18543
# The proxy is an nginx container on the stack's rag-edge network with the example's configuration, its upstreams
# pointed at the slots' containers (re-resolved, as each slot gets a new address when it is replaced). Not through the
# published ports: Docker Desktop's port forwarder accepts a connection to a port whose container is restarting and
# then resets it, which a Linux host's DNAT never does, so the test would measure the forwarder instead of the update.
upstream=docker-host
cp -R "$repo/deploy" "$work/"; rm -f "$work/deploy/.env"
cd "$work"
compose=(docker compose -p "$project" --env-file deploy/.env)
cleanup() {
  docker rm -f "$edge" >/dev/null 2>&1 || true
  "${compose[@]}" down -v >/dev/null 2>&1 || true
  cd / && rm -r -f "$work"
}
trap cleanup EXIT

images=()
for v in RAG_API_IMAGE RAG_ADMIN_IMAGE RAG_MIGRATIONS_IMAGE RAG_POSTGRES_IMAGE; do
  [ -z "${!v:-}" ] || images+=(--set "$v=${!v}")
done
./deploy/init-deployment.sh --rolling --profiles '' --domain test --api-host rag.test --admin-host rag-admin.test \
  --gateway-url https://llm.test --oidc-authority https://login.microsoftonline.com/common/v2.0 \
  ${images[@]+"${images[@]}"} --yes >/dev/null
mkdir -p edge
for h in rag.test rag-admin.test; do
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "edge/$h.key" -out "edge/$h.crt" -days 1 -subj "/CN=$h" \
    -addext "subjectAltName=DNS:$h" >/dev/null 2>&1
done
sed -e "s/@API_HOST@/rag.test/g" -e "s/@ADMIN_HOST@/rag-admin.test/g" -e "s/@UPSTREAM@/$upstream/g" \
  -e "s/@API_PORT@/8543/g" -e "s/@ADMIN_PORT@/9543/g" -e "s/@API_B_PORT@/8544/g" -e "s/@ADMIN_B_PORT@/9544/g" \
  -e 's/#rolling //' -e '/^#owui/d' deploy/proxy/nginx.conf.example > edge/example.conf
grep -q "server $upstream:8544;" edge/example.conf || { echo "error: the example does not list the second rag-api slot" >&2; exit 1; }
grep -q "server $upstream:9544;" edge/example.conf || { echo "error: the example does not list the second admin slot" >&2; exit 1; }
{ echo "resolver 127.0.0.11 valid=1s ipv6=off;"
  sed -e 's/# ssl_certificate/ssl_certificate/' -e "s/listen 443 ssl;/listen $port ssl;/" \
    -e "s/server $upstream:8543;/zone ume_rag_api 64k; server rag-api:8443 resolve;/" \
    -e "s/server $upstream:8544;/server rag-api-b:8443 resolve;/" \
    -e "s/server $upstream:9543;/zone ume_rag_admin 64k; server rag-admin:8443 resolve;/" \
    -e "s/server $upstream:9544;/server rag-admin-b:8443 resolve;/" edge/example.conf; } > edge/nginx.conf

"${compose[@]}" up -d --wait --wait-timeout 600 rag-api rag-api-b rag-admin rag-admin-b
docker run -d --name "$edge" -p "127.0.0.1:$port:$port" --network "${project}_rag-edge" \
  -v "$work/edge/nginx.conf:/etc/nginx/conf.d/default.conf:ro" -v "$work/edge:/etc/ssl:ro" \
  docker.io/library/nginx:1.29-alpine >/dev/null
ready=0
for _ in $(seq 60); do
  [ "$(curl -sk --resolve "rag.test:$port:127.0.0.1" -o /dev/null -w '%{http_code}' "https://rag.test:$port/v1/models")" = 401 ] && { ready=1; break; }
  sleep 1
done
[ "$ready" = 1 ] || { echo "error: rag-api does not answer through nginx" >&2; docker logs "$edge" 2>&1 | tail -20 >&2; exit 1; }

load="$repo/deploy/tests/rolling-load.py"; stop="$work/stop"; pids=()
# rag-api refuses the missing key (401), or throttles an address that sent too many refused credentials (429).
python3 "$load" rag.test "$port" /v1/chat/completions 401,429 16 "$stop" POST 127.0.0.1 > "$work/post.txt" 2>&1 & pids+=($!)
python3 "$load" rag.test "$port" /v1/models 401,429 8 "$stop" GET 127.0.0.1 > "$work/get.txt" 2>&1 & pids+=($!)
python3 "$load" rag-admin.test "$port" / 200 4 "$stop" GET 127.0.0.1 > "$work/admin.txt" 2>&1 & pids+=($!)
sleep 5
COMPOSE_PROJECT_NAME="$project" ./deploy/rolling-update.sh --force
sleep 5
touch "$stop"
failed=0
for pid in "${pids[@]}"; do wait "$pid" || failed=1; done
cat "$work/post.txt" "$work/get.txt" "$work/admin.txt"
if [ "$failed" = 1 ]; then
  echo "error: requests were lost during the rolling update" >&2
  docker logs "$edge" 2>&1 | grep -E '\[(error|warn)\]' | tail -20 >&2 || true
  exit 1
fi
echo "No request lost while every slot was replaced."
