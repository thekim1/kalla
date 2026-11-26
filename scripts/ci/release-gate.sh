#!/usr/bin/env bash
# release.yml's gate job (Decisions "CI on the self-hosted runner", #1084 R6): a tag run whose commit has a green
# release.yml dry run skips verify.yml and parser.yml, and e2e.yml too when that dry run ran it. It still builds,
# boot-tests and publishes the images.
#
# A dry run counts when it is a workflow_dispatch run of release.yml on the same commit (head_sha) that concluded
# success, its build job succeeded (a portainer_boot run skips build and doesn't count), and it has verify's and parser's
# jobs ("verify / …", "parser / …"), none of them failed or cancelled. It ran the browser tests when it has "e2e / …"
# jobs, none failed or cancelled.
#
# Writes skip_tests=true|false and skip_e2e=true|false to $GITHUB_OUTPUT. Anything else than a tag push, an API error
# or a run's jobs that can't be read gives false for both: the tag run then runs everything, as without a dry run. So
# does a crash of this script: release.yml skips only after a gate that succeeded.
# Needs GH_TOKEN (actions: read), GITHUB_REPOSITORY, GITHUB_SHA, GITHUB_EVENT_NAME; curl and jq (the runners have no gh).
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"
result() { echo "skip_tests=$1" >> "$out"; echo "skip_e2e=$2" >> "$out"; exit 0; }

[ "${GITHUB_EVENT_NAME:-}" = push ] || { echo "not a tag push: nothing is skipped"; result false false; }

api="https://api.github.com/repos/${GITHUB_REPOSITORY:?}"
auth=(-H "Authorization: Bearer ${GH_TOKEN:?}" -H "X-GitHub-Api-Version: 2022-11-28")
get() { curl -fsSL "${auth[@]}" "$api/$1"; }

runs="$(get "actions/workflows/release.yml/runs?event=workflow_dispatch&head_sha=${GITHUB_SHA:?}&status=success&per_page=100" |
  jq -r '.workflow_runs | sort_by(.created_at) | reverse | .[].id')" ||
  { echo "::warning::the dry runs of ${GITHUB_SHA:0:8} could not be listed; everything runs"; result false false; }

for id in $runs; do
  jobs="$(get "actions/runs/$id/jobs?per_page=100&filter=latest")" || continue
  # shellcheck disable=SC2016 # jq's own variables
  verdict="$(jq -r '
    def group($p): [.jobs[] | select(.name | startswith($p))];
    def green($p): (group($p) | length > 0) and all(group($p)[]; .conclusion == "success" or .conclusion == "skipped");
    ([.jobs[] | select(.name == "build" and .conclusion == "success")] | length > 0) as $build
    | if $build and green("verify / ") and green("parser / ") then
        (if green("e2e / ") then "tests e2e" else "tests" end)
      else "none" end' <<< "$jobs" 2>/dev/null)" || verdict=none
  case "$verdict" in
    "tests e2e") echo "dry run $id of ${GITHUB_SHA:0:8} is green, with the browser tests: verify, parser and e2e are skipped"
                 echo "::notice::reusing dry run https://github.com/$GITHUB_REPOSITORY/actions/runs/$id (verify, parser, e2e)"; result true true ;;
    tests)       echo "dry run $id of ${GITHUB_SHA:0:8} is green, without the browser tests: verify and parser are skipped"
                 echo "::notice::reusing dry run https://github.com/$GITHUB_REPOSITORY/actions/runs/$id (verify, parser)"; result true false ;;
    *)           echo "run $id doesn't count (no green build, verify or parser)" ;;
  esac
done
echo "no green dry run of ${GITHUB_SHA:0:8}: everything runs"
result false false
