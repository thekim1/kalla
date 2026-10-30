#!/usr/bin/env bash
# Scheduled pgBackRest backup of ragdb (plans/platform/repo-and-deployment.md §7.4), inside the running postgres
# container (`docker compose exec`, no sidecar), followed by the health metrics of §7.4 rule 5. rag-backup.timer runs it
# daily with --type auto; rag-backup-status.timer runs --status-only every two minutes.
#
#   deploy/backup-postgres.sh --type auto|full|diff [--start-fast]
#   deploy/backup-postgres.sh --status-only
#
# Options:
#   --type auto|full|diff   auto: full on Sundays or when the newest full backup is 7 days old or more (or there is
#                           none), else differential. Expiry (35-day window, deploy/pgbackrest/pgbackrest.conf) follows
#                           each backup. A diff without a full becomes a full (pgBackRest does that itself).
#   --start-fast            an immediate checkpoint instead of a spread one (drills; the timer leaves it off)
#   --status-only           no backup: refresh the metrics only
#   --env-file FILE         [deploy/.env] the deployment (its COMPOSE_FILE and project)
#   --state-dir DIR         [$STATE_DIRECTORY, else /var/lib/ume-rag] where backup.prom and backup-last-run go
#
# Metrics (Prometheus text format in DIR/backup.prom, written atomically; node_exporter's textfile collector or the
# OpenTelemetry Collector reads it, to verify (§14)). Every value is read from pgBackRest's repository and from
# pg_stat_archiver at run time, so a status run reports a backup taken by any means:
#   rag_backup_age_seconds                        now minus the end of the newest backup of any type (rag.backup.age)
#   rag_backup_last_timestamp_seconds{type}       end of the newest full, diff and incr backup (0: none)
#   rag_backup_repo_status                        pgBackRest's stanza status code (0 ok, 2 no valid backup, 99 the
#                                                 repository could not be read)
#   rag_backup_wal_archive_age_seconds            now minus the newest archived WAL while unarchived WAL exists, 0 when
#                                                 everything written is archived (rag.backup.wal_archive_age)
#   rag_backup_wal_last_archived_timestamp_seconds, rag_backup_wal_archive_failed_total,
#   rag_backup_wal_last_failed_timestamp_seconds  from pg_stat_archiver
#   rag_backup_last_run_success, rag_backup_last_run_timestamp_seconds{type}
#                                                 the outcome of the last scheduled backup (this script)
# Alerts (proposed, §7.4 rule 5):
#   - rag_backup_age_seconds > 93600 (26 h);
#   - rag-backup.service failed (systemd), or rag_backup_last_run_success == 0;
#   - rag_backup_wal_archive_age_seconds > 300 (the 5-minute RPO) for 2 minutes. With archive_timeout 60 s a busy
#     server reads at most about 60 s; the 2 minutes absorb the first write after an idle spell, which reads as the
#     time since the previous archived segment until the next segment switch;
#   - rag_backup_repo_status != 0, or rag_backup_status_timestamp_seconds older than 10 minutes (the status timer
#     stopped).
set -euo pipefail
umask 027

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; state_dir="${STATE_DIRECTORY:-/var/lib/ume-rag}"
type=""; start_fast=0; status_only=0

usage() { sed -n '2,38p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --type) type="${2:-}"; shift 2;;
    --start-fast) start_fast=1; shift;;
    --status-only) status_only=1; shift;;
    --env-file) env_file="${2:-}"; shift 2;;
    --state-dir) state_dir="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
if [ "$status_only" = 1 ]; then
  [ -z "$type" ] || die "--status-only takes no --type"
else
  case "$type" in auto|full|diff) ;; *) die "--type auto, full or diff is required (or --status-only)";; esac
fi
[ -r "$env_file" ] || die "cannot read $env_file: run deploy/init-deployment.sh first, or pass --env-file"
mkdir -p "$state_dir"

dc() { docker compose --env-file "$env_file" "$@"; }

# One line from inside the container: now, the stanza status code, the newest full/diff/incr stop times, then
# pg_stat_archiver (last archived time, archive age, failures). pgBackRest reports times as Unix epochs; a repository it
# cannot read gives status 99. The archive age is 0 when the current WAL position is still in the newest archived
# segment, so an idle server does not look like a failing archive.
read_status() {
  # shellcheck disable=SC2016 # expanded by the container's shell
  dc exec -T postgres sh -c '
    info="$(pgbackrest --stanza=ragdb --output=json info 2>/dev/null)" || info="[{\"status\":{\"code\":99}}]"
    exec psql -X -q -A -t -F " " -v ON_ERROR_STOP=1 -U postgres -d ragdb -v info="$info" -f -' <<'SQL'
WITH s AS (SELECT (:'info'::json) -> 0 AS j),
b AS (SELECT e ->> 'type' AS type, (e -> 'timestamp' ->> 'stop')::bigint AS stop
      FROM s, json_array_elements(coalesce(s.j -> 'backup', '[]'::json)) AS e)
SELECT extract(epoch FROM now())::bigint,
       coalesce((SELECT j -> 'status' ->> 'code' FROM s), '99'),
       coalesce((SELECT max(stop) FROM b WHERE type = 'full'), 0),
       coalesce((SELECT max(stop) FROM b WHERE type = 'diff'), 0),
       coalesce((SELECT max(stop) FROM b WHERE type = 'incr'), 0),
       coalesce(extract(epoch FROM a.last_archived_time)::bigint, 0),
       CASE WHEN a.last_archived_wal IS NULL OR pg_is_in_recovery() THEN -1
            WHEN left(a.last_archived_wal, 24) >= pg_walfile_name(pg_current_wal_lsn()) THEN 0
            ELSE extract(epoch FROM now() - a.last_archived_time)::bigint END,
       a.failed_count,
       coalesce(extract(epoch FROM a.last_failed_time)::bigint, 0)
FROM pg_stat_archiver AS a;
SQL
}

