#!/usr/bin/env bash
# The check before (and after) every upgrade of a Compose host (Decisions "Compose host layout and upgrades";
# docs/upgrade-guide.md, "Upgrade procedure (Compose host)"). Read-only: it changes nothing in the deployment.
#
#   deploy/doctor.sh [options]
#
# It reports the Compose project and its services' versions; each volume's host path (named, bind-backed or a direct
# bind, read from `docker inspect`) and its free space; the overlays and profiles; the pgBackRest stanza, the WAL archive
# backlog and the newest backups; the newest key escrow set against the newest key; and which deploy directory and project
# the host units act on (install.sh's host.env) and whether their timers run. Each line is "ok", "warn" or "BLOCK". Run it as root: the volumes' data,
# the WAL backlog and a mode-600 env file are read on the host.
#
# Exit codes: 0 nothing blocks an upgrade, 1 something does, 2 a usage error, 3 the doctor could not look (no docker,
# or no access to it). What blocks: no Källa project (no kalla-postgres container, running or stopped), or two of them
# and no project named; no compose.backup.yaml, unless --no-backups-ok; with backups, a backup status that can't be
# read fresh, a stanza that isn't ok, a WAL backlog beyond the 5-minute RPO, or no backup in the last --max-backup-age
# hours.
#
# The project: --project, else COMPOSE_PROJECT_NAME from the shell, else the env file's, else the only one running.
#
# Options:
#   --env-file FILE         [deploy/.env] the deployment: its COMPOSE_PROJECT_NAME, COMPOSE_FILE and COMPOSE_PROFILES
#   --project NAME          the Compose project, when neither the shell nor the env file names it and more than one runs
#   --escrow-dir DIR        where deploy/escrow-keys.sh writes the key sets: the newest set of this project, and
#                           whether a key changed after it
#   --max-backup-age HOURS  [26] the newest backup (any type) must be younger (the alert of §7.4 rule 5)
#   --no-backups-ok         a deployment without compose.backup.yaml only warns (a test host you can rebuild)
#   --state-dir DIR         [/var/lib/kalla] where rag-backup-status.timer writes backup.prom
#   --unit-dir DIR          [/etc/systemd/system] where deploy/host/install.sh put the host units
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The deployment's env file (Decisions "Compose host layout and upgrades"): the state directory's, else deploy/.env (a
# clone, or a host whose env file init-deployment.sh hasn't moved yet). --env-file wins.
project=""; escrow_dir=""; max_backup_age=26; no_backups_ok=0
env_file="${KALLA_STATE_DIR:-/etc/kalla}/.env"; [ -e "$env_file" ] || [ ! -e "$here/.env" ] || env_file="$here/.env"
state_dir=/var/lib/kalla; unit_dir=/etc/systemd/system
shell_project="${COMPOSE_PROJECT_NAME:-}"

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
value() { if [ $# -lt 2 ] || [ -z "$2" ]; then echo "error: $1 needs a value" >&2; exit 2; fi; }
while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) value "$@"; env_file="$2"; shift 2;;
    --project) value "$@"; project="$2"; shift 2;;
    --escrow-dir) value "$@"; escrow_dir="$2"; shift 2;;
    --max-backup-age) value "$@"; max_backup_age="$2"; shift 2;;
    --no-backups-ok) no_backups_ok=1; shift;;
    --state-dir) value "$@"; state_dir="$2"; shift 2;;
    --unit-dir) value "$@"; unit_dir="$2"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 2;;
  esac
done
[[ "$max_backup_age" =~ ^[0-9]+$ ]] || { echo "error: --max-backup-age takes whole hours" >&2; exit 2; }
command -v docker >/dev/null || { echo "error: no docker command on this host" >&2; exit 3; }
docker info >/dev/null 2>&1 || { echo "error: docker doesn't answer (run as root, or as a member of the docker group)" >&2; exit 3; }

