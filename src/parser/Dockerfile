# ume-rag/parser: FastAPI + Docling with Tesseract `swe` and the models baked in (parser design §12,
# plans/platform/repo-and-deployment.md §5.3). Build with this directory as the context:
#   docker build -t ume-rag/parser:dev src/parser
#   docker build --target test src/parser    (runs the tests during the build, with no network)
# Nothing is downloaded at run time: the venv, the models and the Debian packages are all in the image.

# uv 0.13.5 (released 2026-10-22).
FROM ghcr.io/astral-sh/uv:0.13.8@sha256:5cc1805e2426484abca4ad46ea783bab281fdb225fd82d8ce6d66a81254bac85 AS uv

# Python 3.12.16 on Debian 13 (trixie), built 2026-11-05. The same base for both stages, so the venv's interpreter
# links resolve in the runtime stage.
FROM python:3.15.2-slim-trixie@sha256:65a65fc7b30b06cb6df4930fd4650697cb23f4c2698ab81fc87e4b031bc36928 AS build
COPY --from=uv /uv /usr/local/bin/uv
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv
WORKDIR /src
COPY pyproject.toml uv.lock .python-version ./
# Locked wheels only (`no-build = true`, CPU torch), hashes checked by uv.
RUN --mount=type=cache,target=/root/.cache/uv uv sync --frozen --no-dev --no-install-project
# Drop what the runtime never loads (A-8): torch's C++ test binaries, protoc and C++ headers, and the packages' own
# test suites (`tests`/`test` directories). Package metadata (dist-info, licence files) stays, so scripts/licences.py
# renders the same LICENSES.md; torch_shm_manager stays (torch.multiprocessing's file_system sharing). The test stage
# runs the parser suite on this pruned venv.
RUN SP=/opt/venv/lib/python3.12/site-packages \
    && rm -rf "$SP/torch/test" "$SP/torch/include" "$SP/torch/lib/libtorchbind_test.so" \
    && find "$SP/torch/bin" -mindepth 1 ! -name torch_shm_manager -exec rm -rf {} + \
    && find "$SP" -depth -type d \( -name tests -o -name test \) ! -path '*.dist-info*' -exec rm -rf {} +
# Pinned commits and SHA-256 per file (ume_parser/model_manifest.json); a mismatch fails the build.
COPY ume_parser/model_manifest.json ume_parser/
COPY scripts/download_models.py scripts/
RUN python scripts/download_models.py --out /opt/models

FROM python:3.15.2-slim-trixie@sha256:65a65fc7b30b06cb6df4930fd4650697cb23f4c2698ab81fc87e4b031bc36928 AS runtime
# Debian packages from a dated snapshot at least 7 days old, the only apt source (the base's debian.sources is
# removed). The base's own packages are upgraded from the same snapshot before the installs, so moving the date is the
# security update for them too. The snapshot's Release files are past their Valid-Until, which is expected for an
# archive. tesseract-ocr depends on tesseract-ocr-eng and -osd; they are installed but the parser only asks for `swe`.
# LibreOffice (no GUI: Writer, Calc, Impress) converts legacy Word, Excel, PowerPoint and RTF to OOXML before Docling
# reads them (ume_parser/legacy.py).
ARG DEBIAN_SNAPSHOT=20261109T000000Z
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
    && DEBIAN_FRONTEND=noninteractive apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends \
        tini=0.19.0-3+b8 \
        tesseract-ocr=5.5.0-1+b1 \
        tesseract-ocr-swe=1:4.1.0-2 \
        libreoffice-writer-nogui=4:25.2.3-2+deb13u9 \
        libreoffice-calc-nogui=4:25.2.3-2+deb13u9 \
        libreoffice-impress-nogui=4:25.2.3-2+deb13u9 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /opt/venv /opt/venv
COPY --from=build /opt/models /opt/models
# The models' licence texts beside their weights, and the image's third-party notices (scripts/licences.py).
COPY licences/models/ /opt/models/
COPY LICENSES.md /usr/share/doc/ume-rag-parser/LICENSES.md
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

# Runtime plus the dev dependencies and the tests (`test-env`, where scripts/licences.py regenerates LICENSES.md), then
# the tests run during the build with no network at all (BuildKit's `RUN --network=none`: loopback only),
# test_offline.py and test_licences.py included, so a failing test fails the build and a built test image has passed. The tests generate the fixtures with scripts/make_fixtures.py, the same bytes as
# tests/fixtures/parser/ in the repository. `docker run --rm --network none` on the image runs them again.
FROM runtime AS test-env
USER root
COPY --from=uv /uv /usr/local/bin/uv
COPY uv.lock .python-version ./
RUN --mount=type=cache,target=/root/.cache/uv \
    UV_PROJECT_ENVIRONMENT=/opt/venv UV_PYTHON_DOWNLOADS=never UV_LINK_MODE=copy uv sync --frozen --no-install-project
COPY scripts ./scripts
COPY tests ./tests
USER 10001

FROM test-env AS test
RUN --network=none python -m pytest -q -p no:cacheprovider
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]

# The default target is the runtime image.
FROM runtime
