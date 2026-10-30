# ume-rag/parser: FastAPI + Docling with Tesseract `swe` and the models baked in (parser design §12,
# plans/platform/repo-and-deployment.md §5.3). Build with this directory as the context:
#   docker build -t ume-rag/parser:dev src/parser
#   docker build --target test -t ume-rag/parser:test src/parser && docker run --rm --network none ume-rag/parser:test
# Nothing is downloaded at run time: the venv, the models and the Debian packages are all in the image.

# uv 0.13.5 (released 2026-10-22).
FROM ghcr.io/astral-sh/uv:0.13.5@sha256:32a7bb39b496babbd997b0a39a4d66c126bdaf5661b302bf6f06abda8deca6ff AS uv

# Python 3.12.15 on Debian 13 (trixie), built 2026-10-21. The same base for both stages, so the venv's interpreter
# links resolve in the runtime stage.
FROM python:3.12.15-slim-trixie@sha256:a0f57064cbdcd6285fa45f51237ab0b35ea8ebaed06a1f5973e89e19dfb86704 AS build
COPY --from=uv /uv /usr/local/bin/uv
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv
WORKDIR /src
COPY pyproject.toml uv.lock .python-version ./
# Locked wheels only (`no-build = true`, CPU torch), hashes checked by uv.
RUN --mount=type=cache,target=/root/.cache/uv uv sync --frozen --no-dev --no-install-project
# Pinned commits and SHA-256 per file (ume_parser/model_manifest.json); a mismatch fails the build.
COPY ume_parser/model_manifest.json ume_parser/
COPY scripts/download_models.py scripts/
RUN python scripts/download_models.py --out /opt/models

FROM python:3.12.15-slim-trixie@sha256:a0f57064cbdcd6285fa45f51237ab0b35ea8ebaed06a1f5973e89e19dfb86704 AS runtime
# Debian packages from a dated snapshot at least 7 days old; moving the date is the security update. The snapshot's
# Release files are past their Valid-Until, which is expected for an archive. tesseract-ocr depends on
# tesseract-ocr-eng and -osd; they are installed but the parser only asks for `swe`.
ARG DEBIAN_SNAPSHOT=20261022T000000Z
RUN rm -f /etc/apt/sources.list.d/debian.sources \
    && printf '%s\n' \
        "Types: deb" \
        "URIs: https://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}" \
        "Suites: trixie trixie-updates" \
        "Components: main" \
        "Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp" \
        "" \
        "Types: deb" \
        "URIs: https://snapshot.debian.org/archive/debian-security/${DEBIAN_SNAPSHOT}" \
        "Suites: trixie-security" \
        "Components: main" \
        "Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp" \
        > /etc/apt/sources.list.d/snapshot.sources \
    && apt-get -o Acquire::Check-Valid-Until=false update \
    && apt-get install -y --no-install-recommends \
        tini=0.19.0-3+b8 \
        tesseract-ocr=5.5.0-1+b1 \
        tesseract-ocr-swe=1:4.1.0-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /opt/venv /opt/venv
COPY --from=build /opt/models /opt/models
WORKDIR /app
COPY pyproject.toml ./
COPY ume_parser ./ume_parser
RUN python -m compileall -q ume_parser

ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/thekim1/ume-rag-platform" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.title="ume-rag/parser"

# No runtime downloads (parser design §11): Hugging Face offline, caches on the tmpfs, models from /opt/models.
ENV PATH=/opt/venv/bin:$PATH \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HOME=/tmp \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_DATASETS_OFFLINE=1 \
    HF_HOME=/tmp/cache/huggingface \
    TORCH_HOME=/tmp/cache/torch \
    XDG_CACHE_HOME=/tmp/cache \
    TESSDATA_PREFIX=/usr/share/tesseract-ocr/5/tessdata \
    PARSER_MODELS_DIR=/opt/models \
    PARSER_REVISION=${REVISION}

USER 10001
EXPOSE 8000
HEALTHCHECK --interval=10s --timeout=5s --start-period=10m --retries=3 CMD ["python", "-m", "ume_parser.health"]
ENTRYPOINT ["tini", "--"]
CMD ["python", "-m", "ume_parser"]

# Runtime plus the dev dependencies and the tests, for test_offline.py with --network none. The tests generate the
# fixtures with scripts/make_fixtures.py, the same bytes as tests/fixtures/parser/ in the repository.
FROM runtime AS test
USER root
COPY --from=uv /uv /usr/local/bin/uv
COPY uv.lock .python-version ./
RUN --mount=type=cache,target=/root/.cache/uv \
    UV_PROJECT_ENVIRONMENT=/opt/venv UV_PYTHON_DOWNLOADS=never UV_LINK_MODE=copy uv sync --frozen --no-install-project
COPY scripts ./scripts
COPY tests ./tests
USER 10001
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]

# The same tests as a build step without network, so CI runs them while building and never loads the test image:
#   docker build --target test-run src/parser
FROM test AS test-run
RUN --network=none python -m pytest -q -p no:cacheprovider

# The default target is the runtime image.
FROM runtime
