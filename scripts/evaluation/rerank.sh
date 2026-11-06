#!/usr/bin/env bash
# Rerank comparison on one collection (issue #399, B-12's live run; Decisions "Rerank pulled forward"), by hand on a
# deployment whose worker reaches a gateway (the test host, or the interim gateway).
#
#   scripts/evaluation/rerank.sh --deploy-env FILE --collection SLUG --out DIR [--timeout SECONDS] [--poll SECONDS]
#
# 1. Enqueues an eval job of kind on-demand (contracts/jobs/v1/eval.on-demand.payload) for the collection's active
#    evaluation set with the variants hybrid and hybrid-rerank, through the deployment's Postgres as its superuser:
#    dedup key eval:{collectionId} (the admin's), so an on-demand run already queued is waited for instead.
# 2. Waits until the job ends (exit 1 after --timeout, or when it fails).
# 3. Writes DIR/report.md from its result (contracts/jobs/v1/eval.on-demand.result): per variant recall@10, MRR, p50/p95
#    search latency (total and rerank alone), rerank tokens, cost per question at the model mirror's prices, and the
#    questions whose rerank fell back to the fused order (rerank_unavailable).
# Needs Settings.Retrieval.rerankModel (a chat model within Eu); unset, every question counts as rerank_unavailable.
set -euo pipefail

deploy_env=""; slug=""; out=""; timeout=3600; poll=10
usage() { sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --deploy-env) deploy_env="$2"; shift 2;;
    --collection) slug="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --timeout) timeout="$2"; shift 2;;
    --poll) poll="$2"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
if [ ! -r "$deploy_env" ] || [ -z "$slug" ] || [ -z "$out" ]; then usage 1; fi
mkdir -p "$out"

sql() { docker compose --env-file "$deploy_env" exec -T postgres psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d ragdb "$@"; }

collection="$(sql -v slug="$slug" <<'SQL'
SELECT "Id" FROM config."Collections" WHERE "Slug" = :'slug' AND "DeletedAt" IS NULL;
SQL
)"
[ -n "$collection" ] || { echo "error: no collection with slug $slug" >&2; exit 1; }

# UUID v7 (data-model §2) on Postgres 17, which has no uuidv7(), as nightly.sh makes it. An open on-demand job of the
# collection keeps its dedup key: then that job is the one waited for.
job="$(sql -v collection="$collection" <<'SQL'
WITH inserted AS (
  INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "TeamId", "Classification", "DedupKey", "Payload", "MaxAttempts")
  SELECT encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING
           substring(int8send(floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1),
           'hex')::uuid,
         'eval', c."DepartmentId", c."OwnerTeamId", c."Classification", 'eval:' || c."Id",
         jsonb_build_object('kind', 'on-demand', 'collectionId', c."Id", 'variants', jsonb_build_array('hybrid', 'hybrid-rerank')), 2
  FROM config."Collections" c
  WHERE c."Id" = :'collection'
  ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING
  RETURNING "Id")
SELECT coalesce((SELECT "Id" FROM inserted),
                (SELECT "Id" FROM content."Jobs" WHERE "DedupKey" = 'eval:' || :'collection' AND "State" IN ('queued','running')));
SELECT pg_notify('rag_jobs', 'eval');
SQL
)"
job="${job%%$'\n'*}"
[[ "$job" =~ ^[0-9a-f-]{36}$ ]] || { echo "error: no job enqueued for $slug" >&2; exit 1; }
echo "$(date -u +%FT%TZ): on-demand evaluation job $job for $slug ($collection)"

begin=$SECONDS
while :; do
  state="$(sql -c "SELECT \"State\" FROM content.\"Jobs\" WHERE \"Id\" = '$job'")"
  case "$state" in
    succeeded) break;;
    failed|dead|cancelled)
      echo "error: job $job ended $state: $(sql -c "SELECT \"LastError\" FROM content.\"Jobs\" WHERE \"Id\" = '$job'")" >&2; exit 1;;
  esac
  [ $((SECONDS - begin)) -lt "$timeout" ] || { echo "error: job $job still $state after ${timeout}s" >&2; exit 1; }
  echo "$(date -u +%FT%TZ) job $job: $state"
  sleep "$poll"
done

rows="$(sql -F $'\t' -v job="$job" <<'SQL'
SELECT r->>'variant', (r->>'questionCount'), (r->>'found'),
       coalesce(to_char(100 * (r->>'recallAtK')::numeric, 'FM990.0') || ' %', '–'),
       coalesce(to_char((r->>'mrr')::numeric, 'FM0.0000'), '–'),
       coalesce((r->'search'->>'searches'), '–'),
       coalesce((r->'search'->>'totalP50Ms') || ' / ' || (r->'search'->>'totalP95Ms'), '–'),
       coalesce((r->'search'->>'rerankP50Ms') || ' / ' || (r->'search'->>'rerankP95Ms'), '–'),
       coalesce((r->'search'->>'rerankUnavailable'), '0'),
       coalesce((r->'search'->>'rerankModel'), '–'),
       coalesce((r->'search'->>'rerankPromptTokens') || ' / ' || (r->'search'->>'rerankCompletionTokens'), '–'),
       coalesce(to_char((r->'search'->>'rerankCostPerQueryUsd')::numeric, 'FM0.000000'), '–')
FROM content."Jobs" j, jsonb_array_elements(j."Result"->'runs') r
WHERE j."Id" = :'job';
SQL
)"

{
  echo "# Rerank comparison, $slug, $(date -u +%FT%TZ)"
  echo
  echo "Eval job $job (kind \`on-demand\`), the collection's active evaluation set, k = 10. hybrid-rerank is hybrid with"
  echo "the chat-model reranker (rerank timeout 30 s, ceiling the collection level's capped at Eu). Latency is per"
  echo "question in milliseconds (median / 95th percentile, nearest rank); rerank time includes the passage read. Cost"
  echo "per question is the rerank tokens at config.GatewayModels' prices over every question, fallbacks included"
  echo "('–' when the mirror has no price). Fallbacks = questions whose rerank fell back to the fused order."
  echo
  echo "| Variant | Answerable | Found | Recall@10 | MRR | Questions | Total p50 / p95 ms | Rerank p50 / p95 ms | Fallbacks | Rerank model | Tokens in / out | Cost per question (USD) |"
  echo "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|"
  while IFS=$'\t' read -r -a f; do
    printf '| %s ' "${f[@]}"; echo '|'
  done <<< "$rows"
} > "$out/report.md"
cat "$out/report.md"
