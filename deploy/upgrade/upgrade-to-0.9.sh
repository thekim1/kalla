#!/usr/bin/env bash
# Upgrades a Docker Compose installation (deploy/compose.rag.yaml, set up with init-deployment.sh) from Ume.Rag 0.8 or
# earlier to Källa 0.9.0 or later, where every name from before the rename to Källa changes (docs/upgrade-guide.md,
# "0.9.0"). Run it from the new release's directory, on the Docker host, with the old installation still there:
#
#   ./deploy/upgrade/upgrade-to-0.9.sh                  # the plan: what it finds and what it would do; changes nothing
#   ./deploy/upgrade/upgrade-to-0.9.sh --apply          # does it
#   ./deploy/upgrade/upgrade-to-0.9.sh --old-project ume-rag --env-file deploy/.env --apply
#
# It reads the deployment (deploy/upgrade/lib.sh; Decisions "Compose host layout and upgrades"): the old project by its
# containers' Compose labels (the service rag-postgres or postgres; --old-project when there are two), and each volume's
# data from `docker inspect` or the volume's labels. What it does, each step skipped when it was done already:
#   1. deploy/.env: renames the variables UME_RAG_* to KALLA_*, RAG_*_IMAGE to KALLA_*_IMAGE and the gateway's UME_* to
#      FORS_* (the old file is kept as .env.pre-0.9), and writes what an existing installation keeps: the sign-in client
#      ume-rag-admin when the file declares the sign-in and names no client (the old default), and the backup
#      repository's path /ume-rag when an s3:// target names none (the old default, so the existing backups stay in use);
#   2. stops and removes the old project's containers (`docker compose -p OLD down`; its volumes and data stay);
#   3. gives each new volume <project>_kalla-<name> the old data:
#      - a named volume is copied (a copy that stopped part-way is done again);
#      - data on a host path (a volume backed by a bind, or a direct bind) stays where it is: the new volume is a
#        bind-backed volume of the same path, nothing is copied;
#      - a backup directory named by KALLA_BACKUP_DIR or KALLA_UPLOAD_BACKUP_DIR stays as it is (the variable carries it).
#      It refuses an old postgres-data, certs, secrets or blobs that is missing or empty, and a new volume that already
#      holds other data.
# The database roles (ume_rag_* to kalla_*) are renamed by the new release's kalla-roles when it starts, as in the
# Portainer stack (Decisions "Upgrades in one step"): no step here signs in to Postgres. Then re-run
# ./deploy/init-deployment.sh and start the release as usual. Back up first (docs/runbook.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(cd "$here/.." && pwd)"
# shellcheck source=deploy/upgrade/lib.sh
. "$here/lib.sh"
old_project=""
env_file="${KALLA_STATE_DIR:-/etc/kalla}/.env"; [ -e "$env_file" ] || [ ! -e "$deploy/.env" ] || env_file="$deploy/.env"

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
step() { echo; echo "== $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --old-project) old_project="${2:-}"; [ -n "$old_project" ] || up_die "--old-project needs a name"; shift 2;;
    --env-file) env_file="${2:-}"; [ -n "$env_file" ] || up_die "--env-file needs a file"; shift 2;;
    --apply) UP_APPLY=1; shift;;
    -h|--help) usage;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done

command -v docker >/dev/null || up_die "docker is required"
[ -f "$env_file" ] || up_die "$env_file not found: copy deploy/.env from the old installation here first"
[ "$UP_APPLY" = 1 ] || echo "The plan (nothing is changed; run again with --apply to do it):"

# --- 1. Variables ------------------------------------------------------------------------------------------------------
# set_default FILE NAME VALUE: adds NAME=VALUE unless the file sets NAME (also an empty NAME=, which means unset on purpose).
# A last line without its newline gets one first, so that the new line doesn't run into it.
set_default() {
  grep -qE "^$2=" "$1" && return
  if [ "$UP_APPLY" != 1 ]; then echo "  would add $2=$3"; return; fi
  [ ! -s "$1" ] || [ -z "$(tail -c 1 "$1")" ] || echo >> "$1"
  printf '%s=%s\n' "$2" "$3" >> "$1"
}
# The renames on a copy: the plan reads the renamed file without touching the real one.
renamed="$(mktemp)"; trap 'rm -f "$renamed"' EXIT
sed -E -e 's/(^|[^A-Za-z0-9_])UME_RAG_/\1KALLA_/g' -e 's/(^|[^A-Za-z0-9_])RAG_([A-Z]+)_IMAGE/\1KALLA_\2_IMAGE/g' \
  -e 's/(^|[^A-Za-z0-9_])UME_/\1FORS_/g' "$env_file" > "$renamed"

