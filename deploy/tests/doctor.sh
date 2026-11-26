#!/usr/bin/env bash
# deploy/doctor.sh, the check before every upgrade (verify.yml deploy-checks; Decisions "Compose host layout and
# upgrades"), offline: `docker` and `systemctl` are stand-ins on PATH that answer from files, so no stack runs.
#
#   - a healthy deployment exits 0, with the project, the services' versions, a bind-backed volume at its device path
#     and a direct bind at its source, the overlays, the stanza, the newest backups and the host units' directory
#   - each blocker exits 1 with its line: a stanza that can't be read, a WAL backlog (by age and by .ready segments), no
#     backup in 26 hours, no full backup, a stale backup.prom that can't be read fresh, no compose.backup.yaml, a project
#     that doesn't run, two projects and no project named
#   - --project or the shell's COMPOSE_PROJECT_NAME picks one of two; a project other than kalla is found and named;
#     --no-backups-ok makes no compose.backup.yaml a warning; the escrow set is the project's own, and a key changed
#     after it warns; a stopped service, the profiles the containers show, a site overlay's name, a registry with a port
#   - exit 3 when docker doesn't answer, 2 on a usage error
#
#   deploy/tests/doctor.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; trap 'rm -r "$work"' EXIT
fake="$work/fake"; mkdir -p "$fake/bin" "$work/deploy" "$work/state" "$work/units" "$work/data/postgres/pg_wal/archive_status" "$work/data/blobs" "$work/uploads" "$work/data/secrets/keys"
cp "$root/deploy/doctor.sh" "$root/deploy/backup-postgres.sh" "$root/deploy/compose.rag.yaml" "$root/deploy/compose.backup.yaml" "$work/deploy/"

# docker: answers what doctor.sh asks from files in $FAKE; `compose` fails (the status comes from backup.prom).
cat > "$fake/bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  info) [ ! -e "$FAKE/no-docker" ];;
  ps)
    filter="$4"
    case "$filter" in
      label=com.docker.compose.service=kalla-postgres) cat "$FAKE/projects";;
      label=com.docker.compose.project=*) p="${filter#label=com.docker.compose.project=}"; cat "$FAKE/containers.$p" 2>/dev/null || true;;
    esac;;
  inspect)
    format="$3"; name="$4"
    case "$format" in
      *Mounts*) cat "$FAKE/mounts.$name" 2>/dev/null || true;;
      *config_files*) cat "$FAKE/config_files";;
    esac;;
  volume) cat "$FAKE/device.$5" 2>/dev/null || echo "<no value>";;
  compose) exit 1;;
  *) echo "fake docker: unexpected $*" >&2; exit 3;;
esac
EOF
# shellcheck disable=SC2016 # expanded by the stand-in
printf '#!/usr/bin/env bash\n[ -e "$FAKE/active.${@: -1}" ]\n' > "$fake/bin/systemctl"
chmod +x "$fake/bin/docker" "$fake/bin/systemctl"
export FAKE="$fake" PATH="$fake/bin:$PATH" KALLA_STATE_DIR="$work/kstate"

