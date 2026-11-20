#!/usr/bin/env bash
# Installs the host units of deploy/host/ (plans/platform/repo-and-deployment.md §6.1, §6.2, §7.4; README.md here):
# the ingestion slice, the firewall, and the backup, backup-status, upload-copy, restart-unhealthy and certificate-expiry
# timers. Each unit gets this deployment's directory in place of @DEPLOY_DIR@. Safe to re-run (after an upgrade or a
# change of KALLA_HOST_MEMORY). It enables what deploy/.env's COMPOSE_FILE uses: the slice, the firewall and the
# restart-unhealthy timer with compose.hardened.yaml (run it before the first `docker compose up` then: the ingestion
# containers need the slice), the backup timers with compose.backup.yaml, the certificate check always.
#
#   sudo deploy/host/install.sh [--host-memory 16|24] [--no-enable]
#
# Options:
#   --host-memory 16|24   [KALLA_HOST_MEMORY in deploy/.env, else 16] MemoryMax of rag-ingestion.slice: 7G or 11G
#   --deploy-dir DIR      [the directory above this script] the deployment's deploy/ directory
#   --unit-dir DIR        [/etc/systemd/system] where the units go; another directory only writes them (CI, review)
#   --no-enable           write the units and reload systemd, but enable and start nothing
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy_dir="$(dirname "$here")"; unit_dir=/etc/systemd/system; host_memory=""; enable=1

usage() { sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --host-memory) host_memory="${2:-}"; shift 2;;
    --deploy-dir) deploy_dir="${2:-}"; shift 2;;
    --unit-dir) unit_dir="${2:-}"; shift 2;;
    --no-enable) enable=0; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
deploy_dir="$(cd "$deploy_dir" && pwd)"
env_file="$deploy_dir/.env"
# KEY's last value in deploy/.env, its quotes removed (init-deployment.sh writes KEY='value'); read, never sourced.
env_value() { sed -n "s/^$1=//p" "$env_file" 2>/dev/null | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'; }
[ -n "$host_memory" ] || host_memory="$(env_value KALLA_HOST_MEMORY)"
host_memory="${host_memory:-16}"
case "$host_memory" in 16) memory_max=7G;; 24) memory_max=11G;; *) die "--host-memory must be 16 or 24";; esac
system=0; [ "$unit_dir" = /etc/systemd/system ] && system=1
if [ "$system" = 1 ]; then
  [ "$(id -u)" = 0 ] || die "run as root"
  [ -r "$env_file" ] || die "cannot read $env_file: run deploy/init-deployment.sh first"
fi
mkdir -p "$unit_dir"

units=(rag-ingestion.slice rag-firewall.service rag-firewall.timer rag-backup.service rag-backup.timer
  rag-backup-status.service rag-backup-status.timer rag-upload-backup.service rag-upload-backup.timer
  rag-restart-unhealthy.service rag-restart-unhealthy.timer rag-cert-expiry.service rag-cert-expiry.timer)
for u in "${units[@]}"; do
  sed "s|@DEPLOY_DIR@|$deploy_dir|g" "$here/$u" > "$unit_dir/$u.tmp"
  chmod 0644 "$unit_dir/$u.tmp"
  mv -f "$unit_dir/$u.tmp" "$unit_dir/$u"
done
dropin="$unit_dir/rag-ingestion.slice.d/50-host-memory.conf"
if [ "$memory_max" = 7G ]; then
  rm -f "$dropin"; rmdir "$(dirname "$dropin")" 2>/dev/null || true
else
  mkdir -p "$(dirname "$dropin")"
  printf '# Written by deploy/host/install.sh: a %s GB host (repo-and-deployment §6.2).\n[Slice]\nMemoryMax=%s\n' \
    "$host_memory" "$memory_max" > "$dropin"
  chmod 0644 "$dropin"
fi
echo "installed ${#units[@]} units in $unit_dir (rag-ingestion.slice MemoryMax=$memory_max)"
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
