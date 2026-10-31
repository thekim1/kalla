# Changelog

Notable changes per release. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Shared file: append under **Unreleased**, in a PR of its own.

## Unreleased

## 0.1.0 (2026-10-31)

P0 foundation: the skeleton builds, deploys from a digest-pinned bundle, is observable and can be restored (drilled locally). No
retrieval or chat yet (P1a).

### Added

- Stage 0 skeleton: solution `UmeRag.slnx`, every P0 project, health endpoints and `--health-check` probe in every
  service, AppHost with every P0 resource, `TestPostgres` on the pinned pgvector image, `verify.yml` (backend and the
  stream ownership check).
- Postgres image `ume-rag/postgres`: stock pgvector 0.8.7 on PostgreSQL 17 with pgBackRest, image test (#5).
- Schema and database roles, Swedish text search with the .NET decompounder (#7, #8).
- Config-version library: monitor, grace window, readiness, serialised writes (#13, #24).
- Keys and clients in gateway style; FakeGateway with the residency-ceiling client handler (#17, #18).
- Parser sidecar (Docling, pypdfium2 fast path, Tesseract `swe`) with contract v1 and contract tests (#9, #16).
- Worker: Postgres job queue, dispatcher, reaper, `cancel-run`; `rag_jobs` wake-ups on the config monitor (#20, #25, #30).
- ClamAV image (1.4 LTS, unprivileged) and the dev start scripts (#26).
- Write-once blob store on the file system with reusable contract tests (#33).
- Admin: Postgres session store, BFF sign-in with Keycloak, realm `ume`, write path with audit, organisation sync and
  config retention, admin UI skeleton in Swedish (#23, #32, #35, #38, #39, #45).
- Production Compose, `init-deployment.sh`, host units and firewall, backups, point-in-time restore and key escrow
  (#31, #40).
- Entra registrations as Bicep with `entra.yml` (P2) (#36); on-prem capacity scripts (#37).
- Docs: local development, production setup, runbook, upgrade guide (#42).
- CI: `admin-ui` job (#49); parser jobs in their own `parser.yml` (#50).
- Admin: the worker's `command` enqueue path (#46); pooler-safe SQL rules with a scan test over all service SQL (#53);
  `docs/security-events.md` (#54).
- Ownership rules for P1 (#57).
- Runbook: daily checks, restore drill, key escrow, public-records requests (#58).
- Release: `release.yml` (images to `ghcr.io/<owner>/ume-rag/<service>`, SPDX SBOMs, digest-pinned bundle, smoke test,
  GitHub release), `deploy/build-release-bundle.sh`, `deploy/tests/release-smoke.sh` (#60).
- Docs: install from the release bundle (#62).

### Security

- ClamAV 1.4.6 (CVE-2026-20339, CVE-2026-20345, CVE-2026-20346) (#67).
- `admin-ui` audit: production dependencies strict; two dated exceptions for lint-toolchain advisories until
  2026-11-10 (#64, #61).
