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

### [ ] A-5 Embedding batch cap goes after gateway release 1 (#390)
- **Do:** remove the worker's embedding batch cap below the context window once the gateway checks embeddings per
  input (gateway #65 in release 1); embedding deployments may then set `ContextWindow`.
- **Paths:** `src/Ume.Rag.Worker/Processing/**` (embed stage), worker settings.
- **Done when:** the cap setting is gone; an embed-stage test sends a full batch whose longest input fits the window.
- **Waits on:** gateway release 1 tagged and deployed (B-L4).
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built".

### [ ] A-6 NER loaded on first use, unloaded when idle (resource review)
- **Do:** the parser's `ner-0` process starts on the first `/v1/ner` request (not at container start) and stops after
  `NER_IDLE_STOP_S` (proposed 900) without requests. While it warms, `/v1/ner` answers `not_ready` as today, so the
  worker's existing NER wait (processing §9.6) covers the gap; `/v1/info` reports `ner_ready` honestly.
- **Paths:** `src/parser/**`.
- **Done when:** a parser test shows no NER process at idle start, one after a request, none after the idle time; the
  image test passes; idle RSS measured before/after (about 1 GB less expected, resource review 2026-11-06).
- **Waits on:** –
- **Read:** parser §7.8, §10.1, §10.2; processing §9.6.

### [ ] A-7 Convert slot goes cold between batches (resource review)
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

### [ ] A-4 Go-live pin review, A's part (last code item)
- **Do:** review every image, PyPI and Compose pin younger than 7 days; bump the Open WebUI pin once, with the
  callback change in Compose matching B's realm change (B-6).
- **Paths:** `images/**`, `src/parser/**`, `deploy/**`.
- **Done when:** no pin under 7 days old without a recorded reason; #113 recorded.
- **Waits on:** every other code item of both streams.
- **Read:** Decisions "7-day rule in development"; identity §5.3.

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
