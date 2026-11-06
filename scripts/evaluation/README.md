# Evaluation scripts

Both run on a deployment host (the test host, or a machine whose worker reaches the interim gateway), against its
`docker compose` stack and Postgres. They enqueue `eval` jobs as the admin and the worker's schedule do, wait for them
and write a `report.md` from the jobs' results. Advisory: the exit code says whether the runs happened, never whether
the numbers were good.

| Script | Job | Report |
|---|---|---|
| `nightly.sh` | `nightly` for every published collection (`.github/workflows/evaluation.yml`) | recall per collection, wins/losses, the recall alert, search latency, the spread of the kept runs |
| `rerank.sh` | `on-demand` with variants `hybrid` and `hybrid-rerank` for one collection (#399) | recall@10, MRR, p50/p95 search and rerank latency, cost per question, rerank fallbacks |

## Rerank comparison (B-12's live run)

Before the run:

1. The collection has an active evaluation set (a generated layer: `eval` `generate-set`, from the admin UI or after
   its first syncs).
2. `Settings.Retrieval.rerankModel` names a chat model of the mirror within `Eu` (the admin's `PUT /api/settings/retrieval`). The
   worker holds no `External` key, so the rerank call's ceiling is the collection level's capped at `Eu`. Unset, every
   question falls back to the fused order and the report counts it under Fallbacks.
3. The model's `InputPerMillionUsd` and `OutputPerMillionUsd` are in `config."GatewayModels"` for a cost; without them
   the cost column shows `–`.

Then, on the host, from the repository root:

```bash
scripts/evaluation/rerank.sh --deploy-env /path/to/deploy.env --collection turkos --out /tmp/rerank-turkos
```

`--timeout SECONDS` (default 3600) and `--poll SECONDS` (default 10) bound the wait. The report is
`/tmp/rerank-turkos/report.md`; link it (or paste its table) where B-12's live run is recorded. Each question of
`hybrid-rerank` makes one chat call through the gateway (`x-ume-step: rerank`, `x-ume-run-id` = the evaluation run's id).
The variant allows the call 30 s instead of the API's 500 ms (knowledge design §6.7), so its latency is the chat
model's real one, not the fallback.

The same comparison can be asked for without the script: an `on-demand` job whose payload lists
`"variants": ["hybrid", "hybrid-rerank"]`; the numbers are in its result's `runs[].search`
(`contracts/jobs/v1/eval.on-demand.result.schema.json`).
