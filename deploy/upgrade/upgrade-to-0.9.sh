#!/usr/bin/env bash
# Upgrades a Docker Compose installation (deploy/compose.rag.yaml, set up with init-deployment.sh) from Ume.Rag 0.8 or
# earlier to Källa 0.9.0, where every name from before the rename to Källa changes (docs/upgrade-guide.md, "0.9.0").
# Adapted from Fors's deploy/upgrade/upgrade-to-0.12.sh. Run it from the new release's directory, on the Docker host,
# with the old installation still running:
#
#   ./deploy/upgrade/upgrade-to-0.9.sh                   # asks before it stops anything
#   ./deploy/upgrade/upgrade-to-0.9.sh --yes             # no questions
#   ./deploy/upgrade/upgrade-to-0.9.sh --old-project ume-rag --env-file deploy/.env
#
# What it does, in this order (each step is skipped when it was done already, so a re-run is safe):
#   1. deploy/.env: renames the variables UME_RAG_* to KALLA_*, RAG_*_IMAGE to KALLA_*_IMAGE and the gateway's UME_* to
#      FORS_* (the old file is kept as .env.pre-0.9), and writes what an existing installation keeps: the sign-in client
#      ume-rag-admin when the file declares the sign-in and names no client (the old default), and the backup
#      repository's path /ume-rag when an s3:// target names none (the old default, so the existing backups stay in use);
#   2. stops the old project's services except Postgres;
#   3. renames the database roles ume_rag_* to kalla_* (deploy/upgrade/0.9-rename-roles.sql, as the Postgres superuser);
#   4. removes the old project's containers and networks (its volumes stay);
#   5. copies the volumes <old project>_rag-{postgres-data,certs,secrets,blobs,openwebui-data,pgbackrest-repo,upload-copy} to
#      kalla_kalla-{...}. The old volumes are kept: remove them once the new version runs.
# Then re-run ./deploy/init-deployment.sh (it writes the connection strings with the new role names) and start the
# release as usual. Back up the database and the two files a restore needs first (docs/runbook.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(cd "$here/.." && pwd)"
old_project="ume-rag"; new_project="kalla"
env_file="$deploy/.env"; yes=0
volumes="postgres-data certs secrets blobs openwebui-data pgbackrest-repo upload-copy"

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --old-project) old_project="$2"; shift 2;;
    --env-file) env_file="$2"; shift 2;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done

command -v docker >/dev/null || die "docker is required"
[ -f "$env_file" ] || die "$env_file not found: copy deploy/.env from the old installation here first"

# --- 1. Variables ------------------------------------------------------------------------------------------------------
# set_default FILE NAME VALUE: adds NAME=VALUE unless the file sets NAME (also an empty NAME=, which means unset on purpose).
set_default() { grep -qE "^$2=" "$1" || printf '%s=%s\n' "$2" "$3" >> "$1"; }

step "1. Variables"
if grep -qE '(^|[^A-Za-z0-9_])(UME_|RAG_[A-Z]+_IMAGE)' "$env_file"; then
  [ -e "$env_file.pre-0.9" ] || cp -p "$env_file" "$env_file.pre-0.9"
  # UME_RAG_ first, so that it never becomes FORS_RAG_.
  sed -i -E -e 's/(^|[^A-Za-z0-9_])UME_RAG_/\1KALLA_/g' -e 's/(^|[^A-Za-z0-9_])RAG_([A-Z]+)_IMAGE/\1KALLA_\2_IMAGE/g' \
    -e 's/(^|[^A-Za-z0-9_])UME_/\1FORS_/g' "$env_file"
  echo "renamed the variables in $env_file (the old file is $env_file.pre-0.9)"
fi
# The old defaults, kept as deployment values. Remove a line to take the new default (kalla-admin) once the identity
# provider has that client.
if grep -qE '^KALLA_OIDC_AUTHORITY=.+' "$env_file"; then
  set_default "$env_file" KALLA_OIDC_CLIENT_ID ume-rag-admin
