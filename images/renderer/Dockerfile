# ume-rag/renderer: Playwright's launch-server with Chromium, nothing else (repo-and-deployment §5.4, connectors design
# §8.3; checked in the A-9 spike, results-a row 29). The worker drives it over WebSocket and fetches every request the
# page makes itself; the container sits only on the internal render network. Contract and run options: README.md.
#   docker build -t ume-rag/renderer:dev images/renderer
# Test: images/renderer/test.sh.

# Playwright 1.64.1 on Ubuntu 24.04 (built 2026-11-01), the same exact version as the worker's Microsoft.Playwright
# NuGet. It ships the browsers and their system libraries but no Playwright package.
FROM mcr.microsoft.com/playwright:v1.65.1-noble@sha256:1507cbfc550a63a6b7396791ca4f00df17beb56342d2447b3f106c306f525579 AS build

WORKDIR /srv/renderer
# playwright-core from the lockfile (integrity-checked by npm ci), installed here so nothing is downloaded at start:
# `npx playwright` would fetch it from npm, and the render network has no route out.
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund && rm -rf /root/.npm /tmp/*
# The base's packages lag behind Ubuntu's security fixes (issue #818: apt, libc and others): upgraded at build time,
# so each build takes the fixes released since. npm and yarn go afterwards: only node runs, and npm's bundled packages
# (undici, brace-expansion) carry their own advisories.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get upgrade -y --no-install-recommends \
    && apt-get clean && rm -rf /var/lib/apt/lists/* /var/log/apt /var/log/dpkg.log /tmp/* \
    && rm -rf /usr/lib/node_modules/npm /usr/lib/node_modules/yarn /usr/bin/npm /usr/bin/npx /usr/bin/yarn /usr/bin/yarnpkg
# Only Chromium's headless shell is launched (headless: true): Firefox, WebKit and full Chromium go (about 1 GB). The
# base leaves /ms-playwright world-writable; read-only for everyone but root here.
RUN rm -rf /ms-playwright/firefox-* /ms-playwright/webkit-* /ms-playwright/chromium-[0-9]* \
    && chmod -R go-w /ms-playwright
COPY --chmod=0644 launch-server.json ./
COPY --chmod=0755 start.sh ./

# One layer of the pruned file system: deleting in a RUN step would leave the browsers in the base's layers.
FROM scratch
COPY --from=build / /
ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    HOME=/tmp
WORKDIR /srv/renderer
# No user of that id in the image: Chromium needs none, and HOME is the tmpfs. Compose runs it read-only with a tmpfs
# /tmp, no capabilities, no-new-privileges and images/renderer/seccomp.json (README.md).
USER 10002:10002
EXPOSE 3000
ENTRYPOINT ["/srv/renderer/start.sh"]

# The port answers, without the secret path: launch-server closes it when the browser exits, and then the process ends.
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD ["node", "-e", "require('net').connect(3000, '127.0.0.1').on('connect', () => process.exit(0)).on('error', () => process.exit(1))"]

# Last on purpose (see images/clamav/Dockerfile): only the labels read these values.
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/thekim1/ume-rag-platform" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.title="ume-rag/renderer"
