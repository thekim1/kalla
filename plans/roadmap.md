# Roadmap

Where Ume.Rag stands and what comes next: **the one place for the plan ahead.** The binding rules are in
[`decisions.md`](decisions.md) and the design documents ([`README.md`](README.md)); the work items are in
[`work/`](work/), and `HANDOVER.md` links here instead of restating it. The main session updates this page when a step
lands or an input arrives.

## Done

| Release | Result |
|---|---|
| `v0.1.0` (2026-10-31) | **P0 foundation:** repo, AppHost, schema, Postgres image with pgBackRest, Keycloak, FakeGateway, parser contract, CI; deploys from a digest-pinned bundle, restore drilled locally ([`archive/p0/`](archive/p0/README.md)) |
| `v0.2.0` (2026-11-08) | **P1a + P1b code:** crawl, upload, parse, embed, hybrid retrieval with citations, chat API behind Open WebUI, admin UI for the pilot journey, evaluation, publish and rollback, JavaScript rendering, optional rerank. Demo 2026-11-01 on a 200-page umea.se slice ([`archive/p1/`](archive/p1/README.md), [`archive/p1b/`](archive/p1b/README.md)) |
| `v0.3.0` (2026-11-10) | Deployment options matching the gateway's, upload knowledge bases, legacy Office formats, the #499–#513 review fixes |
| `v0.4.0` (2026-11-11) | Answer length from the model, editable embedding profiles, prompt texts as settings, keyword statistics in the worker, lower idle cost |
| `v0.5.0` (2026-11-13) | **Gateway integration phases 1–3** (#580): one credential key per service, a gateway key per assistant and knowledge base that every call runs as, organisation and models from the gateway's context (no Data API), **cost attribution** stages 1 and 2 (built; stage 2's live done-when waits on step 0 below) with the gateway's cost and budgets beside Ume.Rag's, refusals in chat and admin, content retention, malware scan optional |

Every code item of both stream queues is done. What is left before go-live is live work, and every step of it waits on
an input from people.

## Now: the test chatbot (before the umea.se pilot)

Decided by the user, 2026-11-13 (Decisions "Test chatbot before the umea.se pilot"): a test chatbot on **DoIT's support
documents**, uploaded, for a **small test group** in Open WebUI, on `v0.5.0` and the gateway, **both deployed by hand by
the user**. It shows how well uploaded documents answer real questions before the umea.se pilot. Case plan:
[`cases/support-assistant/`](cases/support-assistant/README.md) (`07-rollout.md` §0b and §1).

| # | Step | Input it waits on | Owner | Status |
|---|---|---|---|---|
| T0 | Gateway on v0.10.1 with the integration *Ume.Rag* and three credential keys (`rag-api`; `rag-admin` with `organisation.read`, `usage.read`; `rag-worker` capped at `OnPrem`+`Eu`), the EU chat and embedding models, DoIT's department and team, and an assigned key for the assistant and the knowledge base (`docs/production-setup.md` *Gateway credential keys*) | the gateway deploy | user | open |
| T1 | Ume.Rag `v0.5.0` deployed from the release bundle (`docs/production-setup.md`; `init-deployment.sh`, Keycloak realm, Open WebUI, ClamAV on), checked with the production-setup's read-backs | T0 | user | **deployed** (user, 2026-11-13: "0.5.0 deployed fine", with the Portainer stack and the `openwebui` and `clamav` profiles); read-backs to confirm |
| T2 | The test group's accounts (Keycloak or the gateway's sign-in) and their Open WebUI access; DoIT's admin in the admin UI | T1 | user | open |
| T3 | DoIT's documents: the source inventory, then an upload knowledge base `support` (`internal`) with preview, first sync, run reports and the personal-data report (`02-knowledge-base.md` §5) | DoIT's files | user + DoIT; main session helps | open |
| T4 | Assistant `support`: the instructions of `04-assistant.md` §3, the knowledge base's key (V39), general knowledge `fallback`, a not-found text with the real escalation links; checked in the test panel with about twenty anonymised or invented tickets | T3 | main session + DoIT | open |
| T5 | The test group uses it in Open WebUI; feedback import on | T2, T4 | test group | open |
| T6 | Findings back: answer quality per ticket, misses and their documents, upload gaps (U1–U7) seen on real files, the condense model, the live checks of the gateway integration (B-42–B-46, A-34, A-35) | T5 | main session | open |

**Open question (user, with the data protection officer):** whether the test group's use needs the DPIA (G1), or part of
it, before T5: the documents are internal, Open WebUI keeps its own copy of each chat (W2), and real tickets wait for G1
in the case plan.

## Then: the umea.se pilot

**Go-live** (P1b done): the gate is met on hand-graded gold-set answers (correct ≥ 80 %, citation precision ≥ 90 %,
false not-found ≤ 10 %, off-topic answered "not found" ≥ 80 %), the DPIA (G1) is finished with W2 inside it, and one
pilot department uses its assistant in Open WebUI. The steps, in order; a step starts when its input exists.

