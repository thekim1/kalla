# R1 release test and chat overhead run

The release test of [repo-and-deployment §8.3](../../plans/platform/repo-and-deployment.md) and the overhead run of
[chat design §16.2](../../plans/chat-api/design.md) items 3 and 4, run by [`perf.yml`](../../.github/workflows/perf.yml)
or by hand on the test host. Modelled on the gateway's `benchmarks/compare` (`k6/chat.js`, `latency.sh`,
`latency_report.py`).

| File | Does |
|---|---|
| `run.sh` | The harness: warm-up, then R1's phase 1 (idle) and phase 2 (during `--ingest-cmd`), or one overhead phase; writes `report.md` |
| `k6/chat.js` | The load: streamed `POST /v1/chat/completions` at a constant arrival rate (k6 1.3.0, the gateway's pin, by digest); whole-stream time and failed streams |
| `stream_probe.py` | Time to first token next to k6's load (k6 cannot read a stream while it arrives; the gateway's `latency.sh` uses its own probe for the same reason). Standard library only |
| `report.py` | The Markdown report and the pass rule; exit 0 pass, 1 fail, 2 missing results |
| `requests/` | The request bodies and headers k6 and the probe share: `question.json` (R1), `openwebui.json` (overhead run, Open WebUI-shaped) |
| `ingest.sh` | Phase 2's background load: enqueues the crawl of the page copy and the OCR batch on the deployment, exits when the ingestion zone is idle |
| `site/build_site.py` | The page copy: a generated **placeholder** (2 000 Swedish pages, 200 linked PDFs, 50 scanned PDFs for the OCR batch, from `tests/fixtures/parser/`), or `sitemap.xml` and `robots.txt` for a real copy |
| `site/take-copy.sh` | Takes the real umea.se copy from a URL list. **Live only**, by hand, never in CI |
| `site/serve.sh`, `site/nginx.conf` | Serves the copy with nginx (1.30.6-alpine, by digest) on the runner |
| `dry-run.sh` | The harness against FakeGateway directly (below) |

## Pass rule (R1)

Chat p95 time to first token in phase 2 ≤ 1.10 × phase 1, no error responses (status, error event, stream without
`data: [DONE]` or without answer text, connection failures; k6's dropped iterations count too), no Postgres connection
refusals (`too many clients already`, reserved slots, role limit, or SQLSTATE 53300 in the logs of `kalla-postgres`,
`kalla-api`, `kalla-admin`, `kalla-worker` during the phase), and the background load still running when phase 2 ends.

Load: `--rate` 5 streamed messages/s in total for 10 minutes per phase, of which the stream probe sends `--probe-rate`
1/s (≈ 600 TTFT samples per phase) and k6 the rest. Behind the chat API the deployment's gateway is FakeGateway with
`FirstTokenMs=400`, `TokenIntervalMs=25`, `JitterPercent=30` and 200-word answers.

## Dry run (not R1)

```bash
benchmarks/release-test/dry-run.sh --duration 60     # builds and starts FakeGateway, then run.sh --dry-run
# Docker Desktop (WSL): the k6 container does not share the distribution's network
benchmarks/release-test/dry-run.sh --duration 60 -- --k6-base-url http://host.docker.internal:5480
```

The load goes straight to FakeGateway's `/v1/chat/completions` with the R1 latency profile: no chat API, no crawl, no
OCR batch, not the test host. It proves the harness and the report, and the report says so. The checks it cannot make
(Postgres refusals without `--deploy-env`, the background load without `--ingest-cmd`) show "not checked" there; in a
run without `--dry-run` they fail. `perf.yml` runs it every Monday and on dispatch (mode `dry-run`).

## R1 on the test host (P1b live item 22)

Once, before the first run:

1. Deploy the release under test with the bundle (`docs/production-setup.md`), its gateway URL pointing at a
   FakeGateway with the R1 profile (`FakeGateway__Latency__FirstTokenMs=400`, `__TokenIntervalMs=25`,
   `__JitterPercent=30`, `__AnswerWords=200`) on the deployment's `llm` network.
2. The page copy: `site/take-copy.sh --urls umea-2000.txt --out /srv/r1-site --base-url http://r1-site:8080` (with
   the web team's consent), or the placeholder (`site/build_site.py generate`). Serve it on the deployment's network:
   `site/serve.sh start /srv/r1-site --network kalla_llm`, and set the worker's `Crawl__AllowedPrivateRanges` to that
   network's subnet (the egress guard blocks private addresses otherwise).
3. In the admin: a collection with a crawl source (seed `http://r1-site:8080/`, its sitemap) and an upload source with
   the 50 PDFs of `ocr-batch/` uploaded but not committed; an assistant on the collection and an API key for it.

Then each run (or `perf.yml`, mode `r1`, with the repository variables and the `test-host` environment it names):

```bash
benchmarks/release-test/run.sh --base-url https://localhost:8543 --model <assistant> --key-file key \
  --ca-file deploy/certs/ca.pem --deploy-env deploy/.env \
  --ingest-cmd "benchmarks/release-test/ingest.sh --deploy-env deploy/.env --crawl-source <id> \
                --upload-source <id> --upload-batch <id>"
```

The overhead run: `--mode overhead` without `--ingest-cmd` (Open WebUI-shaped requests; pass `--header
'X-OpenWebUI-User-Jwt: …'` if the target checks it). It reports TTFT minus FakeGateway's first token, throughput and
CPU per message; `rag.chat.overhead` p50/p95/p99 comes from the deployment's metrics, not from this harness.
