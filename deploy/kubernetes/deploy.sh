#!/usr/bin/env bash
# Deploys or updates an overlay without downtime (docs/kubernetes.md): applies configuration and Postgres, runs the
# kalla-migrate Job and waits for it, then rolls out kalla-api and kalla-admin one pod at a time and the ingestion zone
# (the worker stop-then-start).
#
#   ./deploy/kubernetes/deploy.sh deploy/kubernetes/my-overlay
#
# Only for releases the upgrade guide marks "Rolling update: yes": old pods keep running against the new schema until
# they are replaced. For the others, scale kalla-api and kalla-admin to 0 first. A new Postgres image with a newer pgvector
# needs ALTER EXTENSION vector UPDATE once it runs, before the migration (upgrade guide, Superuser steps): deploy.sh
# runs it when the postgres StatefulSet's image changed. Needs kubectl 1.27 or later.
set -euo pipefail
overlay="${1:?usage: deploy.sh OVERLAY_DIR}"
apps="kalla-api kalla-admin kalla-worker kalla-parser kalla-clamav kalla-renderer"
manifest="$(mktemp)"; trap 'rm -f "$manifest"' EXIT
kubectl kustomize "$overlay" > "$manifest"
# The overlay's namespace, from the kalla-migrate Job (empty: the context's current namespace).
ns="$(awk '/^---/{job=0} /^kind: Job$/{job=1} job && /^  namespace: /{print $2; exit}' "$manifest")"
k=(kubectl ${ns:+-n "$ns"})

echo "==> Configuration, volumes and Postgres"
before="$("${k[@]}" get statefulset postgres -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true)"
kubectl apply -f "$manifest" -l 'app.kubernetes.io/component notin (kalla-api,kalla-admin,kalla-worker,kalla-parser,kalla-clamav,kalla-renderer,kalla-migrate)'
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
"${k[@]}" delete job kalla-migrate --ignore-not-found --wait
kubectl apply -f "$manifest" -l app.kubernetes.io/component=kalla-migrate
while :; do
  state="$("${k[@]}" get job kalla-migrate -o jsonpath='{range .status.conditions[?(@.status=="True")]}{.type} {end}')"
  case " $state " in
    *" Complete "*) break;;
    *" Failed "*) echo "error: kalla-migrate failed" >&2; "${k[@]}" logs job/kalla-migrate --tail=50 >&2 || true; exit 1;;
  esac
  sleep 3
done

echo "==> Applications"
kubectl apply -f "$manifest"
for app in $apps; do
  if "${k[@]}" get deployment "$app" >/dev/null 2>&1; then "${k[@]}" rollout status "deployment/$app" --timeout=15m; fi
done
echo "==> Done"
# Sign-in (identity design §19): while setup is open, kalla-admin prints the setup code at start (both pods the same).
if "${k[@]}" logs deploy/kalla-admin 2>/dev/null | grep 'setup mode is open until' >/dev/null; then
  echo "Setup mode is open: read the setup code with  ${k[*]} logs deploy/kalla-admin | grep 'Setup code'"
  echo "and enter it at https://<admin host>/setup (docs/kubernetes.md, Sign-in)."
fi
