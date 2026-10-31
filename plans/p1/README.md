# P1a work split

P1a is the walking skeleton ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)).
**Done when a member of the pilot department asks a question in Open WebUI and gets a cited answer from umea.se
content.** This file is the single source of the ordered P1a checklist (§3) with every item's done-when; the briefs
only assign and order it. Decided 2026-10-30 (Decisions table, "P1 planning", "P1 pilot sign-in", and the user's
decisions of 2026-10-30 in §1 below). P1b's briefs are written when P1a is done, from what it taught.

Two streams on P0's seam ([`../p0/README.md`](../p0/README.md) §6), started once P0 has tagged `v0.1.0` (no P1 code
before it). Stream B owns all admin UI and admin API work; stream A delivers the worker side against written contracts
in `contracts/`. P1a has no journey pages: the platform team sets the pilot up through Scalar.

| Brief | For |
|---|---|
| [`stream-a.md`](stream-a.md) | Stream A: worker, parser, connectors, pipelines, index writer, the Sitevision preset, evaluation runs, Compose, CI, scripts |
| [`stream-b.md`](stream-b.md) | Stream B: schema, retrieval and the knowledge API, chat API, admin API (Scalar), assistant, gateway client, realm additions |

Progress: [`progress-a.md`](progress-a.md), [`progress-b.md`](progress-b.md); phases: [`../progress.md`](../progress.md).
Prompts: [`prompts/stream-a.md`](prompts/stream-a.md), [`prompts/stream-b.md`](prompts/stream-b.md),
[`prompts/main-session.md`](prompts/main-session.md). Results: `results-a.md`, `results-b.md` (created by each
stream's first PR that records a `to-verify` row, same shape as P0's).

## 1. What changed since the design (user, 2026-10-30)

These override the detail documents for P1a. The main session records them in the Decisions table and the register.

