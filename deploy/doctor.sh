#!/usr/bin/env bash
# The check before (and after) every upgrade of a Compose host (Decisions "Compose host layout and upgrades";
# docs/upgrade-guide.md, "Upgrade procedure (Compose host)"). Read-only: it changes nothing in the deployment.
#
#   deploy/doctor.sh [options]
#
# It reports the Compose project and its services' versions; each volume's host path (named, bind-backed or a direct
# bind, read from `docker inspect`) and its free space; the overlays and profiles; the pgBackRest stanza, the WAL archive
# backlog and the newest backups; the newest key escrow set; and which deploy directory the host units run from and
# whether the firewall timer runs. Each line is "ok", "warn" or "BLOCK".
#
# Exit codes: 0 nothing blocks an upgrade, 1 something does, 2 a usage error. What blocks: no running Källa project,
# or two of them and no --project; with backups configured (compose.backup.yaml), a stanza that isn't ok, a WAL backlog
# beyond the 5-minute RPO, or no backup in the last --max-backup-age hours.
#
# Options:
#   --env-file FILE         [deploy/.env] the deployment: its COMPOSE_PROJECT_NAME, COMPOSE_FILE and COMPOSE_PROFILES
#   --project NAME          the Compose project, when the env file doesn't name it and more than one runs
#   --escrow-dir DIR        where deploy/escrow-keys.sh writes the key sets: report the newest set of this project
#   --max-backup-age HOURS  [26] the newest backup (any type) must be younger (the alert of §7.4 rule 5)
#   --state-dir DIR         [/var/lib/kalla] where rag-backup-status.timer writes backup.prom
#   --unit-dir DIR          [/etc/systemd/system] where deploy/host/install.sh put the host units
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; project=""; escrow_dir=""; max_backup_age=26
state_dir=/var/lib/kalla; unit_dir=/etc/systemd/system

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) env_file="${2:-}"; shift 2;;
    --project) project="${2:-}"; shift 2;;
    --escrow-dir) escrow_dir="${2:-}"; shift 2;;
    --max-backup-age) max_backup_age="${2:-}"; shift 2;;
    --state-dir) state_dir="${2:-}"; shift 2;;
    --unit-dir) unit_dir="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 2;;
  esac
done
[[ "$max_backup_age" =~ ^[0-9]+$ ]] || { echo "error: --max-backup-age takes whole hours" >&2; exit 2; }

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
env_value() { sed -n "s/^$1=//p" "$env_file" 2>/dev/null | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'; }
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
  [ -n "$project" ] || project="$(env_value COMPOSE_PROJECT_NAME)"
  ok "env file $env_file"
else
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
  version="${image%@*}"; version="${version##*:}"; [ "$version" != "${image%@*}" ] || version="?"
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
names() { tr ':,' '\n' | sed -n 's#.*/\{0,1\}compose\.\(.*\)\.yaml$#\1#p' | sort -u | tr '\n' ' ' | sed 's/ $//'; }
in_env="$(names <<< "$compose_file")"; in_use="$(names <<< "$used")"
[ "$env_readable" = 0 ] || ok "COMPOSE_FILE: ${in_env:-none}"
if [ -n "$in_use" ]; then
  if [ "$env_readable" = 1 ] && [ "$in_env" != "$in_use" ]; then
    warn "the running containers were started with: $in_use (the env file's next \`up\` changes that)"
  else ok "started with: $in_use"; fi
  case "$used" in *"$here/"*|"") ;; *) warn "the containers were started from another directory: ${used%%,*}";; esac
fi
[ "$env_readable" = 0 ] || ok "COMPOSE_PROFILES: ${profiles:-none}"

# ---- Volumes -------------------------------------------------------------------------------------------------------
section "Volumes and free space"
seen=" "
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
  warn "no compose.backup.yaml: this deployment takes no backups, so an upgrade has nothing to restore from"
