#!/usr/bin/env bash
# release.yml's four Dockerfile images (postgres, clamav, renderer, parser), built and pushed at the same time (#1084 R1):
# `start` sets the four `docker buildx build --push` going in the background and returns at once, so the admin UI build and
# the .NET publishes run beside them; `wait` waits for all four, prints each build's log, fails if one failed, and writes
# each image's digest to $GITHUB_OUTPUT (postgres=sha256:..., as docker/build-push-action's digest output was).
#
# One after another the four took 10.6 to 12.2 minutes in a tag run and 4.4 in a dry run, the parser alone 4.5 (#1084).
# The builds share the job's buildx builder (docker/setup-buildx-action), which runs concurrent builds.
#
# Usage: scripts/ci/release-images.sh start|wait
# Environment: VERSION, PREFIX (<registry>/<repository>, e.g. ghcr.io/owner/kalla), REVISION (the commit), RUNNER_TEMP,
# and the build cache, one of:
# - CACHE_REGISTRY (a tag release): a registry cache, $CACHE_REGISTRY:<image> (ghcr.io/<owner>/kalla/buildcache), read and
#   written. Only release jobs (packages: write) can write it; pull-request jobs share the runner user's home, so a tag
#   release reads no local cache (Decisions "CI on the self-hosted runner", #704, #1084 R4a). A failed cache write
#   doesn't fail the build.
# - BUILD_CACHE (a dry run): the local cache, one directory per image, read when there; every build writes its new cache
#   to $BUILD_CACHE/<image>-new (the job's "Rotate the build cache" swaps it in).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
dir="${RUNNER_TEMP:?}/release-images"
# name:context:dockerfile (empty: the context's Dockerfile)
images=(postgres:.:images/postgres/Dockerfile clamav:images/clamav: renderer:images/renderer: parser:src/parser:)

start() {
  : "${VERSION:?}" "${PREFIX:?}" "${REVISION:?}"
  [ -n "${CACHE_REGISTRY:-}" ] || : "${BUILD_CACHE:?CACHE_REGISTRY or BUILD_CACHE}"
  rm -rf "$dir"; mkdir -p "$dir"
  for entry in "${images[@]}"; do
    IFS=: read -r name context file <<< "$entry"
    args=(--push --provenance=false --progress=plain --metadata-file "$dir/$name.json"
      --build-arg "VERSION=$VERSION" --build-arg "REVISION=$REVISION" -t "$PREFIX/$name:$VERSION")
    [ -z "$file" ] || args+=(-f "$file")
    if [ -n "${CACHE_REGISTRY:-}" ]; then
      # image-manifest and oci-mediatypes: the cache as an OCI image manifest, the form every registry stores.
      args+=(--cache-from "type=registry,ref=$CACHE_REGISTRY:$name"
        --cache-to "type=registry,ref=$CACHE_REGISTRY:$name,mode=max,image-manifest=true,oci-mediatypes=true,ignore-error=true")
    else
      mkdir -p "$BUILD_CACHE"
      args+=(--cache-to "type=local,dest=$BUILD_CACHE/$name-new,mode=max")
      [ ! -d "$BUILD_CACHE/$name" ] || args+=(--cache-from "type=local,src=$BUILD_CACHE/$name")
    fi
    echo "docker buildx build ${args[*]} $context"
    # Every stream redirected, or the runner would wait for the background build before ending the step; the exit code
    # goes to a file, since a later step can't `wait` for a process this one started (as the job's Syft step).
    (
      docker buildx build "${args[@]}" "$context" > "$dir/$name.log" 2>&1 || rc=$?
      echo "${rc:-0}" > "$dir/$name.exit"
    ) < /dev/null > /dev/null 2>&1 &
  done
}

wait_all() {
  local deadline=$((SECONDS + 1800)) failed=0
  for entry in "${images[@]}"; do
    name="${entry%%:*}"
    until [ -s "$dir/$name.exit" ]; do
      [ "$SECONDS" -lt "$deadline" ] || { echo "::error::kalla/$name did not finish building within 30 minutes"; tail -50 "$dir/$name.log"; exit 1; }
      sleep 5
    done
    status="$(cat "$dir/$name.exit")"
    echo "::group::kalla/$name (exit $status)"; cat "$dir/$name.log"; echo "::endgroup::"
    if [ "$status" != 0 ]; then echo "::error::kalla/$name failed to build (exit $status)"; failed=1; continue; fi
    digest="$(jq -r '."containerimage.digest" // empty' "$dir/$name.json")"
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "::error::no digest for kalla/$name in $dir/$name.json"; failed=1; continue; }
    echo "kalla/$name $digest"
    echo "$name=$digest" >> "${GITHUB_OUTPUT:-/dev/stdout}"
  done
  exit "$failed"
}

case "${1:-}" in
  start) start ;;
  wait) wait_all ;;
  *) echo "usage: $0 start|wait" >&2; exit 2 ;;
esac
