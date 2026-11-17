#!/usr/bin/env bash
# Key escrow (plans/platform/repo-and-deployment.md §7.4): one set with everything a restored deployment needs that no
# backup holds, the same in every form. rag-bootstrap generates these once and derives the rest from them:
#   rag-secrets/.pepper                   the key pepper (the hashes of API keys)
#   rag-secrets/keys/rag-blob-*           the blob master keys (rag-chat-<level> join once A1 allows stored chat content)
#   rag-secrets/pgbackrest/cipher-pass    the pgBackRest passphrase, with a backup target
#   rag-certs/data-protection.pfx         the Data Protection certificate (sign-in cookies, stored OIDC secrets)
#   rag-certs/ca.pem, rag-certs/ca.key    the internal CA: the services' certificates, and the trust in them, stay
# Keep each set as long as the data and backups it protects, sealed in two places: the files are in the clear. Escrow
# again after every rotation.
#
#   deploy/escrow-keys.sh DEST              writes DEST/ume-rag-keys-<project>-<UTC time>/ with SHA256SUMS and MANIFEST
#   deploy/escrow-keys.sh --verify PATH     PATH is one set, or DEST (its newest set): checks the checksums, the formats,
#                                           and that every file equals the deployment's
#   deploy/escrow-keys.sh --restore PATH    writes the set into a new deployment before its first start, and refuses to
#                                           replace a file that differs; rag-bootstrap derives the rest when it starts
#
# The deployment, one of:
#   --env-file FILE       [deploy/.env] a Compose installation: its project's volumes
#   --project NAME        a Portainer stack (its name) or any Compose project: the volumes NAME_rag-secrets and
#                         NAME_rag-certs, read with the image of the project's rag-bootstrap container
#   --secrets-dir DIR --certs-dir DIR    directories instead of volumes (Kubernetes: create-secrets.sh's output)
# Options:
#   --image IMAGE         the image whose shell reads and writes the volumes (--restore before the stack's first
#                         deploy, when no rag-bootstrap container exists yet: the release's rag-bootstrap image)
# Needs openssl. Exit status 0 when the export, the restore or every check succeeded. Run it as a user who may run
# docker.
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; project=""; secrets=""; certs=""; image=""; mode="export"; dest=""

usage() { sed -n '2,27p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --verify|--restore) [ -z "$dest" ] || usage 1; mode="${1#--}"; dest="${2:-}"; shift 2;;
    --env-file) env_file="${2:-}"; shift 2;;
    --project) project="${2:-}"; shift 2;;
    --secrets-dir) secrets="${2:-}"; shift 2;;
    --certs-dir) certs="${2:-}"; shift 2;;
    --image) image="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    -*) echo "unknown option: $1" >&2; usage 1;;
    *) [ -z "$dest" ] || usage 1; dest="$1"; shift;;
  esac
done
[ -n "$dest" ] || usage 1
if [ -n "$secrets$certs" ] && { [ -z "$secrets" ] || [ -z "$certs" ]; }; then die "--secrets-dir and --certs-dir go together"; fi
dest="$(realpath -m "$dest")"
[ -z "$secrets" ] || { secrets="$(realpath -m "$secrets")"; certs="$(realpath -m "$certs")"; }

# The deployment: two directories, or a project's two volumes reached through a container of $image.
if [ -n "$secrets" ]; then
  source_name="$secrets and $certs"; project="${project:-ume-rag}"; image=""
else
  if [ -z "$project" ]; then
    [ -r "$env_file" ] || die "cannot read $env_file: pass --env-file, --project, or --secrets-dir and --certs-dir"
    project="$(docker compose --env-file "$env_file" config | sed -n 's/^name: //p')"
    [ -n "$project" ] || die "cannot render the Compose project from $env_file"
    [ -n "$image" ] || image="$(docker compose --env-file "$env_file" config --images rag-bootstrap)"
  fi
  if [ -z "$image" ]; then
    image="$(docker ps -a --filter "label=com.docker.compose.project=$project" \
      --filter label=com.docker.compose.service=rag-bootstrap --format '{{.Image}}' | head -n 1)"
  fi
  [ -n "$image" ] || die "no rag-bootstrap container in the project $project: pass --image (the release's rag-bootstrap image)"
  source_name="the volumes ${project}_rag-secrets and ${project}_rag-certs"
  if [ "$mode" != restore ]; then
    for v in rag-secrets rag-certs; do
      docker volume inspect "${project}_$v" >/dev/null 2>&1 || die "no volume ${project}_$v: is $project the project's (the stack's) name?"
    done
  fi
fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
current="$work/current"; mkdir -p "$current/rag-secrets" "$current/rag-certs"
secret_files='.pepper keys/rag-blob-* keys/rag-chat-* pgbackrest/cipher-pass pgbackrest/repo.conf'
cert_files='ca.pem ca.key data-protection.pfx'

# The deployment's files of the set (and repo.conf, for --verify) into $current; none when it has none yet.
read_current() {
  if [ -z "$image" ]; then
    # shellcheck disable=SC2086 # the globs expand here
    (cd "$secrets" && for f in $secret_files; do [ ! -f "$f" ] || cp --parents "$f" "$current/rag-secrets/"; done)
    # shellcheck disable=SC2086
    (cd "$certs" && for f in $cert_files; do [ ! -f "$f" ] || cp "$f" "$current/rag-certs/"; done)
    return 0
  fi
  # shellcheck disable=SC2016 # expanded by the container's shell
  docker run --rm --network none -v "${project}_rag-secrets:/rag-secrets:ro" -v "${project}_rag-certs:/rag-certs:ro" \
    --entrypoint /bin/sh "$image" -c '
      cd /rag-secrets && for f in '"$secret_files"'; do [ ! -f "$f" ] || echo "rag-secrets/$f"; done > /tmp/list
      cd /rag-certs && for f in '"$cert_files"'; do [ ! -f "$f" ] || echo "rag-certs/$f"; done >> /tmp/list
      [ ! -s /tmp/list ] || { cd / && tar -cf - -T /tmp/list; }' > "$work/current.tar" || die "cannot read $source_name"
  [ ! -s "$work/current.tar" ] || tar -xf "$work/current.tar" -C "$current"
}

# A set's files, relative to its root (repo.conf is never part of one).
set_files() { (cd "$1" && find rag-secrets rag-certs -type f ! -name repo.conf 2>/dev/null | LC_ALL=C sort); }

check_format() { # check_format NAME FILE
  case "$1" in
    rag-secrets/keys/*)
      local n; n="$(base64 -d < "$2" 2>/dev/null | wc -c)" || n=0
      [ "$n" = 32 ] || { echo "FAIL $1: not 32 bytes of base64 ($n)"; return 1; };;
    rag-certs/ca.pem)
      openssl x509 -noout -in "$2" 2>/dev/null || { echo "FAIL $1: not a certificate"; return 1; };;
    rag-certs/ca.key)
      openssl pkey -noout -in "$2" 2>/dev/null || { echo "FAIL $1: not a private key"; return 1; };;
    rag-certs/data-protection.pfx)
      openssl pkcs12 -noout -passin pass: -in "$2" 2>/dev/null || { echo "FAIL $1: not a PKCS #12 file"; return 1; };;
    *)
      [ -s "$2" ] || { echo "FAIL $1: empty"; return 1; };;
  esac
}

required="rag-secrets/.pepper rag-secrets/keys/rag-blob-internal rag-certs/data-protection.pfx rag-certs/ca.pem"

if [ "$mode" = export ]; then
  [ -d "$dest" ] || die "$dest is not a directory (mount the escrow media first)"
  read_current
  for f in $required; do [ -f "$current/$f" ] || die "no $f in $source_name: has rag-bootstrap run?"; done
  mapfile -t files < <(set_files "$current")
  for f in "${files[@]}"; do check_format "$f" "$current/$f" || die "refusing to escrow a malformed file"; done
  set_dir="$dest/ume-rag-keys-$project-$(date -u +%Y%m%dT%H%M%SZ)"
  [ ! -e "$set_dir" ] || die "$set_dir already exists"
  mkdir -m 0700 "$set_dir"
  (cd "$current" && cp --parents "${files[@]}" "$set_dir/")
  {
    echo "Ume Rag key escrow (repo-and-deployment §7.4)"
    echo "project:  $project"
    echo "host:     $(hostname -f 2>/dev/null || hostname)"
    echo "created:  $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "source:   $source_name"
    echo
    echo "Each file goes back to its path in the volume it is named after (Kubernetes: --secrets-dir, --certs-dir),"
    echo "before the deployment's first start: deploy/escrow-keys.sh --restore <this directory> plus the deployment."
    [ -f "$current/rag-certs/ca.key" ] || echo "rag-certs/ca.key was not in the deployment (kept offline): keep that copy with this set."
  } > "$set_dir/MANIFEST"
  (cd "$set_dir" && sha256sum "${files[@]}" MANIFEST > SHA256SUMS)
  find "$set_dir" -type f -exec chmod 0400 {} +
  find "$set_dir" -type d -exec chmod 0500 {} +
  sync -f "$set_dir/SHA256SUMS" 2>/dev/null || sync
  echo "escrowed ${#files[@]} files to $set_dir"
  (cd "$set_dir" && sha256sum "${files[@]}") | sed 's/^/  /'
  [ -f "$current/rag-certs/ca.key" ] || echo "warning: no rag-certs/ca.key in $source_name: keep its offline copy with the set"
  echo "Next: $0 --verify $set_dir, then seal the media."
  exit 0
fi

# --verify and --restore: one set, or the newest set below PATH.
set_dir="$dest"
if [ ! -f "$set_dir/SHA256SUMS" ]; then
  set_dir="$(find "$dest" -mindepth 1 -maxdepth 1 -type d -name "ume-rag-keys-*" | LC_ALL=C sort | tail -n 1)"
  [ -n "$set_dir" ] || die "no escrow set ume-rag-keys-* in $dest"
fi
(cd "$set_dir" && sha256sum --quiet -c SHA256SUMS) || die "the checksums of $set_dir do not match"
mapfile -t files < <(set_files "$set_dir")

if [ "$mode" = restore ]; then
  for f in $required; do [ -f "$set_dir/$f" ] || die "$set_dir has no $f: not a complete escrow set"; done
  for f in "${files[@]}"; do check_format "$f" "$set_dir/$f" || die "refusing to restore a malformed file"; done
  if [ -n "$image" ]; then
    # Volumes made here carry the project's labels, so Compose and Portainer take them as their own.
    for v in rag-secrets rag-certs; do
      docker volume inspect "${project}_$v" >/dev/null 2>&1 || docker volume create \
        --label "com.docker.compose.project=$project" --label "com.docker.compose.volume=$v" "${project}_$v" >/dev/null
    done
  else
    mkdir -p "$secrets" "$certs"
  fi
  read_current
  for f in "${files[@]}"; do
    if [ -f "$current/$f" ] && ! cmp -s "$current/$f" "$set_dir/$f"; then
      die "$f in $source_name differs from the set: restore before the first start, into new volumes or directories"
    fi
  done
  if [ -z "$image" ]; then
    for f in "${files[@]}"; do
      target="$secrets"; [ "${f%%/*}" = rag-certs ] && target="$certs"
      mkdir -p "$target/$(dirname "${f#*/}")"; cp "$set_dir/$f" "$target/${f#*/}"; chmod 0600 "$target/${f#*/}"
    done
  else
    # rag-bootstrap gives each file its owner when it starts; until then they are root's.
    # shellcheck disable=SC2016 # expanded by the container's shell
    (cd "$set_dir" && tar -cf - "${files[@]}") | docker run --rm -i --network none \
      -v "${project}_rag-secrets:/rag-secrets" -v "${project}_rag-certs:/rag-certs" --entrypoint /bin/sh "$image" -c '
        cd / && tar -xf - && for f in '"${files[*]}"'; do chown 0:0 "/$f" && chmod 0600 "/$f" || exit 1; done' \
      || die "cannot write into $source_name"
  fi
  echo "restored ${#files[@]} files from $set_dir into $source_name"
  echo "Next: start the deployment (rag-bootstrap derives the rest), then $0 --verify $set_dir."
  exit 0
