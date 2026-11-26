#!/usr/bin/env bash
# Upload copy (plans/platform/repo-and-deployment.md §7.4): copies new files below uploads/ of the blob store (the only
# content that cannot be fetched again) to the backup target. rag-upload-backup.timer runs it every 10 minutes, so an
# upload is copied within 15. Blob keys are write-once (data-model §9), so the copy is incremental and never
# overwrites: a file already in the target is skipped, and each new file is written under a temporary name and renamed,
# so an interrupted run leaves no partial file behind a final name. Deletions are not propagated: the copy keeps a
# purged upload (purges follow the 35-day backup retention); prune the copy with the same retention.
#
#   deploy/backup-uploads.sh [--env-file FILE] [--state-dir DIR]
#
# It runs the rag-upload-backup service of compose.backup.yaml (profile backup: a shell as uid 1654, read-only blob
# volume, no network) with KALLA_UPLOAD_BACKUP_DIR from deploy/.env mounted as the target: a directory on the backup
# share, or the volume kalla-upload-copy with --backup-target local. An S3 target is not built yet: replicate the bucket's
# uploads/ prefix (bucket replication or rclone) once the blob store moves to S3 (§6.4).
#
# Options:
#   --env-file FILE    [deploy/.env] the deployment
#   --state-dir DIR    [$STATE_DIRECTORY, else /var/lib/kalla] where upload-backup.prom goes:
#                      rag_upload_backup_last_success_timestamp_seconds, rag_upload_backup_copied (this run),
#                      rag_upload_backup_files (in the copy); alert when the last success is older than 30 minutes
set -euo pipefail
umask 027

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The deployment's env file (Decisions "Compose host layout and upgrades"): the state directory's, else deploy/.env (a
# clone, or a host whose env file init-deployment.sh hasn't moved yet). --env-file wins.
state_dir="${STATE_DIRECTORY:-/var/lib/kalla}"
env_file="${KALLA_STATE_DIR:-/etc/kalla}/.env"; [ -e "$env_file" ] || [ ! -e "$here/.env" ] || env_file="$here/.env"

usage() { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) env_file="${2:-}"; shift 2;;
    --state-dir) state_dir="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
[ -r "$env_file" ] || die "cannot read $env_file: run deploy/init-deployment.sh first, or pass --env-file"
# KEY's last value in deploy/.env, its quotes removed (init-deployment.sh writes KEY='value'); read, never sourced.
env_value() { sed -n "s/^$1=//p" "$env_file" | tail -n 1 | sed -e "s/^'\(.*\)'$/\1/" -e 's/^"\(.*\)"$/\1/'; }
case "$(env_value KALLA_BACKUP_TARGET)" in
  "") die "KALLA_BACKUP_TARGET is empty in $env_file: no backup target, so no upload copy";;
  s3://*) die "an S3 backup target has no upload copy yet";;
esac
target="$(env_value KALLA_UPLOAD_BACKUP_DIR)"
[ -z "$target" ] || [ -d "$target" ] || die "the upload copy's directory $target does not exist (is the backup share mounted?)"
target="${target:-the volume kalla-upload-copy}"
mkdir -p "$state_dir"

# Inside the container: /data/blobs/rag is the blob store's root (BlobStore:Root), .staging/ holds writes in progress
# and is never copied. Prints the number of files copied, then the number in the copy.
# shellcheck disable=SC2016 # expanded by the container's shell
copy='
set -eu
src=/data/blobs/rag/uploads
copied=0
if [ -d "$src" ]; then
  cd "$src"
  find . -type f > /tmp/files
  while IFS= read -r f; do
    f="${f#./}"
    [ -e "/backup/uploads/$f" ] && continue
    mkdir -p "/backup/uploads/$(dirname "$f")"
    cp -p "$f" "/backup/uploads/$f.partial"
    mv "/backup/uploads/$f.partial" "/backup/uploads/$f"
    copied=$((copied + 1))
  done < /tmp/files
fi
mkdir -p /backup/uploads
echo "$copied $(find /backup/uploads -type f ! -name "*.partial" | wc -l)"'

result="$(docker compose --env-file "$env_file" run --rm --no-deps -T --entrypoint sh rag-upload-backup -c "$copy")"
read -r copied total <<<"$result"
now="$(date +%s)"
tmp="$(mktemp "$state_dir/.upload-backup.prom.XXXXXX")"
cat > "$tmp" <<EOF
# HELP rag_upload_backup_last_success_timestamp_seconds When the upload copy last completed.
# TYPE rag_upload_backup_last_success_timestamp_seconds gauge
rag_upload_backup_last_success_timestamp_seconds $now
# HELP rag_upload_backup_copied Files copied by the last run.
# TYPE rag_upload_backup_copied gauge
rag_upload_backup_copied $copied
# HELP rag_upload_backup_files Files in the upload copy.
# TYPE rag_upload_backup_files gauge
rag_upload_backup_files $total
EOF
chmod 0644 "$tmp"
mv -f "$tmp" "$state_dir/upload-backup.prom"
echo "upload copy: $copied new, $total in $target"
