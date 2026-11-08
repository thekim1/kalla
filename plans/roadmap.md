# Roadmap

Where Ume.Rag stands and what comes next, on one page. The binding rules are in the Decisions table
([`../01-general-idea.md`](../01-general-idea.md) §10) and the design documents ([`README.md`](README.md)); the work
itself is in [`work/`](work/). Updated by the main session when a phase step lands.

## Done

| Phase | Result | Record |
|---|---|---|
| **P0 Foundation** | Repo, AppHost, schema with config outbox, Postgres image with pgBackRest, Keycloak, FakeGateway, parser contract, worker and admin skeletons, CI; released as `v0.1.0` (2026-10-31) | [`archive/p0/`](archive/p0/README.md) |
| **P1a Walking skeleton (code)** | Crawl, upload, parse, embed, hybrid retrieval with citations, chat API, one assistant in Open WebUI. **Demo 2026-11-01:** a 200-page slice of umea.se answered with citations in Open WebUI over the interim gateway | [`archive/p1/`](archive/p1/README.md) |
| **P1b Pilot hardening (code)** | Freshness, preview, publish and rollback, evaluation harness, admin journey pages (English), assistant versions, feedback import, organisation (sharing, topics, Map), general knowledge per assistant, browser tests (green on the runner, #377), lean CI, licence notices (#381), optional rerank per assistant (chat-model and gateway rerankers, measured on #403), the M-2 calls (#380), the shared-realm Keycloak files (#409), the hot-path tidy (#411) | [`archive/p1b/`](archive/p1b/README.md) |

## Now: finish P1b and go live

**P1b is done when** the go-live gate is met on hand-graded gold-set answers (correct ≥ 80 %, citation precision
≥ 90 %, false not-found ≤ 10 %, off-topic answered "not found" ≥ 80 %), the DPIA (G1) is finished with W2 inside it,
and one pilot department uses its assistant in Open WebUI.

Three tracks:

1. **Code leftovers: P1b code done** (M-3, 2026-11-08): every code item of both queues is ticked, the deviations are
   settled and the full CI set is green; released as `v0.2.0`. New code items still go to
   [`work/stream-a.md`](work/stream-a.md), [`work/stream-b.md`](work/stream-b.md), [`work/main.md`](work/main.md).
   **JavaScript rendering** (user, 2026-11-07; connectors design §8): some sites the platform will crawl need it, so
   the renderer is built now (stream A, A-9 to A-13: spike, image, worker, settings and UI, live check). It is not a
   go-live gate for the umea.se pilot, which is server-rendered.
2. **The live block**, run in one go once its inputs exist (order where the inputs allow):
   1. gateway and Keycloak for the pilot (gateway session: release 1, EU routes, pilot keys, shared realm);
   2. U1 with the web editors, then the umea.se crawl in full and the `lastmod` sample's first pass;
   3. the gold set written with DoIT, entered, run three ways; not-found thresholds set from it;
   4. test host: deploy, restore drill, R1 performance test, a week of nightly evaluation;
   5. checks on the crawled collection: `lastmod` result, freshness and HNSW recall, personal-data dry run;
   6. gateway release 1 switch, live feedback import, banner and public-records route (after G1);
   7. the go-live gate graded by department reviewers, then the pilot goes live.
3. **Decisions and inputs from people** (below).

**Cost attribution, stage 1** (Decisions "Cost attribution", 2026-11-08; not a go-live gate): Ume.Rag meters tokens and
cost of every model call per assistant, knowledge base, step and client and shows them in the admin UI
([`platform/cost-attribution.md`](platform/cost-attribution.md) §5, steps C1–C6). It needs no gateway change; stage 2, the
gateway's delegated attribution, follows in P2 before a second department.

## Waiting on people

| What | Who | Blocks |
|---|---|---|
| Gold set (100–150 answerable, 30–50 unanswerable, ≥ 10 temporal) and the reviewers who grade | user + DoIT | thresholds, go-live gate |
| G1 DPIA with the data protection officer (EU processor agreement, W2, banner, public-records route) | user | banner/route, go-live |
| Gateway: v0.9.1 is deployed and release 1 is verified live (B-L4, #441: status green, `/v1/models` per ceiling; the Cohere reranker on Foundry, #421). Left: pilot accounts and the interim key allowing the model `turkos` is published on (`claude-opus-5-5` is refused today). Admin OTP is off for the pilot (user, 2026-11-06), which G1 records | user (gateway repo) | pilot accounts, `turkos` in Open WebUI |
| Test host, backup target, key-escrow location; where the pilot runs at go-live (O7) | user | deploy, restore drill, R1 |
| Self-hosted runner on the test host (R1), or the manual stopgap | IT security | R1 test, nightly evaluation in CI |
| U1 (crawler about page) with the web editors | user | full umea.se crawl |
| N1 product name; W1 relation to `chat.umea.se`; owners and dates for G1, W2, N1, U1, R1, A1 | user, product owner | go-live |
| CI runners (proposal, 2026-11-08; nothing changed yet): the runners `ume-rag` and `ume-rag2` share one machine and one Docker daemon, so jobs collide (e2e beside `verify` failed, run 42469437132; a dry run's leftover registry). **A:** one runner per Docker host: remove `ume-rag2` (jobs queue, nothing collides, no workflow change), or give it its own machine or VM. **B:** `release.yml` on GitHub-hosted runners (clean host with sudo and .NET 8; paid minutes, private repo). Either way: a release dry run is part of the full CI set before every release, so host drift shows early (the v0.2.0 dry runs found three: no .NET 8, no sudo, no registry clean-up) | user | nothing; once decided, record it in the Decisions table and drop the collision notes (`docs/ci-runner.md`, `release.yml`) |
| Review #513: #511 splitting the worker into libraries (stream b: only if it keeps growing in P2); #502 is answered and measured (to-verify #117, 2026-11-08: delta 0.0 points, so `halfvec` storage in P2); the rest is queued (A-20–A-28, B-25–B-29, M-10) | user | nothing in P1b |
| Product calls: #333 findings 1–2; [`../architecture_plans.md`](../architecture_plans.md) items 1, 2, 4–7; the W3 grant step; a Git/Markdown connector | user | nothing in P1b |

Every postponed decision with its stopgap and deadline: [`postponed-decisions.md`](postponed-decisions.md).

## Next: P2 Broaden

Several departments, internal sources, tools, and the first multi-host deployment. Themes, not a plan yet; the P2
work split is written when P1b is live.

- **More departments:** gateway release 2 (delegation, member groups from claims) before a second department or a
  group audience; **cost attribution** per department, team and assistant through that delegation, the long-term goal
  of [`platform/cost-attribution.md`](platform/cost-attribution.md) (stage 2); Entra ID with RAG's own registrations; OBO and the E1 spike; one Open WebUI per department until W3
  and E1 are solved; owner-team edit scoping.
- **Assistants:** version diff, per-assistant test-question sets, templates and presets (architecture plan item 3),
  copy; unused-assistant attention items; the drawn Map.
- **Ingestion:** site-profile learning and re-learning, a crawl frontier shared between sources, SharePoint, the
  renderer's replay check and path rules (after measurement), legacy Office/RTF/e-mail/archives (Z1), parse-profile
  proposals, adaptive embedding concurrency; for external single-page apps a per-page burst of the page's own
  subrequests and a renderer asset cache kept across runs (A-13, user 2026-11-07).
- **Storage:** embeddings stored as `halfvec` (#502; to-verify #117 found no recall cost), after repeating the check
  on the full crawl's evaluation set.
- **Answer quality:** nightly answer judge, publish plans from effect tags, rerank (a separate gateway release with an
  on-prem reranker), on-prem models when hardware exists (M2).
- **Tools and skills:** skills with know-how and tools, the in-process tool hub (`hosted` servers, read-only tools),
  `/mcp` with `search_knowledge`, the mock municipal server `ume-mcp-kommun`.
- **Chat:** stored chat content when A1 allows it (history modes, content encryption, conversation review), chat
  attachments, a public widget.
- **Operations:** multi-host topology (leader leases, Redis for shared limits and caches, batched dequeue, a/b slots),
  Postgres standby (D1) before production beyond the pilot, object store (S2), flake hunt, scale and fuzz workflows.

## Later: P3 Scale and refine

Driven by measurements: a Swedish retrieval benchmark (B1), OpenSearch if a trigger fires (O1), a separate
`rag-tools` service, municipal and external tool hosting, per-skill MCP endpoints, Teams client, Entra OBO for
user-delegated tools, write tools with confirmation, a visual pipeline builder if its trigger fires (C2).

## Reference that still binds

- The capacity model, scaling triggers and scale tests of the
  [performance pass](archive/passes/performance-pass.md): §2 (capacity model), the sizing tables of §3.1–§3.3 and
  §3.9, §6 (scaling triggers) and §7 (load and scale tests), as its header lists.
- The ledgers of the archived passes say where each of their rules went; the rules themselves now live in the design
  documents.