| # | Step | Input it waits on | Owner | Status |
|---|---|---|---|---|
| 0 | The gateway and Ume.Rag as deployed for the test chatbot (T0, T1), or the test host's own | T0–T1 | user | after the test chatbot |
| 1 | Gateway and Keycloak for the pilot: pilot accounts, the key that allows the model `turkos` is published on | step 0 | user | open |
| 2 | U1 with the web editors, then the umea.se crawl in full and the `lastmod` sample's first pass (A-L1, A-L4) | U1 page URL, owner, contact | user + web editors | open |
| 3 | Gold set written with DoIT, entered, run three ways (`scripts/grading/`); not-found thresholds set from it | the gold set and its reviewers | user + DoIT | open |
| 4 | Test host: deploy from the release bundle, restore drill, R1 performance test, a week of nightly evaluation (A-L5–A-L7) | test host, backup target, key-escrow location, runner (R1) | user, IT security | open |
| 5 | Checks on the crawled collection: `lastmod` result, freshness and HNSW recall, personal-data dry run (A-L8, A-L9) | steps 2, 4 | stream a | open |
| 6 | Live feedback import, banner and public-records route | G1 | user + data protection officer | open |
| 7 | Before go-live: the 7-day package rule applies again; GitHub Actions pinned by SHA with automated bumps (A-20, A-27); LibreOffice placement with the parser sandbox question (Decisions "Legacy Office and RTF uploads") | – | streams, main | postponed on purpose (2026-11-08) |
| 8 | The gate graded by department reviewers; the pilot goes live (M-4) | steps 1–7 | user + main | open |

**Owners and dates** (umea.se pilot). The phase-start rule (`postponed-decisions.md`) asks for a named person and a date on every
input that blocks a phase. Still unnamed: A1, G1, N1, U1, W2, R1 and the test host. Until the user names them, the
table says "user".

## Meanwhile: work that needs no input

Each becomes a queue item when the main session picks it up.
- **Test chatbot support:** a checklist for T1's manual deploy from the production setup's read-backs, and the
  knowledge base and assistant settings of T3–T4 ready to enter (`work/main.md` M-15).
- **Grading dry run:** `scripts/grading/` end to end on a small synthetic set, so step 3 starts on working tools.
- **Upload gaps** ([`proposals/upload-quality-review.md`](proposals/upload-quality-review.md), U1–U7): measure on public
  documents what the gaps cost before deciding them.
- **CI:** one `WebApplicationFactory` per class in `Ume.Rag.AdminApi.Tests` (still `backend`'s long pole, #580 note to
  stream b); re-measure queue waits with the four extra test databases before deciding the runner layout.
- **Plan files:** the restructuring of session 33 (`work/main.md` M-14).
- **Deploy configuration** (decided 2026-11-13, `work/main.md` M-17, [`proposals/deploy-configuration.md`](proposals/deploy-configuration.md)): a deploy that needs
  only what a process can't start without; C0–C6 need no input, C7/C11/C12 wait on the gateway's #87, #90, #89.
- **Connecting an app, the platform menu and the Settings page:** one guided flow without GUIDs, every platform page
  in the side menu, Settings as a page of sections (`work/main.md` M-16; admin §5.9.1, §5.10).

## Waiting on people

The inputs of the critical path are in its table. The other open calls:

| What | Who | Blocks |
|---|---|---|
| N1 product name; W1 relation to `chat.umea.se` | user, product owner | go-live |
| CI runners: the runners `ume-rag` and `ume-rag2` share one Docker daemon. **A:** one runner per Docker host; **B:** `release.yml` on GitHub-hosted runners. Re-measure after the #648 change first | user | nothing; record the choice in Decisions "CI on the self-hosted runner" |
| Upload gaps U1–U7: suggested picture descriptions (U1, needs a decision entry) and rerank on (U4) first | user | nothing in P1b |
| Ask-a-file workspaces (P2): owned by teams and individuals, against "Organisation" (`work/main.md` M-11) | user | P2 |
| Product calls: #333 finding 1 (`exactScanMaxChunks`, after the recall check on the test host); [`proposals/architecture-plans.md`](proposals/architecture-plans.md) items 1, 2, 4–7; the W3 grant step; a Git/Markdown connector | user | nothing in P1b |
| #511 splitting the worker into libraries | stream b | only if it keeps growing in P2 |

Every postponed decision with its stopgap and deadline: [`postponed-decisions.md`](postponed-decisions.md).

## Next: P2 Broaden

Several departments, internal sources, tools, and the first multi-host deployment. Themes, not a plan yet; the P2
work split is written when P1b is live.

- **More departments:** gateway release 2 (delegation, member groups from claims) before a second department or a
  group audience; **cost attribution** stage 2's done-when (built in `v0.5.0`, steps B1–B7: two departments' objects as their own keys in
  the gateway's usage report, a key budget stopping its assistant, the per-level keys revoked;
  [`platform/cost-attribution.md`](platform/cost-attribution.md) §6); Entra ID with RAG's own registrations; OBO and the E1 spike; one Open WebUI per department until W3
  and E1 are solved; owner-team edit scoping.
- **Assistants:** version diff, per-assistant test-question sets, templates and presets (architecture plan item 3),
  copy; unused-assistant attention items; the drawn Map.
- **Ingestion:** site-profile learning and re-learning, a crawl frontier shared between sources, SharePoint, the
  renderer's replay check and path rules (after measurement), legacy Office/RTF/e-mail/archives (Z1), parse-profile
  proposals, adaptive embedding concurrency; for external single-page apps a per-page burst of the page's own
  subrequests and a renderer asset cache kept across runs (A-13, user 2026-11-07).
- **Storage:** embeddings stored as `halfvec` (#502; to-verify #117 found no recall cost), after repeating the check
  on the full crawl's evaluation set.
- **Answer quality:** nightly answer judge, publish plans from effect tags, an on-prem reranker behind the gateway's
  `/v1/rerank` (rerank itself is built and optional, pulled forward from P2: Decisions "Rerank pulled forward"), on-prem
  models when hardware exists (M2). Upload gaps U1–U7 (picture descriptions, e-mail,
  spreadsheet questions, rerank on, contextual chunk headers, near-duplicates, OCR languages) and their proposed order:
  [`proposals/upload-quality-review.md`](proposals/upload-quality-review.md) (2026-11-09; measure on the support-assistant
  proof of concept first).
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
