# ume-parser

The parser sidecar: FastAPI in front of a supervisor that keeps pre-warmed Docling conversion processes ("slots").
Design: [`plans/parser/design.md`](../../plans/parser/design.md); image and layout:
[`plans/platform/repo-and-deployment.md`](../../plans/platform/repo-and-deployment.md) §2.4, §5.3.

State (P0): the image with Docling, Tesseract `swe` and the models baked in; the supervisor with its slots and
warm-up self-test; `/health/live`, `/health/ready`, `/v1/info`; `/v1/convert` for PDF through the text-layer fast
path or Docling, with the contract's limits and errors; the five schemas in `contracts/parser/v1/`. `/v1/inspect`
answers `501 not_implemented` until P1; then the other formats, `/v1/ner`, the RSS watchdog, recycling and the rest of
design §16 P1.

## Layout

| Path | Content |
|---|---|
| `ume_parser/app.py` | FastAPI app; its lifespan collects `/v1/info` and starts the supervisor |
| `ume_parser/__main__.py` | `python -m ume_parser`: one uvicorn process (no `--workers`, no `--limit-concurrency`) |
| `ume_parser/api.py` | `/v1/convert` (multipart in, `multipart/mixed` out, limits, errors) and the `/v1/inspect` stub |
| `ume_parser/models.py` | the pydantic contract `parser/v1`: request options, results, info, errors |
| `ume_parser/errors.py` | error codes with their HTTP status, `retry` and fixed `detail` (design §7.5) |
| `ume_parser/sniff.py` | format sniffing from the first bytes; refusals |
| `ume_parser/supervisor.py` | spawns the slots, records warm-up, respawns a dead, failed or stuck slot, readiness; hands a job to a free slot and kills it on deadline or disconnect |
| `ume_parser/slot.py`, `convert.py` | the slot process; PDF opening and limits, the Docling pipelines (an LRU of three option sets) and their mapping to blocks, tables and page signals |
| `ume_parser/textlayer.py`, `signals.py` | the pypdfium2 text-layer fast path; the garble score and per-page text-layer facts |
| `ume_parser/selftest.py` | the one-page self-test PDF (text layer, ruled table, scanned strip) and its checks (Docling and fast path) |
| `ume_parser/health.py` | probes and the container health check `python -m ume_parser.health` |
| `ume_parser/info.py` | `/v1/info` and `build_hash` |
| `ume_parser/settings.py`, `limits.py` | `PARSER_*` settings; request limits (design §11.1) |
| `ume_parser/model_manifest.json` | models: Hugging Face repository, pinned commit, SHA-256 per file |
| `scripts/download_models.py` | downloads and checks the manifest's files (image build) |
| `scripts/export_schema.py` | writes `contracts/parser/v1/*.schema.json` from `models.py` |
| `scripts/make_fixtures.py` | writes the synthetic fixtures of `tests/fixtures/parser/` (same bytes on every run) |
| `scripts/update_goldens.py` | writes `tests/fixtures/parser/golden/` from a running parser |
| `tests/` | pytest; `test_convert.py` and `test_offline.py` need the models and Tesseract, so they run in the image; `fake_slot.py` is a slot without Docling for the API and supervisor tests |

## Develop

Needs [uv](https://docs.astral.sh/uv/). From this directory:

```bash
uv sync --frozen
uv run ruff check . && uv run ruff format --check .
uv run pytest
```

Outside the image `test_convert.py` and `test_offline.py` are skipped. To run them, build the test target and run it
with no network:

```bash
docker build --target test -t ume-rag/parser:test .
docker run --rm --network none ume-rag/parser:test
```

## Contract

`models.py` is the contract. After changing it, run `uv run python -m scripts.export_schema` and commit
`contracts/parser/v1/`; CI fails when they differ. Changes to `/v1` are additive only (design §7.7).

`tests/Ume.Rag.ContractTests` runs the worker's client (`src/Ume.Rag.Worker/Parsing/`) against the image, for the
cases in `tests/fixtures/parser/cases.json`, and compares with the goldens:

```bash
docker build -t ume-rag.local/parser:ci .
UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci dotnet test --project ../../tests/Ume.Rag.ContractTests
```

The fixtures are synthetic (`MANIFEST.csv`): change them in `scripts/make_fixtures.py` and rerun it. When a
conversion changes on purpose (a Docling bump, a fast-path change), update the goldens from a running image and
review the diff:

```bash
docker run --rm -p 8000:8000 ume-rag.local/parser:ci
uv run python -m scripts.update_goldens --url http://localhost:8000
```

## Image

```bash
docker build --build-arg REVISION="$(git rev-parse HEAD)" -t ume-rag/parser:dev .
docker run --rm -p 8000:8000 ume-rag/parser:dev
curl -s localhost:8000/health/ready
```

The AppHost builds this Dockerfile (`Parser:Mode=container`, the default) and probes `/health/ready` on port 8000.
Production runs the image with a read-only root, `/tmp` as a 1 GB tmpfs, `cap_drop: [ALL]` and no network
(design §11).

`Parser:Mode=local` runs `uvicorn ume_parser.app:app` from this directory instead. The slots then need the models
and Tesseract with `swe` on the machine:

```bash
uv run python scripts/download_models.py --out .models
export PARSER_MODELS_DIR="$PWD/.models"
```

## Settings

| Variable | Default | Meaning |
|---|---|---|
| `PARSER_WORKERS` | 1 | conversion slots (`PARSER_WORKERS` × `PARSER_THREADS` = the container's CPUs) |
| `PARSER_THREADS` | 4 | torch and OpenMP threads per slot |
| `PARSER_HOST`, `PARSER_PORT` | `0.0.0.0`, 8000 | listener |
| `PARSER_TLS_CERT_FILE`, `PARSER_TLS_KEY_FILE` | unset | serve HTTPS on the same port when both are set |
| `PARSER_MODELS_DIR` | `/opt/models` | baked models (Docling `artifacts_path`) |
| `PARSER_JOBS_DIR` | `/tmp/jobs` | per-request job directories; wiped at start |
| `PARSER_WARMUP_TIMEOUT_S` | 600 | a slot not warm by then is killed and respawned |
| `PARSER_REVISION` | `unknown` | git revision, set by the image build; part of `build_hash` |
| `UME_HEALTHCHECK_URL` | own listener | URL the health check probes |

## Updating pins

Every pin is at least 7 days old when it is set (`AGENTS.md`).

- **Python packages:** move `exclude-newer` in `pyproject.toml` to 7 days before the lock date, update the `==`
  pins of the direct dependencies, run `uv lock`, and check that no file in `uv.lock` has a later `upload-time`.
- **Debian packages:** move `DEBIAN_SNAPSHOT` in the `Dockerfile` (a `snapshot.debian.org` timestamp at least 7
  days old) and update the `=version` pins to what that snapshot has.
- **Base images:** take a digest pushed at least 7 days ago.
- **Models:** change the commit and the SHA-256 of every file in `ume_parser/model_manifest.json` (the Hugging Face
  API lists them: `https://huggingface.co/api/models/<repo>/revision/<commit>?blobs=true`). Any change of a
  package, model or revision changes `build_hash`.
