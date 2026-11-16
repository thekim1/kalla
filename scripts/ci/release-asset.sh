#!/usr/bin/env bash
# Downloads one asset of a release of this repository with curl and jq (the runners have no gh):
#   scripts/ci/release-asset.sh TAG ASSET-NAME OUT-FILE
# Needs GH_TOKEN (contents: read) and GITHUB_REPOSITORY.
set -euo pipefail
tag="$1"; name="$2"; out="$3"
api="https://api.github.com/repos/${GITHUB_REPOSITORY:?}"
auth=(-H "Authorization: Bearer ${GH_TOKEN:?}" -H "X-GitHub-Api-Version: 2022-11-28")
id="$(curl -fsSL "${auth[@]}" "$api/releases/tags/$tag" | jq -r --arg n "$name" '.assets[] | select(.name == $n) | .id')"
[ -n "$id" ] || { echo "error: release $tag has no asset $name" >&2; exit 1; }
mkdir -p "$(dirname "$out")"
curl -fsSL "${auth[@]}" -H "Accept: application/octet-stream" -o "$out" "$api/releases/assets/$id"
[ -s "$out" ] || { echo "error: $name of $tag is empty" >&2; exit 1; }
