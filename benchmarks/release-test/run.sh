#!/usr/bin/env bash
# R1 release test and chat overhead run (plans/platform/repo-and-deployment.md §8.3, plans/chat-api/design.md §16.2
# items 3 and 4), run by .github/workflows/perf.yml or by hand on the test host.
#
#   r1:       phase 1 idle: streamed chat at --rate messages/s for --duration; phase 2 the same while --ingest-cmd runs
#             (the full crawl of the static page copy plus the OCR batch, ingest.sh). Pass: chat p95 TTFT in phase 2
#             <= 1.10 x phase 1, no error responses, no Postgres connection refusals, the background load running
#             through all of phase 2.
#   overhead: one phase of Open WebUI-shaped requests (requests/openwebui.json); reports TTFT minus FakeGateway's first
#             token, throughput and CPU per message. No pass rule.
#
# The load is k6 (k6/chat.js, pinned image) at --rate minus --probe-rate, plus stream_probe.py at --probe-rate, which
# measures time to first token (k6 cannot read a stream while it arrives). The report (report.py) is written to
# <out>/report.md and printed; the exit code is report.py's: 0 pass, 1 fail, 2 missing results.
#
#   benchmarks/release-test/run.sh --base-url URL --model MODEL [options]
#
# Options:
#   --mode r1|overhead     [r1]
#   --request FILE         request file [requests/question.json; overhead: requests/openwebui.json]
#   --key-file FILE        bearer key of the chat API (an API key of the assistant's client)
#   --header 'Name: value' extra request header, repeatable (k6 and the probe)
#   --ca-file FILE         CA bundle for an https target (the deployment's deploy/certs/ca.pem)
#   --rate N               streamed messages per second in total [5]
#   --probe-rate N         of which the stream probe sends [1]
#   --duration SECONDS     per phase [600]
#   --warmup SECONDS       unrecorded warm-up before phase 1 [30]
#   --deploy-env FILE      the deployment's deploy/.env: enables the Postgres refusal check (postgres and service
#                          logs) and CPU per container (Docker API)
#   --app-container NAME   the Compose service whose CPU per message is reported [kalla-api]
#   --ingest-cmd CMD       r1 phase 2 background load, run with bash -c: starts the crawl and the OCR batch and exits
#                          when they are finished (ingest.sh); exiting before phase 2 ends fails R1
#   --fake-latency F,T,J,W the FakeGateway latency profile behind the target (FirstTokenMs, TokenIntervalMs,
#                          JitterPercent, AnswerWords), shown in the report [400,25,30,200]
#   --max-ratio R          [1.10]
#   --out DIR              results and report [results/<UTC time>-<mode>]
#   --dry-run              the target is FakeGateway itself (dry-run.sh): the report says it is not R1 and leaves
#                          the checks it cannot make unchecked instead of failing them
#   --k6-base-url URL      the target as k6's container reaches it [--base-url]; with Docker Desktop (WSL), whose host
#                          network is not the distribution's, http://host.docker.internal:PORT
#
# Needs Docker, python3 3.12+ and curl. k6 runs with --network host, so the target must be reachable from the host.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# k6 1.3.0, the gateway's pin (benchmarks/compare/compose.yaml); multi-arch index digest from Docker Hub.
K6_IMAGE="${K6_IMAGE:-grafana/k6:1.3.0@sha256:3ddc8b1a33a2c3d8edc6e99b6a762ae36cba08788463458f5e6a7703e14eb77d}"

mode=r1; base_url=""; model=""; request=""; key_file=""; headers=(); ca_file=""; rate=5; probe_rate=1; duration=600
warmup=30; deploy_env=""; app_container=kalla-api; ingest_cmd=""; fake_latency=400,25,30,200; max_ratio=1.10; out=""
dry_run=0; k6_base_url=""
usage() { sed -n '2,42p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --mode) mode="$2"; shift 2;;
    --base-url) base_url="$2"; shift 2;;
    --model) model="$2"; shift 2;;
    --request) request="$2"; shift 2;;
    --key-file) key_file="$2"; shift 2;;
    --header) headers+=("$2"); shift 2;;
    --ca-file) ca_file="$2"; shift 2;;
    --rate) rate="$2"; shift 2;;
    --probe-rate) probe_rate="$2"; shift 2;;
    --duration) duration="$2"; shift 2;;
    --warmup) warmup="$2"; shift 2;;
    --deploy-env) deploy_env="$2"; shift 2;;
    --app-container) app_container="$2"; shift 2;;
    --ingest-cmd) ingest_cmd="$2"; shift 2;;
    --fake-latency) fake_latency="$2"; shift 2;;
    --max-ratio) max_ratio="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --dry-run) dry_run=1; shift;;
    --k6-base-url) k6_base_url="$2"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
