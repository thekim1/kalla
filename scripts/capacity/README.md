# On-prem capacity measurements (P0 #22)

`capacity.py` measures the gateway's on-prem models before P1 (weakness review §5; performance pass §3.9;
`to-verify` #98, #99, #104, #125, #149). If capacity falls short, register M2 decides before P1 between more on-prem
capacity and a longer initial-load window.

It is one Python file using only the standard library (Python 3.12+), so nothing is pinned or installed and it can be
copied to whichever host reaches the gateway. Python was chosen over PowerShell, the other script language in
`scripts/`: the PowerShell scripts start the Windows dev stack, while this one runs on a Linux jump host or the test
host, and it needs threads and streamed HTTP, which the Python standard library covers.

## What it measures

| Command | Measures | Against (design estimate, printed next to each number) | Rows |
|---|---|---|---|
| `embed-throughput` | BGE-M3 inputs/s and tokens/s for batch sizes 1, 8, 16, 32, 64 and client concurrency 1, 2, 4; request p50/p95/p99 | 8 CPU cores 0.5–1.5k, one L4/A10 8–20k, one A100/H100 30–80k tokens/s (performance pass §3.9); the pilot needs 10k tokens/s (design-engine §18), 10× bulk 100k (performance pass §2.1); umea.se onboarding 4.6 h at 10k tokens/s | #98, #125 |
| `query-under-load` | single-query embedding p50/p95/p99, first idle, then behind a background ingestion load of batch 32 and of batch 64 (`--load-concurrency` concurrent requests, as `Worker:Concurrency:embed`); the load's own tokens/s | ≤ 60 ms per miss (knowledge-api §11, assumes a GPU); 1–3 s behind 64 × 450-token batches on an L4 (performance pass §3.9); 1,500 ms attempt timeout (knowledge-api §5.3); query lane when the p95 under load exceeds 2 × idle (performance pass §6) | #99, #125 |
| `condense` | condense model latency p50/p95/p99, non-streaming, `max_tokens` 96, temperature 0, the chat-api §5 prompt over a short transcript; share of outputs the §5 rule would accept (2–400 characters, one line) | p95 ≤ 400 ms, cut at 600 ms (chat-api §5, §16.1) | #104 |
| `answer-ttft` | answer model time to response headers, to the first event and to the first content token (TTFT), and total time, streamed, for a full 15,360-token prompt (`ContextTokens` 16,384 minus the 1,024 answer reserve, chat-api §6.3) with `max_tokens` 1024; prefill and decode tokens/s | TTFT p99 below the 15 s headers timeout (chat-api §8.3) | #149 |
| `all` | the four above, in that order | | |

Every request is what the services send: `POST /v1/embeddings` with `encoding_format: float`, or
`POST /v1/chat/completions` (`stream_options.include_usage` when streamed), with `Authorization: Bearer <key>`,
`x-fors-max-residency: <ceiling>` (default `OnPrem`), `x-fors-run-id: capacity-<time>-<random>` and `x-fors-step`
(`embed`, `query-embed`, `condense`, `answer`; knowledge-api §5.2, chat-api §5, §8.1, design-processing §11).
Every 2xx answer must carry `x-fors-residency` at or below the ceiling, as `ResidencyCeilingHandler` checks: if one does
not, the run stops (exit 2), because it would not be measuring on-prem capacity. A 400, 401, 403 or 404 also stops it
(wrong key, ceiling or model). 429 and 503 are counted and honoured (`Retry-After`, at most 10 s), as the worker does.

Each text is unique (a counter in every chunk, query and transcript; a random session id at the start of every answer
prompt), so no provider prefix cache or response cache answers instead of the model. Token counts come from the
gateway's `usage.prompt_tokens`; when a provider reports none, they are estimated at `--chars-per-token` (4, the
gateway's own estimate) and the result says so. `answer-ttft` first calibrates its prompt to the model's tokenizer with
up to three short requests, so the prompt is 15,360 tokens as the model counts them.

## Running it

```bash
python3 -I scripts/capacity/capacity.py <command> --base-url <Gateway:BaseUrl> --key-file <file> [options]
python3 -I scripts/capacity/capacity.py --help
```

| Option | Default | |
|---|---|---|
| `--base-url` | – | the gateway, as `Gateway:BaseUrl` (a trailing `/v1` is accepted) |
| `--key-file` | – | file holding the gateway key of the ceiling's level; read once, never printed or stored |
| `--ceiling` | `OnPrem` | `x-fors-max-residency` on every call |
| `--embedding-model`, `--condense-model`, `--answer-models` | `bge-m3`, `fors/small-onprem`, `fors/chat-onprem` | the gateway's aliases; `--answer-models a,b` measures each (the slowest decides #149) |
| `--duration`, `--warmup` | per command: 60/10, 120/15, 120/10, 600/30 s | measured and discarded seconds per step; the first request of each client thread is always warm-up |
| `--concurrency` | `1,2,4` for `embed-throughput`, else 1 | client threads (`embed-throughput` runs every value; the others take the first) |
| `--batch-sizes`, `--chunk-tokens` | `1,8,16,32,64`, 450 | `embed-throughput` (450 tokens per chunk, design-engine §18) |
| `--load-batch-sizes`, `--load-concurrency`, `--query-concurrency`, `--query-interval-ms` | `32,64`, 2, 1, 200 | `query-under-load` |
| `--prompt-tokens`, `--max-tokens` | 15,360, 1,024 | `answer-ttft` |
| `--request-timeout` | 300 s | read timeout per request (connect: 10 s) |
| `--ca-file` | system store | CA bundle for an https gateway behind a private CA |
| `--out-dir` | `scripts/capacity/results/` (git-ignored) | where the result files go |
| `--smoke`, `--label` | off, empty | `--smoke` marks a run against a fake (header and JSON say so); `--label` records hardware, gateway version, time window |

