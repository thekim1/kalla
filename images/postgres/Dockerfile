# ume-rag/postgres: the stock pgvector image plus the PGDG pgbackrest package and its configuration, nothing else
# (plans/platform/repo-and-deployment.md §5.2, simplification pass §14). Build from the repository root:
#   docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
# Dockerfile.dockerignore next to this file limits the context to the one file copied below.
# postgresql.conf, pg_hba.conf, init.sql, certificates and the pgBackRest repository settings are mounted, not baked.

# pgvector 0.8.7 on PostgreSQL 17 (Debian trixie), pushed 2026-10-01; the same pin as the AppHost and TestKit.
FROM pgvector/pgvector:0.8.7-pg17-trixie@sha256:7a7e9f22015b67edb4bef5c59daeebcd7e74bfa570df6ce60ae01237c8648a84

ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/thekim1/ume-rag-platform" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.title="ume-rag/postgres"

# From the PGDG repository the base image already configures. 2.59.3 (upstream 2026-10-04, PGDG trixie build
# 2026-10-05); 2.60.0 is not yet 7 days old. The package's /etc/pgbackrest.conf is replaced by the file below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends pgbackrest=2.59.3-1.pgdg13+1 \
    && rm -rf /var/lib/apt/lists/* /etc/pgbackrest.conf \
    && install -d -m 0755 /etc/pgbackrest /etc/pgbackrest/conf.d

COPY --chmod=0644 deploy/pgbackrest/pgbackrest.conf /etc/pgbackrest/pgbackrest.conf

USER 999:999