step "1. Variables in $env_file"
if ! cmp -s "$env_file" "$renamed"; then
  if [ "$UP_APPLY" = 1 ]; then
    [ -e "$env_file.pre-0.9" ] || cp -p "$env_file" "$env_file.pre-0.9"
    cat "$renamed" > "$env_file"
    echo "renamed the variables (the old file is $env_file.pre-0.9)"
  else
    echo "  would rename the variables on $(diff "$env_file" "$renamed" | grep -c '^>' || true) lines (UME_RAG_* to KALLA_*, RAG_*_IMAGE to KALLA_*_IMAGE, UME_* to FORS_*)"
  fi
else echo "  nothing to rename"; fi
read_env="$env_file"; [ "$UP_APPLY" = 1 ] || read_env="$renamed"
# The old defaults, kept as deployment values. Remove a line to take the new default (kalla-admin) once the identity
# provider has that client.
if grep -qE '^KALLA_OIDC_AUTHORITY=.+' "$read_env"; then set_default "$read_env" KALLA_OIDC_CLIENT_ID ume-rag-admin; fi
# The value as init-deployment.sh reads it, with no slash at the end (s3://ENDPOINT/BUCKET/ names no path either).
target="$(up_env_value "$read_env" KALLA_BACKUP_TARGET)"
while [ "${target%/}" != "$target" ]; do target="${target%/}"; done
case "$target" in
  s3://*/*/*) ;;
  s3://*/*)
    if [ "$UP_APPLY" = 1 ]; then
      tmp="$(mktemp "$env_file.XXXXXX")"
      grep -vE '^KALLA_BACKUP_TARGET=' "$env_file" > "$tmp" || true
      printf "KALLA_BACKUP_TARGET='%s'\n" "$target/ume-rag" >> "$tmp"
      cat "$tmp" > "$env_file"; rm -f "$tmp"
      echo "kept the backup repository's path: KALLA_BACKUP_TARGET=$target/ume-rag"
    else echo "  would keep the backup repository's path: KALLA_BACKUP_TARGET=$target/ume-rag"; fi ;;
esac

# --- The deployment ----------------------------------------------------------------------------------------------------
old_project="$(COMPOSE_PROJECT_NAME="" up_project "$old_project" /dev/null rag-postgres postgres)"
new_project="$(up_project "" "$read_env" kalla-postgres)"; new_project="${new_project:-kalla}"
step "The deployment"
echo "  old project: ${old_project:-none found (no container of the service rag-postgres or postgres)}"
echo "  new project: $new_project"

# Each volume: NAME (rag-NAME before, kalla-NAME after), the old services that mount it, where, and whether it is required.
volumes=(
  "postgres-data|rag-postgres postgres|/var/lib/postgresql/data|required"
  "certs|rag-bootstrap bootstrap|/certs|required"
  "secrets|rag-bootstrap bootstrap|/secrets|required"
  "blobs|rag-bootstrap bootstrap|/blobs|required"
  "openwebui-data|rag-bootstrap bootstrap|/openwebui-data|"
  "pgbackrest-repo|rag-postgres postgres|/var/lib/pgbackrest|"
  "upload-copy|rag-bootstrap bootstrap|/backup/uploads|"
)
declare -A carried=([pgbackrest-repo]=KALLA_BACKUP_DIR [upload-copy]=KALLA_UPLOAD_BACKUP_DIR)

if [ -z "$old_project" ]; then
  if docker volume inspect "${new_project}_kalla-postgres-data" >/dev/null 2>&1; then
    echo; echo "No old project here and ${new_project}_kalla-postgres-data exists: steps 2 and 3 were done already."
    echo "Next: ./deploy/init-deployment.sh, then start the release (docs/upgrade-guide.md)."
    exit 0
  fi
  up_die "no old project found (a container of the service rag-postgres or postgres): pass --old-project"
fi

# A shell for reading and copying: the old Postgres container's image, else the new release's.
pg="$(up_container "$old_project" rag-postgres postgres)"
image=""; [ -z "$pg" ] || image="$(docker inspect -f '{{.Config.Image}}' "$pg" 2>/dev/null || true)"
if [ -z "$image" ]; then
  image="$(up_env_value "$read_env" KALLA_POSTGRES_IMAGE)"
  # shellcheck disable=SC2016 # the Compose file's own ${...}
  [ -n "$image" ] || image="$(sed -n 's/.*\${KALLA_POSTGRES_IMAGE:-\([^}]*\)}.*/\1/p' "$deploy/compose.rag.yaml" | head -n 1)"
fi

