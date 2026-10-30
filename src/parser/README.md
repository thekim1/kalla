# ume-parser

The parser sidecar: FastAPI in front of a supervisor that keeps pre-warmed Docling conversion processes ("slots").
Design: [`plans/parser/design.md`](../../plans/parser/design.md); image and layout:
[`plans/platform/repo-and-deployment.md`](../../plans/platform/repo-and-deployment.md) §2.4, §5.3.

State (P0, part 1): the image with Docling, Tesseract `swe` and the models baked in, the supervisor with its slots
and warm-up self-test, `/health/live`, `/health/ready` and `/v1/info`. Still to come: `/v1/convert` and the
`contracts/parser/v1/` schemas (P0), then `/v1/inspect`, the text-layer fast path, `/v1/ner` and the rest of
design §16 P1.

## Layout

| Path | Content |
|---|---|
| `ume_parser/app.py` | FastAPI app; its lifespan collects `/v1/info` and starts the supervisor |
| `ume_parser/__main__.py` | `python -m ume_parser`: one uvicorn process (no `--workers`, no `--limit-concurrency`) |
| `ume_parser/supervisor.py` | spawns the slots, records warm-up, respawns a dead, failed or stuck slot, readiness |
| `ume_parser/slot.py`, `convert.py` | the slot process; the default Docling option set (Tesseract `swe`, tables `accurate`, scale 2.0) |
| `ume_parser/selftest.py` | the one-page self-test PDF (text layer, ruled table, scanned strip) and its check |
| `ume_parser/health.py` | probes and the container health check `python -m ume_parser.health` |
| `ume_parser/info.py` | `/v1/info` and `build_hash` |
| `ume_parser/settings.py`, `limits.py` | `PARSER_*` settings; request limits (design §11.1) |
| `ume_parser/model_manifest.json` | models: Hugging Face repository, pinned commit, SHA-256 per file |
| `scripts/download_models.py` | downloads and checks the manifest's files (image build) |
| `tests/` | pytest; `test_offline.py` needs the models and Tesseract, so it runs in the image |

## Develop

Needs [uv](https://docs.astral.sh/uv/). From this directory:

```bash
uv sync --frozen
uv run ruff check . && uv run ruff format --check .
uv run pytest
```

Outside the image `test_offline.py` is skipped. To run it, build the test target and run it with no network:

```bash
docker build --target test -t ume-rag/parser:test .
docker run --rm --network none ume-rag/parser:test
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