if [ -z "$base_url" ] || [ -z "$model" ]; then usage 1; fi
[ -n "$k6_base_url" ] || k6_base_url="$base_url"
case "$mode" in r1|overhead) ;; *) echo "error: --mode is r1 or overhead" >&2; exit 1;; esac
for tool in docker python3 curl; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
if [ "$mode" = r1 ] && [ -z "$ingest_cmd" ] && [ "$dry_run" = 0 ]; then
  echo "error: R1's phase 2 needs --ingest-cmd (the crawl and the OCR batch, ingest.sh); --dry-run runs without one" >&2
  exit 1
fi
[ -z "$deploy_env" ] || [ -r "$deploy_env" ] || { echo "error: cannot read $deploy_env" >&2; exit 1; }
k6_rate="$(python3 -c 'import sys; r = float(sys.argv[1]) - float(sys.argv[2]); print(f"{r:g}")' "$rate" "$probe_rate")"
python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) > 0 else 1)' "$k6_rate" \
  || { echo "error: --rate must be larger than --probe-rate" >&2; exit 1; }
[ -n "$request" ] || request="$here/requests/$([ "$mode" = overhead ] && echo openwebui || echo question).json"
request="$(cd "$(dirname "$request")" && pwd)/$(basename "$request")"
[ -n "$out" ] || out="$here/results/$(date -u +%Y%m%dT%H%M%SZ)-$mode"
mkdir -p "$out"; out="$(cd "$out" && pwd)"

probe_args=(--base-url "$base_url" --model "$model" --request "$request")
for h in ${headers[@]+"${headers[@]}"}; do probe_args+=(--header "$h"); done
[ -z "$key_file" ] || probe_args+=(--key-file "$key_file")
[ -z "$ca_file" ] || probe_args+=(--ca-file "$ca_file")
# k6 gets the same headers as one JSON object, and the key through its environment (never on a command line).
headers_json="$(python3 -c '
import json, sys
print(json.dumps({n.strip(): v.strip() for n, _, v in (h.partition(":") for h in sys.argv[1:])}))' \
  ${headers[@]+"${headers[@]}"})"
k6_mounts=(-v "$here/k6:/scripts:ro" -v "$(dirname "$request"):/request:ro" -v "$out:/results")
[ -z "$ca_file" ] || k6_mounts+=(-v "$(cd "$(dirname "$ca_file")" && pwd)/$(basename "$ca_file"):/ca.pem:ro" -e SSL_CERT_FILE=/ca.pem)
key=""; [ -z "$key_file" ] || key="$(tr -d '\r\n' < "$key_file")"

dc() { docker compose --env-file "$deploy_env" "$@"; }

# Cumulative CPU time (ns) of the deployment's main containers, from the Docker API (as the gateway's lib.sh).
cpu_snapshot() {
  local service id
  for service in "$app_container" kalla-worker kalla-parser kalla-postgres; do
    id="$(dc ps -q "$service" 2>/dev/null || true)"
    [ -n "$id" ] || continue
    printf '%s ' "$service"
    curl -s --unix-socket /var/run/docker.sock "http://localhost/containers/$id/stats?stream=false&one-shot=true" |
      python3 -c 'import json, sys; print(json.load(sys.stdin)["cpu_stats"]["cpu_usage"]["total_usage"])'
  done
}

write_cpu() { # before-file after-file out-file
  python3 - "$@" <<'PY'
import json, sys
read = lambda p: {k: int(v) for k, v in (line.split() for line in open(p) if line.strip())}
before, after = read(sys.argv[1]), read(sys.argv[2])
json.dump({"containers": {k: after[k] - before[k] for k in after if k in before}}, open(sys.argv[3], "w"), indent=2)
PY
}

# Postgres refusals: the server's FATALs, or SQLSTATE 53300 (too_many_connections) in a service's log.
write_refusals() { # since out-file
  if [ -z "$deploy_env" ]; then
    echo '{"checked": false}' > "$2"; return
  fi
  dc logs --no-color --since "$1" kalla-postgres kalla-api kalla-admin kalla-worker 2>&1 |
    { grep -E 'too many clients already|remaining connection slots are reserved|too many connections for role|53300' || true; } |
    python3 -c '
import json, sys
lines = [line.rstrip("\n") for line in sys.stdin if line.strip()]
json.dump({"checked": True, "refusals": len(lines), "first": lines[0][:300] if lines else None}, open(sys.argv[1], "w"))' "$2"
}