else
  prom="$state_dir/backup.prom"; tmp=""
  metric() { sed -n "s/^$1 //p" "$prom" | tail -n 1; }
  # rag-backup-status.timer refreshes backup.prom every two minutes; read it fresh when it is older than ten.
  fresh=0
  if [ -r "$prom" ]; then
    read_at="$(sed -n 's/^rag_backup_status_timestamp_seconds //p' "$prom")"
    [ -n "$read_at" ] && [ $(( $(date +%s) - read_at )) -lt 600 ] && fresh=1
  fi
  if [ "$fresh" = 0 ] && [ "$env_readable" = 1 ]; then
    tmp="$(mktemp -d)"; trap 'rm -r "$tmp"' EXIT
    if COMPOSE_PROJECT_NAME="$project" bash "$here/backup-postgres.sh" --status-only --env-file "$env_file" --state-dir "$tmp" >/dev/null 2>&1; then
      prom="$tmp/backup.prom"; warn "backup.prom in $state_dir is missing or stale: is rag-backup-status.timer running? (read now instead)"
    fi
  fi
  if [ ! -r "$prom" ]; then
    block "the backup status could not be read (no fresh $state_dir/backup.prom, and kalla-postgres didn't answer)"
  else
    now="$(metric rag_backup_status_timestamp_seconds)"
    code="$(metric rag_backup_repo_status)"
    case "$code" in
      0) ok "stanza ragdb: ok";;
      2) block "stanza ragdb: no valid backup (pgBackRest status 2)";;
      99) block "stanza ragdb: the repository could not be read (status 99): no stanza, or the target is unreachable";;
      *) block "stanza ragdb: pgBackRest status ${code:-unknown}";;
    esac
    wal="$(metric rag_backup_wal_archive_age_seconds)"
    if [ "${wal:--1}" -lt 0 ]; then warn "WAL archive: unknown (nothing archived yet, or a standby)"
    elif [ "$wal" -gt 300 ]; then block "WAL archive: a backlog, the newest archived segment is $(ago "$wal") old (RPO 5 min)"
    else ok "WAL archive: no backlog"; fi
    for type in full diff; do
      at="$(sed -n "s/^rag_backup_last_timestamp_seconds{type=\"$type\"} //p" "$prom")"
      if [ "${at:-0}" = 0 ]; then
        if [ "$type" = full ]; then block "no full backup"; else ok "no $type backup yet"; fi
      else
        ok "newest $type backup: $(date -u -d "@$at" +%Y-%m-%dT%H:%MZ) ($(ago $((now - at))) ago)"
      fi
    done
    age="$(metric rag_backup_age_seconds)"
    if [ "${age:-0}" -gt $((max_backup_age * 3600)) ]; then
      block "the newest backup is $(ago "$age") old (more than $max_backup_age h): run deploy/backup-postgres.sh --type full first"
    fi
  fi
fi

# ---- Key escrow ----------------------------------------------------------------------------------------------------
section "Key escrow"
if [ -z "$escrow_dir" ]; then
  warn "not checked: pass --escrow-dir (deploy/escrow-keys.sh's DEST) to see the newest set"
else
  newest="$(find "$escrow_dir" -maxdepth 1 -type d -name "kalla-keys-$project-*" 2>/dev/null | sort | tail -n 1)"
  if [ -z "$newest" ]; then warn "no key set of project $project in $escrow_dir: run deploy/escrow-keys.sh $escrow_dir"
  else ok "newest set: $(basename "$newest") (check it with deploy/escrow-keys.sh --verify $escrow_dir)"; fi
fi

# ---- Host units ----------------------------------------------------------------------------------------------------
section "Host units"
units=("$unit_dir"/rag-*.service)
if [ ! -e "${units[0]}" ]; then
  warn "no host units in $unit_dir (deploy/host/install.sh): no scheduled backups, firewall or restarts"
else
  targets="$(for u in "${units[@]}"; do sed -n 's/^ExecStart=\([^ ]*\).*/\1/p' "$u" | xargs -r dirname; done | sed 's#/host$##' | sort | uniq -c)"
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
    for t in rag-backup.timer rag-backup-status.timer; do
      if systemctl is-active --quiet "$t" 2>/dev/null; then ok "$t is active"; else warn "$t is not active"; fi
    done
  fi
fi

finish
