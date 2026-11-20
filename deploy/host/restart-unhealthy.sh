#!/usr/bin/env bash
# Restarts every container labelled kalla.restart-unhealthy=true that Docker reports unhealthy
# (plans/platform/repo-and-deployment.md §6.2; PR #31 deviation 6). Docker only restarts a container whose main process
# exits, and ClamAV's entrypoint never does, so a hung clamd would stay unhealthy. rag-restart-unhealthy.timer runs this
# every minute; a container is unhealthy only after its health check failed its retries, and is "starting" again (not
# restarted) during its start period, so a slow start does not loop.
#
#   deploy/host/restart-unhealthy.sh [--dry-run]
set -euo pipefail

dry_run=0
case "${1:-}" in
  "") ;;
  --dry-run) dry_run=1;;
  *) sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 1;;
esac

docker ps --filter label=kalla.restart-unhealthy=true --filter health=unhealthy --format '{{.ID}} {{.Names}}' |
  while read -r id name; do
    echo "restarting unhealthy container $name ($id)"
    [ "$dry_run" = 1 ] || docker restart "$id" >/dev/null
  done
