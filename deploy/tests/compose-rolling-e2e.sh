#!/usr/bin/env bash
# End-to-end test of updates without downtime on Docker Compose (docs/zero-downtime-updates.md), adapted from the
# gateway's: boots compose.prod.yaml + compose.rolling.yaml from a copy of deploy/, puts nginx in front with the
# configuration init-deployment.sh --proxy --rolling writes, and replaces every slot with rolling-update.sh --force while
# clients send POST and GET requests to rag-api and load the admin UI. Fails if a single request is lost.
#
#   deploy/tests/compose-rolling-e2e.sh      # the local images (ume-rag/*:latest), or set API_IMAGE, ADMIN_IMAGE,
#                                            # MIGRATIONS_IMAGE and POSTGRES_IMAGE (a release's, pinned by digest)
#
# The ingestion zone is left out (COMPOSE_FILE without compose.ingestion.yaml): rolling-update.sh then has no worker,
# parser, ClamAV or renderer to update, and the test needs no gateway, ClamAV signatures or cgroup slice.
# Needs Docker (Linux host), python3, openssl and sudo (init-deployment.sh gives the files to the containers' users).
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project=ume-rag-rolling-e2e; edge=ume-rag-rolling-e2e-edge; port=18543
# The proxy reaches the slots at the host's own address, as a proxy on another machine would (not through loopback).
upstream="$(hostname -I | awk '{print $1}')"
cp -R "$repo/deploy" "$work/"
cd "$work"
compose=(docker compose -p "$project" --env-file deploy/.env)
cleanup() {
  docker rm -f "$edge" >/dev/null 2>&1 || true
  "${compose[@]}" down -v >/dev/null 2>&1 || true
  cd / && sudo rm -r -f "$work"
}
trap cleanup EXIT

sudo ./deploy/init-deployment.sh --proxy --rolling --yes --oidc-authority https://login.microsoftonline.com/common/v2.0 \
  --gateway-url https://llm.test --api-host rag.test --admin-host rag-admin.test --upstream-host "$upstream" \
  --proxy-address 127.0.0.1 --backup-target local --allow-file-keys >/dev/null
sudo chown "$(id -u):$(id -g)" deploy/.env deploy/proxy/nginx.conf
sed -i -E 's#^COMPOSE_FILE=.*#COMPOSE_FILE='"$work"'/deploy/compose.prod.yaml:'"$work"'/deploy/compose.rolling.yaml#' deploy/.env
for v in API_IMAGE ADMIN_IMAGE MIGRATIONS_IMAGE POSTGRES_IMAGE; do
  [ -z "${!v:-}" ] || echo "$v=${!v}" >> deploy/.env
done
mkdir -p edge
for h in rag.test rag-admin.test; do
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "edge/$h.key" -out "edge/$h.crt" -days 1 -subj "/CN=$h" \
    -addext "subjectAltName=DNS:$h" >/dev/null 2>&1
done
sed -e 's/# ssl_certificate/ssl_certificate/' -e "s/listen 443 ssl;/listen $port ssl;/" deploy/proxy/nginx.conf > edge/nginx.conf
grep -q "server $upstream:8544;" edge/nginx.conf || { echo "error: nginx.conf does not list the second rag-api slot" >&2; exit 1; }
grep -q "server $upstream:9544;" edge/nginx.conf || { echo "error: nginx.conf does not list the second admin slot" >&2; exit 1; }

# The slots listen on 0.0.0.0 (--proxy); this test host has no rag-firewall, which would admit only the proxy.
"${compose[@]}" up -d --wait --wait-timeout 600
docker run -d --name "$edge" --network host -v "$work/edge/nginx.conf:/etc/nginx/conf.d/default.conf:ro" \
  -v "$work/edge:/etc/ssl:ro" docker.io/library/nginx:1.29-alpine >/dev/null
for _ in $(seq 30); do
  [ "$(curl -sk --resolve "rag.test:$port:127.0.0.1" -o /dev/null -w '%{http_code}' "https://rag.test:$port/v1/models")" = 401 ] && break
  sleep 1
done

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
