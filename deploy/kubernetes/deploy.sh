#!/usr/bin/env bash
# Deploys or updates an overlay without downtime (docs/kubernetes.md): applies configuration and Postgres, runs the
# rag-migrate Job and waits for it, then rolls out rag-api and rag-admin one pod at a time and the ingestion zone
# (the worker stop-then-start).
#
#   ./deploy/kubernetes/deploy.sh deploy/kubernetes/my-overlay
#
# Only for releases the upgrade guide marks "Rolling update: yes": old pods keep running against the new schema until
# they are replaced. For the others, scale rag-api and rag-admin to 0 first. A new Postgres image with a newer pgvector
# needs ALTER EXTENSION vector UPDATE once it runs, before the migration (upgrade guide, Superuser steps): deploy.sh
# runs it when the postgres StatefulSet's image changed. Needs kubectl 1.27 or later.
set -euo pipefail
overlay="${1:?usage: deploy.sh OVERLAY_DIR}"
apps="rag-api rag-admin rag-worker rag-parser rag-clamav rag-renderer"
manifest="$(mktemp)"; trap 'rm -f "$manifest"' EXIT
kubectl kustomize "$overlay" > "$manifest"
# The overlay's namespace, from the rag-migrate Job (empty: the context's current namespace).
ns="$(awk '/^---/{job=0} /^kind: Job$/{job=1} job && /^  namespace: /{print $2; exit}' "$manifest")"
k=(kubectl ${ns:+-n "$ns"})

echo "==> Configuration, volumes and Postgres"
before="$("${k[@]}" get statefulset postgres -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true)"
kubectl apply -f "$manifest" -l 'app.kubernetes.io/component notin (rag-api,rag-admin,rag-worker,rag-parser,rag-clamav,rag-renderer,rag-migrate)'
if "${k[@]}" get statefulset postgres >/dev/null 2>&1; then
  "${k[@]}" rollout status statefulset/postgres --timeout=10m
  after="$("${k[@]}" get statefulset postgres -o jsonpath='{.spec.template.spec.containers[0].image}')"
  if [ -n "$before" ] && [ "$before" != "$after" ]; then
    echo "==> New Postgres image: updating the vector extension"
    "${k[@]}" exec statefulset/postgres -- psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c 'ALTER EXTENSION vector UPDATE;'
  fi
fi

echo "==> Migrations"
# A Job's pod template is immutable, and it must run again for every release.
"${k[@]}" delete job rag-migrate --ignore-not-found --wait
kubectl apply -f "$manifest" -l app.kubernetes.io/component=rag-migrate
while :; do
  state="$("${k[@]}" get job rag-migrate -o jsonpath='{range .status.conditions[?(@.status=="True")]}{.type} {end}')"
  case " $state " in
    *" Complete "*) break;;
    *" Failed "*) echo "error: rag-migrate failed" >&2; "${k[@]}" logs job/rag-migrate --tail=50 >&2 || true; exit 1;;
  esac
  sleep 3
done

echo "==> Applications"
kubectl apply -f "$manifest"
for app in $apps; do
  if "${k[@]}" get deployment "$app" >/dev/null 2>&1; then "${k[@]}" rollout status "deployment/$app" --timeout=15m; fi
done
echo "==> Done"
