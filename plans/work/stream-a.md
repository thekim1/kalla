# Stream A: work queue

Stream A owns the worker, parser, connectors, pipelines, evaluation runs, Compose, CI and scripts
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `a/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-a.md`](results-a.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

Finished items move to [`../archive/work/`](../archive/work/) at each release (the main session); find one by its ID.

## Code

### [x] A-42 One path-rule matcher in `Kalla.Pipelines`, for the Topics tab (Decisions "Topics editor") (#948)
- **Do:** move the reading and matching of `pathRules` (`PathRule`, `ReadRules`, the `path` glob with its pasted-address,
  host-first and trailing-`/` forms, `path-regex`, `host`, `title-regex`, `mediaType`, the tag format) from
  `Kalla.Worker/Processing/Chunking/Metadata.cs` into `Kalla.Pipelines` (processing §7 *One matcher*), as a public
  matcher that takes a rule and a document's URL or display name, title and media type and answers whether it matches
  (and with which tags, `documentType` and date groups). The worker's `Metadata.Apply` keeps its behaviour and calls
  it. No behaviour change: the existing worker tests pass unchanged; the matching tests move to
  `Kalla.Pipelines.Tests`.
- **Paths:** `src/Kalla.Pipelines/**`, `src/Kalla.Worker/**`, `tests/Kalla.Pipelines.Tests/**`,
  `tests/Kalla.Worker.Tests/**`.
- **Done when:** `Kalla.Worker.Tests` (`StageRuleTests`, `UploadEngineTests`, `ChunkDateEngineTests`, `PreviewTests`,
  `GoldenTests`) and `Kalla.Pipelines.Tests` green; the golden chunk output unchanged.
- **Waits on:** nothing. B-64 and B-65 use it.
- **Read:** processing §7; admin §4.5 (`topic-suggestions`, `topic-rules/preview`); Decisions "Topics editor".

### [x] A-43 One schedule rule in `Kalla.Pipelines`, for the worker and the admin's next run (#949)
- **Do:** move the rule that decides when a source is next fetched into `Kalla.Pipelines` (with the Cronos reference):
  the schedule in force (the source's own, else `Settings.DefaultSchedules` when it names at least one type,
  else the built-in defaults: `url-list` 02:00 nightly, `crawl` Saturday 01:00; never for uploads), the next occurrence
  after the last `ScheduledFor` (else `CreatedAt`) in Europe/Stockholm with missed occurrences collapsed into one, and
  the jitter (`SHA256(sourceId)` first 8 bytes mod `Settings.ScheduleJitterMinutes`). The worker's `SyncScheduler.Due`
  and `Jitter` and `EngineSettings`' defaults call it. No behaviour change: the existing worker tests pass unchanged;
  the rule's tests move to `Kalla.Pipelines.Tests`. Today `Kalla.AdminApi/CronSchedules.cs` (#935) repeats the rule;
  B-71 switches it over.
- **Paths:** `src/Kalla.Pipelines/**`, `src/Kalla.Worker/**`, `tests/Kalla.Pipelines.Tests/**`,
  `tests/Kalla.Worker.Tests/**`.
- **Done when:** `Kalla.Worker.Tests` (the scheduler tests) and `Kalla.Pipelines.Tests` green, with the jitter pinned
  to the value #935's `CronSchedulesTests` pins (GUID `0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b`, 90 minutes: 8).
- **Waits on:** #935. B-71 uses it.
- **Read:** engine §9.6; #935's review comment.

## Live (the live block, see the roadmap)

### [ ] A-L1 umea.se inventory and the `lastmod` sample's first pass
- **Do:** run the sitemap inventory (formats, legacy share for Z1, JavaScript need); fetch the 500-URL `lastmod`
  sample and note the re-fetch date (≥ 7 days later).
- **Done when:** inventory recorded; renderer confirmed not needed (or reported); first pass stored (#170).
- **Waits on:** U1 with the web editors.
- **Read:** connectors §5.4.

### [ ] A-L2 Parser on real PDFs
- **Do:** OCR acceptance; text layer vs Docling on 200 umea.se PDFs; parser latency.
- **Done when:** #131, #132, #80, #62, #65, #150 recorded, and real municipal legacy Office files converted under
  the 3 GiB limit (the live part of closed row #71).
- **Waits on:** A-L1.
- **Read:** parser §15, §16.

### [ ] A-L3 EU-route capacity and initial-load cost
- **Do:** `scripts/capacity/` against the EU embedding and chat routes.
- **Done when:** numbers recorded, with the TTFT p99 of the slowest pilot answer model for a full 16k-token prompt
  (#149); the user told the expected initial-load time and cost for umea.se.
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
- **Done when:** health green through the proxy; RPO ≤ 5 min and RTO ≤ 4 h achieved and documented (#90, #129,
  #145, #161); Chromium's sandbox under the host's Docker Engine (#29).
- **Waits on:** test host, backup target, key-escrow location (user).
- **Read:** repo-and-deployment §7.4.

### [ ] A-L6 R1 performance test on the test host
- **Do:** `perf.yml` (or the same harness by script); set the pilot CPU caps from the result. Also record the p95 of
  `rag.admin.config_lock.wait` (to-verify #23, after B-13) and whether the chat conversation writer keeps up; only if it
  falls behind does stream B batch its statements into one round trip (#392, to-verify #20).
- **Done when:** chat p95 TTFT in phase 2 ≤ 1.10 × phase 1, no errors; caps in `compose.host-24g.yaml` (#6, #21, #100,
  #101, #102, #104, #106, #140, #141, #162, #181).
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
- **Done when:** flags reviewed with the platform team; `PiiOwnDomains` adjusted if needed (#179).
- **Waits on:** A-L4.
- **Read:** processing §9.

### [ ] A-L10 Banner and public-records route
- **Done when:** the runbook names one route, tried once on the pilot Open WebUI (#172, #173).
- **Waits on:** G1 finished.
- **Read:** chat §13.3; `docs/runbook.md`.

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
- **Paths:** `src/Kalla.Worker/**` (not `Processing/Pii/**`, `Engine/HnswIndexHealth.cs`),
  `tests/Kalla.Worker.Tests/**`.
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

### [ ] A-35 (#643, code merged; the live check is open) Gateway phases 2 and 3 in the deployment and the runbook (#580, cost-attribution B5–B7, deploy and docs part)
- **Do:** `Gateway__IntegrationApiUrl` for kalla-admin (`KALLA_GATEWAY_INTEGRATION_API_URL` in `deploy/.env`; not a
  secret; unset: no read-back) in the Compose, Kubernetes and Portainer templates; `production-setup.md`: a gateway
  admin adds `usage.read` to kalla-admin's credential key, and kalla-admin reaches the gateway's Admin API at
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

