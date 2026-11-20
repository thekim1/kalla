#!/usr/bin/env bash
# Builds the deployment bundle attached to a GitHub release (plans/platform/repo-and-deployment.md §7.2, as the
# gateway's): the files a Docker host needs to run deploy/compose.rag.yaml and its overlays, with every image default
# pinned to the released image by digest.
#
#   KALLA_API_IMAGE=... KALLA_ADMIN_IMAGE=... KALLA_WORKER_IMAGE=... KALLA_MIGRATIONS_IMAGE=... KALLA_PARSER_IMAGE=... \
#   KALLA_CLAMAV_IMAGE=... KALLA_RENDERER_IMAGE=... KALLA_POSTGRES_IMAGE=... deploy/build-release-bundle.sh VERSION OUTPUT_DIR
#
# Each RAG_*_IMAGE (the variables of the Portainer stack and of a release's images.env) is a full reference pinned by
# digest (registry/repo[:tag]@sha256:<64 hex>); an unset or unpinned one fails. Writes OUTPUT_DIR/kalla-VERSION.tar.gz:
# one top-level directory kalla-VERSION/ holding deploy/ (the Compose files, the deployment scripts, the bootstrap and
# the static files compose.rag.yaml names, host/, kubernetes/ and the rest), docs/ (the operator documents) and the
# files the overlays name relative to deploy/ (images/renderer/seccomp.json, images/clamav/clamd.conf and
# freshclam.conf), so the documented commands work unchanged.
#
# Every ${X_IMAGE:-default} (or ${X_IMAGE:?message}) in the bundled Compose files becomes ${X_IMAGE:-<released image>},
# so the variables still override the release. The bundle fails when a Compose file names an image variable that is
# not listed here and whose default is not pinned by digest (OPENWEBUI_IMAGE and KALLA_PROXY_IMAGE are pinned in the
# repository), when an image: line has neither a variable nor a digest, or when a listed variable is used by no Compose
# file.
set -euo pipefail

usage="usage: build-release-bundle.sh VERSION OUTPUT_DIR (with KALLA_API_IMAGE ... KALLA_POSTGRES_IMAGE set)"
version="${1:?$usage}"
out="${2:?$usage}"
die() { echo "error: $*" >&2; exit 1; }

images=(KALLA_API_IMAGE KALLA_ADMIN_IMAGE KALLA_WORKER_IMAGE KALLA_MIGRATIONS_IMAGE KALLA_PARSER_IMAGE KALLA_CLAMAV_IMAGE
  KALLA_RENDERER_IMAGE KALLA_POSTGRES_IMAGE)
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
name="kalla-$version"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
dest="$stage/$name/deploy"
mkdir -p "$dest" "$stage/$name/docs" "$stage/$name/images/renderer" "$stage/$name/images/clamav"

cd "$here"
cp compose.rag.yaml compose.backup.yaml compose.hardened.yaml compose.host-24g.yaml compose.clamav-conf.yaml \
  compose.rolling.yaml init-deployment.sh rolling-update.sh backup-postgres.sh backup-uploads.sh restore-pitr.sh \
  escrow-keys.sh "$dest/"
# compose.rag.yaml's static files (postgres/, openwebui/, proxy/) and its default for the host directories (empty/);
# redis/ is P2: copied once it exists.
for dir in postgres pgbackrest redis proxy otel host kubernetes openwebui keycloak upgrade empty; do
  [ ! -d "$dir" ] || cp -R "$dir" "$dest/"
done
# The bootstrap (kalla-bootstrap, and kubernetes/create-secrets.sh) and the integration manifest a gateway on the same
# host creates Källa's credential keys from (init-deployment.sh --gateway-secrets-dir), at their paths in the
# repository. The Portainer stack file carries its own copies.
mkdir -p "$dest/portainer"
cp portainer/integration-manifest.json portainer/bootstrap.sh "$dest/portainer/"
# compose.clamav-conf.yaml and compose.hardened.yaml name these relative to deploy/.
cp "$repo/images/clamav/clamd.conf" "$repo/images/clamav/freshclam.conf" "$stage/$name/images/clamav/"
cp "$repo/images/renderer/seccomp.json" "$stage/$name/images/renderer/"
# The operator documents (platform plan §10: the upgrade guide ships with the release).
cp "$repo/docs/production-setup.md" "$repo/docs/runbook.md" "$repo/docs/upgrade-guide.md" "$repo/docs/test-host.md" \
  "$repo/docs/zero-downtime-updates.md" "$repo/docs/compose-quick-start.md" "$repo/docs/portainer.md" \
  "$repo/docs/kubernetes.md" "$repo/docs/keycloak.md" "$repo/docs/network-and-ports.md" \
  "$repo/docs/security-events.md" "$stage/$name/docs/"

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

# Kubernetes (docs/kubernetes.md): the manifests keep their local image names; an images transformer in the base and in
# every component (a base's transformer does not reach a component's resources) maps them to the released images.
if [ -d kubernetes ]; then
  images_block="$(echo "images:"
    for var in "${images[@]}"; do
      ref="${!var}"; repo="${ref%@*}"; repo="${repo%:*}"; digest="${ref##*@}"; svc="${var#RAG_}"; svc="${svc%_IMAGE}"
      echo "  - {name: kalla/${svc,,}, newName: $repo, digest: $digest}"
    done)"
  for k in kubernetes/base/kustomization.yaml kubernetes/components/*/kustomization.yaml; do
    printf '%s\n' "$images_block" >> "$k"
  done
fi

# The Portainer stack (the gateway's stack plus RAG, deploy/portainer/compose.portainer.yaml) as a release asset of its
# own, with RAG's RAG_*_IMAGE defaults pinned to this release.
portainer="$here/portainer/compose.portainer.yaml"
if [ -f "$portainer" ]; then
  sed 's/\r$//' "$portainer" > "$out/compose.portainer.yaml"
  for var in "${images[@]}"; do
    ref="${!var}"
    grep -q "\${${var}:-" "$out/compose.portainer.yaml" || die "$var is not used in $portainer"
    sed -i -E "s#\\\$\\{${var}:-[^}]*\\}#\${${var}:-${ref}}#g" "$out/compose.portainer.yaml"
  done
  echo "Wrote $out/compose.portainer.yaml"
fi

cd "$stage"
tar --sort=name --owner=0 --group=0 --numeric-owner -cf - "$name" | gzip -n > "$out/$name.tar.gz"
echo "Wrote $out/$name.tar.gz"