blocks=0; warns=0
ok() { printf '  ok     %s\n' "$*"; }
warn() { printf '  warn   %s\n' "$*"; warns=$((warns + 1)); }
block() { printf '  BLOCK  %s\n' "$*"; blocks=$((blocks + 1)); }
section() { printf '\n%s\n' "$*"; }
finish() {
  printf '\n%d blocking, %d to look at\n' "$blocks" "$warns"
  [ "$blocks" = 0 ] || exit 1
  exit 0
}
# KEY's last value in the env file, its quotes removed (init-deployment.sh writes KEY='value'); read, never sourced.
env_value() { { sed -n "s/^$1=//p" "$env_file" 2>/dev/null || true; } | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'; }
num() { [[ "${1:-}" =~ ^-?[0-9]+$ ]]; } # a whole number (a metric or a timestamp read from a file)
ago() { # ago SECONDS → "3 h 12 min" / "2 d 4 h"
  local s="$1"
  if [ "$s" -ge 86400 ]; then echo "$((s / 86400)) d $((s % 86400 / 3600)) h"
  elif [ "$s" -ge 3600 ]; then echo "$((s / 3600)) h $((s % 3600 / 60)) min"
  else echo "$((s / 60)) min"; fi
}

echo "Källa doctor, $(date -u +%Y-%m-%dT%H:%MZ) on $(hostname)"

# ---- The project ---------------------------------------------------------------------------------------------------
section "Project"
compose_file=""; profiles=""; env_readable=0
if [ -r "$env_file" ]; then
  env_readable=1
  compose_file="$(env_value COMPOSE_FILE)"; profiles="$(env_value COMPOSE_PROFILES)"
  [ -n "$project" ] || project="$shell_project"
  [ -n "$project" ] || project="$(env_value COMPOSE_PROJECT_NAME)"
  ok "env file $env_file"
else
  [ -n "$project" ] || project="$shell_project"
  warn "cannot read $env_file (pass --env-file; as root for a mode-600 file): the overlays and backups are not checked"
fi
# Every project with a kalla-postgres container, by Compose's labels.
running="$(docker ps -a --filter label=com.docker.compose.service=kalla-postgres \
  --format '{{.Label "com.docker.compose.project"}}' | sort -u | tr '\n' ' ')"
running="${running% }"
if [ -n "$project" ]; then
  if [[ " $running " == *" $project "* ]]; then ok "project $project"
  else block "project $project has no kalla-postgres container (Källa projects here: ${running:-none})"; finish; fi
elif [ -z "$running" ]; then
  block "no Källa project on this host (no container with the Compose service kalla-postgres)"; finish
elif [ "${running// /}" = "$running" ]; then
  project="$running"
  # compose.rag.yaml's `name:` is kalla; another name means COMPOSE_PROJECT_NAME must stay set for every command.
  if [ "$project" = kalla ]; then ok "project kalla (found by its containers' labels)"
  else warn "project $project (found by its containers' labels): set COMPOSE_PROJECT_NAME=$project in the env file, or every command needs -p $project"; fi
else
  block "more than one Källa project here ($running): pass --project, or set COMPOSE_PROJECT_NAME in the env file"; finish
fi

containers="$(docker ps -a --filter "label=com.docker.compose.project=$project" \
  --format '{{.Label "com.docker.compose.service"}}|{{.Names}}|{{.Image}}|{{.State}}' | sort)"
section "Services"
while IFS='|' read -r service name image state; do
  [ -n "$service" ] || continue
  # The tag of the image's last path part (a registry's :port is in an earlier part); a digest-only image has none.
  last="${image%@*}"; last="${last##*/}"; version="?"; [[ "$last" != *:* ]] || version="${last##*:}"
  case "$state" in
    running) ok "$(printf '%-24s %-12s %s' "$service" "$version" "$state")";;
    exited) case "$service" in
              # The one-shot services (restart: "no") end after their work.
              kalla-bootstrap|kalla-roles|kalla-migrate|kalla-openwebui-setup|rag-upload-backup) ok "$(printf '%-24s %-12s %s' "$service" "$version" "done")";;
              *) warn "$(printf '%-24s %-12s %s' "$service" "$version" "$state")";;
            esac;;
    *) warn "$(printf '%-24s %-12s %s' "$service" "$version" "$state")";;
  esac
