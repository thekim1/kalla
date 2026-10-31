#!/usr/bin/env bash
# Serves the R1 page copy (build_site.py or take-copy.sh) with nginx on the runner (repo-and-deployment §8.3).
#
#   benchmarks/release-test/site/serve.sh start DIR [--port N] [--network NAME]
#   benchmarks/release-test/site/serve.sh stop
#
# start: container ume-rag-r1-site on 127.0.0.1:PORT [8088]. --network also attaches it to a Docker network (on the
# test host the deployment's non-internal llm network, so rag-worker reaches it by the name r1-site; the worker's
# egress guard then needs Crawl__AllowedPrivateRanges to include that network's subnet). Exit 0 once it answers.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# nginx 1.30.6 (stable), alpine; multi-arch index digest from Docker Hub.
NGINX_IMAGE="${NGINX_IMAGE:-nginx:1.30.6-alpine@sha256:0e52bd054126cdc040b1276c0af347aeb02a41b9cfe305d6094767ac30e97949}"
name=ume-rag-r1-site

case "${1:-}" in
  start)
    dir="${2:?usage: serve.sh start DIR [--port N] [--network NAME]}"; shift 2
    port=8088; network=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --port) port="$2"; shift 2;;
        --network) network="$2"; shift 2;;
        *) echo "unknown option: $1" >&2; exit 1;;
      esac
    done
    dir="$(cd "$dir" && pwd)"
    docker rm -f "$name" >/dev/null 2>&1 || true
    docker run -d -q --name "$name" -p "127.0.0.1:$port:8080" --read-only --tmpfs /var/cache/nginx --tmpfs /run \
      -v "$dir:/usr/share/nginx/html:ro" -v "$here/nginx.conf:/etc/nginx/conf.d/default.conf:ro" \
      "$NGINX_IMAGE" >/dev/null
    [ -z "$network" ] || docker network connect --alias r1-site "$network" "$name"
    for _ in $(seq 1 30); do
      [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/" || true)" = 200 ] \
        && { echo "Serving $dir at http://127.0.0.1:$port/"; exit 0; }
      sleep 1
    done
    docker logs "$name" >&2; echo "error: nginx did not answer" >&2; exit 1
    ;;
  stop) docker rm -f "$name" >/dev/null 2>&1 || true;;
  *) sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 1;;
esac