d="$work/deploy"
# A deployment: project kalla, the backup and hardened overlays, the data on a host disk.
setup() { # setup PROJECT
  local p="$1"
  printf '%s\n' "$p" > "$fake/projects"
  cat > "$fake/containers.$p" <<EOF
kalla-postgres|$p-kalla-postgres-1|ghcr.io/thekim1/kalla/postgres:0.11.0@sha256:aa|running
kalla-api|$p-kalla-api-1|ghcr.io/thekim1/kalla/api:0.11.0@sha256:bb|running
kalla-bootstrap|$p-kalla-bootstrap-1|ghcr.io/thekim1/kalla/api:0.11.0@sha256:bb|exited
kalla-clamav|$p-kalla-clamav-1|ghcr.io/thekim1/kalla/clamav:0.11.0@sha256:cc|running
EOF
  printf 'volume|%s_kalla-postgres-data|/var/lib/docker/volumes/x/_data|/var/lib/postgresql/data\nbind||/var/run/docker.sock|/var/run/docker.sock\n' "$p" > "$fake/mounts.$p-kalla-postgres-1"
  printf 'volume|%s_kalla-blobs|%s|/blobs\nbind||%s|/uploads\n' "$p" "$work/data/blobs" "$work/uploads" > "$fake/mounts.$p-kalla-api-1"
  printf '%s\n' "$work/data/postgres" > "$fake/device.${p}_kalla-postgres-data"
  printf 'volume|%s_kalla-secrets|%s|/secrets\n' "$p" "$work/data/secrets" > "$fake/mounts.$p-kalla-bootstrap-1"
  touch -d '2026-11-01 08:00' "$work/data/secrets/.pepper" "$work/data/secrets/keys/rag-blob-1"
  echo "$d/compose.rag.yaml,$d/compose.backup.yaml,$d/compose.hardened.yaml" > "$fake/config_files"
  printf "COMPOSE_FILE='compose.rag.yaml:compose.backup.yaml:compose.hardened.yaml'\nCOMPOSE_PROFILES='clamav'\n" > "$d/.env"
  sed -e "s|@DEPLOY_DIR@|$d|g" -e "s|@STATE_DIR@|$work/kstate|g" "$root/deploy/host/rag-backup.service" > "$work/units/rag-backup.service"
  mkdir -p "$work/kstate"; printf "KALLA_DEPLOY_DIR='%s'\nCOMPOSE_PROJECT_NAME='%s'\n" "$d" "$p" > "$work/kstate/host.env"
  touch "$fake/active.rag-firewall.timer" "$fake/active.rag-backup.timer" "$fake/active.rag-backup-status.timer" "$fake/active.rag-upload-backup.timer"
}
prom() { # prom CODE WAL_AGE FULL_AGO DIFF_AGO (seconds; 0 = none)
  local now; now="$(date +%s)"
  local full=0 diff=0; [ "$3" = 0 ] || full=$((now - $3)); [ "$4" = 0 ] || diff=$((now - $4))
  local newest=$full; [ "$diff" -gt "$newest" ] && newest=$diff
  cat > "$work/state/backup.prom" <<EOF
rag_backup_age_seconds $((now - newest))
rag_backup_last_timestamp_seconds{type="full"} $full
rag_backup_last_timestamp_seconds{type="diff"} $diff
rag_backup_last_timestamp_seconds{type="incr"} 0
rag_backup_repo_status $1
rag_backup_wal_archive_age_seconds $2
rag_backup_status_timestamp_seconds $now
EOF
}
run() { # run EXPECTED-EXIT [doctor options]: the output in $out
  local want="$1"; shift; local rc=0
  out="$(bash "$d/doctor.sh" --state-dir "$work/state" --unit-dir "$work/units" "$@" 2>&1)" || rc=$?
  [ "$rc" = "$want" ] || { echo "$out"; echo "FAIL: exit $rc, expected $want ($*)"; exit 1; }
}
has() { grep -qF -- "$1" <<< "$out" || { echo "$out"; echo "FAIL: no line with: $1"; exit 1; }; }
hasnt() { ! grep -qF -- "$1" <<< "$out" || { echo "$out"; echo "FAIL: unexpected: $1"; exit 1; }; }

echo "== healthy"
setup kalla; prom 0 0 7200 0
run 0
has "ok     project kalla (found by its containers' labels)"
has "kalla-postgres           0.11.0       running"
has "kalla-bootstrap          0.11.0       done"
has "the containers show the profiles: clamav"
has "ok     WAL archive: 0 segments waiting"
has "bind-backed volume kalla_kalla-postgres-data → $work/data/postgres:"
has "volume kalla_kalla-blobs → $work/data/blobs:"
has "bind → $work/uploads:"
hasnt "docker.sock"
has "started with: backup hardened rag"
has "ok     stanza ragdb: ok"
has "ok     WAL archive: no backlog"
has "ok     newest full backup:"
has "ok     the units act on project kalla ($work/kstate/host.env)"
has "ok     1 unit runs from $d"
has "ok     rag-firewall.timer is active"
has "ok     rag-upload-backup.timer is active"
has "0 blocking"
sed -i '/kalla-clamav/d' "$fake/containers.kalla"; run 0
has "warn   the containers show the profiles: none (the env file's next \`up\` changes that)"
setup kalla

