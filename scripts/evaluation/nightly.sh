#!/usr/bin/env bash
# Nightly retrieval evaluation on the test deployment (repo-and-deployment §8.1 evaluation.yml, processing design §14.8,
# knowledge design §16.4), run by .github/workflows/evaluation.yml on the test host's runner, or by hand there (the
# manual stopgap of repo-and-deployment §12 item 4).
#
#   scripts/evaluation/nightly.sh --deploy-env FILE --out DIR [--timeout SECONDS] [--poll SECONDS] [--no-enqueue]
#
# 1. Enqueues an eval job of kind nightly (contracts/jobs/v1/eval.nightly.payload) for every published, not deleted
#    collection, through the deployment's Postgres as its superuser: dedup key eval-nightly:{collectionId} (the
#    worker's), so a run the worker's own schedule already queued is not doubled; the collection's department, owner
#    team and classification. --no-enqueue only waits for the runs already queued and reports the last 24 hours.
# 2. Waits until no eval-nightly job is queued or running (exit 1 after --timeout).
# 3. Writes DIR/report.md from the jobs' results (contracts/jobs/v1/eval.run.result): per collection tonight's run and
#    the spread of the runs the job table still holds (terminal jobs are kept 7 days, engine design §15), with the
#    embedding profile each collection is searched with: the deployment's own (EU) profile, not BGE-M3 (P1b Open O4).
# Advisory: the exit code says whether the runs happened (0) or not (1), never whether recall was good.
set -euo pipefail

deploy_env=""; out=""; timeout=3600; poll=20; enqueue=1
usage() { sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --deploy-env) deploy_env="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --timeout) timeout="$2"; shift 2;;
    --poll) poll="$2"; shift 2;;
    --no-enqueue) enqueue=0; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
if [ ! -r "$deploy_env" ] || [ -z "$out" ]; then usage 1; fi
mkdir -p "$out"

sql() { docker compose --env-file "$deploy_env" exec -T postgres psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d ragdb "$@"; }

utc="'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'"
started="$(sql -c "SELECT to_char(now() AT TIME ZONE 'UTC', $utc)")"
since="$started"
[ "$enqueue" = 1 ] || since="$(sql -c "SELECT to_char((now() - interval '24 hours') AT TIME ZONE 'UTC', $utc)")"
if [ "$enqueue" = 1 ]; then
  # UUID v7 (data-model §2) on Postgres 17, which has no uuidv7(): the millisecond time over a random v4, version bits 7.
  queued="$(sql -v started="$started" <<'SQL'
WITH inserted AS (
  INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "TeamId", "Classification", "DedupKey", "Payload", "MaxAttempts")
  SELECT encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING
           substring(int8send(floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1),
           'hex')::uuid,
         'eval', c."DepartmentId", c."OwnerTeamId", c."Classification", 'eval-nightly:' || c."Id",
         jsonb_build_object('kind', 'nightly', 'collectionId', c."Id", 'scheduledFor', :'started'), 2
  FROM config."Collections" c
  WHERE c."PublishedAt" IS NOT NULL AND c."DeletedAt" IS NULL
  ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING
  RETURNING 1)
SELECT count(*) FROM inserted;
SELECT pg_notify('rag_jobs', 'eval');
SQL
)"
  echo "$started: enqueued ${queued%%$'\n'*} nightly evaluation job(s)"
fi

begin=$SECONDS
while :; do
  open="$(sql -c "SELECT count(*) FROM content.\"Jobs\" WHERE \"State\" IN ('queued','running') AND \"DedupKey\" LIKE 'eval-nightly:%'")"
  [ "$open" = 0 ] && break
  [ $((SECONDS - begin)) -lt "$timeout" ] || { echo "error: $open evaluation job(s) still open after ${timeout}s" >&2; exit 1; }
  echo "$(date -u +%FT%TZ) open evaluation jobs: $open"
  sleep "$poll"
done

