#!/usr/bin/env bash
# R1 phase 2's background load (repo-and-deployment §8.3), for run.sh --ingest-cmd: starts a full crawl of the page copy
# and the OCR batch on the deployment under test, then waits until the ingestion zone is idle again and exits 0 (exit 1
# on --timeout). run.sh fails R1 if this exits before phase 2 ends, so the copy must be large enough to outlast it.
#
#   benchmarks/release-test/ingest.sh --deploy-env FILE --crawl-source ID [--upload-source ID --upload-batch ID]
#                                     [--timeout SECONDS] [--poll SECONDS]
#
# Before the run (once, on the test host; live item 22): the page copy served by site/serve.sh on the deployment's
# network, a crawl source pointing at it (seed http://r1-site:8080/, sitemap) with Crawl__AllowedPrivateRanges set for
# that network, and an upload source with the 50 scanned PDFs of ocr-batch/ uploaded but not committed (the admin's
# upload, stream B). This script enqueues what the admin's "sync now" and the upload commit enqueue, through the
# deployment's Postgres as its superuser: a sync job per source by the contract (contracts/jobs/v1/sync.payload,
# trigger manual for the crawl, upload with the batch id), with the dedup keys the worker uses (sync:{sourceId},
# sync-upload:{batchId}), the collection's department and owner team and the source's classification. "Idle" = no
# queued or running sync, fetch, parse or embed job on two polls in a row, so nothing else may ingest during R1.
set -euo pipefail

deploy_env=""; crawl=""; upload=""; batch=""; timeout=7200; poll=15
usage() { sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --deploy-env) deploy_env="$2"; shift 2;;
    --crawl-source) crawl="$2"; shift 2;;
    --upload-source) upload="$2"; shift 2;;
    --upload-batch) batch="$2"; shift 2;;
    --timeout) timeout="$2"; shift 2;;
    --poll) poll="$2"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
if [ ! -r "$deploy_env" ] || [ -z "$crawl" ]; then usage 1; fi
{ [ -z "$upload" ] && [ -z "$batch" ]; } || { [ -n "$upload" ] && [ -n "$batch" ]; } \
  || { echo "error: --upload-source and --upload-batch go together" >&2; exit 1; }
uuid_re='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
for id in "$crawl" ${upload:+"$upload"} ${batch:+"$batch"}; do
  [[ "$id" =~ $uuid_re ]] || { echo "error: '$id' is not a lower-case uuid" >&2; exit 1; }
done

sql() { docker compose --env-file "$deploy_env" exec -T kalla-postgres psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d ragdb "$@"; }

# UUID v7 (data-model §2) on Postgres 17, which has no uuidv7(): the millisecond time over a random v4, version bits 7.
enqueue() { # source-id trigger dedup-key [batch-id]
  local payload="jsonb_build_object('kind', 'sync', 'sourceId', s.\"Id\", 'trigger', :'trigger')"
  [ -z "${4:-}" ] || payload="$payload || jsonb_build_object('batchId', :'batch'::uuid)"
  sql -v source="$1" -v trigger="$2" -v dedup="$3" -v batch="${4:-}" <<SQL
INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "TeamId", "Classification", "DedupKey", "Payload", "MaxAttempts")
SELECT encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING
         substring(int8send(floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1),
         'hex')::uuid,
       'sync', c."DepartmentId", c."OwnerTeamId", s."Classification", :'dedup', $payload, 3
FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId"
WHERE s."Id" = :'source'::uuid
ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING
RETURNING "Id";
SELECT pg_notify('rag_jobs', 'sync');
SQL
}

open_jobs() {
  sql -c "SELECT count(*) FROM content.\"Jobs\" WHERE \"State\" IN ('queued','running') AND \"Class\" IN ('sync','fetch','parse','embed')"
}

ids="'$crawl'"; expected=1
[ -z "$upload" ] || { ids="$ids, '$upload'"; expected=2; }
[ "$(sql -c "SELECT count(*) FROM config.\"Sources\" WHERE \"Id\" IN ($ids)")" = "$expected" ] \
  || { echo "error: a source id does not exist in this deployment" >&2; exit 1; }
job="$(enqueue "$crawl" manual "sync:$crawl")"; job="${job%%$'\n'*}"
echo "$(date -u +%FT%TZ) crawl sync of $crawl: job ${job:-already queued or running}"
if [ -n "$upload" ]; then
  job="$(enqueue "$upload" upload "sync-upload:$batch" "$batch")"; job="${job%%$'\n'*}"
  echo "$(date -u +%FT%TZ) upload sync of $upload, batch $batch: job ${job:-already queued or running}"
fi

start=$SECONDS; idle=0
while [ "$idle" -lt 2 ]; do
  sleep "$poll"
  open="$(open_jobs)"
  echo "$(date -u +%FT%TZ) open ingestion jobs: $open"
  if [ "$open" = 0 ]; then idle=$((idle + 1)); else idle=0; fi
  [ $((SECONDS - start)) -lt "$timeout" ] || { echo "error: still busy after ${timeout}s" >&2; exit 1; }
done
echo "$(date -u +%FT%TZ) ingestion idle after $((SECONDS - start)) s"
