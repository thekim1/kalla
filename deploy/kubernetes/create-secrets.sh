#!/usr/bin/env bash
# Creates (or updates) the Kubernetes secrets from the certificates and secrets that deploy/init-deployment.sh
# generated, so Kubernetes and Compose installations are set up the same way (docs/kubernetes.md).
#
#   ./deploy/init-deployment.sh --no-chown --yes --oidc-authority https://idp.example.se/realms/ume \
#       --api-host rag.example.se --admin-host rag-admin.example.se --gateway-url https://llm.example.se \
#       --gateway-credential-key-file api=rag-api.key --gateway-credential-key-file admin=rag-admin.key \
#       --gateway-credential-key-file worker=rag-worker.key \
#       --org-sync-url https://llm-data.example.se --org-sync-key-file data.key \
#       --backup-target s3://... --allow-file-keys [--render]
#   ./deploy/kubernetes/create-secrets.sh --namespace ume-rag [--datastores] [--renderer]
#
#   --namespace NS     namespace of the overlay (created if missing)
#   --datastores       also the secrets of components/datastores (in-cluster Postgres and its pgBackRest repository)
#   --renderer         also the renderer's WebSocket path (components/renderer; init-deployment.sh --render writes the
#                      worker's Render__Endpoint); without it the endpoint is left out, so the worker refuses rendering
#                      instead of waiting for a renderer
#   --certs-dir DIR / --secrets-dir DIR   if init-deployment.sh wrote them elsewhere
#
# Secrets: ume-rag-<service>-secrets (files the app reads as settings, e.g. ConnectionStrings__ragdb and the gateway
# credential key Gateway__Key of api, admin and worker) and
# ume-rag-<service>-certs (TLS certificate, internal CA, Data Protection certificate) for api, admin, worker and
# migrations, ume-rag-parser-certs, and ume-rag-blob-key (the blob master key). The connection strings point at the
# host "postgres"; with your own database, edit deploy/secrets/<service>/ConnectionStrings__ragdb first.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/../certs"; secrets="$here/../secrets"; ns=""; datastores=0; renderer=0
while [ $# -gt 0 ]; do
  case "$1" in
    --namespace|-n) ns="$2"; shift 2;;
    --datastores) datastores=1; shift;;
    --renderer) renderer=1; shift;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done
[ -n "$ns" ] || { echo "error: --namespace is required" >&2; exit 1; }
[ -r "$secrets/api/ConnectionStrings__ragdb" ] || {
  echo "error: $secrets/api is missing or unreadable. Run deploy/init-deployment.sh --no-chown first." >&2; exit 1; }
kubectl get namespace "$ns" >/dev/null 2>&1 || kubectl create namespace "$ns"

apply() { kubectl -n "$ns" create secret generic "$@" --dry-run=client -o yaml | kubectl apply -f - >/dev/null; echo "secret/$1"; }
secret() { # secret NAME DIR [SKIP]: every regular file in DIR becomes a key, except SKIP
  local args=() f
  for f in "$2"/*; do
    [ -f "$f" ] || continue
    [ "$(basename "$f")" != "${3:-}" ] || continue
    args+=("--from-file=$f")
  done
  apply "$1" "${args[@]}"
}
for svc in api admin migrations; do
  secret "ume-rag-$svc-secrets" "$secrets/$svc"
  secret "ume-rag-$svc-certs" "$certs/$svc"
done
if [ "$renderer" = 1 ]; then
  [ -r "$secrets/worker/Render__Endpoint" ] || {
    echo "error: $secrets/worker/Render__Endpoint is missing. Run deploy/init-deployment.sh with --render first." >&2; exit 1; }
  secret ume-rag-worker-secrets "$secrets/worker"
  apply ume-rag-renderer-env --from-env-file="$secrets/renderer.env"
else
  secret ume-rag-worker-secrets "$secrets/worker" Render__Endpoint
fi
secret ume-rag-worker-certs "$certs/worker"
secret ume-rag-parser-certs "$certs/parser"
apply ume-rag-blob-key --from-file="$secrets/keys/rag-blob-internal"
if [ "$datastores" = 1 ]; then
  secret ume-rag-postgres-secrets "$secrets/postgres"
  secret ume-rag-postgres-certs "$certs/postgres"
  apply ume-rag-pgbackrest --from-file="$secrets/pgbackrest/repo.conf"
fi
echo "Back up $secrets/.pepper, $secrets/keys/rag-blob-internal and $certs/data-protection.pfx outside the cluster:"
echo "without them a restored database is unusable (runbook, Key escrow)."