# One row per collection: tonight's run (the latest finished since $since) and the spread of every kept nightly run.
rows="$(sql -F $'\t' -v since="$since" <<'SQL'
WITH runs AS (
  SELECT j."Payload"->>'collectionId' AS collection, j."State", j."CompletedAt", j."LastError",
         (j."Result"->>'questionCount')::int AS questions, (j."Result"->>'found')::int AS found,
         CASE WHEN j."Result" IS NOT NULL THEN coalesce((j."Result"->>'wins')::int, 0) END AS wins,
         CASE WHEN j."Result" IS NOT NULL THEN coalesce((j."Result"->>'losses')::int, 0) END AS losses,
         coalesce((j."Result"->>'recallAlert')::boolean, false) AS alert,
         (j."Result"->'search'->>'totalP50Ms') || ' / ' || (j."Result"->'search'->>'totalP95Ms') AS latency
  FROM content."Jobs" j
  WHERE j."Class" = 'eval' AND j."Payload"->>'kind' = 'nightly' AND j."CompletedAt" IS NOT NULL),
tonight AS (
  SELECT DISTINCT ON (collection) * FROM runs WHERE "CompletedAt" >= :'since'::timestamptz
  ORDER BY collection, "CompletedAt" DESC),
spread AS (
  SELECT collection, count(*) AS n, min(found::numeric / nullif(questions, 0)) AS lo,
         max(found::numeric / nullif(questions, 0)) AS hi, stddev_samp(found::numeric / nullif(questions, 0)) AS sd
  FROM runs WHERE "State" = 'succeeded' GROUP BY collection)
SELECT c."Slug", e."Name" || ' (' || e."GatewayModel" || ', ' || e."MaxResidency" || ')',
       coalesce(t."State", 'not run'), coalesce(t.questions::text, '–'), coalesce(t.found::text, '–'),
       coalesce(to_char(100.0 * t.found / nullif(t.questions, 0), 'FM990.0') || ' %', '–'),
       coalesce(t.wins || ' / ' || t.losses, '–'),
       CASE WHEN t.alert THEN '**yes**' WHEN t.questions IS NULL THEN '–' ELSE 'no' END,
       coalesce(t.latency, '–'),
       coalesce(s.n, 0), coalesce(to_char(100 * s.lo, 'FM990.0') || '–' || to_char(100 * s.hi, 'FM990.0') || ' %', '–'),
       coalesce(to_char(100 * s.sd, 'FM990.00') || ' points', '–'),
       coalesce(replace(left(t."LastError", 120), '|', '/'), '–')
FROM config."Collections" c
JOIN config."EmbeddingProfiles" e ON e."Id" = c."EmbeddingProfileId"
LEFT JOIN tonight t ON t.collection = c."Id"::text
LEFT JOIN spread s ON s.collection = c."Id"::text
WHERE c."PublishedAt" IS NOT NULL AND c."DeletedAt" IS NULL
ORDER BY c."Slug";
SQL
)"

{
  echo "# Nightly retrieval evaluation, $started"
  echo
  echo "Test deployment, every published collection's full evaluation set, kind \`nightly\` (processing design §14.8)."
  echo "Advisory: no gate. Found = answerable questions with an expected document in the top 10. Spread = the"
  echo "succeeded nightly runs still in the job table (up to 7 days): range and standard deviation of the found share."
  echo "The run-to-run variance of an unchanged collection is what P1b live item 23 records. Latency = the questions'"
  echo "total search time in milliseconds, median / 95th percentile (eval.run.result search)."
  echo
  echo "| Collection | Embedding profile | Tonight | Questions | Found | Share | Wins / losses | Recall alert | Latency p50 / p95 ms | Runs kept | Spread | Std dev | Error |"
  echo "|---|---|---|---:|---:|---:|---:|---|---:|---:|---|---:|---|"
  if [ -z "$rows" ]; then
    echo "| (no published collection) | | | | | | | | | | | | |"
  else
    while IFS=$'\t' read -r -a f; do
      printf '| %s ' "${f[@]}"; echo '|'
    done <<< "$rows"
  fi
} > "$out/report.md"
cat "$out/report.md"
grep -q '| not run |\|| failed |\|| dead |' "$out/report.md" && { echo "error: a collection's run did not succeed" >&2; exit 1; }
exit 0
