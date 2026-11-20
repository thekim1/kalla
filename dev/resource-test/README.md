# Local resource test

A short measurement of what each part of the platform uses in memory and CPU, on a developer's machine, against
FakeGateway: idle, while crawling a generated site, and under retrieval load. It also covers the parser on its own when
an image is given. It answers "how much hardware does the platform need" between real measurements on the test host
(R1, [`../../benchmarks/release-test/`](../../benchmarks/release-test/README.md)), and gives the before/after numbers
for footprint changes (B-27, #504).

**Local and on demand only.** No workflow runs it, and `run.sh` refuses to start when `CI` or `GITHUB_ACTIONS` is set.
It is kept short so it writes little to disk: about 10–30 MB of database growth per run, the generated site
(~1.5 MB) in a temporary directory, and the parser's job files on a tmpfs. Nothing is built or pulled when the images are
already there.

## Run

```bash
dev/resource-test/run.sh                                 # stack only, ~6 minutes
dev/resource-test/run.sh --parser-image <parser image>   # plus the parser, ~1 minute more
```

| Option | Default | |
|---|---|---|
| `--out DIR` | a new `kalla-resource-test.*` directory under `$TMPDIR` | `report.md`, `samples.tsv`, `facts.txt`, the site |
| `--pages N` | 150 | generated HTML pages (the crawl runs at the politeness rate, about 1 page/s) |
| `--idle S` / `--load S` | 60 / 20 | idle sample; length of each retrieval load phase (1 and 8 clients) |
| `--ingest-timeout S` | 900 | longest wait for the job queue to empty |
| `--parser-image IMAGE` | none: no parser phase | an existing image, e.g. `docker build -t kalla.local/parser:ci src/parser` (several GB: build only when needed) |
| `--keep` | off | leave the AppHost running afterwards |

Needs Docker, the Aspire CLI, .NET, Python 3.12+ and an AppHost that has run once (its user secrets). It refuses to start
while an AppHost is running or when port 7418 is taken.

## What it does

1. Starts the AppHost with `Parser__Mode=off`, `Render__Mode=off`, `OpenWebUi__Enabled=false` (each would build or pull
   gigabytes) and `E2E__Enabled=true` (the worker may crawl loopback).
2. Adds port 7418 to `Settings.Crawl.allowedPorts` and restarts the worker; the original value is restored on exit.
3. Samples 60 s idle.
4. Generates a Swedish site with `benchmarks/release-test/site/build_site.py` (HTML only, no PDFs since the parser is
   off), serves it on `127.0.0.1:7418`, seeds a URL-list source on a new collection `restest-<time>` with
   `scripts/dev/seed_url_list.py --api-key`, and samples until the job queue is empty.
5. Runs `/v1/retrieve` load with 1 and then 8 clients (`load.py`), sampling during each.
6. With `--parser-image`: starts that image with the Compose limits (2 CPUs, 3.5 GB, 1 worker × 2 threads, read-only
   root, `/tmp` on tmpfs), times the warm-up and converts five fixtures from `tests/fixtures/parser/`.
7. Writes `report.md` (`report.py`), then stops what it started (site, parser container, AppHost) and deletes the API
   key file. The key is held only in a mode-600 temporary file during the run.

`sample.py` reads the .NET services from `/proc` (they run on the host under the AppHost: RSS, and CPU from tick
deltas between samples) and the containers from `docker stats`.

Each run adds a collection, a source and a key to the development database. Wipe the volume to reset.

## What the numbers mean and do not mean

- The .NET services are **Debug builds** run by the AppHost, not the release images; Postgres runs with stock settings
  (128 MB `shared_buffers`), not `deploy/postgres/postgresql.conf` (768 MB). Postgres memory therefore stays low here.
  Add `shared_buffers` and some working memory for the production figure.
- The corpus is small (hundreds of documents). Postgres CPU per query and database size per chunk change with corpus
  size: on a large collection the exact scan gives way to HNSW and the statistics read more.
- Query embeddings come from FakeGateway, and repeated queries hit the query-embedding cache. Model time is the
  gateway's and is not measured.
- Not covered: the renderer (stream a measured 167–215 MB on fixtures, A-9/A-10), Open WebUI (limit 1.5 GB), the chat
  path (retrieval plus streaming relay), backups, and the release images' start-up.

## Results

### 2026-11-08, `4adbe67` (stream b)

WSL2 on a 16-CPU, 19 GB developer machine. The parser image was built on 2026-11-06, before #419: it loads the NER
model at start (current `main` loads it on first use and stops idle convert slots, about 1 GB less at idle). The
report as `run.sh` wrote it, headings demoted:


- **date:** 2026-11-08T17:28:51+01:00
- **commit:** 4adbe67
- **host:** 16 CPUs, 19 GB RAM
- **build:** AppHost (Debug .NET builds, stock Postgres settings), FakeGateway; no parser, renderer or Open WebUI in the stack
- **retrieval, 1 client(s):** clients=1 requests=2825 rps=141 p50=7ms p95=8ms p99=10ms codes={200: 2825}
- **retrieval, 8 client(s):** clients=8 requests=12082 rps=604 p50=13ms p95=18ms p99=22ms codes={200: 12082}
- **parser ready after:** 8 s
- **parser text-layer.pdf (text_layer):** success pages 3 total 52 ms
- **parser text-layer.pdf (docling):** success pages 3 total 2169 ms
- **parser scanned.pdf (docling):** success pages 1 total 1061 ms
- **parser text.docx (docling):** success pages None total 53 ms
- **parser slides.pptx (docling):** success pages 2 total 53 ms
- **ingestion:** 159 documents, 759 chunks in 172 s (150 generated pages, HTML only)
- **database growth:** 6 MB (9 KB per chunk)

#### Memory: max MB per phase

| Component | idle | ingest | load-1 | load-8 | parser |
|---|---:|---:|---:|---:|---:|
| AdminApi | 210 | 211 | 214 | 220 | 220 |
| Api | 176 | 179 | 277 | 319 | 303 |
| FakeGateway | 117 | 121 | 154 | 154 | 154 |
| Worker | 310 | 359 | 361 | 361 | 363 |
| clamav | 1050 | 1050 | 1050 | 1050 | 1050 |
| fixture-site | 14 | 14 | 14 | 14 | 14 |
| keycloak | 860 | 872 | 871 | 871 | 871 |
| parser | – | – | – | – | 1954 |
| postgres | 110 | 167 | 175 | 208 | 163 |

#### CPU: mean / max cores per phase

| Component | idle | ingest | load-1 | load-8 | parser |
|---|---:|---:|---:|---:|---:|
| AdminApi | 0.02 / 0.03 | 0.01 / 0.01 | 0.01 / 0.01 | 0.02 / 0.06 | 0.00 / 0.01 |
| Api | 0.01 / 0.03 | 0.01 / 0.02 | 0.48 / 0.79 | 1.26 / 1.76 | 0.01 / 0.01 |
| FakeGateway | 0.01 / 0.03 | 0.00 / 0.01 | 0.07 / 0.29 | 0.00 / 0.01 | 0.00 / 0.00 |
| Worker | 0.03 / 0.11 | 0.06 / 0.31 | 0.01 / 0.02 | 0.01 / 0.01 | 0.01 / 0.01 |
| clamav | 0.00 / 0.00 | 0.00 / 0.02 | 0.00 / 0.00 | 0.00 / 0.00 | 0.00 / 0.00 |
| fixture-site | 0.00 / 0.00 | 0.00 / 0.00 | 0.00 / 0.00 | 0.00 / 0.00 | 0.00 / 0.00 |
| keycloak | 0.00 / 0.01 | 0.00 / 0.01 | 0.00 / 0.01 | 0.00 / 0.00 | 0.00 / 0.00 |
| parser | – | – | – | – | 0.52 / 1.54 |
| postgres | 0.00 / 0.01 | 0.04 / 0.10 | 0.46 / 0.78 | 2.78 / 4.74 | 0.00 / 0.01 |

Total of the max memory per phase (all components): idle 2.8 GB, ingest 2.9 GB, load-1 3.0 GB, load-8 3.1 GB, parser 5.0 GB

From the exploratory runs the same day (same stack, by hand):

- **Parser peak** while converting: 2.17 GB (1 s sampling; the report's 2 s sampling saw 1.95 GB).
- **Larger crawl:** 309 pages (1,509 chunks) in about 5 minutes, at about 1 page/s: the politeness delay, not
  resources. The worker stayed under 0.3 cores.
- **Retrieval CPU:** about 8 ms per query in total (Postgres 5 ms, kalla-api 3 ms), so one core serves about 120
  retrievals/s on this corpus. 8 clients: 470–690 requests/s, p95 16–22 ms, no errors.
- **`jit` on or off** (for #500): 669 vs 689 requests/s, within noise. At this corpus size no statement reaches
  `jit_above_cost`, so #500 is still unmeasured where it matters (a large collection).

#### Minimum hardware for the platform (estimate from these numbers)

Peak working set per component with the production settings: Postgres about 1.0–1.2 GB (768 MB `shared_buffers`
plus working memory; 0.1–0.2 GB measured with stock settings), kalla-api 0.3 GB, kalla-admin 0.2 GB, worker 0.4 GB (plus
about 0.17 GB for Playwright's driver when rendering), parser 2.2 GB peak (less on current `main`, and close to
nothing once its slots are cold), ClamAV 1.05 GB, renderer about 0.2 GB (A-9/A-10). Total **about 5.5–5.7 GB** at peak,
4.5 GB with the parser cold, plus 1–1.5 GB for the OS and Docker. The Compose `mem_reservation` values alone add up to
about 6.6 GB.

| | vCPU | RAM | SSD | |
|---|---|---|---|---|
| **Floor** | 4 | 8 GB | 50 GB | runs, with no headroom for a large PDF or a ClamAV reload |
| **Realistic minimum** | 4 | 16 GB | 100 GB | the 16 GB profile of `deploy/` (repo-and-deployment §6.2) |
| Pilot host as designed | 8 | 16–24 GB | | faster first bulk load of PDFs; R1 sets the final CPU sizing |

- **CPU:** retrieval is cheap (above). Ingestion of HTML is politeness-bound. PDFs are the expensive part: about
  0.75 s per page on 2 cores with the layout model, about 1.1 s per scanned page with OCR. With 4 vCPU the parser keeps
  its 2-CPU development cap, and a bulk PDF load takes longer.
- **Disk:** about 9 KB per chunk in the database on this corpus (vector, text, search vector and indexes; it grows with
  superseded sets kept 14 days). A pilot of 50–100k chunks is 0.5–1 GB of database, plus WAL and pgBackRest backups,
  stored raw content and uploads, and the images (the parser alone about 2.6 GB).
- **Not included:** the LLMs (the gateway's host), Keycloak (0.86 GB measured; shared with the gateway, #468) and Open
  WebUI (limit 1.5 GB; on its own VM in P2). Add them if they share the host.
- **To confirm** with the pilot corpus and the release images: R1 on the test host (repo-and-deployment §8.3).