| Decision | Effect on P1a |
|---|---|
| **M2 triggered: no on-prem model hardware yet.** Every model call is EU-hosted through the gateway until it exists | See the table below. The P0 capacity numbers on on-prem hardware (H8, to-verify #98, #99, #125, #149) wait for hardware |
| **Cheapest possible test host; production-style tests as late as possible** | P0 #16 (deploy to the test host), the test-host restore drill and to-verify #90 move to P1b, before go-live. P1a done is shown on a **cheap test host** (recommended) or **locally** (§7, user item U-a) |
| **W1 answered**: `chat.umea.se` is built by the user's own team (Team Turkos) | Not an overlap risk. The product owner decides with that team how the pilot assistant relates to it; it does not block P1a |
| **G1 (DPIA) postponed**, still required before go-live | P1b. It must list the EU processor agreement (PUB-avtal) for the embedding, condense and answer models |
| **Gateway production** (release 1, production deploy, the shared Keycloak project) runs in a **separate session in the gateway repo**, briefed by the main session | External hand-offs J10, J11 and J15 (§4), like P0's user items |

**Designs that assume on-prem models, and what P1a configures instead:**

| Design assumption | Where | P1a |
|---|---|---|
| The embedding profile is on-prem BGE-M3 (`bge-m3`, 1024 dims), embed and query-embed calls send `x-ume-max-residency: OnPrem` with the `OnPrem` key | performance pass §3.9; processing §11; knowledge §5.2; data-model §2.1 `EmbeddingProfiles` | The pilot profile is an **EU-hosted embedding model** (user item U-b picks it). Its ceiling is `Eu` and the worker and `rag-api` use their `Eu` key for embed and query-embed. The residency comes from the profile, not a constant (§8 O1) |
| HNSW index on `halfvec(1024)`; response check "exactly 1024 floats" | data-model §3.2; processing §11; knowledge §5.2 | Dimensions come from the profile row. A 1024-dimension EU model keeps the schema, the FakeGateway and the tests unchanged (recommended) |
| Canary embedding on the profile | processing §11 | Recorded from the EU model when the profile is seeded (item 13) |
| Condense and answer models are on-prem; the "on-prem fallback" if the DPIA requires it | chat §5; weakness review §4 | EU models through the gateway at ceiling `Eu`. There is no on-prem fallback until hardware exists: if G1 requires one, the pilot waits (risk for the user) |
| Switching profiles later | processing §11 "Profile migration (P3 outline)" | Moving to on-prem BGE-M3 means re-embedding every chunk: a P1b/P2 step (§8 O3), not P1a |
| Capacity measured on the on-prem hardware | repo-and-deployment §11 #22 | Item 19 measures the EU routes instead, to size the umea.se initial load |

## 1a. Changed 2026-10-31 (user; Decisions table rows of that date). Read before §3.

| Decision | What it means for the streams |
|---|---|
| **Code first, live testing last** | Build every code item of §3 on FakeGateway, fixtures and the dev Keycloak. Items or parts whose done-when needs a live system wait for **one live block at the end**: J10 (real gateway), J11 (shared Keycloak), J12/J13 and items 19–22 (umea.se crawl, capacity on the EU routes, gold set, the pilot member's answer), the 200 umea.se PDFs of item 5. Mark such a part "live" in your progress file and move on. **Don't stop at P1a**: continue with P1b's code items (`../p1b/`, being written now by the main session). |
| **7-day package rule suspended during development** | Pin current releases; exact versions and image digests still required; every pin younger than 7 days is reviewed before go-live. |
| **CPU caps ≤ 2 in the base Compose** (#70) | Parser runs 1 worker × 2 threads; `compose.host-24g.yaml` has the pilot caps. |
| **Interim gateway** | A test/dev gateway (HTTPS, public certificate, EU embedding and chat models) is the interim J10; its Keycloak will hold realm `ume` (J11). Its address and keys never go in the repo (user secrets, `deploy/.env`). U-b (embedding model, dimensions) is named once its catalogue is listed with a key: until then 1024 dims on FakeGateway. |
| **Licence EUPL-1.2** (#65) | New source files need no header; third-party notices (#51, #52) before any distribution. |
| **Identity reworked for the Keycloak pilot** (`../identity/design.md` §11.2, §16) | Item 16 (realm additions) follows §11.2. §16's open questions (realm administrator, import procedure, **whether realm-wide settings such as password policy, brute force and OTP live in RAG's file or the gateway's base realm**) are answered by the gateway session: build the file so those realm-wide parts are a separable section, and say so in the PR. |

## 2. Ownership

`.github/stream-ownership.txt` stays the authority. P0's seam is unchanged. P1a needs these rules added by the main
session before J1 (each line before the broader rule it narrows):

| Path | Owner | Why |
|---|---|---|
| `plans/p1/results-a.md`, `plans/p1/progress-a.md` | a | as P0 |
| `plans/p1/results-b.md`, `plans/p1/progress-b.md` | b | as P0 |
| `contracts/sources/**` | a | source settings schemas with `x-effect` (connectors §2.1, §12.3; admin §4.5 validates against them) |
| `contracts/jobs/**` | a | new: job payload and result schemas for the P1a kinds (engine §9.1, admin §4.7), the "written contracts" of the P1 planning decision |
| `contracts/site-profiles/**` | a | new: the site-profile JSON schema (connectors §5.6) and the hand-checked umea.se profile |
| `src/Ume.Rag.MigrationService/Templates/**` | a | the `website`, `url-list` and `files` template JSON the migrator seeds (engine §13.1); the migrator code stays B's |
| `deploy/keycloak/**` | b | RAG's realm-additions file for the shared Keycloak (Decisions table, "P1 pilot sign-in") |

Unchanged and worth repeating: **B is the single schema owner** (A asks by an issue labelled `stream-b` naming the
data-model section). `Ume.Rag.Retrieval`, `Ume.Rag.Answering`, `Ume.Rag.Domain` (except the two ports) and
`tests/Ume.Rag.IntegrationTests` are B's; A puts its database tests in `tests/Ume.Rag.Worker.Tests` or
`tests/Ume.Rag.Pipelines.Tests` on `TestPostgres`, as P0's `DispatcherTests` do. The AppHost is B's (its
`open-webui` resource included); `deploy/compose.*.yaml` are A's.

## 3. P1a checklist (ordered)

Order within P1a follows [`../README.md`](../README.md) "Order within P1". Items marked *(after Jn)* wait on a
hand-off (§4). Each item's done-when is binding; the refs say where its detail is.

| # | Owner | Item | Done when | Refs | to-verify |
|---|---|---|---|---|---|
| 1 | A | Pilot-start checks: umea.se sitemap inventory script (formats, share of legacy Office/RTF/e-mail for Z1, JavaScript need by raw text yield); then *(after J12)* the first pass of the 500-URL `lastmod` sample | inventory recorded; renderer confirmed not needed (or reported to the main session); sample's first fetch stored, re-fetch due ≥ 7 days later (result in P1b) | README "Order within P1"; connectors §5.4, §8.1; weakness review §3 | 139, 170 (started) |
| 2 | B | **Schema part 1, ingestion** (first, A waits): `Settings`, `DataProtectionPolicies`, `EmbeddingProfiles`, `Collections`, `Sources`, `Pipelines`, `PipelineVersions`; `content` tables of data-model §2.2 needed by P1a (`NodeManifest`, `WorkerNodeSupport`, `CrawlHosts`, `SiteProfiles`, `SiteProfileVersions`, `CrawlFrontier` keyed per source, `CrawlRuns`, `ConnectorCheckpoints`, `Uploads`, `DeletionProposals`, `QuarantinedFiles`, `Documents`, `NodeOutputs`, `ChunkSets`, `Chunks` with its indexes, `EmbeddingCache`, `Runs`, `ParseObservations`, `ParseProfiles`), `BlobTombstones` (#34), grants → **J2** | migrations apply on `ume-rag/postgres`; `DatabaseRoleTests` cover the new grants; P2-only tables absent | data-model §1.2, §2.1, §2.2, §3.2, §9, §10; connectors §6.0 | 22 |
| 3 | B | Schema part 2, serving: `Assistants`, `AssistantVersions` + view `PublishedAssistantVersions`, `AssistantAudiences`, `GatewayModels`, `EvaluationSets`, `EvaluationQuestions`, `EvaluationRuns` (+ `Variant`, coverage fields), `EvaluationResults`, `RetrievalStats` | as item 2 | data-model §2.1, §2.2; weakness review §2 "Data model" | – |
| 4 | A | Worker contracts: `contracts/sources/v1/{url-list,crawl,upload}.schema.json` (`x-effect`), `contracts/pipeline/v2/definition.schema.json`, templates `website`/`url-list`/`files`, `contracts/jobs/v1/` (P1a kinds), `contracts/site-profiles/v1/`; JSON Schema validator chosen; `Ume.Rag.Pipelines` validator and stage keys → **J3** | schemas checked in and exported by a test that fails on drift; `Ume.Rag.Pipelines.Tests` cover engine §4 rules and stage keys | engine §3, §4, §9.1, §13.1, §19; connectors §2.1, §12.3; admin §4.7 | 17 |
| 5 | A | Parser P1 formats: `/v1/inspect`, page ranges with `partial`, every P1 format (§3; legacy → `format_deferred`), OCR and table modes, `/v1/ner`, per-page signals, limits, RSS watchdog; OCR acceptance; text-layer vs Docling on 200 umea.se PDFs | parser §16 P1 row except `bench.py` on the test host (P1b); contract tests green | parser §3, §6–§10, §15, §16; weakness review "Lower-ranked" | 79, 80, 110, 131, 132 |
| 6 | A | Connectors: canonicaliser, egress guard, robots and sitemaps, URL list, crawl with one frontier per source and the shared `CrawlHosts` budget, **full traversals** (no `lastmod` skip, the stricter P1a deletion rule), uploads by slot, ClamAV client (fail closed), change detection with noise normalisation, deletion guard | connectors §18 tests for these parts green (incl. C7) | connectors §3–§7, §9, §11–§13; weakness review §3 "Phasing" | 10, 27, 28, 81, 112 |
| 7 | A | Processing stages: `detect`, parse routing with built-in rules and fixed ranges, worker-side parsers, `clean`, split, `chunk` (breadcrumb prefix, `SearchText` through `SwedishDecompounder`), metadata, `personal-data` (patterns, gated NER, `held`), `embed` (cache, canary, fixed concurrency, profile's model, dimensions and ceiling) | processing §17 goldens, PII targets and flag lifecycle green | processing §2–§11, §17; §1 above | 26, 43, 111 |
| 8 | A | Sitevision preset in code and the hand-checked umea.se manual profile JSON; ≈ 20 saved umea.se pages as the regression corpus | the acceptance page extracts to exactly its main text with breadcrumb and date 2026-01-15 | connectors §5.6; processing §17; simplification pass §13 | 31, 138, 158 |
| 9 | A | Engine *(after J2)*: stage manifest, execution with segments, fence and output cache, sync fan-out, runs and finalizer, scheduler, index writer (invisible batches, swap, re-key, `held`, orphan sweep), blob-tombstone purge sweep, #41 fixed → **J4** | engine §19 integration and crash-recovery tests ("worker restart loses no work"); a URL-list source syncs into live chunks against FakeGateway | engine §2–§10, §14, §16, §19; data-model §9 | 21, 119, 141 |
| 10 | A | Commands and evaluation *(after J5)*: `edit-site-profile`, `rollback-site-profile`, `edit-eval-question`; `eval` `on-demand` over the `human` layer in three variants (vector, hybrid, hybrid without the decompounder incl. the `reindex-fts` scratch copy); per-question coverage and the threshold proposal as numbers → **J6** | processing §17 reproducibility test; evaluator and `/v1/retrieve` return the same ids in the same order | processing §13.4–§13.6; engine §9.1; data-model §3.2 (`reindex-fts`) | – (168 in item 21) |
| 11 | B | Retrieval core in `Ume.Rag.Retrieval`: statements K, V, H+D with the exact/HNSW rule, RRF, dedup and cap, coverage (vector side alone until the keyword floor is calibrated), `IReranker` no-op seam, query normaliser → **J5** | knowledge §16.1, §16.2 tests green | knowledge §6–§8, §16; weakness review §2 | 4, 5, 6, 7, 19 |
| 12 | B | Knowledge role: `KnowledgeService`, in-process `IRetriever`, query embedding (profile's ceiling, in-process cache, circuit, degrade), section expansion, citations, download grants and `/content` (upload citations), `/v1/retrieve` for `internal` clients, Scalar | knowledge §16.3, §16.6 tests green | knowledge §2–§5, §9, §10 | 95, 102, 162 |
| 13 | B | Gateway client, release-1 side: per-level keys (`Eu` used by every P1a call), `GatewayModels` sync with residencies, V8 `/v1/models` check on publish, the pilot embedding profile seeded with its canary, `GET /api/gateway/status` | tests against FakeGateway; status green against the real gateway after J10 | gateway-changes §13.4; admin §4.5, §4.10; identity §4.7 | – |
| 14 | B | Admin API set-up surface in Scalar → **J7**: collections (+ publish, retrieval settings), sources (validated against J3), sync now, upload batches, runs and crawl report, site profiles (edit, roll back), templates (read), clients and keys, settings and data protection (history mode `none` only), assistants (create, draft, publish, audience = client), evaluation questions and runs, `cancel-run`, member groups by hand; cross-rules only for these objects | the whole pilot set-up (§5 step 4) works through Scalar alone; every write audited and bumps the config version; `PermissionMatrix` covers each endpoint | admin §4.1–§4.7, §12 (P1a row); weakness review §1 "P1a set-up" | 32 |
| 15 | B | Chat API → **J9**: `/v1/models` (published versions by caller and client), `/v1/chat/completions` stream and JSON, conversational check, condense, coverage handling and not-found texts, token budget, prompt frame, residency check before the first byte, marker renumbering, Open WebUI `source` events and `url_citation`, output filter, attachments refused, Open WebUI user-JWT trust | chat §18.1–§18.6 tests for these parts green; a FakeGateway answer cites a fixture document in Open WebUI from the AppHost | chat §3–§10, §15.1; chat plan §7 (P1a row); identity §5 | 42, 47, 48, 50, 103, 105, 136, 154, 164, 165 |
| 16 | B | Realm additions for the shared Keycloak: `deploy/keycloak/` file with the clients `ume-rag-admin`, `open-webui`, `ume-rag-api`, the `rag-*` roles, `departmentCodes`, full scope off, no test users, password policy and brute-force protection, OTP for admins → **J8** | imports into a Keycloak 26.6.4 test container next to a minimal `ume` realm without collisions; sign-in test passes | Decisions table "P1 pilot sign-in"; identity §11; issue #47 | 83 |
| 17 | B, A | Evaluation smoke: `tests/evaluation/smoke` (fixture collection + 30 questions) and its test (B); the `evaluation-smoke` job in `verify.yml` (A) | recall@10 ≥ 0.8, every answer cited, job green on a PR | knowledge §16.4; repo-and-deployment §8.2 | – |
| 18 | A | Pilot stack *(after J8, J9)*: Compose wired for P1a (`rag-admin` and Open WebUI OIDC to the shared Keycloak, Open WebUI → `rag-api` key and JWT secret, gateway URL and the `Eu` key, chat §4.3 env, blob root); a run book for the local P1a run (§7 U-a) | `docker compose config` green; the stack starts locally with health green | repo-and-deployment §6, §7.1; chat §4.3; identity plan §1 | 92 |
| 19 | A | EU-route capacity *(after J10)*: `scripts/capacity/` against the EU embedding and chat routes; initial-load time and cost for umea.se | numbers recorded; the user told the expected initial-load duration and cost | performance pass §3.9; §1 above | – |
| 20 | A, user | umea.se set-up and crawl *(after J7, J10, J12)*: the platform team creates the pilot collection, the umea.se crawl source with the manual profile, the pilot URL list and one upload through Scalar; full traversal with real EU embeddings | crawl report reviewed: traversal complete, acceptance page indexed, failures by reason understood | admin §12 (P1a row); connectors §5.5 | 94 |
| 21 | B, A, user | Gold set *(after J13)*: entered as `human` questions; three-way run (item 10) recorded; `coverageMaxDistance` and `coverageMinKeywordScore` set from the best-separating values | results recorded; thresholds saved; hybrid and the decompounder kept or dropped per the result | weakness review §2; processing §13.4; knowledge §7 | 143, 168, 169 |
| 22 | all | **P1a done** *(after J11 or its fallback)*: one assistant (instructions + the pilot collection) published to the Open WebUI client; a pilot department member signs in and gets a cited answer from umea.se content | shown to the user locally, with the pilot member present; screenshot and the answer's citations in the PR | weakness review §1; chat §19 | – |

## 4. Hand-offs

| # | From → to | What | Until it lands |
|---|---|---|---|
| J1 | main → A, B | `v0.1.0` tagged, §2 ownership rules merged, these briefs merged | – (nothing starts before) |
| J2 | B → A | Schema part 1 (item 2) merged | A does items 1, 4–8 (code against data-model §2.2; database tests wait) |
| J3 | A → B | Contracts (item 4) merged | B's endpoints use admin §4.5 and engine §9.1 field lists; schema validation added when J3 lands |
| J4 | A → B | A URL-list source syncs into live chunks (item 9) | B's retrieval and chat tests seed `Chunks` directly through a TestKit fixture writer |
| J5 | B → A | `RetrievalEngine` (item 11) merged | A builds the eval job's plumbing and metrics on a stub engine |
| J6 | A → B | Commands and `eval` `on-demand` applied (item 10) | B's endpoints enqueue per J3 and the tests assert the job row |
| J7 | B → A, user | Admin set-up endpoints in Scalar (item 14) | A sets up its test sources by seed SQL in its own tests |
| J8 | B → user → gateway session | Realm-additions file (item 16) | the AppHost's dev realm (`dev/keycloak/ume-realm.json`) |
| J9 | B → A | Chat API answers with citations; Open WebUI client and key creatable (item 15) | A's Compose wiring is checked by `docker compose config` only |
| J10 | gateway session (user) → A, B | The gateway reachable from the developer machine running P1a, with an EU embedding route (U-b), EU condense and answer routes, and the pilot team's per-level keys (at least `Eu`) | FakeGateway (1024 dims) everywhere; items 19–21 wait |

| J11 | gateway session (user) → A, B | Shared Keycloak with realm `ume`, RAG's additions imported, pilot accounts created | the dev Keycloak on the local stack with one pilot account (§7 U-c) |
| J12 | user → A | U1: crawler about page and umea.se robots.txt check with the web editors | only the sitemap inventory and the saved fixture corpus touch umea.se |
| J13 | user + pilot department → B | The gold set written (100–150 answerable, 30–50 unanswerable, ≥ 10 temporal) | the smoke set; item 21 waits |
| J14 | user → A | **Done 2026-10-30:** P1a is shown locally (U-a), so no demo host | – |
| J15 | gateway session → B | Gateway release 1 shipped | not needed for P1a: the current gateway release works with per-level keys restricted by `AllowedResidencies` (it ignores `x-ume-max-residency`, so the `x-ume-residency` check is the guard; gateway-changes §13.2 rule 1). Switch in P1b, before go-live |

## 5. Sync points

1. **After J1**: both streams start (B on item 2 first, A on items 1, 4, 5).
2. **After J2 and J3**: the main session checks the schema against data-model and the contracts against engine §9.1
   and admin §4.7; folds the first results into `to-verify.md`.
3. **Local skeleton** (after J4, J5, J9): a URL list of fixture pages, ingested with FakeGateway, answered with a
   citation in Open WebUI from the AppHost. Deviations so far settled in the design.
4. **Real models** (after J7, J10, J12): the platform team sets the pilot up through Scalar (item 20); the umea.se crawl
   runs with EU embeddings; then item 21 once J13 lands.
5. **P1a done** (item 22): the main session reviews every deviation and result, ticks P1a in `../progress.md` and
   writes the P1b briefs.

## 6. Rules (both streams)

[`../../AGENTS.md`](../../AGENTS.md) and [`../p0/README.md`](../p0/README.md) §5 apply unchanged (one worktree per
agent, daily merges, requests through issues with the owner's label, shared files append-only in their own PR). In
addition:

- **Contract first.** When an item needs the other stream's output, write or request the contract (issue or
  `contracts/` PR) before the code. Payload changes are additive (engine §9.1 tolerant reader).
- **Respect the deferrals.** Every detail document has "Deferred or removed" sections, and the P1b and P2 rows of the
  phases table are not P1a: no journey pages, preview, publish flow, completeness by ratio, removal check, age decay,
  `demote`, generated sets, nightly runs, feedback, conversation writer, assistant restore, learning.
- **Every model call is EU-hosted** in P1a (§1), still with an explicit ceiling. Tests stay on FakeGateway.
- **Cheap first.** No test-host deploy, restore drill on the test host or `perf.yml` in P1a.

**Interim J10 and the J11 host (user, 2026-10-31).** A **test/dev instance of the gateway** already runs on a fixed
host, reachable over **HTTPS with a publicly trusted certificate**, with **EU-hosted embedding and chat models**. It
serves as J10 for P1a development (production stays with the gateway session, deployed late). Its address and the keys
live only in AppHost user secrets (`FakeGateway:Enabled=false`, `Gateway:BaseUrl`, `Gateway:Keys:Eu`; repo-and-deployment
§4.4) or `deploy/.env` and secret files, never in the repository. The gateway team issues one `Eu` key per deployable
(api, admin, worker) and a `catalog.read` Data API key for the org sync. The same host runs the gateway's Keycloak and
will hold the shared realm `ume` (J11). **Still open:** U-b names the EU embedding model and its dimensions once its
catalogue is listed with a key (1024 dimensions keep the schema and FakeGateway unchanged).

## 7. Owned by the user, outside the streams

| # | Item | Needed by |
|---|---|---|
| U-a | **Decided 2026-10-30: P1a is shown locally**, on a developer machine with the pilot member present (no demo host) | J14, item 18 |
| U-b | The EU embedding model for the pilot profile (1024 dimensions recommended), and its route at the gateway | J10, items 7, 13 |
| U-c | Pilot accounts: the pilot department's staff in the shared Keycloak (or one account on the dev Keycloak until J11) | J11, item 22 |
| U-d | Gateway production in the gateway repo's own session: release 1, the production deploy with EU routes, the shared Keycloak project importing RAG's additions, the pilot team's per-level keys | J10, J11, J15 |
| U-e | U1 with the web editors (about page, robots.txt) | J12 |
| U-f | The gold set with the pilot department (who writes it, by when); the department confirms the go-live thresholds | J13 |
| U-g | Owners and dates for the items blocking P1b (phase-start rule): A1, G1 (postponed, before go-live), W2, N1, P2, M2 (hardware), R1 | before P1b |
| U-h | The product owner's decision with Team Turkos on how the pilot assistant relates to `chat.umea.se` (W1) | before go-live |

## 8. Settled 2026-10-30 (user and main session)

User answers to the plan's open questions (Decisions table, "P1a plan answers"):
- **P1a is shown locally** with the pilot member present: the cheapest option, and no host to harden (U-a, J14).
- **P1a calls the gateway as it is** (v0.8.5): keys already enforce `AllowedResidencies` and the gateway sends
  `x-ume-residency`, which RAG checks on every answer. The Eu-only key and that check are the guard until release 1,
  which lands in P1b before go-live (the phases table already puts it there).
- **File upload stays in P1a** (weakness review §1, phases table).
- **Accepted risk:** with no on-prem hardware, weakness review §4's fallback (drop the pilot ceiling to on-prem if the
  DPIA requires it) does not exist. If G1 (P1b) refuses EU processing, the pilot waits for hardware or another
  provider. Revisited at G1.

Main session, settling the plan's open items:

| # | Item | Settled |
|---|---|---|
| O1 | Where the embedding ceiling lives once it is not always `OnPrem` (knowledge §5.2, processing §11) | the column `EmbeddingProfiles.MaxResidency` in item 2 (B migration); the knowledge and processing designs are updated at the P1a sync point |
| O2 | Weakness review §1 says "Scalar and the generic forms"; admin design §5.8 and §12 put the generic forms in P1b | Scalar only in P1a (item 14); the generic forms in P1b |
| O3 | Re-embedding onto on-prem BGE-M3 ("profile migration, P3 outline" in processing §11) | a P1b/P2 item, written into the P1b briefs when hardware is in sight |
| O4 | P0 deviations P1a inherits (#32, #33/#34, #35, #38, #39, #46; chat §4.3 Open WebUI v0.12.3 vs v0.12.5) | settled in the design at P0's integration sync point, before P1a starts |
| O5 | Issue #47 asked for Keycloak in the pilot Compose | done: #47 carries the refinement (shared Keycloak on the gateway host, B delivers the realm additions) |
