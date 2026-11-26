#!/usr/bin/env bash
# Installs the host units of deploy/host/ (plans/platform/repo-and-deployment.md §6.1, §6.2, §7.4; README.md here):
# the ingestion slice, the firewall, and the backup, backup-status, upload-copy, restart-unhealthy and certificate-expiry
# timers. Safe to re-run (after an upgrade or a change of KALLA_HOST_MEMORY). It enables what deploy/.env's COMPOSE_FILE
# uses: the slice, the firewall and the restart-unhealthy timer with compose.hardened.yaml (run it before the first
# `docker compose up` then: the ingestion containers need the slice), the backup timers with compose.backup.yaml, the
# certificate check always.
#
# The units act on the deployment named in host.env in the state directory (KALLA_STATE_DIR, default /etc/kalla;
# Decisions "Compose host layout and upgrades"): KALLA_DEPLOY_DIR, this deploy/ directory, and COMPOSE_PROJECT_NAME,
# the Compose project. This script writes it, so after an upgrade the units run the new bundle's scripts against the
# same project, and a project that isn't called kalla is never missed.
#
#   sudo deploy/host/install.sh [--host-memory 16|24] [--project NAME] [--no-enable]
#   sudo deploy/host/install.sh --check
#
# Options:
#   --host-memory 16|24   [KALLA_HOST_MEMORY in deploy/.env, else 16] MemoryMax of rag-ingestion.slice: 7G or 11G
#   --project NAME        [COMPOSE_PROJECT_NAME in deploy/.env, else the only Källa project on this host, else kalla]
#                         the Compose project the units act on
#   --deploy-dir DIR      [the directory above this script] the deployment's deploy/ directory
#   --unit-dir DIR        [/etc/systemd/system] where the units go; another directory only writes them, with host.env
#                         beside them unless KALLA_STATE_DIR is set (CI, review)
#   --no-enable           write the units and reload systemd, but enable and start nothing
#   --check               change nothing: exit 1 unless the installed units are this bundle's and host.env names this
#                         deploy/ directory and a project with a kalla-postgres container
# KALLA_STATE_DIR comes from the environment, else from deploy/.env.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy_dir="$(dirname "$here")"; unit_dir=/etc/systemd/system; host_memory=""; enable=1; project=""; check=0

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --host-memory) host_memory="${2:-}"; shift 2;;
    --project) project="${2:-}"; [ -n "$project" ] || die "--project needs a name"; shift 2;;
    --deploy-dir) deploy_dir="${2:-}"; shift 2;;
    --unit-dir) unit_dir="${2:-}"; shift 2;;
    --no-enable) enable=0; shift;;
    --check) check=1; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
deploy_dir="$(cd "$deploy_dir" && pwd)"
env_file="$deploy_dir/.env"
# KEY's last value in deploy/.env, its quotes removed (init-deployment.sh writes KEY='value'); read, never sourced.
env_value() { { sed -n "s/^$1=//p" "$env_file" 2>/dev/null || true; } | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'; }
[ -n "$host_memory" ] || host_memory="$(env_value KALLA_HOST_MEMORY)"
host_memory="${host_memory:-16}"
case "$host_memory" in 16) memory_max=7G;; 24) memory_max=11G;; *) die "--host-memory must be 16 or 24";; esac
system=0; [ "$unit_dir" = /etc/systemd/system ] && system=1
if [ "$system" = 1 ]; then
  [ "$(id -u)" = 0 ] || [ "$check" = 1 ] || die "run as root"
  [ -r "$env_file" ] || die "cannot read $env_file: run deploy/init-deployment.sh first (as root for a mode-600 file)"
fi
state_dir="${KALLA_STATE_DIR:-$(env_value KALLA_STATE_DIR)}"
if [ -z "$state_dir" ]; then if [ "$system" = 1 ]; then state_dir=/etc/kalla; else state_dir="$unit_dir"; fi; fi
host_env="$state_dir/host.env"

# The project: --project, deploy/.env's, else the only one with a kalla-postgres container (on this host's units only:
# CI's daemon runs other tests' projects), else compose.rag.yaml's name.
running=""
if [ "$system" = 1 ] && command -v docker >/dev/null; then
  running="$(docker ps -a --filter label=com.docker.compose.service=kalla-postgres \
    --format '{{.Label "com.docker.compose.project"}}' 2>/dev/null | sort -u | paste -sd' ' || true)"
fi
[ -n "$project" ] || project="$(env_value COMPOSE_PROJECT_NAME)"
if [ -z "$project" ]; then
  case "$running" in
    "") project=kalla;;
    *" "*) die "more than one Källa project here ($running): pass --project, or set COMPOSE_PROJECT_NAME in deploy/.env";;
    *) project="$running";;
  esac
fi

units=(rag-ingestion.slice rag-firewall.service rag-firewall.timer rag-backup.service rag-backup.timer
  rag-backup-status.service rag-backup-status.timer rag-upload-backup.service rag-upload-backup.timer
  rag-restart-unhealthy.service rag-restart-unhealthy.timer rag-cert-expiry.service rag-cert-expiry.timer)
