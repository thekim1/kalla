# Changelog

Notable changes per release. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Shared file: append under **Unreleased**, in a PR of its own.

## Unreleased

## 0.4.0 (2026-11-11)

Answer length from the model, editable embedding profiles, prompt texts as settings, keyword statistics kept by the
worker, lower idle and telemetry cost, and Aspire 13.6.5. **Rolling update: yes** from 0.3.0; migrations
`ProfileIndexReset`, `AnswerLengthFromModel` and `KeywordStatistics` add functions, a column and a table, and
`AnswerLengthFromModel` removes every assistant version's stored answer length and context size, so each follows its
model's limits. See `docs/upgrade-guide.md`.

### Added

- `Admin:OrganisationName` (default `Umeå kommun`; empty names no organisation) for the admin UI's header and sign-in
  texts; admin UI wording works for any organisation, and admin forms opt out of browser autofill and password managers
  (#544).
- `Crawl:UserAgent` on rag-worker (default `UmeaKommunBot/1.0`): robots.txt, meta robots and `X-Robots-Tag` are matched
  against its product token (#545).
- Embedding profiles can be edited on Platform → Gateway models until content is indexed with them (then only the
  residency ceiling), with **Test** and a test before every save; worker writes are guarded against a profile changed
  mid-embedding (#554, #559, #558).
- Models' context window and output limit can be entered on the gateway model mirror; V17 is bounded by them (#561).
- `UME_RAG_ORGANISATION_NAME` sets `Admin:OrganisationName` in Compose, Portainer and Kubernetes (#566).
- Answer length from the model: an assistant's *Longest answer* and *Context size* use the model's limits unless
  overridden, and the test panel shows the `max_tokens` sent and when an answer stopped at the length limit (#564, #569,
  #571).
- An optional platform ceiling for every answer, `Settings.Chat.MaxAnswerTokens` (Settings → *Longest answer, all
  assistants*) (#573, #574).
- Chat prompt texts as settings (`Settings.Chat.prompts`), each falling back to the built-in text; the citation and
  data-safety lines stay fixed (V38) (#575).
- The worker keeps the keyword stop list's statistics in `content."KeywordStatistics"`, after each run and nightly
  (#578, #579); rag-api reads them from B-33 on.

### Changed

- Deployment templates request `dimensions` from the embedding model by default and set `MaxInputTokens` 8191 (#546).
- Reverse proxy: the api host and Open WebUI allow 2 MB request bodies, as rag-api's chat route does; with 1 MB, long
  conversations were refused by the proxy (#552).
- PostgreSQL: `jit = off`, `random_page_cost = 1.1`, `effective_io_concurrency = 200` for SSD storage and short
  queries (#553).
- Traces are sampled (`UME_RAG_OTEL_TRACES_SAMPLER`, default `parentbased_traceidratio` at `0.1`), and the .NET
  containers' health probe runs every 30 s (every 5 s while starting) (#555).
- Idle job lanes wait for a `rag_jobs` notification or their next due job (at most `Worker:IdleWaitSeconds`, 30 s)
  instead of polling every 2 s; `Worker:PollSeconds` is gone (#563).
- `rag-renderer` runs only with the Compose profile `render` (`init-deployment.sh --render`); without it, `browser`
  sources wait with `render.not_configured` (#563). The release smoke test boots the bundle both ways (#567).
- Release workflow: a build cache per image, one smoke boot for the renderer on and off, and a Docker client config
  per job, so an expired registry login on the runners can't break a build (#572).

### Fixed

- Portainer stack: rag-admin reaches the gateway's Data API; `dataapi` joins the internal network `rag-dataapi`, so the
  organisation and model sync no longer fail with `HttpRequestException` (#542).
- Portainer stack: the grants for the embedding profile functions were missing from the generated file (#566).

### Security

- Aspire 13.6.5 for GHSA-f7vh-h5g3-7jrh (CVE-2026-120006, elevation of privilege in `Aspire.Hosting` < 13.6.5), with the
  .NET packages at 10.0.14 that it requires (#549).

## 0.3.0 (2026-11-10)

Deployment options matching the gateway's, upload knowledge bases, legacy Office formats, and the review fixes of
#499–#513. **Rolling update: yes** from 0.2.0 (no schema migration); see `docs/upgrade-guide.md`.

### Added

- Updates without downtime: a second `rag-api` and `rag-admin` (`compose.rolling.yaml`, `init-deployment.sh --rolling`),
  `deploy/rolling-update.sh`, nginx retries on the other slot, and an end-to-end test that lost no request (#537).
- Kubernetes manifests (`deploy/kubernetes`): base, in-cluster Postgres, ingestion, renderer, NetworkPolicies, an
  example overlay, `create-secrets.sh` and `deploy.sh`; the release bundle pins them by digest (#538).
- Portainer: the gateway's stack plus Ume Rag in one compose file, attached to the release as `compose.portainer.yaml`
  (#536, #538).
- An OpenTelemetry Collector example that sends security events to a SIEM, and a Keycloak guide (#538).
- Upload knowledge bases: Files tab, one-step create, live progress, folder paths, drag-and-drop and folder upload
  (#527, #532, #534).
- Legacy Word, Excel, PowerPoint and RTF uploads, converted with LibreOffice in the parser (#526).
- Cancel a run from the run and source pages (#531).

### Changed

- Statement K: one keyword statement with the search vector as a parameter (#509, #523).
- Open WebUI user JWTs are validated with Microsoft.IdentityModel.JsonWebTokens (#512, #525).
- Search statistics stay within their own server-side limit (#499, #516); less telemetry overhead (#503, #518).
- `/version` has one camelCase shape in every service (#497, #529); the EF command log is at Warning everywhere (#522).

### Fixed

- A `readySelector` or `noiseSelector` that is not CSS is refused on save (#530).
- The worker records an upload's outcome (indexed or refused) on its slot (#533, #534).

## 0.2.0 (2026-11-08)

P1a and P1b code: crawl, upload, parse and embed; hybrid retrieval with citations; the chat API behind Open WebUI;
the admin UI for the pilot journey; evaluation, publishing and rollback; JavaScript rendering. Built on the
FakeGateway and checked live on the local stack against the interim gateway. Not in production yet: the live block
(gold set, test host, DPIA) comes before the pilot goes live.

### Added

- Retrieval core (`Ume.Rag.Retrieval`) and the knowledge API in `rag-api`: access policy and snapshot, keys and
  grants, citations and downloads through the blob envelope, section expansion, security events and the auth-failure
  throttle (#112, #125, #127, #146, #152, #163, #165).
- Blob envelope URB2: blobs encrypted by data-protection level, segment-verified AES-256-GCM (#153).
- Retrieval tuning: age decay and source demote after fusion, the keyword stop list, topics that limit the scope,
  HNSW index health with concurrent rebuild, optional rerank per assistant (a chat model, or the gateway's
  `/v1/rerank`) (#235, #241, #248, #330, #347, #388, #400, #415).
- Chat API, OpenAI-compatible: `/v1/models`, chat completions with streaming, condense, the answer filter, rate limits
  per user and per key, Open WebUI user-JWT trust, long questions condensed into the search query, the `ume` chat
  extension (outcome, related documents) and follow-up suggestions (#198, #203, #204, #205, #430, #431, #433, #434).
- Conversations and feedback: issued conversation ids, conversation list and delete, chat retention,
  `POST /v1/feedback` (ratings only), the Open WebUI feedback importer (#262, #263, #264, #267).
- Admin API for the pilot journey: knowledge bases, sources, runs and the crawl report, uploads, API keys, site
  profiles, assistants with drafts, versions, publish, restore, archive and retention, documents, personal-data
  clearance and deletion decisions, pipelines with preview, publish and rollback, evaluation, the retrieval and
  assistant test panels, the audit log and explain access, organisation (#176, #178, #180, #185, #188, #254, #255,
  #257, #258, #259, #260, #261, #298, #299, #361).
- Admin UI (Vue, English): knowledge bases, Add source with check and preview (Before / After), source and run pages,
  files and quarantine, deletion decisions, assistants (draft, test, publish, versions, audience, storage),
  publishing, test questions and quality, search settings, the audit log, feedback, organisation and the Map, generic
  forms for platform objects, the setup checklist (#279, #281, #285, #288, #301, #303, #304, #305, #306, #309, #310,
  #312, #313, #320, #321, #322, #366).
- General knowledge per assistant, a confirmed warning on a public assistant; a new assistant starts from a default
  (#358, #359, #369, #464).
- Organisation: sharing levels, collection shares, collection links with topics, member groups (#346, #347, #348,
  #349).
- Evaluation: test-question generation with a forecast, candidate comparison and threshold proposal, the
  `evaluation-smoke` CI job, rerank variants with latency and cost per question (#208, #220, #258, #402, #416).
- Browser tests: the pilot journey with mouse and keyboard and the accessibility spec on a fixture website
  (`tests/e2e`, `e2e.yml`) (#362, #368, #377).
- Shared Keycloak realm: realm additions, `deploy/keycloak` for keycloak-config-cli, role groups as `rag-` names or
  Entra object ids, OIDC claim names configurable in Compose, `init-deployment --proxy` writes the nginx config
  (#207, #409, #423, #469, #470, #474).
- AppHost: Open WebUI, fixed host ports, the feedback importer and the renderer container locally (#206, #340, #341).
- Restore a deleted uploaded file within the 14-day grace; publish checks that a live worker runs the template
  version (#486, #489).
- Licence notices; the sv_SE word list as separate, replaceable files (#381, #450).
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
- JavaScript rendering: `images/renderer` (Playwright 1.64.1 `launch-server`, Chromium with its sandbox, secret
  WebSocket path, WebRTC off) on the internal `render` network; the worker drives it and fetches every browser request
  through its egress guard; `PageFetcher` as the one fetch path; source settings `fetchMode`, `readySelector`,
  `scrollToLoad`; `Documents.FetchMode`; the preflight compares raw and rendered text (#459, #462, #463, #472).
- Politeness profiles per host in `Settings.Crawl.hostOverrides` (presets, rate, connections, `Crawl-delay`, window,
  back-off, breaker, robots.txt ignore with a reason, User-Agent) with the ceilings `Crawl:MaxHostRate` and
  `Crawl:MaxHostConnections`, V37 and a host table in the admin UI (#461).
- Crawler: meta refresh as a redirect hop, `redirect.cookie_required`, sniffing a missing or `octet-stream` type,
  a soft-404 fingerprint per host (`http.soft_404`), challenge pages as `http.challenge`, sitemap retries and text
  sitemaps (#460).
- Settings model fields as pickers that grey out models the platform's key may not use (#473).

### Changed

- Embedding requests no longer have a token cap: `Worker:Embed:MaxTokensPerRequest` is removed and a request carries up to
  64 inputs, since gateway v0.9.0 checks each input against the context window (A-5, #429).

- HTML noise rules v2 (Sitevision `data-cid` and `sv_` ids): **HTML documents are re-processed once after the
  upgrade**, because the change-detection hash is version-tagged (#107).
- CI on the self-hosted runners: `verify.yml` on every non-draft pull request, path-filtered (images, evaluation smoke
  and renderer only when their paths change); `e2e.yml` by hand or from a release when asked, after `verify` and
  `parser`; no schedules (#109, #151, #367, #372, #373, #397, #494).
- Site profiles are keyed by host without a port (#136).
- Files waiting more than 24 h for a ClamAV verdict end `failed` (`clamav.unavailable`) (#143).
- Open WebUI: password sign-in off (`ENABLE_PASSWORD_AUTH=false`, on only while the key script runs) and
  `DEFAULT_USER_ROLE=user` for OIDC sign-ins (#135).
- A URL that failed at any depth in a run is never a `not-seen` deletion candidate in that run, not only a seed
  (#460).
- Site-profile identity rules match with the non-backtracking engine and no timeout: under load a timeout kept a
  URL's path form and gave one page two documents (#475).
- `Retrieval:GatewayRerankTimeout` defaults to 1.5 s (was 500 ms) for the Foundry reranker (#439).
- Crawl and render knobs after live checks: `Worker:Concurrency:fetch` 32 with fetched bodies spooled to disk,
  `Render:MaxConcurrent` 4 with `renderPerHost` capped at the host's connections, `Render:QuietPeriod` 500 ms, the
  ready rule is `readySelector` and quiet (#482, #491, #492, #493).
- Gateway clients retry 502 and 504 with the same ceiling, and once on a connection failure (#406, #422).
- Open WebUI v0.12.5 (OIDC callback `/oauth/oidc/login/callback`), Keycloak 26.8.1, Aspire 13.6.4 (#339, #409, #457,
  #458).
- Parser image 2.88 GB → 2.58 GB; NER loads on demand and an idle convert slot goes cold (#418, #419).

### Security

- The gateway address stays out of logs and telemetry (#233, #238, #247, #272).
- API keys' `AllowedIpRanges` are enforced, with the client address taken behind a trusted proxy only (#432).
- Regex patterns and identity rules refuse constructs the non-backtracking engine lacks (#476, #477).

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
