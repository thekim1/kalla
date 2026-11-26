#!/usr/bin/env bash
# The upgrade scripts' shared library (Decisions "Compose host layout and upgrades"; repo-and-deployment §7.3): it reads
# the deployment instead of assuming it. Source it; it defines functions and sets nothing else.
#
#   - the project: --project, else COMPOSE_PROJECT_NAME from the shell, else the env file's, else the only project with
#     one of the given services; never a guess between two (up_project)
#   - the containers by their Compose labels, among a service's old and new names, running or stopped (up_container)
#   - each mount from `docker inspect` or the volume's labels: a named volume, a named volume backed by a bind (its
#     device option), or a direct bind (up_resolve). Data already on a host path is pointed to, never copied
#   - a plan first: up_do prints what it would run and runs it only when UP_APPLY=1 (the scripts' --apply)
#
# A mount is one line "KIND|NAME|PATH": KIND volume, bind-backed or bind; NAME the volume (empty for a bind); PATH the
# host path (a bind-backed volume's device, a bind's source, a named volume's _data directory).

UP_APPLY="${UP_APPLY:-0}"

up_die() { echo "error: $*" >&2; exit 1; }

# up_env_value FILE KEY: KEY's last value in FILE, its quotes removed; read, never sourced. Empty when unset or no file.
up_env_value() {
  { sed -n "s/^$2=//p" "$1" 2>/dev/null || true; } | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'
}

# up_projects SERVICE...: the Compose projects with a container of one of these services, one per line.
up_projects() {
  local s
  for s in "$@"; do
    docker ps -a --filter "label=com.docker.compose.service=$s" --format '{{.Label "com.docker.compose.project"}}'
  done | sed '/^$/d' | LC_ALL=C sort -u
}

# up_project GIVEN ENV_FILE SERVICE...: the project, from GIVEN (--project), the shell's COMPOSE_PROJECT_NAME, the env
# file's, or the only project running one of SERVICE. Dies between two; prints nothing when there is none.
up_project() {
  local given="$1" env_file="$2"; shift 2
  local p="${given:-${COMPOSE_PROJECT_NAME:-}}"
  [ -n "$p" ] || p="$(up_env_value "$env_file" COMPOSE_PROJECT_NAME)"
  if [ -n "$p" ]; then echo "$p"; return 0; fi
  local found; found="$(up_projects "$@")"
  case "$(grep -c . <<< "$found" || true)" in
    0) ;;
    1) echo "$found";;
    *) up_die "more than one project has $1 ($(paste -sd' ' <<< "$found")): name one with --project";;
  esac
}

# up_container PROJECT SERVICE...: the newest container of PROJECT with one of these service names, running or stopped.
up_container() {
  local project="$1" s id; shift
  for s in "$@"; do
    id="$(docker ps -a --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$s" \
      --format '{{.ID}}' | head -n 1)"
    if [ -n "$id" ]; then echo "$id"; return 0; fi
  done
}

# up_volume_kind VOLUME: "bind-backed|VOLUME|DEVICE" for a volume whose driver options bind a host path, else
# "volume|VOLUME|MOUNTPOINT". Nothing when the volume doesn't exist.
up_volume_kind() {
  local device mountpoint
  docker volume inspect "$1" >/dev/null 2>&1 || return 0
  device="$(docker volume inspect -f '{{index .Options "device"}}' "$1" 2>/dev/null || true)"
  [ "$device" != "<no value>" ] || device=""
  if [ -n "$device" ]; then echo "bind-backed|$1|$device"
  else
    mountpoint="$(docker volume inspect -f '{{.Mountpoint}}' "$1" 2>/dev/null || true)"
    echo "volume|$1|$mountpoint"
  fi
}

# up_mount CONTAINER DESTINATION: the mount at DESTINATION in CONTAINER, as a mount line. Nothing when there is none.
up_mount() {
  local type name source dest
  while IFS='|' read -r type name source dest; do
    [ "$dest" = "$2" ] || continue
    case "$type" in
      volume) up_volume_kind "$name"; return 0;;
      bind) echo "bind||$source"; return 0;;
    esac
  done < <(docker inspect -f '{{range .Mounts}}{{.Type}}|{{.Name}}|{{.Source}}|{{.Destination}}{{"\n"}}{{end}}' "$1" 2>/dev/null || true)
}

# up_resolve PROJECT "SERVICES" DESTINATION VOLUME: where the data of VOLUME (the Compose name, e.g. rag-certs) is: the
# mount at DESTINATION in the project's container of one of SERVICES, else the volume with the project's and VOLUME's
# Compose labels (the container may be gone). Nothing when neither exists.
up_resolve() {
  local project="$1" services="$2" dest="$3" volume="$4" id line name
  # shellcheck disable=SC2086 # the service names are words
  id="$(up_container "$project" $services)"
  if [ -n "$id" ]; then
    line="$(up_mount "$id" "$dest")"
    if [ -n "$line" ]; then echo "$line"; return 0; fi
  fi
  name="$(docker volume ls -q --filter "label=com.docker.compose.project=$project" \
    --filter "label=com.docker.compose.volume=$volume" | head -n 1)"
  [ -z "$name" ] || up_volume_kind "$name"
}

# up_mount_arg MOUNT-LINE TARGET [ro]: the docker run --mount argument for a mount line.
up_mount_arg() {
  local kind name path; IFS='|' read -r kind name path <<< "$1"
  case "$kind" in
    volume|bind-backed) echo "type=volume,src=$name,dst=$2${3:+,readonly}";;
    bind) echo "type=bind,src=$path,dst=$2${3:+,readonly}";;
  esac
}

# up_has_data IMAGE MOUNT-LINE: whether the mount holds any file (read in a throwaway container of IMAGE, as root).
up_has_data() {
  local mount; mount="$(up_mount_arg "$2" /s ro)"
  [ -n "$(docker run --rm --user 0 --network none --mount "$mount" --entrypoint sh "$1" -c 'ls -A /s | head -n 1')" ]
}

# up_do COMMAND...: runs it with UP_APPLY=1, else prints it as part of the plan.
up_do() {
  if [ "$UP_APPLY" = 1 ]; then "$@"
  else printf '  would run:'; printf ' %q' "$@"; printf '\n'; fi
}
