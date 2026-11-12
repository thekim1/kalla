# Stream A: work queue

Stream A owns the worker, parser, connectors, pipelines, evaluation runs, Compose, CI and scripts
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `a/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-a.md`](results-a.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

## Code

### [x] A-1 Licence notices before distribution (#51; #381)
- **Do:** generate `src/parser/LICENSES.md` (docling and its parts, Tesseract, models) and ship it in the parser
  image; add a repository `NOTICE` naming EUPL-1.2; put the model licence texts where the image build copies them.
- **Paths:** `src/parser/**`, `images/**`, `NOTICE` (new, root).
- **Done when:** the parser image contains `LICENSES.md` (checked in its image test); `NOTICE` exists; #51 closed.
- **Waits on:** –
- **Read:** [`archive/licence-review-p2.md`](../archive/licence-review-p2.md); parser §15.

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

### [ ] A-20 Pin GitHub Actions by commit SHA (#506)
- **Do:** every `uses:` in `.github/workflows/*.yml` pinned to a full commit SHA with the version in a comment, checked
  against the action's release; optionally `zizmor` in `audit.yml`.
- **Paths:** `.github/**`.
- **Done when:** no workflow references an action by tag; `verify.yml` green.
- **Waits on:** postponed by the user (2026-11-08) until before go-live, with the 7-day package rule's return; A-27
  follows it.
- **Read:** #506; repo-and-deployment §8.

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

### [ ] A-27 Image CVE scan and automated pin bumps (#507)
- **Do:** a scheduled scan of the built images in `audit.yml` (fixable High/Critical fail it), and Renovate or Dependabot
  for image digests, NuGet, npm, uv and Actions with a minimum release age of 7 days (the 7-day rule).
- **Paths:** `.github/**`, `images/**`.
- **Done when:** the first scan report and the first bump PR exist.
- **Waits on:** A-20.
- **Read:** #507; AGENTS.md "7-day package rule".

### [x] A-28 Schema contract test for positional SQL readers (#510, harness)
- **Do:** a test that runs each SQL constant with `CommandBehavior.SchemaOnly` against the test database and checks the
  column names and types its reader expects; the worker's statements first. Stream b adds its stores' statements after
  it (a follow-up item when this lands).
- **Paths:** `tests/Ume.Rag.ContractTests/**` or `tests/Ume.Rag.Worker.Tests/**`, `src/Ume.Rag.Worker/**`.
- **Done when:** a renamed column in a worker statement fails the test.
- **Waits on:** –
- **Read:** #510.

## Live (the live block, see the roadmap)

### [ ] A-L1 umea.se inventory and the `lastmod` sample's first pass
- **Do:** run the sitemap inventory (formats, legacy share for Z1, JavaScript need); fetch the 500-URL `lastmod`
  sample and note the re-fetch date (≥ 7 days later).
- **Done when:** inventory recorded; renderer confirmed not needed (or reported); first pass stored (#139, #170).
- **Waits on:** U1 with the web editors.
- **Read:** connectors §5.4.

### [ ] A-L2 Parser on real PDFs
- **Do:** OCR acceptance; text layer vs Docling on 200 umea.se PDFs; parser latency.
- **Done when:** #131, #132, #79, #80, #110 recorded.
- **Waits on:** A-L1.
- **Read:** parser §15, §16.

### [ ] A-L3 EU-route capacity and initial-load cost
- **Do:** `scripts/capacity/` against the EU embedding and chat routes.
- **Done when:** numbers recorded; the user told the expected initial-load time and cost for umea.se.
- **Waits on:** the gateway session (EU routes, pilot keys).
- **Read:** performance pass §3.9.

### [ ] A-L4 The pilot crawl in full
- **Do:** with the platform team: the pilot collection, the umea.se crawl source with the manual profile, the pilot
  URL list, one upload; a full traversal with EU embeddings; the Compose stack's first run.
- **Done when:** crawl report reviewed (traversal complete, failures understood); #94, #92 recorded.
- **Waits on:** A-L1, A-L3; the user's go-ahead (no full crawl until then).
- **Read:** connectors §5.5; admin §12.

### [ ] A-L5 Test host: deploy and restore drill
- **Do:** deploy the current release behind the municipal proxy; point-in-time restore drill at a realistic size;
  read escrowed keys back.
- **Done when:** health green through the proxy; RPO ≤ 5 min and RTO ≤ 4 h achieved and documented (#90, #123,
  #129).
- **Waits on:** test host, backup target, key-escrow location (user).
- **Read:** repo-and-deployment §7.4.

### [ ] A-L6 R1 performance test on the test host
- **Do:** `perf.yml` (or the same harness by script); set the pilot CPU caps from the result. Also record the p95 of
  `rag.admin.config_lock.wait` (to-verify #23, after B-13) and whether the chat conversation writer keeps up; only if it
  falls behind does stream B batch its statements into one round trip (#392, to-verify #20).
- **Done when:** chat p95 TTFT in phase 2 ≤ 1.10 × phase 1, no errors; caps in `compose.host-24g.yaml` (#100, #101,
  #106).
- **Waits on:** A-L5; the self-hosted runner or the accepted stopgap.
- **Read:** repo-and-deployment §6.2, §8.3.

### [ ] A-L7 A week of nightly evaluation
- **Done when:** a week of nightly runs on the pilot collection recorded, with the run-to-run variance (#44).
- **Waits on:** A-L4, A-L5.
- **Read:** processing §14.8.

### [ ] A-L8 `lastmod` sample result
- **Done when:** re-fetched ≥ 7 days after A-L1; `lastmodReliable` set (incremental runs on) or full traversals kept
  (#170).
- **Waits on:** A-L1 + 7 days.

### [ ] A-L9 Personal-data dry run on the full crawl
- **Done when:** flags reviewed with the platform team; `PiiOwnDomains` adjusted if needed.
- **Waits on:** A-L4.
- **Read:** processing §9.

### [ ] A-L10 Banner and public-records route
- **Done when:** the runbook names one route, tried once on the pilot Open WebUI (#172, #173).
- **Waits on:** G1 finished.
- **Read:** chat §13.3; `docs/runbook.md`.

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

### [ ] A-32 Runbook: reading Ume.Rag's cost beside the gateway's (cost-attribution C6)
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

### [ ] A-34 (#621, code merged; the live check is open) Gateway phase 2 in the worker: every job's calls as the knowledge base's key, the echo, a spent budget or an unusable key waits (#580, cost-attribution B5, B6, worker part)
- **Do:** the worker registers B-42's context reader and handler with its own credential key (identity §4.7), so it
  acts only while its context shows `act-as-key`; every call of a job (`EmbeddingClient` for indexing, re-embedding and
  preview, `EvalModelClient` for `eval-generate` and `eval-judge`, the evaluation's dedup and query embeddings and its
  rerank) carries the document's or evaluation's collection's `GatewayKeyId` (engine §7.7); a collection without a key
  runs as the credential key. Engine §7.3 on B-42's typed refusal: `402 budget_exceeded` → attempt counted,
  `AvailableAt` the next full hour, after `MaxAttempts` `failed` with `gateway.budget_exhausted`; `403
  delegation_not_allowed`, `403 key_disabled`, `403 key_revoked`/`key_expired` the same with `gateway.key_unavailable`;
  a missing or different echo fatal `gateway.delegation_not_applied` with 5011 and nothing stored; the key's `429` as
  any `429`.
- **Paths:** `src/Ume.Rag.Worker/**` (not `Processing/Pii/**`, `Engine/HnswIndexHealth.cs`),
  `tests/Ume.Rag.Worker.Tests/**`.
- **Done when:** against the FakeGateway with `act-as-key`: a sync of a collection with a key sends every embedding
  call as that key; a collection without a key sends none; with `BudgetExceeded: Key` the job's `AvailableAt` is the
  next full hour and the run item ends `failed` `gateway.budget_exhausted` after `MaxAttempts`; a disabled key gives
  `gateway.key_unavailable`; with `DropOnBehalfOfEcho` the job fails `gateway.delegation_not_applied`, 5011 is logged
  and no vector is stored; a set generation's calls carry the collection's key.
- **Live check:** on the interim gateway on v0.10.1 (not deployed yet): an ingestion run of a knowledge base with an
  assigned key shows under that key in the gateway's usage report, with the worker's credential key as the via key.
- **Waits on:** B-42 (the shared reader and handler, the typed refusal, the FakeGateway's modes), B-36
  (`Collections.GatewayKeyId`), A-31 (the same clients: its cost reading goes first).
- **Read:** engine §7.3, §7.7; identity §4.7, §10; gateway-changes §5.2, §5.7; cost-attribution §4.1, §4.2.

### [ ] A-35 Gateway phases 2 and 3 in the deployment and the runbook (#580, cost-attribution B5–B7, deploy and docs part)
- **Do:** `Gateway__IntegrationApiUrl` for rag-admin (`UME_RAG_GATEWAY_INTEGRATION_API_URL` in `deploy/.env`; not a
  secret; unset: no read-back) in the Compose, Kubernetes and Portainer templates; `production-setup.md`: a gateway
  admin adds `usage.read` to rag-admin's credential key, and rag-admin reaches the gateway's Admin API at
  `/integration/v1`; `docs/runbook.md`: event 5011 (an older gateway instance during a rolling update; fail closed by
  `Gateway:RequireDelegationEcho`, which stays `true`), an assistant shown unavailable (the key disabled, revoked or
  expired, or a budget used up: fixed by the department in the gateway), worker jobs waiting with
  `gateway.budget_exhausted` or `gateway.key_unavailable`, and the read-back's reasons on the *Gateway* page (scope
  missing, rate limit).
- **Paths:** `deploy/**` (not `deploy/postgres/**`, `deploy/keycloak/**`, `deploy/kubernetes/base/app-roles.sql`),
  `docs/**` (not `docs/security-events.md`, `docs/accessibility-statement.md`).
- **Done when:** `deploy-checks` pass; with the variable set the admin container gets `Gateway__IntegrationApiUrl`,
  unset it gets none; the runbook names each refusal reason and its fix.
- **Live check:** on the test host with the gateway on v0.10.1: the admin's budget read-back answers for a real key.
- **Waits on:** B-45 (the setting in code), A-30 (one credential key per service in the deployment).
- **Read:** gateway-changes §5.6, §5.7; admin §8.6; identity §4.7, §10; engine §7.3; repo-and-deployment §6.3, §7.1.

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