Each command writes `<out-dir>/<UTC time>-<command>.json` (parameters without the key, the residencies, providers and
model deployments that answered, every statistic, and pass/fail checks against the design limits) and a `.txt` with the
human summary it also prints. A run with defaults takes about 18 min (`embed-throughput`), 7 min
(`query-under-load`), 2 min (`condense`) and 11 min per answer model (`answer-ttft`): about 40 min for `all`.

Reading the numbers: p99 from fewer than 100 samples is close to the maximum (the summary says so). The client's own
overhead is small but not zero: the `idle` phase of `query-under-load` shows the floor; embedding answers are not
fully parsed (only the vector count and `usage`) so a large batch does not delay a concurrent query in the client.
`64 × 450` tokens exceeds the worker's 16,384 tokens per request (design-processing §11); it is measured anyway because
#125 asks for 64 vs 32 inputs, and the result notes it.

## Smoke test against the local FakeGateway

The FakeGateway enforces what release 1 of the gateway will (a dev key requires `x-fors-max-residency`, refuses a
ceiling that is not the key's level, answers with `x-fors-residency`), which the gateway in `~/git/llmgateway` (v0.8.5)
does not do yet. Its numbers test the script only: label such runs `--smoke` and never record them as results.

```bash
dotnet build src/Kalla.FakeGateway -c Release
FakeGateway__Keys__OnPrem="$(cat onprem.key)" \
FakeGateway__Latency__FirstTokenMs=300 FakeGateway__Latency__PrefillMsPer1kInputTokens=60 \
FakeGateway__Latency__TokenIntervalMs=10 FakeGateway__Latency__AnswerWords=150 FakeGateway__Latency__JitterPercent=20 \
  dotnet run --project src/Kalla.FakeGateway -c Release --no-build --no-launch-profile --urls http://127.0.0.1:18900
python3 -I scripts/capacity/capacity.py all --base-url http://127.0.0.1:18900 --key-file onprem.key \
  --duration 15 --warmup 3 --smoke
```

The fake answers embeddings at once; to see head-of-line blocking in `query-under-load`, point
`FakeGateway__Upstream__Embeddings` at an OpenAI-compatible embeddings server (Ollama with `bge-m3`, as
`Ollama:Enabled` in the AppHost does).

## H8: what the real run needs from the user

1. **A host that reaches the production gateway** (or one with the same on-prem hardware and models) over the network
   path the services will use, with Python 3.12+. The test host (H6) will do if it reaches the gateway.
2. **The pilot team's `OnPrem` gateway key** (`fors-sk-…`, `RequireMaxResidency`) in a file readable only by the person
   running the script; its `MaxConcurrentRequests`, rate limit and budget must allow the load (or the 429s are counted
   and the throughput is the key's, not the hardware's).
3. **The gateway's model aliases** for BGE-M3, the condense model (`Settings.Chat.CondenseModel`) and every pilot answer
   model that can run on-prem.
4. **A time window agreed with the gateway owner** (about 40 min for `all` with one answer model, plus about 11 min per
   further answer model): `embed-throughput` and `query-under-load` saturate the shared on-prem embedding model, and
   `answer-ttft` sends 15k-token prompts, so other teams' calls slow down meanwhile.
5. **What the hardware is** (GPU class or CPU, replicas per model, gateway version, whether release 1 is deployed),
   for `--label` and for comparison with the estimates. Without release 1 the gateway ignores
   `x-fors-max-residency`; the `x-fors-residency` check still makes sure every measured answer came from on-prem.

The user runs `all` (or lets the agent run it on the test host) and hands over the result directory.

## Recording the results

1. Run `all` (or the commands one by one) with `--label "<hardware>, gateway <version>, <date/window>"`.
2. Record each row of `plans/to-verify.md` in `plans/archive/p0/results-a.md`: #98 and #125 from `embed-throughput` and
   `query-under-load`, #99 from the `idle` phase of `query-under-load`, #104 from `condense`, #149 from `answer-ttft`.
   Evidence: the command line, the summary lines and the run ids (also visible in the gateway's usage by
   `x-fors-run-id`). Attach the JSON files to the PR or issue; they are not committed.
3. Compare each number with the estimate printed next to it. A `FAIL` on embedding throughput (below the pilot's 10k
   tokens/s), on condense p95 (above 600 ms) or on answer TTFT p99 (15 s or more) is a shortfall: tell the user, so
   register M2 is decided before P1. Query-embedding p95 under load above 2 × the idle p95 is the performance pass §6
   trigger for a query lane; the summary checks it next to the 60 ms budget.
4. Tick `plans/archive/p0/progress-a.md` §9 boxes 2 and 3.