done <<< "$containers"

# ---- Overlays and profiles -----------------------------------------------------------------------------------------
section "Overlays and profiles"
any="$(sed -n '1s/^[^|]*|\([^|]*\)|.*/\1/p' <<< "$containers")"
used="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "$any" 2>/dev/null || true)"
# compose.<name>.yaml is named <name>; any other file (a site overlay of your own) by its file name.
names() { tr ':,' '\n' | sed -e '/^$/d' -e 's#.*/##' -e 's#^compose\.\(.*\)\.yaml$#\1#' | sort -u | tr '\n' ' ' | sed 's/ $//'; }
in_env="$(names <<< "$compose_file")"; in_use="$(names <<< "$used")"
[ "$env_readable" = 0 ] || ok "COMPOSE_FILE: ${in_env:-none}"
if [ -n "$in_use" ]; then
  if [ "$env_readable" = 1 ] && [ "$in_env" != "$in_use" ]; then
    warn "the running containers were started with: $in_use (the env file's next \`up\` changes that)"
  else ok "started with: $in_use"; fi
  case "$used" in *"$here/"*|"") ;; *) warn "the containers were started from another directory: ${used%%,*}";; esac
fi
[ "$env_readable" = 0 ] || ok "COMPOSE_PROFILES: ${profiles:-none}"
# The profiles the containers show: a profile's services exist only when it was on at the last `up`.
files="$(tr ',' '\n' <<< "$used")"; [ -n "$files" ] || files="$(ls "$here"/compose*.yaml 2>/dev/null || true)"
gated="$(while read -r f; do [ -r "$f" ] && awk '/^  [a-z0-9-]+:$/ { svc = substr($1, 1, length($1) - 1) }
  /^    profiles: \[/ { gsub(/[][,]/, " "); for (i = 2; i <= NF; i++) print svc, $i }' "$f"; done <<< "$files" \
  | grep -v '^rag-upload-backup ' || true)" # the upload copy runs from its timer, not from `up`
shown=""; wanted=""
while read -r svc prof; do
  [ -n "$svc" ] || continue
  if grep -q "^$svc|" <<< "$containers" && [[ " $shown " != *" $prof "* ]]; then shown="${shown:+$shown }$prof"; fi
  if [[ ",$profiles," == *",$prof,"* ]] && [[ " $wanted " != *" $prof "* ]]; then wanted="${wanted:+$wanted }$prof"; fi
done <<< "$gated"
if [ -n "$gated" ]; then
  if [ "$env_readable" = 1 ] && [ "$(tr ' ' '\n' <<< "$shown" | sort | tr '\n' ' ')" != "$(tr ' ' '\n' <<< "$wanted" | sort | tr '\n' ' ')" ]; then
    warn "the containers show the profiles: ${shown:-none} (the env file's next \`up\` changes that)"
  else ok "the containers show the profiles: ${shown:-none}"; fi
fi

# ---- Volumes -------------------------------------------------------------------------------------------------------
section "Volumes and free space"
seen=" "; pgdata=""; secrets_path=""; certs_path=""
while IFS='|' read -r _ name _ _; do
  [ -n "$name" ] || continue
  while IFS='|' read -r type vol source _; do
    case "$type" in volume|bind) ;; *) continue;; esac
    key="$type:${vol:-$source}"; [[ "$seen" == *" $key "* ]] && continue; seen+="$key "
    if [ "$type" = volume ]; then
      # A bind-backed volume (driver local, o=bind) keeps its data at its device option, not under /var/lib/docker.
      device="$(docker volume inspect -f '{{index .Options "device"}}' "$vol" 2>/dev/null || true)"
      [ "$device" != "<no value>" ] || device=""
      if [ -n "$device" ]; then kind="bind-backed volume $vol"; path="$device"
      else kind="volume $vol"; path="$source"; fi
      case "$vol" in
        "${project}_kalla-postgres-data") pgdata="$path";;
        "${project}_kalla-secrets") secrets_path="$path";;
        "${project}_kalla-certs") certs_path="$path";;
      esac
    else
      # The Docker socket, the host's /etc and the like are not data.
      case "$source" in /run/*|/var/run/*|/etc/*|/proc/*|/sys/*|/dev/*) continue;; esac
      kind="bind"; path="$source"
    fi
    free="$(df -Pk "$path" 2>/dev/null | awk 'NR == 2 { printf "%.1f GB free of %.1f GB, %s used", $4 / 1048576, $2 / 1048576, $5 }' || true)"
    if [ -n "$free" ]; then ok "$kind → $path: $free"
    else warn "$kind → $path: free space unknown (not readable here; run as root)"; fi
  done < <(docker inspect -f '{{range .Mounts}}{{.Type}}|{{.Name}}|{{.Source}}|{{.Destination}}{{"\n"}}{{end}}' "$name" 2>/dev/null || true)
done <<< "$containers"

# ---- Backups -------------------------------------------------------------------------------------------------------
section "Backups (pgBackRest)"
if [[ " $in_env $in_use " != *" backup "* ]]; then
  if [ "$no_backups_ok" = 1 ]; then
    warn "no compose.backup.yaml: this deployment takes no backups (--no-backups-ok), so an upgrade has nothing to restore from"
  else
    block "no compose.backup.yaml: this deployment takes no backups, so an upgrade has nothing to restore from (set up a backup target, or pass --no-backups-ok)"
  fi
else
  prom="$state_dir/backup.prom"; tmp=""; stale_by=0
  metric() { sed -n "s/^$1 //p" "$prom" | tail -n 1; }
  # rag-backup-status.timer refreshes backup.prom every two minutes; read it fresh when it is older than ten. A stale
  # file that can't be replaced by a fresh read is not trusted: a dead status timer is what hid the VM's backlog.
  fresh=0; read_at=""
  if [ -r "$prom" ]; then
    read_at="$(metric rag_backup_status_timestamp_seconds)"
    if num "$read_at"; then stale_by=$(( $(date +%s) - read_at )); [ "$stale_by" -lt 600 ] && fresh=1
    else read_at=""; fi
  fi
  if [ "$fresh" = 0 ] && [ "$env_readable" = 1 ]; then
    tmp="$(mktemp -d)"; trap 'rm -r "$tmp"' EXIT
    if COMPOSE_PROJECT_NAME="$project" bash "$here/backup-postgres.sh" --status-only --env-file "$env_file" --state-dir "$tmp" >/dev/null 2>&1 \
      && [ -r "$tmp/backup.prom" ]; then
      prom="$tmp/backup.prom"; fresh=1; stale_by=0
      warn "backup.prom in $state_dir is missing or stale: is rag-backup-status.timer running? (read now instead)"
    fi
  fi
  if [ "$fresh" = 0 ]; then
    if [ -n "$read_at" ]; then
      block "the backup status is $(ago "$stale_by") old ($state_dir/backup.prom) and could not be read now (kalla-postgres stopped, or the env file not readable): is rag-backup-status.timer running?"
    else
      block "the backup status could not be read (no current $state_dir/backup.prom, and kalla-postgres didn't answer)"
    fi
  fi
  if [ -r "$prom" ] && num "$(metric rag_backup_status_timestamp_seconds)"; then
    [ "$fresh" = 1 ] || echo "         as of $(date -u -d "@$(metric rag_backup_status_timestamp_seconds)" +%Y-%m-%dT%H:%MZ), not trusted:"
    now="$(date +%s)"
    code="$(metric rag_backup_repo_status)"
    case "$code" in
      0) ok "stanza ragdb: ok";;
      2) block "stanza ragdb: no valid backup (pgBackRest status 2)";;
      99) block "stanza ragdb: the repository could not be read (status 99): no stanza, or the target is unreachable";;
      *) block "stanza ragdb: pgBackRest status ${code:-unknown}";;
    esac
    wal="$(metric rag_backup_wal_archive_age_seconds)"; num "$wal" || wal=-1
    if [ "$wal" -lt 0 ]; then warn "WAL archive: unknown (nothing archived yet, or a standby)"
    elif [ $((wal + stale_by)) -gt 300 ]; then block "WAL archive: a backlog, the newest archived segment is $(ago $((wal + stale_by))) old (RPO 5 min)"
    else ok "WAL archive: no backlog"; fi
    for type in full diff; do
      at="$(sed -n "s/^rag_backup_last_timestamp_seconds{type=\"$type\"} //p" "$prom")"; num "$at" || at=0
      if [ "$at" = 0 ]; then
        if [ "$type" = full ]; then block "no full backup"; else ok "no $type backup yet"; fi
      else
        ok "newest $type backup: $(date -u -d "@$at" +%Y-%m-%dT%H:%MZ) ($(ago $((now - at))) ago)"
      fi
    done
    # The age when the file was written, plus how old the file is.
    age="$(metric rag_backup_age_seconds)"; num "$age" || age=0
    age=$((age + stale_by))
    if [ "$age" -gt $((max_backup_age * 3600)) ]; then
      block "the newest backup is $(ago "$age") old (more than $max_backup_age h): run deploy/backup-postgres.sh --type full first"
    fi
  fi
  # The segments waiting to be archived, counted on the host (the data directory as root).
  if [ -n "$pgdata" ]; then
    if [ -r "$pgdata/pg_wal/archive_status" ]; then
      ready="$(find "$pgdata/pg_wal/archive_status" -maxdepth 1 -name '*.ready' 2>/dev/null | wc -l)"
      if [ "$ready" -gt 2 ]; then
        block "WAL archive: $ready segments wait to be archived (about $((ready * 16 / 1024)) GiB in pg_wal)"
      else ok "WAL archive: $ready segments waiting"; fi
    else warn "WAL archive: pg_wal not readable here, the waiting segments not counted (run as root)"; fi
  fi
fi

# ---- Key escrow ----------------------------------------------------------------------------------------------------
section "Key escrow"
if [ -z "$escrow_dir" ]; then
  warn "not checked: pass --escrow-dir (deploy/escrow-keys.sh's DEST) to see the newest set"
else
  # escrow-keys.sh's own name, so project kalla doesn't take kalla-test's sets.
  newest="$(find "$escrow_dir" -mindepth 1 -maxdepth 1 -type d -name "kalla-keys-$project-????????T??????Z" 2>/dev/null | LC_ALL=C sort | tail -n 1)"
  if [ -z "$newest" ]; then warn "no key set of project $project in $escrow_dir: run deploy/escrow-keys.sh $escrow_dir"
  else
    stamp="${newest##*-}"
    set_at="$(date -u -d "${stamp:0:4}-${stamp:4:2}-${stamp:6:2} ${stamp:9:2}:${stamp:11:2}:${stamp:13:2}" +%s 2>/dev/null || echo "")"
    ok "newest set: $(basename "$newest") (check it with deploy/escrow-keys.sh --verify $escrow_dir)"
    # The files escrow-keys.sh exports; one changed after the set means it is missing from every set.
    if [ -z "$secrets_path$certs_path" ]; then :
    elif [ ! -r "${secrets_path:-$certs_path}" ]; then warn "the keys' dates not read (the volumes as root)"
    elif num "$set_at"; then
      key_at="$( { [ -z "$secrets_path" ] || find "$secrets_path" -maxdepth 3 -type f \( -path "$secrets_path/.pepper" \
          -o -path "$secrets_path/keys/rag-blob-*" -o -path "$secrets_path/pgbackrest/cipher-pass" \
          -o -path "$secrets_path/postgres/*_password" -o -path "$secrets_path/api/Security__ConversationIdKeys__*" \) -printf '%T@\n'
        [ -z "$certs_path" ] || find "$certs_path" -maxdepth 1 -type f \( -name data-protection.pfx -o -name ca.pem -o -name ca.key \) -printf '%T@\n'
      } 2>/dev/null | cut -d. -f1 | sort -n | tail -n 1)"
      if num "$key_at" && [ "$key_at" -gt "$set_at" ]; then
        warn "a key changed after the newest set ($(date -u -d "@$key_at" +%Y-%m-%dT%H:%MZ)): run deploy/escrow-keys.sh $escrow_dir again"
      elif num "$key_at"; then ok "no key changed after the newest set"; fi
    fi
  fi
fi

# ---- Host units ----------------------------------------------------------------------------------------------------
section "Host units"
units=("$unit_dir"/rag-*.service)
if [ ! -e "${units[0]}" ]; then
  warn "no host units in $unit_dir (deploy/host/install.sh): no scheduled backups, firewall or restarts"
else
  # install.sh's host.env names the deployment the units act on (D6); units installed before it name their scripts' paths.
  kalla_state="${KALLA_STATE_DIR:-$(env_value KALLA_STATE_DIR)}"; host_env="${kalla_state:-/etc/kalla}/host.env"
  hv() { sed -n "s/^$1=//p" "$host_env" 2>/dev/null | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/"; }
  if grep -qs '^EnvironmentFile=.*/host.env$' "${units[@]}"; then
    if [ ! -r "$host_env" ]; then
      block "the units read $host_env, which is missing: they act on nothing (re-run deploy/host/install.sh)"; targets=""
    else
      targets="${#units[@]} $(hv KALLA_DEPLOY_DIR)"
      if [ "$(hv COMPOSE_PROJECT_NAME)" = "$project" ]; then ok "the units act on project $project ($host_env)"
      else block "the units act on project $(hv COMPOSE_PROJECT_NAME), not $project: their backups and checks miss this deployment (re-run deploy/host/install.sh)"; fi
    fi
  else
    targets="$(for u in "${units[@]}"; do sed -n 's/^ExecStart=\([^ ]*\).*/\1/p' "$u" | xargs -r dirname; done | sed 's#/host$##' | sort | uniq -c)"
    warn "units from before host.env: they act on deploy/.env's project, not a fixed one (re-run deploy/host/install.sh)"
  fi
  while read -r count dir; do
    [ -n "$dir" ] || continue
    units_run="$count units run"; [ "$count" != 1 ] || units_run="1 unit runs"
    if [ ! -d "$dir" ]; then warn "$units_run from $dir, which doesn't exist: re-run deploy/host/install.sh from this bundle"
    elif [ "$dir" != "$here" ]; then warn "$units_run from $dir, not from $here (expected before an upgrade's install.sh, not after)"
    else ok "$units_run from $dir"; fi
  done <<< "$targets"
  if [[ " $in_env $in_use " == *" hardened "* ]]; then
    if systemctl is-active --quiet rag-firewall.timer 2>/dev/null; then ok "rag-firewall.timer is active"
    else warn "rag-firewall.timer is not active, with compose.hardened.yaml in use"; fi
  fi
  if [[ " $in_env $in_use " == *" backup "* ]]; then
    backup_timers=(rag-backup.timer rag-backup-status.timer)
    # install.sh enables the upload copy's timer except with an S3 target.
    [[ "$(env_value KALLA_BACKUP_TARGET)" == s3://* ]] || backup_timers+=(rag-upload-backup.timer)
    for t in "${backup_timers[@]}"; do
      if systemctl is-active --quiet "$t" 2>/dev/null; then ok "$t is active"; else warn "$t is not active"; fi
    done
  fi
fi

finish