fi
# The value as init-deployment.sh reads it: its quotes removed (the script writes every value in single quotes), and
# no slash at the end (s3://ENDPOINT/BUCKET/ names no path either).
target="$(sed -n -E 's/^KALLA_BACKUP_TARGET=//p' "$env_file" | tail -n1)"
case "$target" in
  \'*\') target="${target#\'}"; target="${target%\'}";;
  \"*\") target="${target#\"}"; target="${target%\"}";;
esac
while [ "${target%/}" != "$target" ]; do target="${target%/}"; done
case "$target" in
  s3://*/*/*) ;;
  s3://*/*)
    tmp="$(mktemp "$env_file.XXXXXX")"
    grep -vE '^KALLA_BACKUP_TARGET=' "$env_file" > "$tmp" || true
    printf "KALLA_BACKUP_TARGET='%s'\n" "$target/ume-rag" >> "$tmp"
    cat "$tmp" > "$env_file"; rm -f "$tmp"
    echo "kept the backup repository's path: KALLA_BACKUP_TARGET=$target/ume-rag" ;;
esac

containers() { docker ps -q --filter "label=com.docker.compose.project=$1" "${@:2}"; }
postgres="$(containers "$old_project" --filter label=com.docker.compose.service=rag-postgres)"
if [ -z "$postgres" ]; then
  docker volume inspect "${new_project}_kalla-postgres-data" >/dev/null 2>&1 \
    || die "Postgres of project $old_project is not running: start the old installation (or pass --old-project)"
  echo; echo "Project $old_project is not running and ${new_project}_kalla-postgres-data exists: steps 2 to 5 were done already."
  echo "Next: ./deploy/init-deployment.sh, then start the release (docs/upgrade-guide.md)."
  exit 0
fi

if [ "$yes" != 1 ]; then
  echo
  read -r -p "Stop the services of project $old_project now (Postgres keeps running until its roles are renamed)? [y/N] " answer
  case "$answer" in y|Y|yes) ;; *) die "stopped; nothing else was changed";; esac
fi

# --- 2. Stop the services ----------------------------------------------------------------------------------------------
step "2. Stopping the services of $old_project (Postgres keeps running)"
others="$(containers "$old_project" | grep -v "^${postgres}$" || true)"
# shellcheck disable=SC2086
[ -z "$others" ] || docker stop $others >/dev/null

# --- 3. Database roles -------------------------------------------------------------------------------------------------
step "3. Renaming the database roles"
docker exec -i "$postgres" sh -c 'PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q' \
  < "$here/0.9-rename-roles.sql"

# --- 4. Old project ----------------------------------------------------------------------------------------------------
step "4. Removing the containers of $old_project (volumes stay)"
image="$(docker inspect -f '{{.Config.Image}}' "$postgres")"
docker compose -p "$old_project" down

# --- 5. Volumes --------------------------------------------------------------------------------------------------------
step "5. Copying the volumes of $old_project to $new_project"
for v in $volumes; do
  from="${old_project}_rag-$v"; to="${new_project}_kalla-$v"
  if ! docker volume inspect "$from" >/dev/null 2>&1; then
    echo "$from does not exist: skipped"; continue
  fi
  # A copy in progress has the file .copy-unfinished: a copy that stopped part-way is done again, not taken as done.
  if docker volume inspect "$to" >/dev/null 2>&1; then
    state="$(docker run --rm -v "$to:/to:ro" --entrypoint sh "$image" -c \
      'if [ -e /to/.copy-unfinished ]; then echo unfinished; elif [ -n "$(ls -A /to)" ]; then echo filled; fi')"
    case "$state" in
      filled) echo "$to exists and is not empty: left as it is"; continue;;
      unfinished) echo "$to holds a copy that did not finish: copying again";;
    esac
  fi
  docker volume create "$to" >/dev/null
  docker run --rm --user 0 -v "$from:/from:ro" -v "$to:/to" --entrypoint sh "$image" -c \
    'touch /to/.copy-unfinished && cp -a /from/. /to/ && rm -f /to/.copy-unfinished'
  echo "copied $from to $to"
done

cat <<EOF

Done. Next (docs/upgrade-guide.md, "0.9.0"):
  1. ./deploy/init-deployment.sh          writes the connection strings with the new role names; keeps your settings
  2. start the release as usual (docker compose -f deploy/compose.rag.yaml --env-file deploy/.env up -d --wait)
  3. once everything runs: docker volume rm $(for v in $volumes; do printf '%s ' "${old_project}_rag-$v"; done)
EOF
