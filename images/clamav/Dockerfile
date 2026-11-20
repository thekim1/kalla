# kalla/clamav: the official ClamAV 1.4 LTS image plus our clamd.conf and freshclam.conf, nothing else
# (plans/platform/repo-and-deployment.md §5.5). The AppHost builds it from this directory; by hand:
#   docker build -t kalla/clamav:dev images/clamav
# Mount a volume on /var/lib/clamav: the first start downloads the signatures from database.clamav.net (no mirror),
# later starts reuse them, and freshclam updates them FRESHCLAM_CHECKS times a day. clamd answers on TCP 3310.
# Runs hardened (read-only root file system, tmpfs /tmp, no capabilities): it writes only to /tmp and the volume.
# Test: images/clamav/test.sh.

# ClamAV 1.4.6 on Debian 13, "_base": without bundled signatures, as ClamAV advises when they live in a volume. 1.4.6
# (released 2026-08-07) fixes CVE-2026-20339, -20345 and -20346. The tag's rebuild of 2026-11-16 (Debian's apt and
# zlib fixes, issue #818), pinned on 2026-11-17: the 7-day rule does not apply during development (user, Decisions
# table "7-day rule in development").
FROM clamav/clamav-debian:1.4.6_base@sha256:3976f67ab16e8a96b802aedf7e6f930c38262b95fbcb571d2ec21aa62e547cbe

COPY --chmod=0644 clamd.conf freshclam.conf /etc/clamav/

# Read by /init-unprivileged: freshclam checks for new signatures 4 times a day (§5.5).
ENV FRESHCLAM_CHECKS=4

# The image's clamav user (uid and gid 1000), with the image's entrypoint for running without root: it downloads the
# signatures if the volume has none, then starts freshclam and clamd in the foreground.
USER 1000:1000
ENTRYPOINT ["/init-unprivileged"]

# A real scan of a small clean file through clamd's socket, not `clamdscan --ping`: after a failed reload with
# ConcurrentDatabaseReload no, clamd 1.4 still answers PING but has no engine and every scan ends in ERROR. The
# timeout covers a reload, during which scans wait; the start period covers the first download into an empty volume.
HEALTHCHECK --interval=30s --timeout=30s --start-period=10m --retries=3 \
    CMD ["clamdscan", "--no-summary", "/etc/clamav/clamd.conf"]

# Last on purpose: every RUN after an ARG sees it as an environment variable, so a new REVISION (each commit) would
# invalidate the cache of the layers above. Only the labels read these values.
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/thekim1/kalla" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.title="kalla/clamav"
