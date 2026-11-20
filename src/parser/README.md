# ume-parser

The parser sidecar: FastAPI in front of a supervisor that keeps pre-warmed Docling conversion processes ("slots").
Design: [`plans/parser/design.md`](../../plans/parser/design.md); image and layout:
[`plans/platform/repo-and-deployment.md`](../../plans/platform/repo-and-deployment.md) §2.4, §5.3.

State (P1a item 5): every P1 format of design §3 (PDF through the text-layer fast path or Docling; DOCX, PPTX, XLSX,
ODT, ODS, ODP through Docling's declarative backends; JPEG, PNG and TIFF through Docling's OCR), with legacy Office,
RTF and e-mail refused as `format_deferred`; page ranges with `partial` for PDF and TIFF; `/v1/inspect` on its own
process; `/v1/ner` on `ner-0`; the bomb and size limits; the RSS watchdog and recycling; the garble detector with its
bigram table. Not yet: OpenTelemetry traces and metrics, the licence inventory, `bench.py` (design §16 P1).

## Layout

| Path | Content |
|---|---|
| `ume_parser/app.py` | FastAPI app; its lifespan collects `/v1/info` and starts the supervisor |
| `ume_parser/__main__.py` | `python -m ume_parser`: one uvicorn process (no `--workers`, no `--limit-concurrency`) |
| `ume_parser/api.py` | `/v1/convert` (multipart in, `multipart/mixed` out), `/v1/inspect` and `/v1/ner`; limits, errors |
| `ume_parser/models.py` | the pydantic contract `parser/v1`: request options, results, info, errors |
| `ume_parser/errors.py` | error codes with their HTTP status, `retry` and fixed `detail` (design §7.5) |
| `ume_parser/sniff.py` | format sniffing: first bytes and OLE stream names (API process), a zip's parts (worker processes); refusals |
| `ume_parser/supervisor.py` | the lanes `convert`, `inspect` and `ner`: spawn, warm-up, respawn, readiness, the RSS watchdog and recycling; hands a job to a free process and kills it on deadline or disconnect |
| `ume_parser/slot.py` | the worker processes' pipe protocol and the conversion slot |
| `ume_parser/convert.py`, `assemble.py` | conversion by format: PDF ranges and the Docling pipelines (an LRU of option sets), images and TIFF frame ranges, the declarative formats; the mapping to blocks, tables and page signals |
| `ume_parser/inspect.py` | `inspect-0`: page count and per-page signals without models, under `RLIMIT_AS` |
| `ume_parser/ner.py` | `ner-0`: the KB BERT NER model, label mapping and code-point spans |
| `ume_parser/limits.py` | request limits and the bomb pre-checks (zip, pixels, frames) |
| `ume_parser/textlayer.py`, `signals.py`, `garble_bigrams.json` | the pypdfium2 text-layer fast path; the garble score with its bigram table, and per-page text-layer facts |
| `ume_parser/selftest.py` | the one-page self-test PDF (text layer, ruled table, scanned strip) and its checks (Docling and fast path) |
| `ume_parser/health.py` | probes and the container health check `python -m ume_parser.health` |
| `ume_parser/info.py` | `/v1/info` and `build_hash` |
| `ume_parser/settings.py` | `PARSER_*` and `NER_*` settings |
| `ume_parser/model_manifest.json` | models: Hugging Face repository, pinned commit, SHA-256 per file |
| `scripts/download_models.py` | downloads and checks the manifest's files (image build) |
| `scripts/export_schema.py` | writes `contracts/parser/v1/*.schema.json` from `models.py` |
| `scripts/make_fixtures.py` | writes the synthetic fixtures of `tests/fixtures/parser/` (same bytes on every run) |
| `scripts/update_goldens.py` | writes `tests/fixtures/parser/golden/` from a running parser |
| `scripts/build_bigrams.py` | writes `ume_parser/garble_bigrams.json` from the repository's Swedish word list and English documents |
| `scripts/ocr_check.py` | the OCR acceptance check (design §9.3): å/ä/ö recall, CER, ms/page through a running parser |
| `scripts/compare_paths.py` | text-layer fast path against Docling, page by page, for a URL list of PDFs (to-verify #132) |
| `scripts/parser_http.py` | the scripts' client of a running parser |
| `tests/` | pytest; `test_convert.py`, `test_acceptance.py`, `test_offline.py` and the model part of `test_ner.py` need the models and Tesseract, so they run in the image; `fake_slot.py` gives workers without Docling for the API and supervisor tests |

## Develop

Needs [uv](https://docs.astral.sh/uv/). From this directory:

```bash
uv sync --frozen
uv run ruff check . && uv run ruff format --check .
uv run pytest
```

Outside the image the tests that need the models or Tesseract are skipped. To run them, build the test target: the tests
run during the build with no network (`RUN --network=none`), and a failing test fails the build. CI's `parser-image`
job (`.github/workflows/parser.yml`) does the same.

```bash
docker build --target test -t kalla/parser:test .
docker run --rm --network none kalla/parser:test   # runs them again
```

## Measuring

Both scripts talk to a running parser and write a Markdown report of numbers (no document text):

```bash
# OCR acceptance (design §9.3): the repository's synthetic sample, or a directory of page images with <stem>.txt
uv run python -m scripts.ocr_check --sample /tmp/ocr --generate --url http://localhost:8000 --out ocr.md
# Fast path against Docling (to-verify #132): one PDF URL per line
uv run python -m scripts.compare_paths --urls pdfs.txt --url http://localhost:8000 --out compare.md
```

## Contract

`models.py` is the contract. After changing it, run `uv run python -m scripts.export_schema` and commit
`contracts/parser/v1/`; CI fails when they differ. Changes to `/v1` are additive only (design §7.7).

`tests/Kalla.ContractTests` runs the worker's client (`src/Kalla.Worker/Parsing/`) against the image, for the
cases in `tests/fixtures/parser/cases.json`, and compares with the goldens:

```bash
docker build -t kalla.local/parser:ci .
KALLA_TEST_PARSER_IMAGE=kalla.local/parser:ci dotnet test --project ../../tests/Kalla.ContractTests
```

The fixtures are synthetic (`MANIFEST.csv`): change them in `scripts/make_fixtures.py` and rerun it. When a
conversion changes on purpose (a Docling bump, a fast-path change), update the goldens from a running image and
review the diff:

```bash
docker run --rm -p 8000:8000 kalla.local/parser:ci
uv run python -m scripts.update_goldens --url http://localhost:8000
```

## Image

```bash
docker build --build-arg REVISION="$(git rev-parse HEAD)" -t kalla/parser:dev .
docker run --rm -p 8000:8000 kalla/parser:dev
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
| `PARSER_SLOT_MAX_RSS_MB` | 2800 | RSS watchdog: a slot above it (with Tesseract) is killed, its request answers `resource_limit` |
| `PARSER_RECYCLE_JOBS` | 50 | a slot is restarted after this many conversions |
| `PARSER_RECYCLE_RSS_GROWTH` | 0.4 | a slot is restarted when idle and its RSS exceeds its warm RSS by this share |
| `PARSER_IDLE_STOP_S` | 900 | a convert slot without a job this long stops (`cold`); the next convert gets `not_ready` and warms it; 0 = always warm |
| `PARSER_PIPELINE_CACHE` | 3 | Docling option sets kept warm per slot |
| `NER_ENABLED` | true | start `ner-0`; when false `/v1/ner` answers `not_ready` and `/v1/info` has `ner: null` |
| `NER_THREADS`, `NER_QUANTIZE`, `NER_QUEUE` | 2, true, 4 | `ner-0`'s torch threads, int8 quantisation, requests that may wait for it |
| `NER_PERSON_LABELS` | `PER,PRS` | model labels counted as `person` (mixed labels with them are `person_ambiguous`) |
| `NER_RECYCLE_REQUESTS` | 5000 | `ner-0` is restarted after this many requests |
| `NER_IDLE_STOP_S` | 900 | `ner-0` starts on the first `/v1/ner` (answered `not_ready` while it warms) and stops after this long without one; 0 = warm from start |
| `FORS_HEALTHCHECK_URL` | own listener | URL the health check probes |

## Updating pins

Every pin is at least 7 days old when it is set (`AGENTS.md`).

- **Python packages:** update the `==` pins of the direct dependencies and run `uv lock`. `exclude-newer = "7 days"`
  in `pyproject.toml` is relative (`exclude-newer-span` in `uv.lock`), so every lock takes only files uploaded at least
  7 days before it; nothing is moved by hand. A security fix younger than that is a deliberate exception: an
  `exclude-newer-package` entry for that one package, removed once the fix is 7 days old.
- **Debian packages:** move `DEBIAN_SNAPSHOT` in the `Dockerfile` (a `snapshot.debian.org` timestamp at least 7
  days old) and update the `=version` pins to what that snapshot has.
- **Base images:** take a digest pushed at least 7 days ago.
- **Models:** change the commit and the SHA-256 of every file in `ume_parser/model_manifest.json` (the Hugging Face
  API lists them: `https://huggingface.co/api/models/<repo>/revision/<commit>?blobs=true`). Any change of a
  package, model or revision changes `build_hash`.
