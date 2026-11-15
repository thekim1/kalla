#!/usr/bin/env bash
# Runs the backend test projects of a built tree (dotnet build UmeRag.slnx -c <configuration>), every project as a
# process of its own and all of them at once, as `dotnet test --solution` does, with one difference:
# Ume.Rag.AdminApi.Tests runs as five processes (shards).
#
# Why: that project set the backend job's time (5 min 56 s of 7 min 40 s, 2026-11-15). Its test collections wait for one
# another inside one process: the same tests took 234-259 s in one process with four or with eight database collections,
# and 148 s as two processes side by side (measured locally; a larger thread pool, Postgres without fsync and the server
# garbage collector changed nothing). What they wait for is not found yet; processes don't share it.
#
# The shards: the classes of the database collections admin-db-N (tests/Ume.Rag.AdminApi.Tests, AdminDatabaseGroupN) go
# to shard (N-1) mod 4, read from the sources, so a new class or collection is placed without a change here; every
# other class runs in a fifth process that excludes those. Each class therefore runs in exactly one shard.
#
# Usage: scripts/ci/backend-tests.sh [configuration]   (default Release; run from anywhere in the repository)
# Tests with the traits Category=Flaky and Category=EvaluationSmoke are left out, as in verify.yml before this script.
# Each process's output goes to a file under $RUNNER_TEMP (or a new temporary directory) and is printed when it ends
# badly; the summary line of every process and the sum are always printed. Exit code 1 when any process failed.
set -euo pipefail

configuration="${1:-Release}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
logs="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/backend-tests.XXXXXX")"
shards=4
admin=Ume.Rag.AdminApi.Tests
skip=(-trait- "Category=Flaky" -trait- "Category=EvaluationSmoke")

exe() { echo "tests/$1/bin/$configuration/net10.0/$1"; }

names=(); pids=()
start() { # start <name> <executable> [arguments]
  local name="$1"; shift
  "$@" > "$logs/$name.log" 2>&1 &
  names+=("$name"); pids+=("$!")
}

# Every test project but the shared kit (a library) and the benchmarks (a console app run by hand).
for dir in tests/Ume.Rag.*/; do
  project="$(basename "$dir")"
  case "$project" in Ume.Rag.TestKit|Ume.Rag.Benchmarks|"$admin") continue ;; esac
  [ -x "$(exe "$project")" ] || { echo "error: $(exe "$project") is not built (dotnet build UmeRag.slnx -c $configuration)" >&2; exit 1; }
  start "$project" "$(exe "$project")" "${skip[@]}"
done

[ -x "$(exe "$admin")" ] || { echo "error: $(exe "$admin") is not built" >&2; exit 1; }
# "<N> <class>" for every class in a database collection: the attribute, other attributes and comments, then the class.
grouped="$(python3 -I - "tests/$admin" <<'EOF'
import pathlib, re, sys
pattern = re.compile(r'\[Collection\(AdminDatabaseGroup(\d+)\.Name\)\]\s*(?:(?:\[[^\n]*\]|//[^\n]*)\s*)*public\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)')
for path in sorted(pathlib.Path(sys.argv[1]).rglob('*.cs')):
    if {'bin', 'obj'} & set(path.parts):
        continue
    text = path.read_text(encoding='utf-8')
    found = pattern.findall(text)
    if len(found) != text.count('[Collection(AdminDatabaseGroup'):
        sys.exit(f'{path}: a [Collection(AdminDatabaseGroupN.Name)] attribute is not followed by its class')
    namespace = re.search(r'^namespace\s+([\w.]+)', text, re.M)
    for group, name in found:
        print(group, f'{namespace.group(1)}.{name}' if namespace else name)
EOF
)"
[ -n "$grouped" ] || { echo "error: no class of a database collection found in tests/$admin" >&2; exit 1; }
all_grouped=()
while read -r group class; do all_grouped+=(-class- "$class"); done <<< "$grouped"
for ((shard = 0; shard < shards; shard++)); do
  include=()
  while read -r group class; do
    if (( (group - 1) % shards == shard )); then include+=(-class "$class"); fi
  done <<< "$grouped"
  [ "${#include[@]}" -gt 0 ] || continue
  start "$admin.shard$((shard + 1))" "$(exe "$admin")" "${skip[@]}" "${include[@]}"
done
start "$admin.rest" "$(exe "$admin")" "${skip[@]}" "${all_grouped[@]}"

failed=0; total=0
for i in "${!pids[@]}"; do
  name="${names[$i]}"; status=0
  wait "${pids[$i]}" || status=$?
  summary="$(grep -a -E 'Total: [0-9]+, Errors: ' "$logs/$name.log" | tail -1 | sed -E 's/\x1b\[[0-9;]*m//g; s/^ *//')"
  count="$(sed -nE 's/.*Total: ([0-9]+),.*/\1/p' <<< "$summary")"
  total=$((total + ${count:-0}))
  if [ "$status" -ne 0 ] || [ -z "$summary" ] || [ "${count:-0}" -eq 0 ]; then
    failed=1
    echo "::group::FAILED $name (exit $status)"; cat "$logs/$name.log"; echo "::endgroup::"
    echo "FAILED  $name (exit $status): ${summary:-no summary line}"
  else
    echo "passed  $name: $summary"
  fi
done
echo "total: $total tests in ${#pids[@]} processes; logs in $logs"
exit "$failed"
