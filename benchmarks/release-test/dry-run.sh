#!/usr/bin/env bash
# Dry run of the R1 harness: run.sh --dry-run against FakeGateway's own /v1/chat/completions with the R1 latency
# profile (FirstTokenMs=400, TokenIntervalMs=25, JitterPercent=30, 200-word answers; repo-and-deployment §8.3, as the
# gateway's benchmarks/compare/latency.sh). It proves the harness and the report; it is NOT R1: no chat API, no crawl,
# no OCR batch, and not the test host. perf.yml runs it on a GitHub-hosted runner (mode dry-run).
#
#   benchmarks/release-test/dry-run.sh [options] [-- run.sh options]
#
# Options:
#   --mode r1|overhead   [r1]
#   --duration SECONDS   per phase [60]
#   --warmup SECONDS     [10]
#   --port N             FakeGateway's port on 127.0.0.1 [5480]
#   --out DIR            [results/<UTC time>-dry-run-<mode>]
#
# Builds src/Kalla.FakeGateway (Release) and starts it with a throwaway Eu key; needs the .NET SDK besides what
# run.sh needs. Exit code: run.sh's.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

mode=r1; duration=60; warmup=10; port=5480; out=""; extra=()
usage() { sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --mode) mode="$2"; shift 2;;
    --duration) duration="$2"; shift 2;;
    --warmup) warmup="$2"; shift 2;;
    --port) port="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --) shift; extra=("$@"); break;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
[ -n "$out" ] || out="$here/results/$(date -u +%Y%m%dT%H%M%SZ)-dry-run-$mode"
mkdir -p "$out"

work="$(mktemp -d)"
fake_pid=""
cleanup() {
  [ -z "$fake_pid" ] || kill "$fake_pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

echo "== building FakeGateway"
dotnet build "$root/src/Kalla.FakeGateway" -c Release --nologo -v quiet -o "$work/fake"
head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n' | sed 's/^/ume-rag-dry-run-/' > "$work/key"

echo "== starting FakeGateway on 127.0.0.1:$port (FirstTokenMs=400, TokenIntervalMs=25, JitterPercent=30, 200 words)"
FakeGateway__Keys__Eu="$(cat "$work/key")" \
FakeGateway__Latency__FirstTokenMs=400 FakeGateway__Latency__TokenIntervalMs=25 \
FakeGateway__Latency__JitterPercent=30 FakeGateway__Latency__AnswerWords=200 \
ASPNETCORE_URLS="http://127.0.0.1:$port" Logging__LogLevel__Default=Warning OTEL_SDK_DISABLED=true \
  dotnet "$work/fake/Kalla.FakeGateway.dll" --contentRoot "$work/fake" > "$out/fake-gateway.log" 2>&1 &
fake_pid=$!
for _ in $(seq 1 60); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/" || true)" = 200 ] && break
  kill -0 "$fake_pid" 2>/dev/null || { cat "$out/fake-gateway.log" >&2; echo "error: FakeGateway exited" >&2; exit 1; }
  sleep 1
done

# The key level must match the ceiling (FakeGateway's StrictKeyLevel); fors/chat-eu answers at Eu.
bash "$here/run.sh" --dry-run --mode "$mode" --base-url "http://127.0.0.1:$port" --model fors/chat-eu \
  --key-file "$work/key" --header 'x-fors-max-residency: Eu' --duration "$duration" --warmup "$warmup" \
  --fake-latency 400,25,30,200 --out "$out" ${extra[@]+"${extra[@]}"}
