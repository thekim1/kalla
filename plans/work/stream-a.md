# Stream A: work queue

Stream A owns the worker, parser, connectors, pipelines, evaluation runs, Compose, CI and scripts
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `a/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-a.md`](results-a.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

Finished items move to [`../archive/work/`](../archive/work/) at each release (the main session); find one by its ID.

## Code

### [x] A-20 Pin GitHub Actions by commit SHA (#506)
- **Do:** every `uses:` in `.github/workflows/*.yml` pinned to a full commit SHA with the version in a comment, checked
  against the action's release; optionally `zizmor` in `audit.yml`.
- **Paths:** `.github/**`.
- **Done when:** no workflow references an action by tag; `verify.yml` green.
- **Waits on:** postponed by the user (2026-11-08) until before go-live, with the 7-day package rule's return; A-27
  follows it.
- **Read:** #506; repo-and-deployment §8.

### [x] A-27 Image CVE scan and automated pin bumps (#507)
- **Do:** a scheduled scan of the built images in `audit.yml` (fixable High/Critical fail it), and Renovate or Dependabot
  for image digests, NuGet, npm, uv and Actions with a minimum release age of 7 days (the 7-day rule).
- **Paths:** `.github/**`, `images/**`.
- **Done when:** the first scan report and the first bump PR exist.
- **Waits on:** A-20.
- **Read:** #507; AGENTS.md "7-day package rule".

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

### [ ] A-35 (#643, code merged; the live check is open) Gateway phases 2 and 3 in the deployment and the runbook (#580, cost-attribution B5–B7, deploy and docs part)
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