write_metrics() { # write_metrics STATUS-LINE
  local now code full diff incr archived archive_age failed last_failed newest run_ok=1 run_ts=0 run_type=none
  read -r now code full diff incr archived archive_age failed last_failed <<<"$1"
  newest="$full"; [ "$diff" -gt "$newest" ] && newest="$diff"; [ "$incr" -gt "$newest" ] && newest="$incr"
  if [ -r "$state_dir/backup-last-run" ]; then read -r run_ok run_ts run_type < "$state_dir/backup-last-run"; fi
  local tmp; tmp="$(mktemp "$state_dir/.backup.prom.XXXXXX")"
  cat > "$tmp" <<EOF
# HELP rag_backup_age_seconds Seconds since the end of the newest pgBackRest backup of any type.
# TYPE rag_backup_age_seconds gauge
rag_backup_age_seconds $((now - newest))
# HELP rag_backup_last_timestamp_seconds End of the newest pgBackRest backup by type (0: none).
# TYPE rag_backup_last_timestamp_seconds gauge
rag_backup_last_timestamp_seconds{type="full"} $full
rag_backup_last_timestamp_seconds{type="diff"} $diff
rag_backup_last_timestamp_seconds{type="incr"} $incr
# HELP rag_backup_repo_status pgBackRest stanza status code (0 ok).
# TYPE rag_backup_repo_status gauge
rag_backup_repo_status $code
# HELP rag_backup_wal_archive_age_seconds Seconds since the newest archived WAL while unarchived WAL exists (-1: unknown).
# TYPE rag_backup_wal_archive_age_seconds gauge
rag_backup_wal_archive_age_seconds $archive_age
# HELP rag_backup_wal_last_archived_timestamp_seconds When the newest WAL file was archived (pg_stat_archiver).
# TYPE rag_backup_wal_last_archived_timestamp_seconds gauge
rag_backup_wal_last_archived_timestamp_seconds $archived
# HELP rag_backup_wal_archive_failed_total Failed archive_command runs since the statistics were reset.
# TYPE rag_backup_wal_archive_failed_total counter
rag_backup_wal_archive_failed_total $failed
# HELP rag_backup_wal_last_failed_timestamp_seconds When archive_command last failed (0: never).
# TYPE rag_backup_wal_last_failed_timestamp_seconds gauge
rag_backup_wal_last_failed_timestamp_seconds $last_failed
# HELP rag_backup_last_run_success Whether the last scheduled backup run succeeded.
# TYPE rag_backup_last_run_success gauge
rag_backup_last_run_success{type="$run_type"} $run_ok
# HELP rag_backup_last_run_timestamp_seconds When the last scheduled backup run ended (0: none yet).
# TYPE rag_backup_last_run_timestamp_seconds gauge
rag_backup_last_run_timestamp_seconds $run_ts
# HELP rag_backup_status_timestamp_seconds When these values were read.
# TYPE rag_backup_status_timestamp_seconds gauge
rag_backup_status_timestamp_seconds $now
EOF
  chmod 0644 "$tmp"
  mv -f "$tmp" "$state_dir/backup.prom"
}

if [ "$status_only" = 1 ]; then
  status="$(read_status)" || die "could not read the backup status from the postgres container"
  write_metrics "$status"
  exit 0
fi

# A failed run is recorded as such, and the metrics are refreshed when the database still answers.
finish() {
  local rc=$?
  [ "$rc" = 0 ] && return
  echo "0 $(date +%s) ${type}" > "$state_dir/backup-last-run"
  if status="$(read_status 2>/dev/null)"; then write_metrics "$status"; fi
  echo "backup failed (exit $rc)" >&2
}
trap finish EXIT

status="$(read_status)" || die "could not read the backup status from the postgres container (is it running?)"
if [ "$type" = auto ]; then
  read -r now _ full _ <<<"$status"
  if [ "$(date +%u)" = 7 ] || [ $((now - full)) -ge $((7 * 86400)) ]; then type="full"; else type="diff"; fi
fi

echo "pgBackRest $type backup of stanza ragdb"
args=(--stanza=ragdb "--type=$type" --log-level-console=info)
[ "$start_fast" = 1 ] && args+=(--start-fast)
dc exec -T postgres pgbackrest "${args[@]}" backup
dc exec -T postgres pgbackrest --stanza=ragdb info

echo "1 $(date +%s) $type" > "$state_dir/backup-last-run"
status="$(read_status)" || die "the backup succeeded but the status could not be read"
write_metrics "$status"
echo "metrics: $state_dir/backup.prom"
