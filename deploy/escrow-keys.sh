#!/usr/bin/env bash
# Key escrow (plans/platform/repo-and-deployment.md §7.4, platform/plan.md §8a): exports the long-lived keys that no
# backup can replace to escrow media, and reads them back. They are read from the deployment's rag-secrets volume,
# read-only, through `docker run` with rag-bootstrap's image:
#   - keys/rag-blob-*: the blob master keys of the file key provider (rag-blob-internal; rag-chat-<level> joins once A1
#     allows stored chat content). Without them the `internal` blobs cannot be decrypted (data-model §7).
#   - pgbackrest/cipher-pass, with a backup target (compose.backup.yaml): the passphrase of the pgBackRest repository.
#     Without it no backup can be read.
# Keep them as long as the data and backups they protect; re-run after every rotation. The media must be sealed (two
# places, or the key service's own backup): the files are written in the clear, readable by the owner only.
#
#   deploy/escrow-keys.sh DEST            writes DEST/ume-rag-keys-<project>-<UTC time>/ with SHA256SUMS and MANIFEST
#   deploy/escrow-keys.sh --verify PATH   PATH is one escrow set, or DEST (its newest set): checks the checksums, the
#                                         key formats, and that every key equals this deployment's current one
#
# Options:
#   --env-file FILE      [deploy/.env] the deployment (its Compose project, whose rag-secrets volume is read)
#   --secrets-dir DIR    a secrets directory instead of the volume (the output of Kubernetes' create-secrets.sh)
# Exit status: 0 when the export or every check succeeded. Run it as a user who may run docker.
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; secrets=""; verify=0; dest=""

usage() { sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --verify) verify=1; dest="${2:-}"; shift 2;;
    --env-file) env_file="${2:-}"; shift 2;;
    --secrets-dir) secrets="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) [ -z "$dest" ] || usage 1; dest="$1"; shift;;
  esac
done
[ -n "$dest" ] || usage 1
project=ume-rag; source_name="$secrets"
if [ -z "$secrets" ]; then
  # The keys out of the volume, read-only, into a directory only this user can read; removed at the end.
  [ -r "$env_file" ] || die "cannot read $env_file: pass --env-file or --secrets-dir"
  dc() { docker compose --env-file "$env_file" "$@"; }
  project="$(dc config | sed -n 's/^name: //p')"
  image="$(dc config --images rag-bootstrap)"
  [ -n "$project" ] && [ -n "$image" ] || die "cannot render the Compose project from $env_file"
  secrets="$(mktemp -d)"; trap 'rm -rf "$secrets"' EXIT
  source_name="the volume ${project}_rag-secrets"
  # shellcheck disable=SC2016 # expanded by the container's shell
  docker run --rm --network none -v "${project}_rag-secrets:/s:ro" --entrypoint /bin/sh "$image" -c \
    'cd /s && tar -cf - keys $(ls pgbackrest/cipher-pass pgbackrest/repo.conf 2>/dev/null)' | tar -xf - -C "$secrets" \
    || die "cannot read $source_name"
fi

# The keys of this deployment, relative to the secrets directory.
keys=()
for f in "$secrets"/keys/rag-blob-* "$secrets"/keys/rag-chat-*; do
  [ -e "$f" ] && keys+=("keys/${f##*/}")
done
[ ! -e "$secrets/pgbackrest/cipher-pass" ] || keys+=(pgbackrest/cipher-pass)
[ ${#keys[@]} -gt 0 ] || die "no key in $source_name: has rag-bootstrap run?"
for k in "${keys[@]}"; do
  [ -r "$secrets/$k" ] || die "cannot read $secrets/$k (run as root)"
done

check_format() { # check_format NAME FILE
  case "$1" in
    keys/rag-blob-*|keys/rag-chat-*)
      local n; n="$(base64 -d < "$2" 2>/dev/null | wc -c)" || n=0
      [ "$n" = 32 ] || { echo "FAIL $1: not 32 bytes of base64 ($n)"; return 1; };;
    pgbackrest/cipher-pass)
      [ -s "$2" ] || { echo "FAIL $1: empty"; return 1; };;
  esac
}

