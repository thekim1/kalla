#!/usr/bin/env bash
# Updates a stack started with compose.rolling.yaml one slot at a time, so kalla-api and the admin UI stay up
# (docs/zero-downtime-updates.md, repo-and-deployment §7.3). Run it after setting the new image pins in deploy/.env
# or unpacking a new release bundle over deploy/ (keep deploy/.env; the certificates and secrets are in the volumes).
#
#   ./deploy/rolling-update.sh                  # kalla-bootstrap and the migration, then kalla-api, kalla-api-b, kalla-admin,
#                                               # kalla-admin-b, the rest
#   ./deploy/rolling-update.sh --pull           # pull the images first
#   ./deploy/rolling-update.sh --force          # recreate every slot even if its image did not change (new certificates)
#   ./deploy/rolling-update.sh --with-postgres  # also a new KALLA_POSTGRES_IMAGE: restarts the database (an announced
#                                               # outage) and runs ALTER EXTENSION vector UPDATE before migrating
#
# Only for releases the upgrade guide marks "Rolling update: yes": the old version keeps running against the new
# schema until its slot is replaced. The worker is stopped before its replacement starts (two would exceed its role's
# connection limit; its job leases make the pause harmless); the parser, ClamAV and the renderer are recreated only if
# they run and their image changed (the worker retries what was in flight).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pull=0; force=(); with_postgres=0
settle="${KALLA_ROLLING_SETTLE_SECONDS:-15}"
while [ $# -gt 0 ]; do
  case "$1" in
    --pull) pull=1; shift;;
    --force) force=(--force-recreate); shift;;
    --with-postgres) with_postgres=1; shift;;
    -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

# deploy/.env names the files (COMPOSE_FILE, init-deployment.sh --rolling adds compose.rolling.yaml).
env_file="${KALLA_STATE_DIR:-/etc/kalla}/.env"; [ -e "$env_file" ] || [ ! -e "$here/.env" ] || env_file="$here/.env"
compose=(docker compose --env-file "$env_file")
services="$("${compose[@]}" config --services)"
grep -qx kalla-api-b <<<"$services" || {
  echo "error: no kalla-api-b: COMPOSE_FILE in $env_file does not list compose.rolling.yaml." >&2
  echo "  Re-run ./deploy/init-deployment.sh with --rolling, then: ${compose[*]} up -d --wait" >&2
  exit 1
}
pairs=(kalla-api:kalla-api-b kalla-admin:kalla-admin-b)
singles=()
running="$("${compose[@]}" ps --services --status running)"
# The ones that run: a single left stopped stays so.
for s in kalla-worker kalla-parser kalla-clamav kalla-renderer; do grep -qx "$s" <<<"$running" && singles+=("$s"); done

health() { # health SERVICE: the container's health status, or "missing"
  local id; id="$("${compose[@]}" ps -q "$1" 2>/dev/null || true)"
  [ -n "$id" ] || { echo missing; return; }
  docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id"
}
image_changed() { # image_changed SERVICE: true when the configured image is not the one the container runs
  local id want
  id="$("${compose[@]}" ps -q "$1" 2>/dev/null || true)"
  [ -n "$id" ] || return 0
  want="$("${compose[@]}" config --images "$1")"
  # By image id, so a re-pulled tag counts too; an image not on the host yet is a change.
  want="$(docker image inspect -f '{{.Id}}' "$want" 2>/dev/null)" || return 0
  [ "$want" != "$(docker inspect -f '{{.Image}}' "$id")" ]
}

for pair in "${pairs[@]}"; do
  for svc in ${pair/:/ }; do
    status="$(health "$svc")"
    [ "$status" = healthy ] || {
      echo "error: $svc is $status. Start the stack with compose.rolling.yaml first (docs/zero-downtime-updates.md):" >&2
      echo "  ${compose[*]} up -d --wait" >&2
      exit 1
    }
  done
done

if [ "$pull" = 1 ]; then
  "${compose[@]}" pull kalla-bootstrap kalla-postgres kalla-migrate kalla-api kalla-api-b kalla-admin kalla-admin-b "${singles[@]}"
fi

# The release's bootstrap first: it writes what the new version reads (secrets, certificates, kalla-config) and touches
# nothing that runs.
echo "==> kalla-bootstrap"
"${compose[@]}" run --rm --no-deps kalla-bootstrap

# A new Postgres image restarts the database: never rolling (§7.3), so only on request.
if image_changed kalla-postgres; then
  [ "$with_postgres" = 1 ] || {
    echo "error: KALLA_POSTGRES_IMAGE changed. Replacing kalla-postgres restarts the database, a short outage of every service." >&2
    echo "  Announce it and run again with --with-postgres (upgrade guide, Superuser steps)." >&2
    exit 1
  }
  echo "==> Replacing kalla-postgres (the database restarts)"
  "${compose[@]}" up -d --no-deps --wait --wait-timeout 600 kalla-postgres
  echo "==> Updating the vector extension"
  "${compose[@]}" exec -T kalla-postgres psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c 'ALTER EXTENSION vector UPDATE;'
fi

echo "==> Migrating the database"
"${compose[@]}" run --rm --no-deps kalla-migrate

replace() { # replace SERVICE OTHER: recreate SERVICE while OTHER serves
  local svc="$1" other="$2"
  [ "$(health "$other")" = healthy ] || { echo "error: $other is not healthy; not stopping $svc" >&2; exit 1; }
  echo "==> Replacing $svc ($other is serving)"
  "${compose[@]}" up -d --no-deps --wait --wait-timeout 600 "${force[@]}" "$svc"
  # Give the proxy time to take the new slot back into rotation (nginx retries a failed server after fail_timeout).
  sleep "$settle"
}
for pair in "${pairs[@]}"; do
  a="${pair%%:*}"; b="${pair##*:}"
  replace "$a" "$b"
  replace "$b" "$a"
done

# One instance each. Compose stops the old container before starting the new one, which the worker needs.
for svc in "${singles[@]}"; do
  echo "==> Updating $svc (single instance: recreated only if it changed)"
  "${compose[@]}" up -d --no-deps --wait --wait-timeout 600 "${force[@]}" "$svc"
done

echo "==> Done"
"${compose[@]}" ps
