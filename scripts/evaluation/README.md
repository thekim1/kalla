# Evaluation scripts

Both run on a deployment host (the test host, or a machine whose worker reaches the interim gateway), against its
`docker compose` stack and Postgres. They enqueue `eval` jobs as the admin and the worker's schedule do, wait for them
and write a `report.md` from the jobs' results. Advisory: the exit code says whether the runs happened, never whether
the numbers were good.

| Script | Job | Report |
|---|---|---|
| `nightly.sh` | `nightly` for every published collection (`.github/workflows/evaluation.yml`) | recall per collection, wins/losses, the recall alert, search latency, the spread of the kept runs |
| `rerank.sh` | `on-demand` with variants `hybrid`, `hybrid-rerank` and `hybrid-rerank-gateway` for one collection (#399, #414) | recall@10, MRR, p50/p95 search and rerank latency, cost per question, rerank fallbacks, variants skipped |

## Rerank comparison (B-12's and B-16's live runs)

Before the run:

1. The collection has an active evaluation set (a generated layer: `eval` `generate-set`, from the admin UI or after
   its first syncs).
2. `Settings.Retrieval.rerankModel` names a chat model of the mirror within `Eu` (for `hybrid-rerank`), and
   `Settings.Retrieval.gatewayRerankModel` a rerank model of the mirror within `Eu` (for `hybrid-rerank-gateway`;
   Berget AI `bge-reranker-v2-m3` once the gateway's rerank release is live). Both are set with the admin's
   `PUT /api/settings/retrieval`. The worker holds no `External` key, so the rerank call's ceiling is the collection
   level's capped at `Eu`. Rerank is optional: a variant whose model is unset does not run; the job's result lists it
   under `skipped` (`no_rerank_model`) and the report names it under the table. A model that is set but fails counts
   under Fallbacks.
3. The model's prices are in `config."GatewayModels"` for a cost: `InputPerMillionUsd` and `OutputPerMillionUsd` for the
   chat model, `InputPerMillionUsd` alone for the gateway rerank model (billed per input token). Without them the cost
   column shows `–`.

Then, on the host, from the repository root:

```bash
scripts/evaluation/rerank.sh --deploy-env /path/to/deploy.env --collection turkos --out /tmp/rerank-turkos
```

`--timeout SECONDS` (default 3600) and `--poll SECONDS` (default 10) bound the wait. The report is
`/tmp/rerank-turkos/report.md`; link it (or paste its table) where B-12's live run is recorded. Each question of
`hybrid-rerank` makes one chat call through the gateway and each of `hybrid-rerank-gateway` one `POST /v1/rerank`
(`x-fors-step: rerank`, `x-fors-run-id` = the evaluation run's id). Both variants allow the call 30 s instead of the API's
limits (`Retrieval:RerankTimeout` 3 s, `Retrieval:GatewayRerankTimeout` 1.5 s), so their latency is the reranker's
real one, not the fallback.

The same comparison can be asked for without the script: an `on-demand` job whose payload lists
`"variants": ["hybrid", "hybrid-rerank", "hybrid-rerank-gateway"]`; the numbers are in its result's `runs[].search`
(`contracts/jobs/v1/eval.on-demand.result.schema.json`).