fi

echo "verifying $set_dir"
read_current
failed=0
for f in "${files[@]}"; do
  check_format "$f" "$set_dir/$f" || { failed=1; continue; }
  if [ ! -f "$current/$f" ]; then
    if [ "$f" = rag-certs/ca.key ]; then echo "ok   $f: not in the deployment (kept offline)"; continue; fi
    echo "FAIL $f: not in the deployment"; failed=1
  elif cmp -s "$set_dir/$f" "$current/$f"; then
    echo "ok   $f: equals the deployment's"
  else
    echo "FAIL $f: differs from the deployment's (rotated since this escrow? escrow again)"; failed=1
  fi
done
for f in $(set_files "$current"); do
  [ -f "$set_dir/$f" ] || { echo "FAIL $f: not in the escrow set (escrow again)"; failed=1; }
done
# The passphrase pgBackRest actually uses is the one in repo.conf.
if [ -f "$current/rag-secrets/pgbackrest/repo.conf" ] && [ -f "$set_dir/rag-secrets/pgbackrest/cipher-pass" ]; then
  if [ "$(sed -n 's/^repo1-cipher-pass=//p' "$current/rag-secrets/pgbackrest/repo.conf")" = "$(cat "$set_dir/rag-secrets/pgbackrest/cipher-pass")" ]; then
    echo "ok   rag-secrets/pgbackrest/cipher-pass: equals repo1-cipher-pass in repo.conf"
  else
    echo "FAIL rag-secrets/pgbackrest/cipher-pass: differs from repo1-cipher-pass in repo.conf"; failed=1
  fi
fi
if [ "$failed" = 0 ]; then echo "PASS: the escrow set reads back and matches"; else echo "escrow verification failed" >&2; fi
exit "$failed"