render() { sed -e "s|@DEPLOY_DIR@|$deploy_dir|g" -e "s|@STATE_DIR@|$state_dir|g" "$here/$1"; }
host_env_text() {
  printf '# Written by deploy/host/install.sh: the deployment the host units act on (Decisions "Compose host layout and\n'
  printf '# upgrades"). Re-run install.sh from a new bundle after an upgrade; change nothing here by hand.\n'
  printf "KALLA_DEPLOY_DIR='%s'\nCOMPOSE_PROJECT_NAME='%s'\n" "$deploy_dir" "$project"
}

if [ "$check" = 1 ]; then
  bad=0
  fail() { echo "  differs  $*"; bad=1; }
  for u in "${units[@]}"; do
    if [ ! -e "$unit_dir/$u" ]; then fail "$u: not installed in $unit_dir"
    elif ! cmp -s <(render "$u") "$unit_dir/$u"; then fail "$u: not this bundle's (re-run install.sh)"; fi
  done
  hv() { sed -n "s/^$1=//p" "$host_env" 2>/dev/null | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/"; }
  if [ ! -r "$host_env" ]; then fail "$host_env: missing, so the units have no deployment (re-run install.sh)"
  else
    [ "$(hv KALLA_DEPLOY_DIR)" = "$deploy_dir" ] || fail "$host_env: the units run from $(hv KALLA_DEPLOY_DIR), not $deploy_dir"
    [ "$(hv COMPOSE_PROJECT_NAME)" = "$project" ] || fail "$host_env: the units act on project $(hv COMPOSE_PROJECT_NAME), not $project"
    if [ "$system" = 1 ] && command -v docker >/dev/null && [[ " $running " != *" $(hv COMPOSE_PROJECT_NAME) "* ]]; then
      fail "$host_env: project $(hv COMPOSE_PROJECT_NAME) has no kalla-postgres container (Källa projects here: ${running:-none})"
    fi
  fi
  if [ "$bad" = 0 ]; then echo "ok: ${#units[@]} units from $deploy_dir, acting on project $project ($host_env)"; exit 0; fi
  exit 1
fi

mkdir -p "$unit_dir" "$state_dir"
for u in "${units[@]}"; do
  render "$u" > "$unit_dir/$u.tmp"
  chmod 0644 "$unit_dir/$u.tmp"
  mv -f "$unit_dir/$u.tmp" "$unit_dir/$u"
done
host_env_text > "$host_env.tmp"; chmod 0644 "$host_env.tmp"; mv -f "$host_env.tmp" "$host_env"
dropin="$unit_dir/rag-ingestion.slice.d/50-host-memory.conf"
if [ "$memory_max" = 7G ]; then
  rm -f "$dropin"; rmdir "$(dirname "$dropin")" 2>/dev/null || true
else
  mkdir -p "$(dirname "$dropin")"
  printf '# Written by deploy/host/install.sh: a %s GB host (repo-and-deployment §6.2).\n[Slice]\nMemoryMax=%s\n' \
    "$host_memory" "$memory_max" > "$dropin"
  chmod 0644 "$dropin"
fi
echo "installed ${#units[@]} units in $unit_dir (rag-ingestion.slice MemoryMax=$memory_max), acting on project $project from $deploy_dir ($host_env)"
[ "$system" = 1 ] || exit 0

systemctl daemon-reload
if [ "$(docker info --format '{{.CgroupDriver}} {{.CgroupVersion}}' 2>/dev/null)" != "systemd 2" ]; then
  echo "warning: Docker does not use the systemd cgroup driver on cgroup v2 ($(docker info --format \
'{{.CgroupDriver}} {{.CgroupVersion}}' 2>/dev/null || echo unknown)): rag-ingestion.slice will not cap the ingestion \
zone (to-verify #90). Set \"exec-opts\": [\"native.cgroupdriver=systemd\"] in /etc/docker/daemon.json." >&2
fi
[ "$enable" = 1 ] || exit 0
compose_file="$(env_value COMPOSE_FILE)"
timers=(rag-cert-expiry.timer)
if [[ "$compose_file" == *compose.hardened.yaml* ]]; then
  timers+=(rag-firewall.timer rag-restart-unhealthy.timer)
  systemctl enable rag-firewall.service
  systemctl start rag-firewall.service
else
  echo "the firewall and rag-restart-unhealthy.timer not enabled: COMPOSE_FILE has no compose.hardened.yaml" >&2
fi
if [[ "$compose_file" == *compose.backup.yaml* ]]; then
  timers+=(rag-backup.timer rag-backup-status.timer)
  case "$(env_value KALLA_BACKUP_TARGET)" in
    s3://*) echo "rag-upload-backup.timer not enabled: an S3 backup target has no upload copy yet" >&2 ;;
    *) timers+=(rag-upload-backup.timer) ;;
  esac
else
  echo "the backup timers not enabled: COMPOSE_FILE has no compose.backup.yaml" >&2
fi
systemctl enable --now "${timers[@]}"
systemctl list-timers 'rag-*' --no-pager