if [ "$verify" = 0 ]; then
  [ -d "$dest" ] || die "$dest is not a directory (mount the escrow media first)"
  set_dir="$dest/ume-rag-keys-$project-$(date -u +%Y%m%dT%H%M%SZ)"
  [ ! -e "$set_dir" ] || die "$set_dir already exists"
  mkdir -m 0700 "$set_dir"
  for k in "${keys[@]}"; do
    check_format "$k" "$secrets/$k" || die "refusing to escrow a malformed key"
    mkdir -p "$set_dir/$(dirname "$k")"
    cp "$secrets/$k" "$set_dir/$k"
  done
  {
    echo "Ume Rag key escrow (repo-and-deployment §7.4)"
    echo "project:  $project"
    echo "host:     $(hostname -f 2>/dev/null || hostname)"
    echo "created:  $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "source:   $source_name"
    echo
    echo "keys/rag-blob-*        blob master keys of the file key provider (base64, 32 bytes): copy back to keys/ in"
    echo "                       the rag-secrets volume, mode 0400, owner 1654"
    echo "pgbackrest/cipher-pass pgBackRest repo1-cipher-pass: copy back to pgbackrest/ in the rag-secrets volume"
    echo "                       before rag-bootstrap writes repo.conf, mode 0600, owner 999"
  } > "$set_dir/MANIFEST"
  (cd "$set_dir" && sha256sum "${keys[@]}" MANIFEST > SHA256SUMS)
  find "$set_dir" -type f -exec chmod 0400 {} +
  find "$set_dir" -type d -exec chmod 0500 {} +
  sync -f "$set_dir/SHA256SUMS" 2>/dev/null || sync
  echo "escrowed ${#keys[@]} keys to $set_dir"
  (cd "$set_dir" && sha256sum "${keys[@]}") | sed 's/^/  /'
  echo "Next: $0 --verify $set_dir, then seal the media."
  exit 0
fi

# --verify: one set, or the newest set below DEST.
set_dir="$dest"
if [ ! -f "$set_dir/SHA256SUMS" ]; then
  set_dir="$(find "$dest" -mindepth 1 -maxdepth 1 -type d -name "ume-rag-keys-$project-*" | sort | tail -n 1)"
  [ -n "$set_dir" ] || die "no escrow set ume-rag-keys-$project-* in $dest"
fi
echo "verifying $set_dir"
failed=0
(cd "$set_dir" && sha256sum --quiet -c SHA256SUMS) || { echo "FAIL checksums"; failed=1; }
for k in "${keys[@]}"; do
  if [ ! -f "$set_dir/$k" ]; then echo "FAIL $k: not in the escrow set (escrow again)"; failed=1; continue; fi
  check_format "$k" "$set_dir/$k" || { failed=1; continue; }
  if cmp -s "$set_dir/$k" "$secrets/$k"; then
    echo "ok   $k: equals the deployment's key"
  else
    echo "FAIL $k: differs from the deployment's key (rotated since this escrow? escrow again)"; failed=1
  fi
done
# The passphrase pgBackRest actually uses is the one in repo.conf.
if [ -r "$secrets/pgbackrest/repo.conf" ] && [ -f "$set_dir/pgbackrest/cipher-pass" ]; then
  if [ "$(sed -n 's/^repo1-cipher-pass=//p' "$secrets/pgbackrest/repo.conf")" = "$(cat "$set_dir/pgbackrest/cipher-pass")" ]; then
    echo "ok   pgbackrest/cipher-pass: equals repo1-cipher-pass in repo.conf"
  else
    echo "FAIL pgbackrest/cipher-pass: differs from repo1-cipher-pass in repo.conf"; failed=1
  fi
fi
if [ "$failed" = 0 ]; then echo "PASS: the escrow set reads back and matches"; else echo "escrow verification failed" >&2; fi
exit "$failed"
