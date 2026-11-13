# Finished queue items up to v0.5.0

The ticked items of the three work queues, moved here unchanged when `v0.5.0` was released (2026-11-13, session 33).
History: it does not bind. An item is found by its ID (`A-35`, `B-42`, `M-12`); the queues in `plans/work/` hold only
open and waiting items.

## Stream A (`stream-a.md`)

### [x] A-1 Licence notices before distribution (#51; #381)
- **Do:** generate `src/parser/LICENSES.md` (docling and its parts, Tesseract, models) and ship it in the parser
  image; add a repository `NOTICE` naming EUPL-1.2; put the model licence texts where the image build copies them.
- **Paths:** `src/parser/**`, `images/**`, `NOTICE` (new, root).
- **Done when:** the parser image contains `LICENSES.md` (checked in its image test); `NOTICE` exists; #51 closed.
- **Waits on:** –
- **Read:** [`archive/licence-review-p2.md`](../licence-review-p2.md); parser §15.

### [x] A-2 First green `e2e` run (#377; https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Do:** dispatch `e2e.yml` once the runner host has Chromium's libraries; fix what fails in A's paths, file the
  rest for B.
- **Paths:** `.github/workflows/e2e.yml`, `scripts/Start-E2E.ps1`.
- **Done when:** a dispatched `e2e.yml` run is green; link it on #370 and tell B (B-5).
- **Waits on:** the runner host set up per `docs/ci-runner.md` (#370).
- **Read:** repo-and-deployment §8.2.

### [x] A-3 Sitevision query parameters on hosts without a profile (#382)
- **Do:** as the user decides on #363 point 3 (today only the Sitevision preset drops `open.*`/`sv.*`).
- **Paths:** `src/Ume.Rag.Worker/Connectors/**`.
- **Done when:** the decided rule has a connectors test; the fixture crawl stores no `?open.…` duplicates.
- **Waits on:** the user's decision.
- **Read:** connectors §3.1 (rule 7).

### [x] A-5 Embedding batch cap goes after gateway release 1 (#390, #429)
- **Do:** remove the worker's embedding batch cap below the context window once the gateway checks embeddings per
  input (gateway #65 in release 1); embedding deployments may then set `ContextWindow`.
- **Paths:** `src/Ume.Rag.Worker/Processing/**` (embed stage), worker settings.
- **Done when:** the cap setting is gone; an embed-stage test sends a full batch whose longest input fits the window.
- **Waits on:** gateway release 1 (tagged v0.9.0, 2026-11-06) deployed (B-L4).
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built".

### [x] A-6 NER loaded on first use, unloaded when idle (resource review) (#419)
- **Do:** the parser's `ner-0` process starts on the first `/v1/ner` request (not at container start) and stops after
  `NER_IDLE_STOP_S` (proposed 900) without requests. While it warms, `/v1/ner` answers `not_ready` as today, so the
  worker's existing NER wait (processing §9.6) covers the gap; `/v1/info` reports `ner_ready` honestly.
- **Paths:** `src/parser/**`.
- **Done when:** a parser test shows no NER process at idle start, one after a request, none after the idle time; the
  image test passes; idle RSS measured before/after (about 1 GB less expected, resource review 2026-11-06).
- **Waits on:** –
- **Read:** parser §7.8, §10.1, §10.2; processing §9.6.

### [x] A-7 Convert slot goes cold between batches (resource review) (#419)
- **Do:** a convert slot with no job for `PARSER_IDLE_STOP_S` (proposed 900) stops; the next convert request warms one
  (readiness and the worker's parser-slot gate already handle a warming parser). Inspect stays warm (its 10 s deadline).
  Keep at least the option of "always warm" (`PARSER_IDLE_STOP_S=0`).
- **Paths:** `src/parser/**`.
- **Done when:** a parser test covers stop-on-idle and warm-on-demand; idle RSS measured before/after (about 1.1 GB
  less expected); the first convert after cold start succeeds within the worker's parse timeout (measured).
- **Waits on:** –
- **Read:** parser §10.1, §10.2; processing §3 (parse slots, timeouts).

### [x] A-8 Slimmer parser image (resource review) (#418)
- **Do:** measure the parser image per layer (2.88 GB today), then remove what the runtime doesn't need (build tools,
  caches, unused torch parts, docs, test data) without changing behaviour; keep `LICENSES.md` and model licences.
- **Paths:** `src/parser/Dockerfile`, `src/parser/**`.
- **Done when:** a before/after layer table in the PR; image tests and the parser test suite pass; the parser answers
  the golden documents identically.
- **Waits on:** –
- **Read:** parser §12; licence review (`archive/licence-review-p2.md`).

### [x] A-4 Go-live pin review, A's part (last code item)
- **Do:** review every image, PyPI and Compose pin younger than 7 days; bump the Open WebUI pin once, with the
  callback change in Compose matching B's realm change (B-6).
- **Paths:** `images/**`, `src/parser/**`, `deploy/**`.
- **Done when:** no pin under 7 days old without a recorded reason; #113 recorded.
- **Waits on:** every other code item of both streams.
- **Read:** Decisions "7-day rule in development"; identity §5.3.

### [x] A-9 Renderer spike (to-verify #29)
- **Do:** prove the risky parts before building: Playwright .NET `ConnectAsync` to a `playwright run-server` container
  on an internal network; `RouteAsync("**/*")` + `FulfillAsync` from a worker-side fetch for every resource type
  (module scripts, CORS preflights, redirects); service workers blocked; WebSockets routed and closed; DNS prefetch and
  anything unrouted fail closed on the internal network; Chromium's sandbox under Compose (seccomp profile vs
  `--no-sandbox`); exact version pairing of the NuGet and the server. Measure the §8.8 scenarios on a fixture SPA and
  on 2–3 real JavaScript sites (the user names them), Chromium and Lightpanda side by side. Throwaway code in a
  scratch project; the result goes into to-verify #29 and the `Render:*` defaults.
- **Paths:** `plans/work/results-a.md` (results); scratch code outside the repo or under `tests/spikes/` (not shipped).
- **Done when:** results-a #29 records each check (pass/fail with evidence) and the measurements; the design points
  that change are listed for main.
- **Waits on:** the user's list of JavaScript sites for the real-site measurements (the fixture SPA can go first).
- **Read:** Decisions "JavaScript rendering"; connectors §8, §22; repo-and-deployment §5.4.

### [x] A-10 Renderer image and deployment
- **Do:** `images/renderer/Dockerfile` (official Playwright image by digest, `run-server` only, user 10002); Compose
  service `rag-renderer` on a new internal `render` network (172.30.18.0/24) with the hardening and limits of
  repo-and-deployment §5.4; `rag-worker` joins `render` and gets `Render__Endpoint`; the AppHost adds the container
  (on by default, `Render:Mode` `off` skips it); `docker-user-firewall.sh` knows `render` (no egress); the images CI job
  builds and tests it; `docs/network-and-ports.md` and `production-setup.md` updated.
- **Paths:** `images/renderer/**`, `deploy/**`, `src/Ume.Rag.AppHost/**`, `.github/workflows/**`, `docs/**`.
- **Done when:** the image builds in CI; a Compose config check shows the renderer only on `render`; a container test
  shows it cannot resolve or reach anything outside `render`.
- **Waits on:** A-9 (versions, sandbox decision).
- **Read:** repo-and-deployment §5.4, §6.1; connectors §8.3.

### [x] A-11 Worker: `PageFetcher` and the Playwright renderer
- **Do:** `Connectors/Render/` (`IRenderer`, `IRenderFetcher`, `PlaywrightRenderer`, the asset cache, blocked hosts,
  limits, ready detection, DOM serialisation without scripts) and `PageFetcher` (fetch → render for a `browser` source →
  `RawContentWriter.StoreAsync`), used by the crawl and URL-list connectors, the preflight and the stage preview in
  place of their own copies. `EgressPurpose.Render`; render slots taken only when free, else the job is released
  (`render.busy`, no attempt); `render.timeout`/`unavailable`/`not_configured` transient; a failed render keeps the last
  good content and its links for the traversal; no conditional GET for `browser` sources; `Documents.FetchMode`
  (migration); metrics and facts of §8.6; remove `SiteProfile.JavascriptNeeded`; the Crawlee-review points of §8.3
  (bounded browser operations, script navigation/popups/downloads, per-page cookie jar, shadow DOM and same-host
  iframes, `scrollToLoad`).
- **Paths:** `src/Ume.Rag.Worker/**`, `src/Ume.Rag.Pipelines/**` (settings records), `src/Ume.Rag.Infrastructure/**`
  (migration), `contracts/sources/v1/**`, their tests.
- **Done when:** unit tests with a fake renderer (slot release, failures keep content and links, no fallback to the
  shell, settings effect); an integration test against the real renderer container rendering a fixture SPA through the
  worker's guard (a request to a private address is refused with 5101 `Purpose = Render`); the HTTP-only crawl tests
  unchanged.
- **Waits on:** A-9; A-10 for the integration test.
- **Read:** connectors §8, §12, §5.4 (deletions); engine design (job release); data-model §2.2.

### [x] A-12 Settings and UI for `browser` sources
- **Do:** source settings `fetchMode` and `readySelector` in the URL-list and crawl forms (generic forms where they
  apply); the preflight's raw-vs-rendered word counts and the "seems to need JavaScript" hint; crawl report counts
  (rendered, render failures, kept from last good content); `render.*` texts in `messages.ts`; the stage preview shows
  the fetch mode. Built on a `main-session/` branch (admin API/UI paths are stream B's).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `src/Ume.Rag.Worker/Preview/**`.
- **Done when:** vitest and API tests; a browser check of the source form with a `browser` source and its preflight
  (screenshot in the PR).
- **Waits on:** A-11.
- **Read:** connectors §8.1, §12.3; admin §4.7, §5.5.3.

### [x] A-13 Renderer live check (§8.8)
- **Do:** run the §8.8 scenarios on the local stack: the HTTP-only baseline, a mixed crawl with about 5 % `browser`
  pages, and a JavaScript-only site from the user's list including a content change behind an unchanged shell; record
  pages/hour, render p50/p95, requests per page, peak memory, text against a browser and chat p95 during the crawl.
- **Paths:** `plans/work/results-a.md`.
- **Done when:** results recorded; `Render:*` defaults adjusted in a follow-up PR if the numbers say so.
- **Waits on:** A-11, A-12; the user's site list.
- **Read:** connectors §8.8.

### [x] A-14 Politeness profiles per host (connectors §6.6)
- **Do:** widen `HostOverride` into the §6.6 profile (preset and members; `robots: ignore` with `reason`, `userAgent`)
  and read it in `PlatformCrawlSettings`; apply it in the host budget (rate, connections, `Crawl-delay`, latency term,
  back-off caps, circuit breaker), the crawl window, `RobotsCache` (ignore per host; `Sitemap:` lines still read) and
  `HttpFetcher` (User-Agent per host, also for the renderer's fetches); deployment ceilings `Crawl:MaxHostRate` and
  `Crawl:MaxHostConnections`; V37 in the admin API; the host-politeness table and drawer in the crawl settings (admin
  UI, on a `main-session/` branch); the crawl report and source page show non-default politeness.
- **Paths:** `src/Ume.Rag.Worker/Connectors/**`, `src/Ume.Rag.Worker/Engine/**`, `src/Ume.Rag.AdminApi/**`,
  `src/admin-ui/**`, their tests.
- **Done when:** worker tests per member (a `fast` host is paced at 10 req/s, `crawlDelay: ignore`, `crawlWindow: any`,
  robots ignored with a reason, a custom User-Agent on the wire); V37 tests (ceilings, missing reason); vitest for the
  table.
- **Waits on:** –
- **Read:** Decisions "Configurable politeness per host"; connectors §5.1, §6.3–§6.6; admin §4.6 V37.

### [x] A-15 HTTP crawler gaps from the Crawlee review
- **Do:** meta refresh as a redirect hop and `redirect.cookie_required` (§3.2); sniff a missing or `octet-stream`
  Content-Type (§4); soft-404 fingerprint per host and run (`http.soft_404`) and challenge pages as `http.challenge`
  (§5.3); sitemap retries, plain-text sitemaps and `/sitemap_index.xml` (§5.1); `messages.ts` texts for the new codes.
- **Paths:** `src/Ume.Rag.Worker/Connectors/**`, `src/admin-ui/src/utils/messages.ts`, their tests.
- **Done when:** a test per point against the fake HTTP server (a meta-refresh chain, a PDF as `octet-stream` with
  `Content-Disposition`, a 200 not-found page that would have replaced good content, a challenge page keeping the last
  good content, a sitemap that fails once and then answers).
- **Waits on:** –
- **Read:** Decisions "Crawlee review: ideas adopted"; connectors §3.2, §4, §5.1, §5.3.

### [x] A-16 `files` template gets `pathRules` (M-3, #349)
- **Do:** the Decisions row "M-2 open design calls" says path rules on the display name can tag uploads, but
  `src/Ume.Rag.MigrationService/Templates/files.v1.json` has no `pathRules` parameter. Add it (same schema as the
  website template's), so an upload's display name can set topics; the processing already reads `pathRules`.
- **Paths:** `src/Ume.Rag.MigrationService/Templates/**`, `contracts/**` if the template contract lists parameters, tests.
- **Done when:** a test: an upload whose display name matches a rule gets the topic on its chunks.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; processing §7.

### [x] A-17 Render follow-ups from A-13 (preflight hint, report count)
- **Do:** (1) the preflight's render check: a `browser` render whose main text has fewer than `MinRenderedWords` while
  the raw page has fewer still is a `warn` with a new code `render.empty` ("the rendered page has no main text: name
  the loaded content in `readySelector`"), not `render.compared` *pass*; the admin UI shows the code's message
  (`messages.ts`). (2) The run report's `render.rendered` counts every ready render, including pages whose main text
  was empty (A-13 saw 3 empty renders reported as 0); add `render.empty` for those.
- **Paths:** `src/Ume.Rag.Worker/Preview/**`, `src/Ume.Rag.Worker/Connectors/**`, `src/Ume.Rag.Worker/Engine/**`,
  `src/admin-ui/src/**/messages.ts` (on a `main-session/` branch if CI's ownership check refuses it), their tests.
- **Done when:** worker tests: a fake render with an empty `<main>` gives `render.empty` *warn* in the preflight and
  counts in `render.rendered` and `render.empty` of the run report; vitest for the message if the UI changes.
- **Waits on:** –
- **Read:** connectors §8.1, §8.4, §8.8; `plans/work/results-a.md` "item A-13 (live sites)".

### [x] A-18 Crawl concurrency probe (local, many hosts)
- **Do:** a test in `Ume.Rag.Worker.Tests` with trait `Category=Scale` (excluded from CI and the default run) that
  starts the worker through `EngineKit` against one test site answering many virtual hosts (the test resolver maps
  `h01.scale.test` … `hNN.scale.test` to it; deterministic Swedish pages with links, configurable latency, a share of
  `browser` pages on a fake renderer with a configurable render time) and the FakeGateway, crawls N sources at once,
  and prints pages/s, max in-flight requests per host (counted by the site), fetch-lane utilisation and render-slot
  waits (`render.busy`). Run it at 10, 50 and 100 hosts with the `polite` (1 req/s) and `fast` (10 req/s) profiles and
  with 5 % `browser` pages; record the numbers.
- **Paths:** `tests/Ume.Rag.Worker.Tests/Scale/**` (new), `tests/Ume.Rag.Worker.Tests/Engine/EngineTestKit.cs` and the
  connector test kit if they need a multi-host resolver, `plans/work/results-a.md`.
- **Done when:** results row: pages/s and the binding limit (host pacing, `Worker:Concurrency:fetch` 8,
  `Render:MaxConcurrent` 2) per scenario; never more than a host's `MaxConnections` in flight; a proposal for the
  `Worker:Concurrency:fetch` and `Render:MaxConcurrent` defaults if the numbers say so (a follow-up, not this item).
- **Waits on:** –
- **Read:** performance pass §2.3, §4, §7 (the FakeSites row, for shape only; `scale.yml` stays P2); connectors §6.0,
  §6.3, §8.3.

### [x] A-19 Ready rule and render reasons (#488, #490)
- **Do:** (1) **Ready rule** (user 2026-11-08): with a `readySelector`, a page is ready when the selector holds text
  **and** the quiet rule holds (`load`, no routed request open for `Render:QuietPeriod`), so a label the app renders
  before its data (e.umea.se's translated `h2`) can't make it ready alone. (2) **Distinct, permanent reasons** in place of
  `render.timeout`: `render.bad_selector` when the browser refuses the selector (Playwright's parse error, today caught
  as a navigation interruption) and `render.selector_not_found` when the selector never matched within
  `Render:PageTimeout`, kept apart from a page still loading; neither is retried as transient when it fails the same way
  (the run item fails with the reason, the document keeps its last good content as for other render failures). The
  preflight's render check reports both codes too. (3) **Run verdict:** a crawl run where at least half of its ready
  renders ended `parse.empty` (A-17's `render.empty` count) is `degraded` with a reason naming `readySelector`, not a
  plain `succeeded`. (4) Messages for the new codes in `messages.ts` (English, naming the "Content is ready when this
  element has text" field).
- **Paths:** `src/Ume.Rag.Worker/**`, `src/Ume.Rag.Pipelines/**` if a reason list lives there,
  `src/admin-ui/src/utils/messages.ts` (on a `main-session/` branch), their tests.
- **Done when:** worker tests on the fake renderer and `RenderTests`' real-route paths: a placeholder that holds text
  before a slow data request is not ready until the request ends; a refused selector gives `render.bad_selector` with
  one attempt; a selector that never matches gives `render.selector_not_found`; a run with most renders empty is
  `degraded`; vitest for the messages.
- **Waits on:** –
- **Read:** connectors §8.3, §8.4, §8.6; issues #488 (part 2) and #490; `results-a.md` "item A-13 (live sites)".

### [x] A-21 Compose knobs for telemetry and health checks (#503, #504, Compose part)
- **Do:** `OTEL_TRACES_SAMPLER` (`parentbased_traceidratio`) and its ratio as deployment knobs in Compose; the .NET
  health-check interval from B-27's measurement (30–60 s).
- **Paths:** `deploy/**`, `docs/production-setup.md`.
- **Done when:** the bundle's smoke test passes with the new values.
- **Waits on:** B-26, B-27.
- **Read:** #503, #504.

### [x] A-22 Postgres settings for SSD and short queries (#500)
- **Do:** `jit = off`, `random_page_cost = 1.1`, `effective_io_concurrency = 200` in `deploy/postgres/postgresql.conf`,
  with `EXPLAIN (ANALYZE, BUFFERS)` of statements K, V-exact, V-approximate and H on the scale data before and after.
- **Paths:** `deploy/postgres/**`.
- **Done when:** the before/after plans and timings are in `results-a.md`.
- **Waits on:** –
- **Read:** #500; performance pass §3.3 (P-52).

### [x] A-23 Idle cost: notification-driven job lanes, renderer as a profile (#505)
- **Do:** a lane waits for `rag_jobs` or the class's next `AvailableAt`, capped at about 30 s, instead of polling every
  2 s; `rag-renderer` behind a Compose profile that `init-deployment.sh` enables, with `render.not_configured` for
  `browser` sources when it is off (as the AppHost's `Render:Mode` `off`).
- **Paths:** `src/Ume.Rag.Worker/Jobs/**`, `deploy/**`.
- **Done when:** an idle worker issues no lane query between notifications (statement count over a minute); the smoke
  test passes with and without the profile.
- **Waits on:** –
- **Read:** #505; engine §9; connectors §8.

### [x] A-24 The no-decompounder variant on statement K (#509, evaluation part)
- **Do:** `NoDecompounderSearchIndex` uses B-28's statement with the variant's expression; its own copy goes.
- **Paths:** `src/Ume.Rag.Worker/Evaluation/**`.
- **Done when:** the variant's evaluation results are unchanged on the smoke set.
- **Waits on:** B-28.
- **Read:** #509; processing §14.

### [x] A-25 Write each chunk once (#501) (#589)
- **Do:** engine §14 rule 2a: a chunk set of at most 1,000 chunks is inserted (set row and chunks) inside the swap
  transaction, visible at commit; larger sets keep today's path.
- **Paths:** `src/Ume.Rag.Worker/Engine/**`.
- **Done when:** about half the WAL bytes and dead chunk tuples for the same re-crawl (`pg_stat_wal`,
  `pg_stat_user_tables`); swap, rollback and fencing tests pass.
- **Waits on:** – (M-10 settled it: engine §14 rule 2a, data-model §3.2; respect the profile guard of processing §11
  inside the transaction, #554/#559).
- **Read:** #501; engine §12, §14.

### [x] A-26 One stage runner for engine and preview (#508)
- **Do:** one runner with the stage order, input hashing and skip rules, parameterised by the output store and a stop
  stage; engine and preview call it; a test runs both on the same documents and compares outputs.
- **Paths:** `src/Ume.Rag.Worker/Engine/**`, `src/Ume.Rag.Worker/Preview/**`.
- **Done when:** the comparison test passes and both old orchestrations are gone.
- **Waits on:** –
- **Read:** #508; engine §7, §11.

### [x] A-28 Schema contract test for positional SQL readers (#510, harness)
- **Do:** a test that runs each SQL constant with `CommandBehavior.SchemaOnly` against the test database and checks the
  column names and types its reader expects; the worker's statements first. Stream b adds its stores' statements after
  it (a follow-up item when this lands).
- **Paths:** `tests/Ume.Rag.ContractTests/**` or `tests/Ume.Rag.Worker.Tests/**`, `src/Ume.Rag.Worker/**`.
- **Done when:** a renamed column in a worker statement fails the test.
- **Waits on:** –
- **Read:** #510.

### [x] A-29 Keyword statistics kept by the worker (#499 step 2, worker part) (#578, #579; taken by stream b)
- **Do:** table `content."KeywordStatistics"` (data-model §2.2; migration, `S` for `ume_rag_api` in `app-roles.sql` and
  its Kubernetes copy) and the worker's refresh of knowledge §6.2: per collection with ≥ 100 live child chunks,
  `ts_stat` on `ConnectionStrings:ragdb-maintenance` (else `ragdb`) with `statement_timeout = 0` when it has no rows,
  its live child count moved by more than 10 % from the stored `ChunkCount`, or its rows are over 7 days old; checked
  at the end of each sync run of the collection's sources and in the nightly maintenance; one collection at a time; its
  rows replaced in one transaction; a collection under 100 chunks loses its rows.
- **Paths:** `src/Ume.Rag.Worker/**`, `src/Ume.Rag.Infrastructure/Persistence/**` (migration), `deploy/postgres/app-roles.sql`,
  `deploy/kubernetes/base/app-roles.sql`, tests.
- **Done when:** a test shows rows written at the first run, kept on a run that changes < 10 %, recomputed past 10 %,
  removed under 100 chunks; the rows equal `RetrievalStatements.FrequentLexemes`' result on the same data.
- **Waits on:** –. B-33 reads the table.
- **Read:** #499; knowledge §6.2, §6.4; data-model §2.2; results-b B-25.

### [x] A-30 (#597, #611) Gateway phase 1: one credential key per service in the deployment (#580, cost-attribution B2, deploy part)
- **Do:** `init-deployment.sh --gateway-credential-key-file SERVICE=FILE` (for `api`, `admin`, `worker`) writes
  `secrets/{api,admin,worker}/Gateway__Key` and drops the per-level key files and `--org-sync-key-file`
  (`OrgSync__DataApiKey`: the organisation now comes from the context, B-35); the Compose, Kubernetes
  (`create-secrets.sh`) and Portainer templates mount the one key per service; the runbook and `production-setup.md`
  say how a gateway admin issues the three credential keys (integration *Ume.Rag*, `RequireMaxResidency`, address list,
  residencies capped per service, an upper bound on every key it acts as: the worker's at `OnPrem`+`Eu`; the scope
  `organisation.read` on `rag-admin`'s only, later `usage.read` too) and how to rotate one; the Data API key goes.
- **Paths:** `deploy/**` (not `deploy/postgres/**`), `docs/**`.
- **Done when:** `deploy-checks` pass with the new flag; the release smoke test boots with one key per service; no
  template or doc names `Gateway__Keys__*` or `OrgSync__DataApiKey`.
- **Waits on:** B-37 (the key names in code), B-35 (the Data API key gone from code).
- **Read:** repo-and-deployment §6.3, §7.1; identity §4.7; gateway-changes §5.1.

### [x] A-31 (#613) Cost per knowledge base: every worker model call's tokens and cost (cost-attribution C3)
- **Do:** engine §10.6: every gateway call of the worker (`EmbeddingClient` for indexing, re-embedding and preview,
  `EvalModelClient` for `eval-generate` and `eval-judge`, the dedup embeddings `eval-dedup`, the evaluation's query
  embeddings and rerank) reads its usage (input, cached input, output tokens) and cost with B-38's shared reader:
  `x-ume-cost-usd`, else an estimate from `GatewayModels`' prices, kept apart. A document's sum fills
  `RunItems.CostUsd` (so `Runs.CostUsd` is no longer 0); each job attempt that made a call adds its deltas to
  `content."CollectionDailyUsage"` per (day, collection, level, step) as the last statement of the transaction that
  records its outcome, keys sorted. Every call carries `x-ume-run-id` (engine §7.7): the run's id, the evaluation run's
  id, or the job's id for set generation and preview.
- **Paths:** `src/Ume.Rag.Worker/**` (not `Processing/Pii/**`, `Engine/HnswIndexHealth.cs`), `tests/Ume.Rag.Worker.Tests/**`.
- **Done when:** against the FakeGateway (B-38's cost headers): a sync of two documents gives each run item the sum of
  its embedding calls' `x-ume-cost-usd` and the run that sum; `CollectionDailyUsage` holds the day's `embed` row with
  the same tokens and cost; a set generation adds `eval-generate`, `eval-judge` and `eval-dedup` rows, a retrieval run
  `query-embed` (cache misses only) and `rerank` rows; on `fake-no-cost` the cost lands in `EstimatedCostUsd`; a
  retried attempt counts its calls once; every recorded call carried an `x-ume-run-id`.
- **Waits on:** B-38 (the table, the shared reader and estimate, the FakeGateway's cost headers, the rerankers' usage).
- **Read:** cost-attribution §5; engine §7.7, §10.2, §10.4, §10.6; processing §11, §13.1, §14.7; data-model §2.2.

### [x] A-32 (#641) Runbook: reading Ume.Rag's cost beside the gateway's (cost-attribution C6)
- **Do:** a section in `docs/runbook.md`: where the figures come from (admin §8.6: `AssistantDailyUsage`,
  `CollectionDailyUsage`, the gateway's cost or an estimate), why they differ from the gateway's usage report for the
  same key and day (the gateway counts fallbacks and retries Ume.Rag doesn't see; estimated calls; aborted streams;
  Knowledge API calls without an assistant and the admin's own checks are counted only in the gateway; days in
  Europe/Stockholm against the gateway's time zone), how to match them per step (`x-ume-run-id`, `x-ume-step` in the
  gateway's request list), what a rising estimated share means (`rag.chat.cost{source=estimate}`), and that the
  gateway is the record for internal charging.
- **Paths:** `docs/runbook.md`.
- **Done when:** the section answers cost-attribution §5's done-when for an operator: a day of chat on two assistants
  and one ingestion run read in the admin and the CSV and reconciled with the gateway's report, each difference named.
- **Waits on:** B-40 (the report it describes), A-31.
- **Read:** cost-attribution §5; admin §8.6; chat §8.3, §17; engine §10.6; gateway-changes §7.1.

### [x] A-33 (#609) Member groups read-only in the platform UI (#580, gateway v0.10.0)
- **Do:** the platform resource view of departments shows `memberGroups` read-only with a link to the gateway's
  *Organisation* page (`admin_url` + `/organisation`, admin §2.3); its `update` on
  `/api/organisation/departments/{id}/member-groups` goes with the route (B-35 part 2). Labels in `labels.ts` only.
- **Paths:** `src/admin-ui/src/platform/**`.
- **Done when:** Vitest: the departments resource has no edit action and lists member groups; no call to the removed
  route remains.
- **Waits on:** B-35 part 2 (the route removed).
- **Read:** identity §2.1; admin §4.5, §4.10.

### [x] A-36 (#602) Malware scan optional in the worker: `ClamAv:Mode`, the scanner that doesn't scan, `unscanned` slots (#535)
- **Do:** `ClamAv:Mode` (`on` default | `off`) beside `ClamAv:Host` and `ClamAv:Port` (connectors §11).
  - **`on`:** as today. Outside Development the worker refuses to start without `ClamAv:Host`, with a message that
    names both settings; Development without a host keeps the `EicarOnlyScanner`.
  - **`off`:** a scanner that contacts no clamd and answers "not scanned", a result of its own and never clean; no
    `rag.clamav.*` metric; a Warning and event 5106 at start; the `clamav` health check Healthy with "Malware scan is
    off (ClamAv:Mode = off)". The stage preview uses the same scanner.
  - **Upload slots (§9.1):** a passed scan sets `clean` and `ScannedAt`; "not scanned" sets `unscanned` and leaves
    `ScannedAt` null; an add or a replace clears `ScannedAt`; `indexed` keeps it.
  - **Run report (§5.5):** the finalizer writes `malwareScan: "off"` into `Runs.Report` while the scan is off.
- **Paths:** `src/Ume.Rag.Worker/**`, `tests/Ume.Rag.Worker.Tests/**`.
- **Done when:** worker tests with `ClamAv:Mode=off` and no clamd: an uploaded file goes `unscanned` and then
  `indexed` with `ScannedAt` null; a crawled PDF is stored; the run's report has `malwareScan: "off"`; 5106 is logged
  once at start; the health check is Healthy with the text. With `on`: a scanned upload has `ScannedAt`, a replace
  clears it until the new bytes pass, and a host outside Development without `ClamAv:Host` fails at start. The
  worker's `sql-shapes.json` is regenerated.
- **Waits on:** B-47's first part (the state, the column, the event).
- **Read:** Decisions "Malware scan optional"; connectors §5.5, §9.1, §11, §16, §18.

### [x] A-37 (#601) Malware scan optional in the deployment and the docs (#535)
- **Do:**
  - **Compose:** `rag-clamav` under the profile `clamav`; the worker's `depends_on` on it not required;
    `ClamAv__Mode: ${UME_RAG_CLAMAV_MODE:-on}` on the worker (repo-and-deployment §5.5).
  - **`init-deployment.sh --clamav on|off`** (§7.1): default `on`, a re-run without the option keeps the value in
    `deploy/.env`. It writes `UME_RAG_CLAMAV_MODE` and the profile; `off` skips the ClamAV confs and says so in the
    closing notes.
  - **Kubernetes:** `components/clamav` split from `components/ingestion`, in the example by default.
  - **Portainer:** `UME_RAG_CLAMAV_MODE` and the profile in the template; regenerate the stack.
  - **Docs:** `production-setup.md` (the choice, what `off` means, sizing without ClamAV, files stored while it was
    off are not scanned later), `runbook.md`, `kubernetes.md`, `network-and-ports.md`, `local-development.md`
    (`ClamAv:Mode` in place of `ClamAv:Enabled`) and `upgrade-guide.md`: an existing deployment re-runs
    `init-deployment.sh`, or `rag-clamav` doesn't start and files wait with `clamav.unavailable`.
- **Paths:** `deploy/**` (not `deploy/postgres/**`, `deploy/keycloak/**`, `deploy/kubernetes/base/app-roles.sql`),
  `docs/**` (not `docs/security-events.md`, `docs/accessibility-statement.md`), `.github/workflows/verify.yml`.
- **Done when:** `deploy-checks` pass and cover: `docker compose config` with and without the profile; a default
  `init-deployment.sh` run writes the profile and `on`; `--clamav off` writes `off`, no profile and no ClamAV confs,
  and a re-run without the option keeps it; `kubectl kustomize` of the example with and without `components/clamav`.
  The release smoke test also boots with `--clamav off`: the worker is healthy and no `rag-clamav` container runs.
- **Waits on:** A-36 (the worker reads `ClamAv:Mode`).
- **Open after the merge:** the release smoke test's scan-off phase has not run yet (it needs release images); it
  runs with the next release dry run.
- **Read:** Decisions "Malware scan optional"; repo-and-deployment §5.5, §6.2, §7.1; connectors §11.

### [x] A-38 (#623) The gateway's limits and version in the platform UI (#606, gateway v0.10.0)
- **Do:** on the platform resource *Gateway models* (`resources.ts`, id `gateway-models`) a row with `limitsSource`
  `catalog` shows `contextWindow` and `maxOutputTokens` read-only, with a note that the gateway sends them; only a
  `manual` row keeps the edit action (`PUT /api/gateway/models/{name}` answers `400 platform_rule` with `constraint:
  limits_from_gateway` for a `catalog` row; show that message if it comes back anyway). The same view shows the
  gateway's `version` and `features` from `GET /api/gateway/status` (`gateway`, admin §4.10), and nothing when
  `gateway` is null. Labels in `labels.ts` only (stream b's path: the PR goes on a `main-session/` branch, as A-33).
- **Paths:** `src/admin-ui/src/platform/**`, `src/admin-ui/src/utils/labels.ts` (additions only).
- **Done when:** Vitest: a `catalog` row has no edit action and shows its limits; a `manual` row keeps it; the version
  and features show from a mocked status and are absent when `gateway` is null. Looked at in the browser on the
  FakeGateway stack.
- **Waits on:** nothing (B-35 part 3, #607, added `limitsSource` and the status's `gateway`).
- **Read:** admin §4.10; Decisions "Answer length from the model".

### [x] A-39 (#626) The worker's content retention step: the age rules of data-model §8 for rows without blobs
- **Do:** a nightly step function `content.rag_retention_content_step(batch_size)` in the style of
  `chat.rag_retention_chat_step` (data-model §8: `SECURITY DEFINER`, owner migrator, `SET search_path = pg_catalog,
  pg_temp`, batched, the minimums inside the function, returns the rows deleted), granted to the worker role and called
  by `MaintenanceService` after the existing steps until a batch deletes nothing; each run logged and written to
  `audit."AuditLog"` as `system:retention`. Its rules, all from data-model §8:
  - `CollectionDailyUsage`: `Day` older than 3 years (Europe/Stockholm, as the chat step);
  - `Runs`: finished more than 13 months ago (their `RunItems` partitions are long dropped);
  - `ParseObservations`: older than 180 days; `RetrievalStats`: older than 13 months;
  - `EmbeddingCache`: unused for 30 days;
  - `EvaluationRuns` of kind `assistant-tests` (with their results): beyond the last 20 per assistant;
  - `SiteProfileVersions`: `superseded`/`rejected` older than 1 year, keeping the last 10 per host.
  Rows that carry blobs (`NodeOutputs`, `QuarantinedFiles`, `tmp/` and `previews/`) are not in this item: they need the
  tombstone path of §9.
- **Paths:** `src/Ume.Rag.Worker/**`, `src/Ume.Rag.Infrastructure/Jobs/**`, `tests/Ume.Rag.Worker.Tests/**`, and
  stream b's migration (`src/Ume.Rag.Infrastructure/Persistence/Migrations/`) and `deploy/postgres/app-roles.sql` (the
  grant; regenerate the Portainer stack): the PR goes on a `main-session/` branch, announced on a stream-b issue.
- **Done when:** a test seeds each table with a row just inside and just outside its rule and runs the step: only the
  outside rows go, the batch size holds, an audit row is written; the maintenance run calls it; the connection budget
  test still passes.
- **Waits on:** nothing.
- **Read:** data-model §8, §9, §4.4; the chat step in migrations `ChatRetentionStep` and `DailyUsage`; ADR 016 in the
  gateway's `docs/adr.md`.

### [x] A-40 (#646) The worker's model-call metrics: `rag.embed.tokens` and `rag.ingestion.cost` (engine §17)
- **Do:** the worker records `rag.embed.tokens` (counter, the input tokens of every embedding call) and
  `rag.ingestion.cost` (counter, USD, tag `class`, with `source` = `gateway` | `estimate` as `rag.chat.cost` has it)
  from the usage it already reads for `CollectionDailyUsage` (A-31), once per answered call, in the meter the worker
  uses for its other `rag.*` metrics. The runbook's cost section (A-32) then names the metric for knowledge bases.
- **Paths:** `src/Ume.Rag.Worker/**`, `tests/Ume.Rag.Worker.Tests/**`, `docs/runbook.md` (the one line).
- **Done when:** a test with a `MeterListener` sees both after an indexing run on the FakeGateway, the cost split by
  `source` (`fake-no-cost` gives `estimate`); no tag carries an id or text.
- **Waits on:** nothing (A-32 for the runbook line).
- **Read:** engine §17 (metrics), §10.6; chat §17 (`rag.chat.cost`).

## Stream B (`stream-b.md`)

### [x] B-1 Chat models only in the model picker (#363 point 1; #379)
- **Do:** add `GatewayModels.ModelKind` (`chat`/`embedding`) filled by `GatewayModelSync` from the catalogue's `Kind`
  (manual route rows `chat`); return `modelKind` from `GET /api/gateway/models`; the assistant's model picker lists
  chat models only; V34 and V32 refuse an embedding model. No compatibility step: the next sync fills the column.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/**` (migration), `src/Ume.Rag.AdminApi/**`,
  `src/admin-ui/**`.
- **Done when:** a sync test maps `Chat`/`Embedding`; a rules test refuses an embedding model on publish; vitest: the
  picker hides `bge-m3`.
- **Waits on:** –
- **Read:** data-model §2.1 (`GatewayModels`); admin §4.6 (V32, V34), §4.10.

### [x] B-2 Archived assistants and one document count (#374; #385)
- **Do:** show "Archived" for archived assistants in *Used by* and leave them off the Map (or mark them); use one
  document count on the knowledge base list and its Overview (live documents), or label the difference.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`.
- **Done when:** vitest for both pages; the Map route test leaves out an archived assistant.
- **Waits on:** –
- **Read:** admin §5.5.2, §5.5.6.

### [x] B-3 Close the open records of items 3, 9, 10 and 12 (#393)
- **Do:** record what is left of #23 (config lock wait, `rag.admin.config_lock.wait`), #20 (the writer's batch),
  #146 (generic forms for platform objects, data protection `none` only); confirm knowledge §16.1 tests and
  "evaluator order == `/v1/retrieve` order" (the evaluation smoke checks it).
- **Paths:** `plans/work/results-b.md`, tests if a check is missing.
- **Done when:** each row recorded as met, partly met with the reason, or moved with the main session's agreement.
- **Waits on:** –
- **Read:** [`../to-verify.md`](../../to-verify.md).

### [x] B-4 Accessibility statement and one NVDA pass (#96) (#427; approved by the user 2026-11-06 without the NVDA pass, which follows soon)
- **Do:** finish `docs/accessibility-statement.md` once the DOS-lagen scope is known; one screen-reader pass through
  the pilot journey with NVDA.
- **Paths:** `docs/accessibility-statement.md`, `src/admin-ui/**` for fixes.
- **Done when:** NVDA findings fixed or filed; statement final; #96 recorded.
- **Waits on:** the user (DOS-lagen scope) and a person with NVDA.
- **Read:** admin §10.1.

### [x] B-5 Item 36's browser-test step (#377; https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Done when:** the first green `e2e` run (A-2) passes step 14 of `tests/e2e/specs/pilot.spec.ts` (share with the
  municipality, link with a topic from another department, by keyboard).
- **Waits on:** A-2.

### [x] B-7 HNSW rebuild on its own connection string (M-2) (#383)
- **Do:** `ConnectionStrings:ragdb-maintenance` (falls back to `ragdb`) for `HnswIndexHealth.RebuildAsync`; set
  `statement_timeout = 0` with `SET` on the session, not as a startup option (PgBouncer drops `options`); AppHost
  passes it like `ragdb-listen`.
- **Paths:** `src/Ume.Rag.Worker/Engine/**`, `src/Ume.Rag.Worker/Jobs/**`, AppHost wiring.
- **Done when:** a Worker test shows the rebuild uses the maintenance string and sets the timeout by `SET`.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4; data-model §1 (`ragdb-listen`).

### [x] B-8 Fired PII flag types stored and shown (#217, M-2) (#404)
- **Do:** the PII stage stores the fired types next to `PiiCounts`; the PII report and document page show them.
- **Paths:** `src/Ume.Rag.Worker/Processing/Pii/**`, `src/Ume.Rag.AdminApi/PiiReportEndpoints.cs`,
  `DocumentEndpoints.cs`, migration, admin UI.
- **Done when:** a document with fired rules shows their types in the report (test and a look in the browser).
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; processing §9.4–§9.5.

### [x] B-9 Index health for multi-collection scopes (M-2) (#388)
- **Do:** sample every HNSW-searched scope an assistant link uses, including multi-collection ones; report the worst
  recall per profile and log the failing scope.
- **Paths:** `src/Ume.Rag.Worker/Engine/HnswIndexHealth.cs`.
- **Done when:** a test with several small collections that together pass the HNSW threshold samples that scope.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4.

### [x] B-10 Scope size counts topic-filtered chunks (M-2) (#389)
- **Do:** `ScopeSizeCache` counts the chunks of a (collection, tag) link, so a narrow topic in a large collection gets
  exact search.
- **Paths:** `src/Ume.Rag.Retrieval/**`.
- **Done when:** a retrieval test: a topic of a few hundred chunks in a collection over the limit is searched exactly.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4; to-verify #180.

### [x] B-11 "Open in Open WebUI" link on the assistant page (#363 point 2) (#395)
- **Do:** the assistant page links to the client's `PortalUrl` for a published assistant on a client with one.
- **Paths:** `src/admin-ui/**`, `src/Ume.Rag.AdminApi/AssistantEndpoints.cs`.
- **Done when:** the link shows on a published `turkos` and opens Open WebUI (look in the browser); the pilot-journey
  spec's step 13 follows it.
- **Waits on:** –
- **Read:** admin §5.4 step 13.

### [x] B-12 Rerank per assistant: `ChatModelReranker` now, `GatewayReranker` later (live run #421)
- **Do:** `ChatModelReranker` behind `IReranker`: one chat call through the gateway (`/v1/chat/completions`, the
  assistant's residency ceiling) with the question and the top 20 fused candidates, asking for JSON scores per
  passage; invalid output or the timeout degrades to the fused order (`rerank_unavailable`). The model is a platform
  setting (`Settings.Retrieval.RerankModel`, a chat model from the mirror); no `temperature` for Claude models.
  An assistant setting (Advanced) turns rerank on, off by default. The evaluation harness runs a set with and
  without rerank and reports latency and cost per query. FakeGateway answers the rerank prompt deterministically.
  `GatewayReranker` follows on the gateway's rerank endpoint when it exists.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.FakeGateway/**`, assistant and platform settings in the admin
  API and UI, `scripts/evaluation/**`.
- **Done when:** retrieval tests for scoring, malformed output, timeout and degrade; a live evaluation run on
  `turkos`'s generated set over the interim gateway reports recall@10/MRR, p50/p95 latency and cost with and without
  rerank (link it on the PR).
- **Waits on:** – (the live run: the other main session, #403).
- **Read:** Decisions "Rerank pulled forward"; knowledge-api §6.7.

### [x] B-13 Config lock wait histogram (#391, to-verify #23; #396)
- **Do:** emit `rag.admin.config_lock.wait` (histogram, seconds) around the `config."ConfigVersion"` lock wait in the
  admin's config writes, on the admin's meter; the measurement at the real write rate is A-L6's.
- **Paths:** `src/Ume.Rag.Infrastructure/Config/**`, `src/Ume.Rag.AdminApi/**`, their tests.
- **Done when:** a test records one measurement per config write, and a longer one while another write holds the lock.
- **Waits on:** –
- **Read:** [`../to-verify.md`](../../to-verify.md) #23; results-b #23 (B-3, #393).

### [x] B-14 FakeGateway matches gateway release 1 and rerank (#390) (#406)
- **Do:** align `Ume.Rag.FakeGateway` with #390's Feature B points 1–4 and 8 (no circuit-open 503: failing in-ceiling
  targets end in `502`/`504`; 403 vs 503 + `Retry-After` only on the ceiling 503; `400 invalid_request` for a
  malformed, empty, repeated or required-but-missing header; `/v1/models` filtered by the ceiling) and serve
  `/v1/rerank` in the Cohere shape (documents as strings or `{text}`, `results[]` with `index` and
  `relevance_score`, `usage`), with a **no-rerank-model mode** that answers 404/400 as the gateway does. List any
  remaining difference on #390. Ume.Rag's gateway clients retry 502/504 with the same ceiling, never a wider one.
- **Paths:** `src/Ume.Rag.FakeGateway/**`, its tests; the retry policy where the gateway clients live.
- **Done when:** FakeGateway tests cover each point and the rerank shape; #390 has the comparison comment.
- **Waits on:** –
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built"; gateway-changes §3.3, §7.4.

### [x] B-15 `deploy/keycloak` for the shared realm (#390, #215) (#409)
- **Do:** `rag-additions.json` gets `"realm": "ume"` and `$(env:NAME)` placeholders (`$(env:NAME:-default)` for
  defaults); drop `rag-realm-settings.json` and the OTP flow for the shared realm (keep a standalone variant only if
  cheap); `apply-additions.py` only for a standalone realm; the Keycloak test pin moves to 26.8.1 by the digest in
  #390; local redirect URIs stay out of the production file.
- **Paths:** `deploy/keycloak/**`, its tests.
- **Done when:** the additions import with keycloak-config-cli after a base realm in a 26.8.1 test container, and the
  `ume-rag-admin` token carries only `rag-*` roles plus `departmentCodes`; #215 closed.
- **Waits on:** –
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built"; identity §11.2.

### [x] B-16 `GatewayReranker` on `/v1/rerank` (live run #421; timeout #438)
- **Do:** `GatewayReranker` behind `IReranker` calling `/v1/rerank` (Berget AI `bge-reranker-v2-m3`, `Eu` ceiling
  until on-prem), selectable per assistant next to `ChatModelReranker`. **Rerank is optional everywhere** (user,
  2026-11-06): with no rerank model set, no rerank call is made, retrieval is plain fused, and there is no
  `rerank_unavailable`, no degraded health and nothing that looks broken in the admin UI; the assistant's rerank
  switch is disabled with a hint while the platform has none. A 404/400 or timeout from the gateway falls back to
  fused order, logged once, never a failed answer. Timeouts: 500 ms for `GatewayReranker` (1.5 s since #438); `ChatModelReranker` gets
  its own longer default (3 s, then set from B-12's live p95). The evaluation compares both rerankers.
- **Paths:** `src/Ume.Rag.Retrieval/**`, assistant and platform settings in the admin API and UI.
- **Done when:** retrieval tests against the FakeGateway rerank (B-14); the chat path, the test panel and the
  evaluation are tested with rerank fully unconfigured; a live evaluation run once the endpoint exists.
- **Waits on:** the live run only (the other main session): gateway v0.9.0 (2026-11-06) with Berget AI
  `BAAI/bge-reranker-v2-m3` as a Rerank model, `Settings.Retrieval.gatewayRerankModel` set, then the evaluation's
  `hybrid-rerank-gateway` variant (#416).
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built", "Rerank pulled forward"; gateway-changes §7.4.

### [x] B-17 Chat: one retry on a connection failure (chat-api §8.3) (#422)
- **Do:** the chat's gateway call retries once when the connection fails with no response, within 300 ms, same
  request, same ceiling and key; never after the first byte. Found by the hot-path tidy (#417): today only 502/504
  are retried (`GatewayRetryHandler`). Decide whether the handler gets the case for every client or only the chat
  client; the query embedder keeps its own loop (knowledge §5.3).
- **Paths:** `src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.Answering/Chat/**`.
- **Done when:** a test with a refused connection then an answer shows one retry, and none after the first byte; the
  chat golden files (`tests/Ume.Rag.Api.Tests/Golden/chat/`) unchanged.
- **Waits on:** –
- **Read:** chat-api §8.3; Decisions "Gateway release 1 and shared Keycloak as built".

### [x] B-6 Go-live pin review, B's part (last code item)
- **Do:** review every NuGet and npm pin younger than 7 days; the realm callback for the Open WebUI pin bump
  (`/oauth/oidc/login/callback`) matching A-4; condense stays at `Eu` until an on-prem route exists (M2).
- **Paths:** `Directory.Packages.props` (shared), `src/admin-ui/package*.json`, `deploy/keycloak/**`.
- **Done when:** no pin under 7 days old without a recorded reason; #113 recorded.
- **Waits on:** every other code item of both streams.
- **Read:** Decisions "7-day rule in development"; identity §5.3.

### [x] B-18 General knowledge on a public assistant: warn and confirm (#445)
- **Do:** V36 turns from a refusal into a confirmed warning: draft save returns `general_knowledge_public_unconfirmed`
  as a warning; publish, restore and `PUT /api/assistants/{id}` with a level change to `public` answer 409 with that
  code until the body carries `confirmGeneralKnowledge: true`, and the audit event records `generalKnowledgeConfirmed`.
  The Utkast tab keeps all four choices enabled for `public` and shows the warning under the group; *Publicera
  utkastet*, restore and the level change (B-19) ask one extra confirmation and resend with the flag. Remove the
  run-time override in `ChatService` (`Classification.Public` → `GeneralKnowledge.Off`): the published level runs.
  Texts only in `labels.ts`/`messages.ts`.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Answering/**`, `src/admin-ui/**`, their tests.
- **Done when:** rules tests: warning on save, 409 without the flag and success with it on publish, restore and level
  change, audit field set; a chat test runs `fallback` on a `public` assistant with the §10.4 caveat line; vitest: the
  radios are enabled and the confirmation is asked.
- **Waits on:** –
- **Read:** Decisions "General knowledge on a public assistant"; admin §4.5 (publish, restore, assistants), §4.6 V36,
  §5.5.4; chat §7.3, §10.4.

### [x] B-19 Level change in the edit drawer (#446)
- **Do:** add the level to the collection's *Edit* drawer (in the `⋯` menu; create the drawer with name, contact and
  level if the page has none) and to the assistant's *Ändra* drawer, `edit` right, saved with the other fields through
  the existing `PUT`s. Show the API's refusals (`collection_level_conflict`, `assistant_below_collection`, V3/V7/V8/V9)
  in the drawer with `affected[]` by name; a change to `public` with general knowledge on asks B-18's confirmation.
- **Paths:** `src/admin-ui/**` (`CollectionView.vue`, `AssistantView.vue`, drawers, labels), the admin API only if a
  field is missing.
- **Done when:** vitest: the level field saves, a refusal lists the affected objects; a browser check of both drawers
  (screenshot in the PR).
- **Waits on:** B-18 for the confirmation (the rest can go first).
- **Read:** Decisions "Level change in the edit drawer"; admin §4.5, §5.5.2, §5.5.4.

### [x] B-20 Model pickers show what the key may use (#447)
- **Do:** `GET /api/gateway/models?level=` adds `available` (`true`/`false`/`null`) per row from the gateway's
  `/v1/models` called with the platform's key at the level's ceiling (no `level`: the platform ceiling), cached 5
  minutes per ceiling; `null` when the gateway can't be asked. Every model picker (assistant model; Settings:
  evaluation, judge, condense, skill gate, `rerankModel`, `gatewayRerankModel`) greys out `available: false` with "Not
  available to this platform's key" and keeps every model choosable with "Availability unknown" on `null`. V32 also
  calls `/v1/models` on save (`model_not_listed` 409, `gateway_unavailable` 503), as V8 does on publish.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**` (the assistant picker; the Settings forms' model fields,
  coordinating with stream A where the generic forms live), FakeGateway if its `/v1/models` needs a key filter.
- **Done when:** API tests for `available` true/false/null and the cache; a V32 test refuses an unlisted model; vitest:
  a greyed-out model can't be chosen; checked live on the real gateway (15 mirror models, 5 available).
- **Waits on:** –
- **Read:** Decisions "Model pickers show what the key may use"; admin §4.5 (`/api/gateway/models`), §4.6 V8/V32,
  §4.10.

### [x] B-21 One connection pool in rag-api (M-3, #127) (#485)
- **Do:** rag-api registers a second keyed `NpgsqlDataSource` (`"retrieval"`, `Answering/KnowledgeExtensions.cs:40`,
  `Retrieval/Postgres/RetrievalDataSource.cs`) next to `ragdb` on the same connection string, which can double the
  connections per instance and breaks `MaxConcurrentSearches = ⌊(pool − 3) ÷ 2⌋` (knowledge §6.6, data-model §1.1:
  one pool per process). Move retrieval onto `ragdb` (it maps pgvector since #118); remove the keyed pool.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**`, their tests.
- **Done when:** one `NpgsqlDataSource` per process (a DI test); retrieval and integration tests pass.
- **Waits on:** –
- **Read:** knowledge §6.6; data-model §1.1.

### [x] B-22 Publish checks the worker supports the template version (M-3, #298) (#486)
- **Do:** pipeline publish calls `PipelineValidator.ValidateForPublish` (rules 1–4, with the live workers' supported
  template versions) instead of `Validate` (`PipelineEndpoints.cs:227`), so engine §4 rule 4 / admin V24 hold during a
  rolling update.
- **Paths:** `src/Ume.Rag.AdminApi/**`, its tests.
- **Done when:** an admin API test refuses a publish whose template version no live worker supports.
- **Waits on:** –
- **Read:** engine §4; admin §4.6 V24.

### [x] B-23 Small admin gaps from the deviation review (M-3) (#487)
- **Do:** (1) the feedback import alert after 4 failed runs (chat §14.3; today only `LastOutcome`,
  `FeedbackEndpoints.cs:99-107`); (2) the collection page's sources table shows documents and the last run (the API
  returns both, `CollectionView.vue:92-96`, admin §5.5.2); (3) the deletion card's "at most 20 % (at least 5)" comes from
  the configured deletion guard (`labels.ts:1032`, `DeletionGuardSettings`); (4) `deploy/keycloak/rag-additions.json`'s
  `open-webui` description no longer says "legacy callback path".
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `deploy/keycloak/**`, their tests.
- **Done when:** a test per point (vitest for 2 and 3; an API or importer test for 1; the realm-additions test for 4).
- **Waits on:** –
- **Read:** chat §14.3; admin §5.5.2, §5.5.3.

### [x] B-24 Restore a deleted uploaded file (M-3, #309) (#489)
- **Do:** `POST /api/sources/{id}/files/restore` within the upload grace (connectors §9.3, admin §4.5 files row): the
  UI promises "Within 14 days the platform team can bring them back" (`labels.ts:580`, `:1044`) and no route exists.
  Build the route (platform admin) and a *Restore* action on the files list for deleted slots, or change the texts if
  the design decides otherwise (ask main first).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Worker/**` (if the restore needs a job), `src/admin-ui/**`.
- **Done when:** a deleted file restored within the grace is searchable again after its sync; tests on both sides.
- **Waits on:** –
- **Read:** connectors §9.3, §13.4; admin §4.5.

### [x] B-25 Search statistics within the role's timeout (#499, step 1) (#516)
- **Do:** measure `RetrievalStatements.CountChunks` and `FrequentLexemes` on the scale test data (A-18's probe or the
  largest local collection) as `ume_rag_api` with its 5 s `statement_timeout`. If either can exceed it, run both inside a
  transaction with `SET LOCAL statement_timeout` equal to their client timeouts (30 s, 60 s), and log a failed refresh
  at Warning with the statement's name. List it under "Design deviations" (knowledge §6.4, §20). The single-writer
  redesign of #499 is step 2 (M-10), not this item.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `tests/Ume.Rag.Retrieval.Tests/**`.
- **Done when:** the measured times are in `results-b.md`; a test shows a refresh slower than the role's timeout
  completes instead of being cancelled.
- **Waits on:** –
- **Read:** #499; knowledge §6.2, §6.4, §20.

### [x] B-26 Less telemetry overhead (#503, code part) (#518)
- **Do:** `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"` in every service's `appsettings.json`;
  register tracing only when an OTLP endpoint is configured; leave sampling to the standard `OTEL_TRACES_SAMPLER`
  variables (the Compose knob is A-21).
- **Paths:** `src/*/appsettings*.json`, `src/Ume.Rag.ServiceDefaults/**`.
- **Done when:** a local run logs no EF SQL text at Information; traces still reach the Aspire dashboard locally.
- **Waits on:** –
- **Read:** #503; chat §17.

### [x] B-27 Smaller .NET footprint (#504, csproj part) (#520; measured, no gain: no csproj change)
- **Do:** measure idle and loaded RSS per .NET container first (local Compose with the release images, or the
  AppHost), then `<ServerGarbageCollection>false</ServerGarbageCollection>` for AdminApi, Worker and MigrationService
  (rag-api keeps Server GC). ReadyToRun only with a measured start-up gain: report it, main changes
  `Directory.Build.*`. The health-check interval is A-21.
- **Paths:** `src/Ume.Rag.AdminApi/*.csproj`, `src/Ume.Rag.Worker/*.csproj`, `src/Ume.Rag.MigrationService/*.csproj`.
- **Done when:** a before/after RSS table in `results-b.md`.
- **Waits on:** –
- **Read:** #504.

### [x] B-28 One statement K: the search-vector expression as a parameter (#509, statement part) (#523)
- **Do:** `RetrievalStatements.Keyword` takes the search-vector expression (the stored `SearchVector` by default,
  `to_tsvector('swedish', "Text")` for the no-decompounder variant), cached per variant like the other statements.
  The worker's variant switches to it in A-24.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `tests/Ume.Rag.Retrieval.Tests/**`.
- **Done when:** both forms are built from one statement and tested.
- **Waits on:** –
- **Read:** #509; knowledge §6.2.

### [x] B-29 Open WebUI user JWT through a library (#512) (#525)
- **Do:** validate the forwarded user JWT with `Microsoft.IdentityModel.JsonWebTokens` (HS256 only, issuer
  `open-webui`, 30 s clock skew, the existing maximum lifetime), keeping every refusal code.
- **Paths:** `src/Ume.Rag.Api/**`, `tests/Ume.Rag.Api.Tests/**`, `Directory.Packages.props` (shared: its own small PR).
- **Done when:** the existing refusal tests pass unchanged; a token signed with another algorithm is refused.
- **Waits on:** –
- **Read:** #512; identity §5.

### [x] B-30 Editable embedding profiles before use (#554, #559, #558)
- **Do:**
  - The three endpoints `GET /api/embedding-profiles[/{id}]`, `PUT /api/embedding-profiles/{id}` and
    `POST /api/embedding-profiles/{id}/test`, as admin design §4.5 lists them: the `inUse` rule, the test before each
    save, the canary stored on save, and the index rebuilt on a dimension change.
  - In the admin UI: the profile on Platform → Gateway models (generic form, §5.8), with a **Test** button whose outcome
    names the fix on the gateway side.
  - The seeder's "exists with another model" warning points to the admin UI.
  - The seeder's code defaults follow the Decisions row "Pilot embedding model, U-b" (`RequestDimensions` true,
    `MaxInputTokens` 8191), as the deployment defaults have done since #546.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `src/Ume.Rag.MigrationService/EmbeddingProfileSeeder.cs`,
  `tests/Ume.Rag.AdminApi.Tests/**`, `tests/Ume.Rag.IntegrationTests/**`.
- **Done when:** tests show all of these:
  - An unused profile's model, dimensions and `RequestDimensions` change, and its canary is recorded again.
  - A save whose test fails is refused with 422 `profile_test_failed`, and the profile is unchanged.
  - A profile with a chunk set refuses every field but `MaxResidency` with 409 `profile_in_use`.
  - **Test** against FakeGateway reports `model_not_allowed`, `max_residency_not_met` and a wrong dimension count in
    plain words.
  - After a dimension change the profile has exactly one HNSW index, at the new size.
- **Waits on:** –
- **Read:** Decisions "Editable embedding profiles before use"; admin §4.5, §5.8; data-model §2.1; processing §11.

### [x] B-31 Answer length from the model (#561, #564, #569, #571, #573, #574)
- **Do:** add `GatewayModels.MaxOutputTokens` (migration, check constraint) and let `PUT /api/gateway/models/{name}`
  set `contextWindow` and `maxOutputTokens` on any row, kept by the sync; make `answer.maxOutputTokens` and
  `answer.contextTokens` optional (`AssistantChatSettings` with nullable values; new drafts write neither), resolve
  them against the version's model row as chat §6.3 (`C`, `R`, `F` = 1,024, `max_tokens` = override, else
  `min(O, C − P)`, else left out in `GatewayChatClient`); V17's new bounds and the run-time lowering of an override
  above a lowered model limit; the Utkast tab's *Use the model's limit* checkboxes with the model's limit shown; the
  Gateway models page edits both limits; the test panel returns and shows `maxTokens`, `maxTokensSource` and
  `finishReason` ("The answer stopped at the length limit"); the optional platform ceiling
  `Settings.Chat.MaxAnswerTokens` (`X` in §6.3: caps `R`, override included, fills an unknown `R`; on
  `PUT /api/settings/chat` with its field on the Settings page; `maxTokensSource` `ceiling`; the Utkast tab shows it
  when lower). No compatibility step: the migration removes
  `answer.maxOutputTokens` and `answer.contextTokens` from every development `QuerySteps`.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/**` (migration), `src/Ume.Rag.Answering/**`,
  `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Api/**` if the client's `max_tokens` cap moves, `src/Ume.Rag.FakeGateway/**`
  (echo the `max_tokens` it got, `finish_reason: length` on request), `src/admin-ui/**`, their tests.
- **Done when:** unit tests for the §6.3 table (override; model limit known; unknown → no `max_tokens`; override above
  the model limit lowered; `C − P` below `O`; the ceiling lowering an override, capping the model's limit and filling an
  unknown one); a FakeGateway test sees no `max_tokens` for a model without limits;
  rules tests for V17's bounds; vitest for the checkboxes and the test panel's length notice.
- **Waits on:** –
- **Read:** Decisions "Answer length from the model"; chat §6.3, §8.1; admin §4.9, §4.10, V17, §5.5.4 (Utkast tab);
  data-model `GatewayModels`, `AssistantVersions`.

### [x] B-32 Chat prompt texts in settings (#575, #576)
- **Do:** `Settings.Chat.Prompts` (chat §7.3 table) read through the config snapshot; `PromptBuilder`, `Condenser`
  (both prompts), the follow-up prompt and the two caveats take each text from it with today's text as the built-in
  default, the locked lines staying in code (the follow-up prompt's locked lines after its editable block, chat §9.8);
  `{assistant}` and `{N}` filled; V38 on `PUT /api/settings/chat`; the Settings page's **Prompt texts** panel (the
  whole frame in order, editable fields with **Reset to default**, locked lines read-only, the "changes every
  assistant at once" note). Labels and messages in `labels.ts` and `messages.ts`.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**`,
  `src/admin-ui/**`, their tests.
- **Done when:** a golden test shows the default frame, condense prompts, follow-up prompt and caveats unchanged in
  wording (the follow-up lines in their new order); a test with every member set shows the locked lines still
  present; V38 refuses a `<sources>` tag, an unknown placeholder and an unknown member; the test panel answers with an
  edited identity line; vitest for the panel and its reset.
- **Waits on:** – (independent of B-31; touches `PromptBuilder` too, so merge one, rebase the other)
- **Read:** Decisions "Chat prompt texts configurable"; chat §5, §5.1, §7.2–§7.4, §9.8, §10.2, §10.4; admin §4.5
  (`chat` section), V38; data-model `Settings`.

### [x] B-33 rag-api reads the keyword statistics (#499 step 2, api part)
- **Do:** `ScopeSizeCache` builds the stop list from `content."KeywordStatistics"` (knowledge §6.2: share = `Ndoc` /
  the row's `ChunkCount`, the same floors and the multi-collection lower bound) instead of `FrequentLexemes`;
  `RetrievalStatements.FrequentLexemes` and its `SET LOCAL statement_timeout` leave rag-api (the worker's A-29 owns the
  statement). `CountChunks` stays.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**` if needed, their tests.
- **Done when:** rag-api runs no `ts_stat` (`pg_stat_statements` on a test database, or a test that the api role
  issues none); the stop list on the evaluation collection equals today's.
- **Waits on:** A-29 (the table).
- **Read:** #499; knowledge §6.2.

### [x] B-34 Schema contract for stream b's positional SQL readers (#510, follow-up of A-28)
- **Do:** add the raw-SQL statements of stream b's stores to A-28's schema contract (`SqlSchemaContractTests`,
  `sql-shapes.json`, #588): the retrieval statements and hydrator (`src/Ume.Rag.Retrieval/Postgres/**`), the admin
  API's and rag-api's raw readers, the column names and types each reader expects, with `CommandBehavior.SchemaOnly`.
  Move the harness to `tests/Ume.Rag.ContractTests/**` if both streams' statements fit there better.
- **Paths:** `tests/**` (the harness and shapes), `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.AdminApi/**`,
  `src/Ume.Rag.Api/**` (only where a statement must be exposed as a constant).
- **Done when:** a renamed column in a retrieval or admin statement fails the test.
- **Waits on:** –
- **Read:** #510, #588.
### [x] B-35 Gateway phase 1: context and model sync with the organisation, the keys mirror, the FakeGateway contract (#580, cost-attribution B3)
- **Do:** the admin's sync of admin §4.10 from the gateway's discovery with the admin's key, `If-None-Match`/`ETag`
  and nothing written on `304`: `GET /v1/ume/context` → `gateway.features`, `version`, `admin_url` in memory and on the
  Gateway page; `departments` and `teams` → `Departments`/`Teams`, taken only when `features` contains `organisation`
  and `key.integration_scopes` contains `organisation.read` (otherwise left as they are, `degraded` with the reason);
  member groups from each department's `member_groups` when `features` contains `member-groups` (identity §2.1), and
  the hand-entry route `PUT /api/organisation/departments/{id}/member-groups` goes (stream a removes its platform UI
  edit, A-33); `assigned_keys` →
  the `GatewayKeys` mirror with `Usable` (data-model §2.1; migration and grants, rag-api `S`); `GET /v1/models` →
  `GatewayModels` from the `ume` fields (`kind`, `residency`/`residencies`, `context_window`, `max_output_tokens`,
  `features`, `pricing`), the manual limit fields kept only while the gateway sends `null`. Drop the Data API path:
  `OrgSync:DataApiKey`, the catalogue client and the FakeGateway's `/v1/catalog/*`. The FakeGateway answers as
  the gateway's contract `docs/openapi/integrations.json` describes (`key.integration_scopes`, `key.allowed_residencies`,
  `usable`, the features `credential-residencies` and `organisation`; the admin's dev credential key holds
  `organisation.read` and gets every department and team, an inactive one included). Pin the document in
  `contracts/gateway/` with the gateway release it was taken from (`v0.10.0`; part 1 pinned #84's `7b678a1`, which
  lacks `member_groups` and the feature `member-groups`), and a contract test checks the FakeGateway's `/v1/ume/context` and `/v1/models`
  answers against its schemas: an undescribed field or a missing required one fails, as in the gateway's own tests.
- **Paths:** `src/Ume.Rag.AdminApi/**` (`OrganisationSync.cs`, `GatewayModelSync.cs`, `GatewayEndpoints.cs`),
  `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`, `deploy/postgres/app-roles.sql` (+ its
  copy `deploy/kubernetes/base/app-roles.sql`; stream a regenerates the Portainer stack, issue labelled `stream-a`), `src/Ume.Rag.FakeGateway/**`, `src/Ume.Rag.AppHost/**` (the Data API key
  goes), `contracts/gateway/**`, `src/admin-ui/src/**` outside `platform/` (the Gateway page's features and version),
  `tests/**`.
- **Done when:** against the FakeGateway: the sync fills `GatewayKeys` from `assigned_keys` (a revoked key's row goes,
  a disabled one keeps its `Status`, `usable: false` is stored); departments and teams come from the context, an
  inactive one turns `IsActive = false`, a department's `member_groups` replace its stored ones and the PUT route is
  gone; without `organisation.read` the
  sync leaves departments and teams unchanged and reports `degraded`; a second run with an unchanged `ETag` writes
  nothing; a model's limits come from `ume.context_window`/`max_output_tokens` and the manual fields don't show for
  it; no `OrgSync:DataApiKey` or `/v1/catalog/` call is left; the contract test fails when the FakeGateway renames a
  field or drops a required one.
- **Waits on:** –. The live run needs the gateway on v0.10.1 (released 2026-11-12).
  A-30 removes the Data API key from the deployment.
- **Read:** Decisions "Gateway keys"; admin §4.10; identity §2, §2.1; data-model `GatewayKeys`; gateway-changes §5,
  §5.5; repo-and-deployment §4.3; the gateway's `docs/openapi/integrations.json` and `docs/integrations.md` at `7b678a1`;
  #580 (the gateway owner's comment of 2026-11-12).

### [x] B-36 Gateway phase 1: a gateway key per assistant and knowledge base, the key picker, V39 (#580, cost-attribution B4)
- **Do:** `GatewayKeyId` on `Assistants` and `Collections` (data-model §2.1); `GET /api/gateway/keys?departmentId=`
  (admin §4.5); the key picker on the assistant's and knowledge base's pages with the key's settings read-only and a
  link to the gateway (`admin_url`), a warning when the key has a system prompt (admin §5.5.4); V39 on save (admin §4.6:
  an `Active` and `usable` key of the object's department, owner active, a residency within the object's level, every
  model the object uses allowed); a key the gateway marks `usable: false` shows in the picker as not selectable, with
  the reason (it shares no residency with Ume.Rag's credential key). No call is made as the key yet, and publishing doesn't need one until the gateway's `features`
  contain `act-as-key` (phase 2).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`,
  `src/admin-ui/src/views/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`, `tests/**`.
- **Done when:** the picker lists only the department's keys, unusable ones marked with their reason; V39 refuses
  another department's key, a disabled key, a key with `usable: false`, a key whose residencies are all above the object's level and a key that
  lacks one of the object's models (the embedding profile's and rerank models included), each with its code; a saved
  key shows its settings read-only.
- **Waits on:** B-35 (the mirror).
- **Read:** admin §4.5, §4.6 V39, §5.5.2, §5.5.4; data-model §2.1; gateway-changes §5.3, §5.5; cost-attribution §4.

### [x] B-37 Gateway phase 1: one credential key per service (#580, cost-attribution B2, code part)
- **Do:** `Gateway:Key` (secret file `Gateway__Key`) in place of `Gateway:Keys:OnPrem/Eu/External` in the gateway
  client of api, admin and worker; `ResidencyCeilingHandler` refuses a call without a ceiling or with one above the
  service's maximum (the worker `Eu`; api and admin the top of their `Gateway:MaxResidency` ladder), unit-tested at
  every call site as before (identity §4.7); the FakeGateway accepts a credential key; the AppHost passes one dev key per
  service. Don't send `x-ume-on-behalf-of` yet (phase 2).
- **Paths:** `src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.AppHost/**`, `src/Ume.Rag.FakeGateway/**`, the
  services' gateway registration, `tests/**`.
- **Done when:** no `Gateway:Keys:*` is read anywhere; a call above the service's maximum is refused before sending and
  logged; the call-site tests pass with one key per service; the local stack answers a chat on the FakeGateway.
- **Waits on:** –. A-30 (deploy) follows with the same key names.
- **Read:** identity §4.7; repo-and-deployment §6.3, §7.1; gateway-changes §5.1.

### [x] B-38 (#608, #615, by the main session) Cost per step: every chat model call's tokens and cost, the usage tables, the FakeGateway's cost (cost-attribution C1)
- **Do:** every model call of a chat turn (condense, long-question condense, query embedding, rerank, answer,
  follow-ups) yields a usage record of chat §8.3: its `x-ume-step`, input, cached input
  (`usage.prompt_tokens_details.cached_tokens`) and output tokens, and its cost from `x-ume-cost-usd`, else the stream
  comment `: ume-cost-usd=<usd>` (every streamed answer sends `x-ume-stream-cost: 1`; the stream reader takes the
  comment and never forwards it), else an estimate from `GatewayModels`' prices (cached input at its own price), kept
  apart. The reading and the estimate live once in `src/Ume.Rag.Infrastructure/Gateway/` so the worker (A-31) uses the
  same code; `IReranker` and the query embedder return their call's usage. `ChatTurn` carries the turn's usage records;
  `ConversationWriter` upserts `chat."AssistantDailyUsage"` per (day, assistant, client, level, step) last in the
  batch (chat §12.2), and a turn that fails after a model call is enqueued usage-only. Migration: the new
  `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"` (data-model §2.2, §2.3; the worker's table here so
  A-31 needs no stream-b path), `InputTokens`/`OutputTokens`/`CostUsd` dropped from `AssistantDailyTotals` (and from
  the totals inserts of `FeedbackEndpoints` and `FeedbackImporter`), `rag-admin` `I U` on `AssistantDailyUsage`
  (data-model §1.2), retention of both (data-model §8). Metrics `rag.chat.tokens` (`cached-input`, steps `rerank`,
  `query-embed`) and `rag.chat.cost{step, source}` (chat §17). The FakeGateway sends `x-ume-cost-usd` on every JSON
  answer (chat, embeddings, rerank) from its catalogue prices, writes the stream comment just before `data: [DONE]` when the
  request has `x-ume-stream-cost: 1` (gateway-changes §5.4), reports `cached_tokens` when scripted, and a model `fake-no-cost` that sends
  neither.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**`,
  `src/Ume.Rag.AdminApi/FeedbackEndpoints.cs`, `src/Ume.Rag.Infrastructure/Gateway/**`,
  `src/Ume.Rag.Infrastructure/Persistence/**`, `src/Ume.Rag.Domain/**`, `deploy/postgres/app-roles.sql` (+ its
  copy `deploy/kubernetes/base/app-roles.sql`; stream a regenerates the Portainer stack, issue labelled `stream-a`), `src/Ume.Rag.FakeGateway/**`, `tests/**`.
- **Done when:** against the FakeGateway (chat §18.2): a turn with condense, query embedding, rerank, answer and
  follow-ups writes one `AssistantDailyUsage` row per step whose tokens and `CostUsd` equal the FakeGateway's; the
  streamed answer's cost comes from the comment and the comment is absent from the client's stream; on `fake-no-cost`
  the cost is in `EstimatedCostUsd` (cached input priced at `CachedInputPerMillionUsd`), `EstimatedCalls` counts it
  and `CostUsd` stays 0; a failed answer call still writes condense's and the embedding's usage; a cached query
  embedding writes nothing; `AssistantDailyTotals` has no token or cost column left.
- **Waits on:** –. Gateway phase 2 (the stream comment) is merged. **Taken by the main session** (stream b released it
  on #580, 2026-11-12). **Part 1 is done** (#608): the two usage tables with grants and the chat retention step, the
  shared reader and estimate (`src/Ume.Rag.Infrastructure/Gateway/GatewayUsage.cs`), usage from the rerankers and the
  query embedder, the FakeGateway's cost. **Part 2 is done** (#615): the chat turn's wiring, the totals' columns
  dropped, the metrics, the §18.2 tests. B-39 can pass `chat.Usage()` from the test panel (hand-off in #615).
- **Read:** cost-attribution §5; chat §8.3, §12.1, §12.2, §12.4, §17, §18.2; data-model §1.2, §2.2, §2.3, §8;
  gateway-changes §5.4, §7.1.

### [x] B-39 Cost per step: the test panel's calls under `admin-console` (cost-attribution C2) (#625)
- **Do:** the test panel (admin §4.9) collects the usage records of its turn as chat does (B-38) and `rag-admin`
  upserts them into `AssistantDailyUsage` under the assistant and the `admin-console` client row (its id from the
  snapshot by name and kind `internal`; without the row the usage is not written and a warning is logged), never into
  `AssistantDailyTotals`; `AssistantTestEndpoints` uses that row instead of a random client id. The panel's response
  carries `gateway.costUsd` and `costEstimated`, shown on the panel ("0,0012 USD", "estimated"). The dev seed adds the
  `admin-console` row (identity §4.1).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.MigrationService/DevSeeder.cs`, `src/admin-ui/src/views/**`,
  `src/admin-ui/src/components/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`,
  `tests/**`.
- **Done when:** a test-panel turn on the FakeGateway adds its steps to `AssistantDailyUsage` under `admin-console`
  with the FakeGateway's costs and leaves `AssistantDailyTotals` unchanged; the response's `costUsd` is the sum of its
  calls and `costEstimated` is true on `fake-no-cost`; a write failure leaves the answer intact.
- **Waits on:** B-38.
- **Read:** admin §4.9, §11.2; chat §8.3, §12.2; identity §4.1; data-model §1.2, §2.3.

### [x] B-40 Cost report API: per assistant, knowledge base and department, by day or month, CSV (cost-attribution C4) (#627)
- **Do:** admin §8.6's routes `GET /api/assistants/{id}/costs`, `GET /api/collections/{id}/costs` (Read; the
  collection's own department only) and `GET /api/costs` with `format=csv` (Manage: department admins their
  departments, platform admins all) over `AssistantDailyUsage` and `CollectionDailyUsage`: per period (Europe/Stockholm
  day or calendar month), step, client (assistants) and object, with calls, tokens, `costUsd`, `estimatedCostUsd`,
  `estimatedCalls`; scope by the row's `DepartmentId`; names from the snapshot, deleted objects as id + "(deleted)";
  limits 400 days by day and 36 months by month (400 `period_too_long`); the CSV with `;` and the headers of §8.6;
  `PermissionMatrix` entries (§3.4).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**` (`PermissionMatrix`), `contracts/admin/**`,
  `tests/**`.
- **Done when:** on seeded usage rows the routes return the expected sums by day and by month with gateway and
  estimated cost apart; the generated role matrix passes (a department admin gets 404 for another department's
  assistant and no foreign rows from `/api/costs`; a platform admin sees all; another department gets no cost for a
  shared knowledge base); the CSV has the documented headers; a too-long period gives `period_too_long`.
- **Waits on:** B-38 (the tables). The knowledge-base figures are empty until A-31 fills them; the routes and their
  tests don't wait on it.
- **Read:** admin §3.3, §3.4, §4.8, §8.6, §11.2; data-model §2.2, §2.3; cost-attribution §4.1, §5.

### [x] B-41 Cost in the admin UI: assistant and knowledge base cards, the Costs page (cost-attribution C5) (#628)
- **Do:** admin §8.6's UI: the *Cost* card on the assistant's Statistics tab (period picker, total with gateway and
  estimated parts, by step and client), the *Cost* card on the knowledge base's Overview (indexing and evaluation, last
  30 days, owning department only), and the *Costs* page under FOLLOW-UP for department and platform admins
  (department filter, period, granularity, one row per assistant and knowledge base with its cost, expandable by step,
  totals, **Download CSV**, the two notes on estimates and the gateway as the record). Route guard as admin §3.3. Every
  label in `labels.ts` (the step names too), every sentence in `messages.ts`.
- **Paths:** `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`, `src/admin-ui/src/router/**`,
  `src/admin-ui/src/api/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/e2e/**` if a browser test is
  added.
- **Done when:** Vitest covers the cards and the page against the B-40 contract (sums, estimated parts, empty states);
  a viewer has no Costs entry and the route sends them to `/forbidden`; the CSV link carries the filters; no UI string
  outside `labels.ts`/`messages.ts`.
- **Waits on:** B-40.
- **Read:** admin §3.3, §5.2, §5.3, §5.5.2, §5.5.4, §5.7, §8.6; Decisions "Admin UI in English".

### [x] B-L1 Status green against the real gateway
- **Done when:** `GET /api/gateway/status` green with the pilot keys; #95, #102, #162 rechecked.
- **Waits on:** the gateway session (EU routes, pilot keys).

### [x] B-L4 Gateway release 1 switch
- **Do:** the pilot team's per-level keys with `RequireMaxResidency`; every call sends its ceiling.
  Remove the worker's embedding batch cap below the context window (the gateway checks per input from release 1);
  `502`/`504` retry with the same ceiling, never wider.
- **Done when:** status green against release 1; `/v1/models` at a ceiling lists only models within it (#41).
- **Waits on:** gateway release 1 (tagged v0.9.0, 2026-11-06) deployed for the pilot.
- **Read:** gateway-changes §13.2–§13.4.

### [x] B-47 Malware scan optional: the `unscanned` state, `ScannedAt`, event 5106, the AppHost's `ClamAv:Mode`, the admin's note (#535)
- **Do:**
  - **State and column:** `UploadStates.Unscanned` (`unscanned`) in the `State` check and `Uploads.ScannedAt
    timestamptz NULL`, in one migration (connectors §9.1). No data step: the PR gives the SQL that sets `ScannedAt` on
    development rows that were scanned.
  - **Event:** 5106 `rag.malware.scan_off` (Warning) in `SecurityEvents`, with its row in `docs/security-events.md`.
  - **AppHost:** `ClamAv:Mode` = `on` (default) | `off` replaces `ClamAv:Enabled`. `off` starts no `clamav` resource
    and passes `ClamAv__Mode=off` to the worker.
  - **Admin API:** `unscanned` counts as *processing*; each file carries `scannedAt` (admin §4.5); the contract follows.
  - **Admin UI:** *Not scanned for malware* on a file's row and details when it is `unscanned`, or `indexed` with no
    `scannedAt`; *Files in this run were not scanned for malware* in a run's summary when its report says
    `malwareScan: "off"` (admin §5.5.7). Texts in `labels.ts` and `messages.ts` only.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`,
  `src/Ume.Rag.Infrastructure/Security/**`, `src/Ume.Rag.AppHost/**`, `src/Ume.Rag.AdminApi/**`,
  `src/admin-ui/src/views/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`,
  `docs/security-events.md`, `tests/**`.
- **Done when:** the migration applies and the schema contract shapes are regenerated; admin API tests: an `unscanned`
  slot is among *processing*, and an `indexed` slot without `ScannedAt` answers `scannedAt: null`; Vitest: the note
  shows on those files and not on a scanned one, and the run line shows for `malwareScan: "off"`; the AppHost with
  `ClamAv:Mode=off` has no `clamav` resource and the worker's environment has `ClamAv__Mode=off`.
- **Waits on:** –. **Part 1 is done** (#599, by the main session with the user's go-ahead, 2026-11-12): the state,
  the column and its migration `UploadScannedAt`, event 5106 and the AppHost's `ClamAv:Mode`. **Left for stream b:**
  the admin API (`unscanned` among *processing*, `scannedAt` on each file, the contract) and the admin UI's two notes.
  The worker writes `unscanned`, `ScannedAt` and `Runs.Report.malwareScan` since A-36 (#602).
- **Read:** Decisions "Malware scan optional"; connectors §9.1, §11; admin §4.5, §5.5.7; identity §10;
  repo-and-deployment §4.1.

### [x] B-48 A cancelled turn is recorded: the user's message, the partial answer, its usage, the `Cancelled` count (chat §8.3) (#636)
- **Do:** when the client disconnects or the idle or total timeout ends a streamed answer (chat §8.3), the writer gets
  the turn as any other (§12): the user's message, the partial answer as delivered so far with `Outcome = cancelled`,
  the turn's usage records (the aborted answer at its prompt estimate, as B-38 records it now, plus the calls before
  it), and `AssistantDailyTotals.Cancelled` counted in the same transaction. Today such a turn writes usage only:
  no message row, and `ConversationWriter` inserts `Errors` and `Cancelled` as constant `0`. Count `Errors` the same
  way for a turn that ends `error` after the user's message was accepted, if it doesn't already.
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.Answering/**`, their tests.
- **Done when:** a test disconnects at the first byte and mid-stream (FakeGateway `fake-slow` or a stalled stream):
  one user and one assistant message are stored, the second with `Outcome = cancelled` and the delivered text, the
  `chat-answer` usage row is estimated, and the day's `Cancelled` is 1; an idle timeout does the same; chat §18.4's
  "partial turn `cancelled`" case passes.
- **Waits on:** nothing (B-38 part 2, #615, records the usage).
- **Read:** chat §8.3 (client disconnect, usage and cost), §12, §18.4; data-model §2.3 `Messages`, `AssistantDailyTotals`.

## Main session (`main.md`)

### [x] M-1 Run the browser tests locally (covered by the runner's green run of #377: https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Do:** `scripts/Start-E2E.ps1` (stops the dev stack; own test database), then `npx playwright test` in
  `tests/e2e`.
- **Done when:** the pilot journey (admin §5.4 steps 1–13, mouse and keyboard) and the accessibility spec pass
  locally, or their failures are fixed or filed. This is the local evidence for the old items 11, 12 and 14; B-5 and
  A-2 still need the runner.

### [x] M-2 Recommendations for the open design calls
- **Do:** one short proposal each, with a default: topics for uploads; `UrlCanonicalizer` into Pipelines (#178);
  #217's fired-types list; identity §16 Q2–4, Q8; a direct connection for the HNSW rebuild behind a pooler; index
  health for multi-collection scopes; recall for a narrow topic in a large collection.
- **Done when:** the user has answered; the answers are in the design or the Decisions table.

### Support-assistant items before the pilot (user, 2026-11-06)

Stream B, which owns these paths, is inactive, so M-5 to M-8 are built on `main-session/<id>-<slug>` branches (the
ownership check restricts only `a/*` and `b/*` branches), by the main session or a stream A agent, one PR each,
reviewed and merged by the main session. Case plan: [`../cases/support-assistant/06-gaps.md`](../../cases/support-assistant/06-gaps.md).

### [x] M-5 Chat rate limit per key (SA-6, #424)
- **Do:** when the key itself is the subject (`SubjectKind.Application`), `ChatRateLimiter` uses the key's
  `Keys.RequestsPerMinute`, else `Settings.Chat.messagesPerMinutePerUser`; Open WebUI users keep the per-user value.
  Carry the value on the principal's `KeyEntry` (set in `KnowledgeCallers`). Add the admin key form's hint
  (`PLATFORM.fieldHints.requestsPerMinute`). Answer #424's `RequestsPerMinute` question in the PR (enforced, kept);
  the `AllowedIpRanges` half of #424 stays open (SA-7).
- **Paths:** `src/Ume.Rag.Api/ChatStreaming.cs` (`ChatRateLimiter`), `src/Ume.Rag.Api/ChatEndpoints.cs`,
  `src/Ume.Rag.Api/KnowledgeCallers.cs`, `src/Ume.Rag.Domain/Access/AccessModel.cs`,
  `src/admin-ui/src/utils/labels.ts`, `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Rate limit per key" tests of chat §18.9 pass (`dotnet test tests/Ume.Rag.Api.Tests`); the key
  form shows the hint (admin UI unit test or screenshot).
- **Waits on:** –
- **Read:** Decisions "Chat rate limit per key"; chat §4.1 step 4; identity §3, §6.3; data-model `Keys`; admin §5.8.

### [x] M-6 Long questions condensed into the search query (SA-4)
- **Do:** accept a question up to 16,000 characters (`ChatRequestParser.MaxQuestionChars`); keep its prompt form
  (line breaks) for the answer and its query form for kind, language, condense and retrieval; over 2,000 characters
  run the long-question condense (`x-ume-step: condense-long`, `Chat:LongQuestionCondenseTimeout` 3 s) on every turn,
  with the word-boundary fallback, counting and logging; a fallback counts as a failed condense for coverage. Give
  FakeGateway a default `condense-long` answer.
- **Paths:** `src/Ume.Rag.Answering/Chat/ChatRequest.cs`, `Condenser.cs`, `ChatService.cs`, `ChatOptions.cs`,
  `PromptBuilder.cs`; `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs` (only if `ParsedChat` changes shape);
  `src/Ume.Rag.FakeGateway/FakeChatScripts.cs`; `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Long questions" tests of chat §18.9 and the 16,001-character row of §18.6 pass; a golden prompt
  file shows a multi-line ticket reaching the answer prompt unchanged; the long-condense latency on the EU model is
  measured once against the dev gateway and recorded in the PR (chat §22).
- **Waits on:** –
- **Read:** Decisions "Long questions condensed into the search query", "Condense on the EU route"; chat §3.2, §5,
  §5.1, §6.2, §6.3, §7.1, §16.1, §17.

### [x] M-7 The `ume` extension: outcome and related documents (SA-1, SA-2)
- **Do:** parse and validate `ume` (§3.8; ignored for `open-webui`); build `related_documents` from the turn's
  retrieval (§9.7); write the `ume` member in the JSON answer and the `ume` chunk in the stream (after the final
  chunk, before usage and `[DONE]`), with `outcome` and `degraded`; metrics `rag.chat.ume` and
  `rag.chat.related_documents`.
- **Paths:** `src/Ume.Rag.Answering/Chat/ChatRequest.cs`, `ChatPlan.cs`, a new `RelatedDocuments.cs` in
  `src/Ume.Rag.Answering/Chat/`; `src/Ume.Rag.Api/ChatEndpoints.cs`, `src/Ume.Rag.Api/ChatStreaming.cs`;
  `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "`ume` contract", "Related documents" and "Open WebUI" tests of chat §18.9 and the `ume` rows
  of §18.6 pass; a request without `ume` gives a byte-identical answer to today's (golden JSON and SSE files).
- **Waits on:** –
- **Read:** Decisions "Related documents and the `ume` chat extension"; chat §3.2, §3.3, §3.6, §3.8, §6.2, §9.2,
  §9.3, §9.7, §17; knowledge §4.6, §10.

### [x] M-8 Follow-up suggestions (SA-3)
- **Do:** after a model answer with `ume.follow_ups`, one call to the condense model at the condense ceiling
  (`x-ume-step: follow-ups`, `Chat:FollowUpTimeout` 2 s, 0 = off), the prompt and accept rule of chat §9.8, delivered
  in the second `ume` chunk or the JSON `ume`; never stored or logged; metrics `rag.chat.follow_ups` and
  `rag.chat.tokens{step=follow-ups}`. Give FakeGateway a default `follow-ups` answer.
- **Paths:** a new `FollowUps.cs` in `src/Ume.Rag.Answering/Chat/`, `ChatService.cs`, `ChatOptions.cs`;
  `src/Ume.Rag.Api/ChatEndpoints.cs`, `src/Ume.Rag.Api/ChatStreaming.cs`;
  `src/Ume.Rag.FakeGateway/FakeChatScripts.cs`; `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Follow-ups" tests of chat §18.9 and the follow-up row of §18.6 pass, including the telemetry
  scan; the follow-up latency on the EU model is measured once against the dev gateway and recorded in the PR.
- **Waits on:** M-7 (the `ume` parsing and chunk writer).
- **Read:** Decisions "Follow-up suggestions"; chat §3.8, §5, §7.2, §7.4, §8.2, §9.5, §9.8, §10.3, §16.1, §17.

### [x] M-3 Code complete (2026-11-08, `e80c42c`: verify 42469432178, parser 42469434705, e2e 42471519711 run alone, #494)
- **Do:** when every code item of both queues is ticked: settle every remaining design deviation, fold the results
  files into `to-verify.md`, run the full CI set once.
- **Done when:** nothing open in the code sections of the queues; the roadmap says P1b code done.

### [x] M-9 Cost attribution stage 1: settle the design and queue the streams
- **Do:** write stage 1 of [`../platform/cost-attribution.md`](../../platform/cost-attribution.md) §5 into the designs it names
  (chat §8.3, §17 and the `AssistantDailyTotals` columns or a per-step table, admin §4.9 and a cost subsection under
  §8, processing §11/§14, engine §10): the cost of each call is the gateway's (`x-ume-cost-usd`, the stream comment
  from gateway phase 2, the Integration API's usage by run id from phase 3), estimated from the mirror's prices only
  where none is given, and split per step on `x-ume-run-id`/`x-ume-step`. Then queue C1–C6 in `stream-a.md` and
  `stream-b.md` with paths and done-whens. Bring Q3 of the plan's §7 to the user (Q1, Q2 and Q4 are answered by #580).
- **Done when:** the design sections state stage 1's rules, the queue items exist, and the user has answered Q3 (or
  confirmed the default).
- **Waits on:** nothing for the `x-ume-cost-usd` part; the stream cost and the read-back follow M-12's phases.
- **Written (2026-11-12):** stage 1 is in the designs (cost-attribution §5 lists the sections): a per-step table per
  owner schema, `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"` (data-model §2.3 says why, not
  columns on the totals row), gateway and estimated cost apart; the stream cost needs no wait (gateway phase 2 is
  merged), and the Integration API read-back is stage 2's B7. Queued: B-38–B-41 (stream b), A-31, A-32 (stream a).
  Q6 marked answered. Q3 answered by the user (2026-11-12): no hard budget in the pilot.

### [x] M-10 Settle #499 step 2 and #501 in the designs
- **Do:** #499 step 2: who maintains scope sizes and the keyword stop list (the worker at the chunk-set swap, a
  statistics table rag-api reads) in knowledge §6.4, §20 and data-model; #501: chunk sets written visible inside the
  swap transaction in engine §12 and data-model §3.2. Then queue the code items (A-25 waits on this).
- **Done when:** the design sections say it and the items are queued.
- **Waits on:** B-25's measurement (for #499).
- **Settled (2026-11-11):** scope sizes stay in rag-api (cheap); the stop list's statistics move to the worker
  (`KeywordStatistics`, knowledge §6.2); sets ≤ 1,000 chunks are written in the swap transaction (engine §14 rule 2a).
  Queued: A-29, B-33, A-25.

### [x] M-11 Design documents that contradict the decisions (found in the rewrite, 2026-11-08)
- **Do:** align each design with [`../decisions.md`](../../decisions.md), or bring the call to the user where the decision
  itself looks wrong:
  - engine §13.1 says `files` has no `pathRules` (it has them, "Organisation"); connectors §5.1 says robots.txt is
    always honoured ("Configurable politeness per host").
  - repo-and-deployment §6.2 still counts six containers; §8.1 states the deployer's `Group.Create` as settled (H7 is
    open); §8 names one runner (there are two); §8.2 says the backend tests run on the pinned pgvector image (they
    build `ume-rag/postgres`); the daily `audit.yml` schedule is in no decision.
  - identity: the provider-per-phase table gives admins OTP in P1 ("P1 pilot sign-in": no OTP); the brokered AD and
    Entra ID requirement of "Identity providers" is missing; identity §4.7 retries a gateway 503, chat §8.3 does not.
  - gateway-changes §1 and §13.1 call rerank a separate P2 release ("Rerank pulled forward").
  - `MaxInputTokens`: data-model §2.1 and `compose.prod.yaml` say 8192, "Pilot embedding model, U-b" 8191 (the model's
    limit is 8191: check and align).
  - admin: the judge model is "on-prem, V32" ("Evaluation models at the collection's ceiling": the collection's
    ceiling); Swedish labels and wireframes remain ("Admin UI in English"); Edit is a header button, "Level change in
    the edit drawer" puts it in the ⋯ menu.
  - roadmap and postponed B1 give the gold set as 100–150 answerable (decision: 120–150); processing §14.8 has an
    Overview alert, which "M-3 settlements" makes P2; the roadmap's P2 rerank bullet reads as if rerank were still P2.
  - Seven design headers (admin, chat, gateway-changes, identity, knowledge, parser, tools) call their archived area
    plan "binding, incl. its answered questions"; `AGENTS.md` says the archive does not bind. Fold what still holds
    into the design and drop the word.
  - "Ask-a-file workspaces" (P2) are owned by teams and individuals, "Organisation" by departments only (user call
    before P2).
  - repo-and-deployment §8.4 and §12.10 call SHA pins for actions "proposed", and postponed decision S1 is open
    ("CI/CD": postponed until before go-live, A-20, A-27); connectors §8.3 names `Render:MaxPerHost`, which the code
    does not have (it is the host's `renderPerHost`, capped by `Render:MaxConcurrent`).
- **Done when:** each point is fixed in its design or answered by the user, and `check-doc-refs.py` passes.
- **Waits on:** the decisions register (this PR).
- **Left for the user:** who owns "Ask-a-file workspaces" (teams and individuals) against "Organisation" (departments
  only); answer before P2.

### [x] M-12 Gateway integration (#580): queue the stream items when the gateway's phase PRs exist
- **Do:** when a gateway integration phase is merged in `~/git/llmgateway` (phase 1 is draft PR thekim1/llmgateway#81),
  pin its OpenAPI document in `contracts/gateway/`, check the designs against what was built (gateway-changes §5 is
  Ume.Rag's view, the gateway's `docs/integrations.md` wins), and queue that phase's items in `stream-a.md` (worker)
  and `stream-b.md` (api, admin, FakeGateway) with done-whens from cost-attribution §6:
  - **Phase 1** (B2–B4), **queued 2026-11-12** as B-35, B-36, B-37 and A-30 (designs checked against `6a8f0a5`: they match);
    **amended 2026-11-12** after the gateway owner's decisions on #580 (#84): B-35 takes the organisation from the
    context with `organisation.read` and drops the Data API key, and its contract test checks against the gateway's
    `docs/openapi/integrations.json`; B-36 handles `usable: false`; A-30 removes the Data API key: one credential key per service (`Gateway:Key`) in place of the per-level keys, and the
    residency handler's service maximum (`src/Ume.Rag.Infrastructure/Gateway/GatewayOptions.cs`,
    `ResidencyCeilingHandler.cs`, `GatewayApi.cs`; `deploy/init-deployment.sh`; `docs/production-setup.md`); the
    context and model sync with `ETag` and the `GatewayKeys` mirror, the manual model limit fields replaced by the
    gateway's (`src/Ume.Rag.AdminApi/OrganisationSync.cs`, `GatewayModelSync.cs`, `GatewayEndpoints.cs`;
    `src/Ume.Rag.Domain/Entities/`, a migration in `src/Ume.Rag.Infrastructure/Persistence/Migrations/`,
    `deploy/postgres/app-roles.sql`); `GatewayKeyId` on assistants and collections with the key picker and V39
    (`AssistantEndpoints.cs`, `CollectionEndpoints.cs`, `src/admin-ui/src/views/AssistantView.vue`,
    `CollectionView.vue`, `labels.ts`, `messages.ts`); the FakeGateway's `/v1/ume/context`, `ume` model fields and
    contract test (`src/Ume.Rag.FakeGateway/**`, `tests/Ume.Rag.ContractTests/**`).
  - **Phase 2** (B5–B6), **queued 2026-11-12** as B-42 (the shared client's acting and echo check on every api and
    admin call, the FakeGateway's acting, drop-echo, refusal and budget modes), B-43 (save checks as the key, publish
    needs a key), B-44 (a spent budget or an unusable key in chat and the admin), A-34 (the worker's calls as the
    knowledge base's key and its waiting jobs) and A-35 (deployment and runbook, with phase 3); the stream cost stays
    B-38's. Scope as listed: `x-ume-on-behalf-of` from the object's key with the echo check on every call
    (`src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.Answering/Chat/GatewayChatClient.cs`,
    `GatewayStreamLine.cs`, `src/Ume.Rag.Answering/GatewayQueryEmbedder.cs`, `src/Ume.Rag.Retrieval/GatewayReranker.cs`,
    `src/Ume.Rag.Api/ChatStreaming.cs`, `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs`,
    `src/Ume.Rag.Worker/Processing/Embedding/EmbeddingClient.cs`, `src/Ume.Rag.Worker/Evaluation/Generation/EvalModelClient.cs`);
    run headers stored; the stream cost read and never forwarded; `assistant_unavailable`, the worker's
    `gateway.key_unavailable` and the attention item; save checks as the key; publish needs a key; the FakeGateway's
    acting, echo, drop-echo and budget modes and the stream cost comment.
  - **Phase 3** (B7): the Integration API read-back (`usage.read` on `rag-admin`'s key, `Gateway:IntegrationApiUrl`):
    cost and budget status per assistant and knowledge base (`src/Ume.Rag.AdminApi/**`, the two views); the
    FakeGateway's `/integration/v1`. **Queued 2026-11-12** as B-45 (the read-back, `GET /api/gateway/keys/{id}/budget`
    and `/usage`, the FakeGateway's `/integration/v1`) and B-46 (the admin UI); A-35 deploys `Gateway:IntegrationApiUrl`.
  - **Designs checked against v0.10.0** (2026-11-12, from source at the tag): gateway-changes §5.2–§5.7, §7.1 (a revoked
    or expired acted key answers `401 key_revoked`/`key_expired`, not `403`; the echo is the id's `D` form, on every
    answer after acting, errors included, and is checked on `2xx`/`304`; the `402` names its scope only in the message;
    the stream comment comes before `data: [DONE]`, six decimals, exactly `x-ume-stream-cost: 1`; `/v1/models` as a key
    still needs the ceiling; the Integration API's parameters, defaults, fields, `401`/`403`/`404`/`429` and the budgets
    answer; event 1006's fields); identity §4.7 (each service reads `act-as-key` from its own context), §10 (5011);
    chat §3.6, §8.1–§8.3; knowledge §5.2; engine §7.3, §7.7; admin §4.5, §4.6 V39, §4.10, §5.5.1, §5.5.4, §8.6 (the
    gateway's usage per key beside the cost report); cost-attribution §4.2, §6 (queue ids); repo-and-deployment §4.3
    (the FakeGateway's modes).
- **Done when:** each merged phase has its stream items queued and the designs match what the gateway built.
- **Waits on:** nothing since **gateway v0.10.0** (released 2026-11-12: phases 1–3, the credential bound,
  `organisation.read`, member groups). Member groups now come from the context (B-35, A-33). The live checks of
  B-42–B-46, A-34 and A-35 wait on the interim gateway on v0.10.1.
- **Closed (2026-11-12):** the 401-vs-403 difference in the gateway's docs is reported on #580; "each service reads
  the features from its own context" is in Decisions "Gateway keys".
- **Read:** Decisions "Cost attribution", "Gateway keys", "Gateway releases"; gateway-changes §5, §13.4;
  cost-attribution §4, §6; identity §4.7; admin §4.6 V39, §4.10, §5.5.4; data-model `GatewayKeys`; chat §8;
  repo-and-deployment §4.3.

### [x] M-13 Malware scan optional (#535): the decision, the designs and the queue
- **Done (2026-11-12):** Decisions "Malware scan optional" (the user's knob of 2026-11-09; the `unscanned` state, the
  health text and no re-scan by the user, 2026-11-12); connectors §5.5, §9.1, §11, §16, §18; engine §2, §6; identity §10
  (5106); admin §4.5, §5.5.7; repo-and-deployment §3, §4.1, §5.5, §6.2, §7.1. Queued as B-47 (state, column, event,
  AppHost, admin) and A-36 (worker), A-37 (deployment and docs).
- **Left open on purpose:** an automatic re-scan of files stored while the scan was off. `Uploads.ScannedAt` null
  finds them when it is wanted.
