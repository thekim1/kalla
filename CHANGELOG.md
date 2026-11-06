# Changelog

Notable changes per release. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Shared file: append under **Unreleased**, in a PR of its own.

## Unreleased

P1a code on FakeGateway and fixtures (code first, live testing last); nothing deployed yet.

### Added

- Worker contracts (J3) and P1b contracts (L1): source settings, pipeline definition v2, job payloads and results,
  site profiles, with a drift test (#78, #88).
- Schema part 1 (ingestion) and part 2 (serving); platform pipelines seeded from the built-in templates (#76, #89, #93).
- Parser P1 formats: Office/ODF, images and TIFF, `/v1/inspect`, `/v1/ner`, page ranges, limits, RSS watchdog (#84).
- Connectors: canonicaliser, egress guard with pinned connect, robots, sitemaps, URL lists, crawl with one frontier per
  source and the shared host budget, uploads by slot, ClamAV client (fail closed), deletion guard, Postgres stores
  (#82, #91, #97); crawler freshness: completeness by ratio, removal check, `demote`, gated `lastmod` skip (#101).
- Processing stages `detect` → `embed`, personal-data flags with NER, embedding cache (#98).
- Sitevision preset, the hand-checked umea.se profile and a 20-page umea.se regression corpus (#104).
- Engine: stage manifest, segments, fenced commit, URL-list and upload sync into live chunks, runs and finalizer,
  scheduler, index writer with invisible batches and swap, sweeps (#108).
- Security events 5101 and 5102 for the worker (#83).
- Pilot stack on Keycloak with per-service gateway keys and the P1a local run book (#94).
- Scripts: gold-set grading (#99), umea.se sitemap inventory (#111); R1 harness with `perf.yml`, `evaluation.yml`,
  `e2e.yml` (#103, #105).
- Worker commands for every P1 kind: deletion decisions, release hold, PII clearance, `reprocess-document`, site-profile
  edits and rollback (#115).
- Preview class: `stage-preview` with isolation, `preflight`, `publish-plan` (#117, #131).
- Engine: crawl sync through the engine, `reprocess` and `retry-failed`, the nightly drift sweep, parse slicing and
  resume from stored ranges, parse cache keyed by parser build (#121, #122); publish with per-document swap, previous
  sets kept 14 days, rollback swap-back, `publish-report` (#131); one `EnsureCurrent` decision with swap-back in the
  drift sweep (#138); forced `reprocess` and `reprocess-document` bypass the stage caches from `fromStage` (#140).
- Source settings-change effects: a scope or identity change starts a `config-change` sync; narrowed scope and
  site-profile `excludedTypes` remove documents without fetching (`scope-narrowed`); identity changes re-sync
  (`identity-changed`) (#136, #141).
- Admin-made parse rules matched like the built-in ones, also in the preview's parse key (#133, #136).
- Evaluation class: on-demand three-way runs, `publish-report`, `nightly` with the recall@10 alert (#128); generated
  sets with filtering on FakeGateway (#130); set triggers (first syncs, weekly, document count +25 %) and the
  reproducibility test (#137).
- Pilot stack: test-host run book, the Open WebUI feedback service account and `deploy/openwebui-feedback-key.sh`,
  the runbook's public-records routes, `deploy/tests/pilot-stack.sh` (#135).

### Changed

- Embedding requests no longer have a token cap: `Worker:Embed:MaxTokensPerRequest` is removed and a request carries up to
  64 inputs, since gateway v0.9.0 checks each input against the context window (A-5, #429).

- HTML noise rules v2 (Sitevision `data-cid` and `sv_` ids): **HTML documents are re-processed once after the
  upgrade**, because the change-detection hash is version-tagged (#107).
- CI runs `verify.yml` and `parser.yml` on a pull request only while it carries the label `ci`; the nightly and weekly
  schedules are off (#109).
- Site profiles are keyed by host without a port (#136).
- Embedding requests carry at most `Worker:Embed:MaxTokensPerRequest` tokens (default 6000, was 16,384), so a batch
  stays under the gateway's per-request context-window check; files waiting more than 24 h for a ClamAV verdict end
  `failed` (`clamav.unavailable`) (#143).
- Open WebUI: password sign-in off (`ENABLE_PASSWORD_AUTH=false`, on only while the key script runs) and
  `DEFAULT_USER_ROLE=user` for OIDC sign-ins (#135).

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