echo "== blockers"
prom 99 0 7200 0; run 1; has "BLOCK  stanza ragdb: the repository could not be read"
prom 2 0 0 0; run 1; has "BLOCK  stanza ragdb: no valid backup"; has "BLOCK  no full backup"
prom 0 900 7200 0; run 1; has "BLOCK  WAL archive: a backlog"
prom 0 0 $((30 * 3600)) 0; run 1; has "BLOCK  the newest backup is 1 d 6 h old (more than 26 h)"
run 0 --max-backup-age 48
prom 0 0 $((30 * 3600)) 3600; run 0; has "ok     newest diff backup:"
prom 0 0 7200 0
touch "$work/data/postgres/pg_wal/archive_status/"{1,2,3,4,5}.ready
run 1; has "BLOCK  WAL archive: 5 segments wait to be archived"
rm "$work/data/postgres/pg_wal/archive_status/"*.ready
# A status file two days old, written when the backup was 25 h old, and compose fails: not trusted.
prom 0 0 $((25 * 3600)) 0
sed -i "s/^rag_backup_status_timestamp_seconds .*/rag_backup_status_timestamp_seconds $(( $(date +%s) - 2 * 86400 ))/" "$work/state/backup.prom"
run 1; has "BLOCK  the backup status is 2 d 0 h old"; has "BLOCK  the newest backup is 3 d"
printf 'rag_backup_status_timestamp_seconds soon\n' > "$work/state/backup.prom"
run 1; has "BLOCK  the backup status could not be read"
prom 0 0 7200 0
run 1 --project other; has "BLOCK  project other has no kalla-postgres container (Källa projects here: kalla)"

echo "== two projects"
setup kalla; setup second; printf 'kalla\nsecond\n' > "$fake/projects"
run 1; has "BLOCK  more than one Källa project here (kalla second): pass --project"
run 0 --project second; has "ok     project second"; has "volume second_kalla-blobs"
COMPOSE_PROJECT_NAME=second run 0; has "ok     project second"
printf "COMPOSE_PROJECT_NAME='second'\n" >> "$d/.env"; run 0; has "ok     project second"

echo "== another project name, no backups, stale units"
setup ume-rag; prom 0 0 7200 0
printf "COMPOSE_FILE='compose.rag.yaml:/etc/kalla/site.yaml'\nCOMPOSE_PROFILES='clamav'\n" > "$d/.env"; echo "$d/compose.rag.yaml,/etc/kalla/site.yaml" > "$fake/config_files"
sed -i "s|$d|$work/old-bundle/deploy|g" "$work/kstate/host.env"
run 1; has "BLOCK  no compose.backup.yaml: this deployment takes no backups"
run 0 --no-backups-ok
has "warn   project ume-rag (found by its containers' labels): set COMPOSE_PROJECT_NAME=ume-rag"
has "warn   no compose.backup.yaml: this deployment takes no backups (--no-backups-ok)"
has "started with: rag site.yaml"
has "warn   1 unit runs from $work/old-bundle/deploy, which doesn't exist"
hasnt "stanza"
# The units act on another project (the VM's failure: backups that pointed at nothing after a rename).
sed -i "s|^COMPOSE_PROJECT_NAME=.*|COMPOSE_PROJECT_NAME='kalla'|" "$work/kstate/host.env"
run 1 --no-backups-ok; has "BLOCK  the units act on project kalla, not ume-rag"
mv "$work/kstate/host.env" "$work/kstate/host.env.gone"; run 1 --no-backups-ok
has "BLOCK  the units read $work/kstate/host.env, which is missing"; mv "$work/kstate/host.env.gone" "$work/kstate/host.env"
# Units installed before host.env name their scripts' paths.
printf '[Service]\nExecStart=%s/backup-postgres.sh --type auto\n' "$d" > "$work/units/rag-backup.service"
run 0 --no-backups-ok; has "warn   units from before host.env"; has "ok     1 unit runs from $d"

echo "== a stopped service, a registry with a port"
sed -i -e 's/|running$/|exited/' -e 's#ghcr.io/thekim1/kalla/api:0.11.0@#registry.local:5000/kalla/api@#' "$fake/containers.ume-rag"
run 0 --no-backups-ok
has "warn   kalla-postgres           0.11.0       exited"
has "kalla-bootstrap          ?            done"
hasnt "5000"

echo "== escrow"
setup kalla; prom 0 0 7200 0
mkdir -p "$work/escrow/kalla-keys-kalla-20261101T090000Z" "$work/escrow/kalla-keys-kalla-20261120T090000Z" \
  "$work/escrow/kalla-keys-kalla-test-20261124T090000Z" "$work/escrow/kalla-keys-other-20261124T090000Z"
run 0 --escrow-dir "$work/escrow"; has "ok     newest set: kalla-keys-kalla-20261120T090000Z"
has "ok     no key changed after the newest set"
touch -d '2026-11-22 10:00' "$work/data/secrets/keys/rag-blob-1"
run 0 --escrow-dir "$work/escrow"; has "warn   a key changed after the newest set (2026-11-22T"
run 0; has "warn   not checked: pass --escrow-dir"

echo "== no project"
: > "$fake/projects"; run 1; has "BLOCK  no Källa project on this host"
run 2 --max-backup-age soon
run 2 --env-file
touch "$fake/no-docker"; run 3

echo "doctor.sh: all checks passed"
