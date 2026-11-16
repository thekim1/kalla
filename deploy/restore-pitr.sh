#!/usr/bin/env bash
# Point-in-time restore of ragdb from the pgBackRest repository (plans/platform/repo-and-deployment.md §7.4):
#   1. archives the current WAL segment, if rag-postgres is running, so the newest changes are in the repository;
#   2. stops rag-api, rag-admin (their second slots too, with --rolling) and rag-worker, then rag-postgres;
#   3. `pgbackrest restore --type=time --delta` in a one-off container of the rag-postgres service (`docker compose run
#      --rm`, so the same image, user, volumes and repository settings) on the rag-postgres-data volume;
#   4. starts Postgres, which recovers to the target and pauses there; prints the last transaction it replayed;
#   5. promotes it (a new timeline; the old one stays in the repository), checks WAL archiving with `pgbackrest check`;
#   6. starts the services it stopped and waits until they are healthy; prints the timings (the achieved RTO).
# The restore replaces the current database. Everything archived before it stays restorable (WAL up to the moment of
# step 1), but nothing written after step 1 is. Take a full backup once the restored database is checked.
#
#   deploy/restore-pitr.sh --target-time '2026-10-30 21:15:00+01:00' --confirm
#
# Options:
#   --target-time TIME   the moment to restore to, in any form `date -d` reads; give a UTC offset (without one the
#                        host's time zone applies). Transactions committed up to and including TIME are kept.
#   --confirm            required: without it the script prints what it would do and stops
#   --set LABEL          restore from this backup set instead of the newest one that ended before TIME
#   --env-file FILE      [deploy/.env] the deployment (its COMPOSE_FILE and project)
#   --timeout SECONDS    [10800] how long recovery may take before the script gives up
#
# After it: the worker's orphan audit reports blobs without a referencing row and documents whose derived blob is
# missing (§7.4 rule 2); restore missing uploads from the upload copy (backup-uploads.sh).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; target=""; confirm=0; set_label=""; timeout=10800

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --target-time) target="${2:-}"; shift 2;;
    --confirm) confirm=1; shift;;
    --set) set_label="${2:-}"; shift 2;;
    --env-file) env_file="${2:-}"; shift 2;;
    --timeout) timeout="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
[ -n "$target" ] || die "--target-time is required"
[ -r "$env_file" ] || die "cannot read $env_file: pass --env-file"
case "$timeout" in ''|*[!0-9]*) die "--timeout must be a number of seconds";; esac
target_epoch="$(date -d "$target" +%s 2>/dev/null)" || die "cannot read the time '$target'"
# Normalised with an explicit offset, so Postgres reads the same moment whatever its own time zone is.
target_pg="$(date -d "@$target_epoch" '+%Y-%m-%d %H:%M:%S%:z')"
[ "$target_epoch" -le "$(date +%s)" ] || die "the target time $target_pg is in the future"

dc() { docker compose --env-file "$env_file" "$@"; }
sql() { dc exec -T rag-postgres psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d ragdb -c "$1"; }
stamp() { date +%s.%N; }
since() { awk -v a="$1" -v b="$2" 'BEGIN { printf "%7.1f s", b - a }'; }

project="$(dc config | sed -n 's/^name: //p')"
[ -n "$project" ] || die "cannot render the Compose project from $env_file"
mapfile -t defined < <(dc config --services)
running="$(dc ps --services --status running)"
services=()
for s in rag-api rag-api-b rag-admin rag-admin-b rag-worker; do
  if printf '%s\n' "${defined[@]}" | grep -qx "$s" && grep -qx "$s" <<<"$running"; then services+=("$s"); fi
done

cat <<EOF
Point-in-time restore of ragdb in Compose project '$project'
  target time:   $target_pg (inclusive)
  backup set:    ${set_label:-the newest that ended before the target}
  stops:         ${services[*]:-(no services running)} rag-postgres
  restores:      the rag-postgres-data volume, in place (--delta); everything after the target is discarded
EOF
if [ "$confirm" != 1 ]; then
  echo
  echo "Nothing done. This replaces the current database: re-run with --confirm to restore." >&2
  exit 1
fi