# The plan for every volume, resolved before anything stops (the containers' mounts are gone after `down`).
plan=()
step "The volumes, resolved now and carried over after the old containers are removed"
for entry in "${volumes[@]}"; do
  IFS='|' read -r name services dest required <<< "$entry"
  to="${new_project}_kalla-$name"
  from="$(up_resolve "$old_project" "$services" "$dest" "rag-$name")"
  if [ -z "$from" ]; then
    [ -z "$required" ] || up_die "rag-$name of project $old_project not found (no mount at $dest, no volume with its labels)"
    echo "  rag-$name: not in this deployment, skipped"; continue
  fi
  IFS='|' read -r kind vol path <<< "$from"
  var="${carried[$name]:-}"
  if [ "$kind" = bind ] && [ -n "$var" ] && [ "$(up_env_value "$read_env" "$var")" = "$path" ]; then
    echo "  rag-$name: the host directory $path, which $var keeps naming: nothing to do"; continue
  fi
  if [ -n "$required" ] && ! up_has_data "$image" "$from"; then
    up_die "rag-$name of project $old_project is empty ($kind ${vol:-$path}): refusing to carry an empty $name over"
  fi
  existing="$(up_volume_kind "$to")"
  if [ -n "$existing" ]; then
    IFS='|' read -r ekind _ epath <<< "$existing"
    if [ "$kind" != volume ] && [ "$ekind" = bind-backed ] && [ "$epath" = "$path" ]; then
      echo "  rag-$name: $to is already bind-backed to $path: nothing to do"; continue
    fi
    if [ "$kind" = volume ] && [ "$(docker run --rm --network none --mount "type=volume,src=$to,dst=/to,readonly" \
      --entrypoint sh "$image" -c 'if [ -e /to/.copy-unfinished ]; then echo unfinished; fi')" = unfinished ]; then
      echo "  rag-$name: $to holds a copy that did not finish: copy again"; plan+=("copy|$name|$to|$from"); continue
    fi
    if up_has_data "$image" "$existing"; then
      [ "$kind" = volume ] || up_die "$to exists and holds data, but rag-$name is on $path: remove $to, or point it at $path yourself"
      echo "  rag-$name: $to exists and is not empty: left as it is"; continue
    fi
    [ "$kind" = volume ] || echo "  rag-$name: $to exists and is empty: it is replaced"
  fi
  case "$kind" in
    volume) echo "  rag-$name: copy the volume $vol to $to"; plan+=("copy|$name|$to|$from");;
    *) echo "  rag-$name: on the host path $path ($kind): $to becomes a bind-backed volume of that path, nothing copied"
       plan+=("bind|$name|$to|$from");;
  esac
done

# --- 2. Stop the old project -------------------------------------------------------------------------------------------
step "2. Stopping and removing the containers of $old_project (volumes and data stay)"
up_do docker compose -p "$old_project" down

# --- 3. Volumes --------------------------------------------------------------------------------------------------------
step "3. The volumes"
for p in "${plan[@]}"; do
  IFS='|' read -r how name to from <<< "$p"
  path="${from##*|}"
  labels=(--label "com.docker.compose.project=$new_project" --label "com.docker.compose.volume=kalla-$name")
  case "$how" in
    copy)
      docker volume inspect "$to" >/dev/null 2>&1 || up_do docker volume create "${labels[@]}" "$to"
      up_do docker run --rm --user 0 --network none --mount "$(up_mount_arg "$from" /from ro)" --mount "type=volume,src=$to,dst=/to" \
        --entrypoint sh "$image" -c 'touch /to/.copy-unfinished && cp -a /from/. /to/ && rm -f /to/.copy-unfinished'
      [ "$UP_APPLY" != 1 ] || echo "copied rag-$name to $to";;
    bind)
      if docker volume inspect "$to" >/dev/null 2>&1; then up_do docker volume rm "$to"; fi # empty, checked above
      up_do docker volume create "${labels[@]}" --driver local --opt type=none --opt o=bind --opt "device=$path" "$to"
      [ "$UP_APPLY" != 1 ] || echo "$to is bind-backed to $path";;
  esac
done

if [ "$UP_APPLY" != 1 ]; then
  echo; echo "Nothing was changed. Run again with --apply to do this."
  exit 0
fi
cat <<EOF

Done. Next (docs/upgrade-guide.md, "0.9.0"):
  1. ./deploy/init-deployment.sh          writes the connection strings with the new role names; keeps your settings
  2. start the release as usual (docker compose -f deploy/compose.rag.yaml --env-file deploy/.env up -d --wait):
     kalla-roles renames the database roles before kalla-migrate runs
  3. once everything runs, remove the old volumes that were copied (never the host paths the new volumes point at)
EOF
