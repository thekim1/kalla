#!/usr/bin/env bash
# Builds the deployment bundle attached to a GitHub release (plans/platform/repo-and-deployment.md §7.2, as the
# gateway's): the files a Docker host needs to run the production Compose files, with every image default pinned to
# the released image by digest.
#
#   API_IMAGE=... ADMIN_IMAGE=... WORKER_IMAGE=... MIGRATIONS_IMAGE=... PARSER_IMAGE=... CLAMAV_IMAGE=... \
#   POSTGRES_IMAGE=... deploy/build-release-bundle.sh VERSION OUTPUT_DIR
#
# Each *_IMAGE is a full reference pinned by digest (registry/repo[:tag]@sha256:<64 hex>); an unset or unpinned one
# fails. Writes OUTPUT_DIR/ume-rag-VERSION.tar.gz: one top-level directory ume-rag-VERSION/ holding deploy/ (the Compose
# files, the deployment scripts, postgres/, pgbackrest/, host/, the ClamAV confs init-deployment.sh copies, and redis/,
# proxy/ and otel/ once they exist) and docs/ (the operator documents), so the documented commands work unchanged.
#
# Every ${X_IMAGE:-default} (or ${X_IMAGE:?message}) in the bundled Compose files becomes ${X_IMAGE:-<released image>},
# so the variables still override the release. The bundle fails when a Compose file names an image variable that is
# not listed here and whose default is not pinned by digest (OPENWEBUI_IMAGE is pinned in the repository), when an
# image: line has neither a variable nor a digest, or when a listed variable is used by no Compose file.
set -euo pipefail

usage="usage: build-release-bundle.sh VERSION OUTPUT_DIR (with API_IMAGE ... POSTGRES_IMAGE set)"
version="${1:?$usage}"
out="${2:?$usage}"
die() { echo "error: $*" >&2; exit 1; }

images=(API_IMAGE ADMIN_IMAGE WORKER_IMAGE MIGRATIONS_IMAGE PARSER_IMAGE CLAMAV_IMAGE POSTGRES_IMAGE)
# A reference pinned by digest; the character set also keeps it safe in the sed replacement below.
pinned='^[a-z0-9][a-z0-9._/:-]*@sha256:[0-9a-f]{64}$'

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "VERSION must be X.Y.Z or X.Y.Z-pre, not '$version'"
for var in "${images[@]}"; do
  ref="${!var:-}"
  [ -n "$ref" ] || die "$var is not set"
  [[ "$ref" =~ $pinned ]] || die "$var=$ref is not pinned by digest (expected <image>@sha256:<64 hex>)"
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
name="ume-rag-$version"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
dest="$stage/$name/deploy"
mkdir -p "$dest" "$stage/$name/docs"

cd "$here"
cp compose.prod.yaml compose.ingestion.yaml compose.host-24g.yaml compose.clamav-conf.yaml compose.openwebui.yaml \
  init-deployment.sh backup-postgres.sh backup-uploads.sh restore-pitr.sh escrow-keys.sh "$dest/"
# redis/ is P2, proxy/ and otel/ come with their items: copied once they exist.
for dir in postgres pgbackrest redis proxy otel host; do
  [ ! -d "$dir" ] || cp -R "$dir" "$dest/"
done
# init-deployment.sh writes its ClamAV confs (24 GB profile, --freshclam-proxy) from deploy/clamav/ in the bundle.
mkdir -p "$dest/clamav"
cp "$repo/images/clamav/clamd.conf" "$repo/images/clamav/freshclam.conf" "$dest/clamav/"
# The operator documents (platform plan §10: the upgrade guide ships with the release).
cp "$repo/docs/production-setup.md" "$repo/docs/runbook.md" "$repo/docs/upgrade-guide.md" "$stage/$name/docs/"

cd "$dest"
# Line endings matter: these files are mounted into Linux containers or run by bash.
find . -type f -exec sed -i 's/\r$//' {} +
find . -name '*.sh' -exec chmod 0755 {} +

compose_files=(compose*.yaml)
for var in "${images[@]}"; do
  ref="${!var}"
  grep -q "\${${var}[:}]" "${compose_files[@]}" || die "$var is used by none of the bundled Compose files"
  sed -i -E 's#\$\{'"$var"':[-?][^}]*\}#${'"$var"':-'"$ref"'}#g' "${compose_files[@]}"
done

# What is left must be pinned: every image variable's default and every literal image.
bad=0
while IFS= read -r hit; do
  file="${hit%%:*}"; use="${hit#*:}"
  var="${use#\$\{}"; var="${var%%[:\}]*}"; default="${use#*:-}"; default="${default%\}}"
  if [ "$default" = "$use" ]; then
    echo "error: $file: $use has no default image" >&2; bad=1
  elif [[ ! "$default" =~ $pinned ]]; then
    echo "error: $file: \${$var:-$default} was not rewritten to a digest" >&2; bad=1
  fi
done < <(grep -HoE '\$\{[A-Z0-9_]+_IMAGE(:[-?][^}]*)?\}' "${compose_files[@]}")
while IFS= read -r hit; do
  echo "error: ${hit%%:*}: image without a variable or digest: ${hit#*:}" >&2; bad=1
done < <(grep -HE '^\s*image:' "${compose_files[@]}" | grep -vF "\${" | grep -vE '@sha256:[0-9a-f]{64}\s*$' || true)
[ "$bad" = 0 ] || exit 1

cd "$stage"
tar --sort=name --owner=0 --group=0 --numeric-owner -cf - "$name" | gzip -n > "$out/$name.tar.gz"
echo "Wrote $out/$name.tar.gz"