run_phase() { # name seconds
  local name="$1" seconds="$2" k6_pid since status=0
  since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  [ -z "$deploy_env" ] || cpu_snapshot > "$out/$name.cpu-before"
  KEY="$key" docker run --rm --network host -u "$(id -u):$(id -g)" "${k6_mounts[@]}" \
    -e KEY -e BASE_URL="$k6_base_url" -e MODEL="$model" -e RATE="$k6_rate" -e DURATION="${seconds}s" \
    -e REQUEST="/request/$(basename "$request")" -e HEADERS="$headers_json" -e OUT="/results/$name.k6.json" \
    "$K6_IMAGE" run -q /scripts/chat.js > "$out/$name.k6.log" 2>&1 &
  k6_pid=$!
  python3 -I "$here/stream_probe.py" "${probe_args[@]}" --rate "$probe_rate" --duration "$seconds" \
    --label "$name" --out "$out/$name.probe.json" || status=$?
  wait "$k6_pid" || status=$?
  grep -vE '^\s*$|level=warn' "$out/$name.k6.log" || true
  if [ -n "$deploy_env" ]; then
    cpu_snapshot > "$out/$name.cpu-after"
    write_cpu "$out/$name.cpu-before" "$out/$name.cpu-after" "$out/$name.cpu.json"
    rm -f "$out/$name.cpu-before" "$out/$name.cpu-after"
  fi
  write_refusals "$since" "$out/$name.postgres.json"
  return "$status"
}

python3 - "$out/meta.json" "$mode" "$dry_run" "$base_url" "$model" "$(basename "$request" .json)" "$rate" "$probe_rate" \
  "$duration" "$warmup" "$(git -C "$here" rev-parse --short HEAD 2>/dev/null || true)" "$app_container" "$ingest_cmd" \
  "$fake_latency" <<'PY'
import json, sys, time
(path, mode, dry, base_url, model, request, rate, probe_rate, duration, warmup, revision, app, ingest, fake) = sys.argv[1:]
first_token, interval, jitter, words = (int(x) for x in fake.split(","))
json.dump({
    "mode": mode, "dry_run": dry == "1", "base_url": base_url, "model": model, "request": request,
    "rate": float(rate), "probe_rate": float(probe_rate), "duration_s": int(duration), "warmup_s": int(warmup),
    "started_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "revision": revision or None,
    "app_container": app, "ingest_cmd": ingest or None,
    "fake_latency": {"first_token_ms": first_token, "token_interval_ms": interval, "jitter_percent": jitter,
                     "answer_words": words},
}, open(path, "w"), indent=2)
PY

docker pull -q "$K6_IMAGE" >/dev/null
echo "== warm-up (${warmup} s, not recorded)"
python3 -I "$here/stream_probe.py" "${probe_args[@]}" --rate "$rate" --duration "$warmup" --label warm-up \
  --out "$out/warmup.probe.json"

if [ "$mode" = overhead ]; then
  echo "== overhead (${duration} s)"
  run_phase overhead "$duration"
else
  echo "== phase 1: idle (${duration} s)"
  run_phase idle "$duration"
  ingest_pid=""
  if [ -n "$ingest_cmd" ]; then
    echo "== starting the background load: $ingest_cmd"
    setsid bash -c "$ingest_cmd" > "$out/ingest.log" 2>&1 &
    ingest_pid=$!; ingest_start=$SECONDS
  fi
  echo "== phase 2: crawl + OCR batch (${duration} s)"
  run_phase load "$duration"
  if [ -n "$ingest_pid" ]; then
    if kill -0 "$ingest_pid" 2>/dev/null; then
      echo '{"finished_early": false, "exit": null, "ran_s": '"$((SECONDS - ingest_start))"'}' > "$out/load.ingest.json"
      kill -- "-$ingest_pid" 2>/dev/null || true # stop waiting; the deployment keeps working on what was enqueued
    else
      code=0; wait "$ingest_pid" || code=$?
      echo '{"finished_early": true, "exit": '"$code"', "ran_s": '"$((SECONDS - ingest_start))"'}' > "$out/load.ingest.json"
    fi
  fi
fi

echo "== report"
code=0
python3 -I "$here/report.py" "$out" --max-ratio "$max_ratio" > "$out/report.md" || code=$?
cat "$out/report.md"
echo "Results: $out"
exit "$code"