t0="$(stamp)"
if grep -qx rag-postgres <<<"$running"; then
  # Close the segment in progress and wait until it is archived, so the restore can reach the newest commits.
  seg="$(sql "SELECT pg_walfile_name(pg_switch_wal())" 2>/dev/null || true)"
  if [ -n "$seg" ]; then
    for _ in $(seq 1 60); do
      last="$(sql "SELECT coalesce(left(last_archived_wal, 24), '') FROM pg_stat_archiver")"
      if [[ "$last" > "$seg" || "$last" == "$seg" ]]; then break; fi
      sleep 1
    done
    [[ "$last" > "$seg" || "$last" == "$seg" ]] || echo "warning: WAL segment $seg was not archived within 60 s" >&2
  fi
fi
if [ ${#services[@]} -gt 0 ]; then dc stop "${services[@]}"; fi
dc stop rag-postgres
t_stopped="$(stamp)"

restore=(--stanza=ragdb --delta --type=time "--target=$target_pg" --target-action=pause --log-level-console=info)
[ -z "$set_label" ] || restore+=("--set=$set_label")
dc run --rm --no-deps -T --entrypoint pgbackrest rag-postgres "${restore[@]}" restore
t_restored="$(stamp)"

dc up -d --no-deps rag-postgres
deadline=$(( $(date +%s) + timeout ))
state=""
until [ "$state" = paused ] || [ "$state" = promoted ]; do
  [ "$(date +%s)" -lt "$deadline" ] || die "recovery did not reach the target within $timeout s (docker compose logs rag-postgres)"
  # Recovery that ends before the target (no WAL that far) is fatal in Postgres: the container restarts in a loop.
  if dc logs --since "$(date -d "@${t_restored%.*}" --iso-8601=seconds)" rag-postgres 2>/dev/null \
      | grep -q 'recovery ended before configured recovery target was reached'; then
    die "the repository has no WAL up to $target_pg: pick an earlier time (pgbackrest info shows the archive range)"
  fi
  state="$(sql "SELECT CASE WHEN NOT pg_is_in_recovery() THEN 'promoted' ELSE pg_get_wal_replay_pause_state() END" \
    2>/dev/null || true)"
  [ "$state" = paused ] || [ "$state" = promoted ] || sleep 2
done
t_recovered="$(stamp)"
replayed="$(sql "SELECT coalesce(pg_last_xact_replay_timestamp()::text, 'none')")"
echo "recovery paused at the target; last replayed transaction committed at: $replayed"

if [ "$state" = paused ]; then sql "SELECT pg_promote(true, 300)" >/dev/null; fi
for _ in $(seq 1 150); do
  [ "$(sql "SELECT pg_is_in_recovery()")" = f ] && break
  sleep 2
done
[ "$(sql "SELECT pg_is_in_recovery()")" = f ] || die "rag-postgres did not leave recovery after pg_promote()"
# The control file shows the new timeline only after the next checkpoint; the current WAL file name has it now.
timeline="$(sql "SELECT ('x' || left(pg_walfile_name(pg_current_wal_lsn()), 8))::bit(32)::int")"
t_promoted="$(stamp)"
echo "promoted: timeline $timeline"
dc exec -T rag-postgres pgbackrest --stanza=ragdb check
t_checked="$(stamp)"

if [ ${#services[@]} -gt 0 ]; then dc up -d --wait "${services[@]}"; fi
t_done="$(stamp)"

cat <<EOF

Restore complete.
  target time:              $target_pg
  last replayed commit:     $replayed
  timeline:                 $timeline
  archive and stop:         $(since "$t0" "$t_stopped")
  pgbackrest restore:       $(since "$t_stopped" "$t_restored")
  recovery to the target:   $(since "$t_restored" "$t_recovered")
  promote:                  $(since "$t_recovered" "$t_promoted")
  pgbackrest check:         $(since "$t_promoted" "$t_checked")
  services healthy:         $(since "$t_checked" "$t_done")
  total (RTO):              $(since "$t0" "$t_done")
Next: check the data (§7.4 rule 3), then take a full backup:
  $here/backup-postgres.sh --type full --env-file $env_file
EOF
