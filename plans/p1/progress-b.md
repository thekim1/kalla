# Progress: stream B (the serving side, P1a)

Owner: stream B. Brief: [`stream-b.md`](stream-b.md). Rules: [`../../AGENTS.md`](../../AGENTS.md). Checklist and binding
done-when criteria: [`README.md`](README.md) §3.

**How to use:** work top to bottom; item 1 first, because stream A waits on it. Take the first unticked step whose
hand-off has landed; if blocked, take the next item and come back. Tick each box in the PR that does the work and put
the PR number after it (`[x] … (#12)`). Record every `to-verify` row in `results-b.md` before ticking it here.

## Waiting on (tick when it lands)
- [ ] **J1** `v0.1.0` tagged, P1 ownership rules and these briefs merged
- [ ] **J3** A's contracts (then validate source settings against them)
- [ ] **J4** A's ingestion end to end (then replace the fixture writer in the retrieval and chat tests where it helps)
- [ ] **J6** A's commands and `on-demand` runs (then the evaluation and site-profile endpoints run end to end)
- [ ] **J10** gateway session: gateway reachable with EU routes and the `Eu` key (needed for the real-model status check)
- [ ] **J11** gateway session: shared Keycloak with RAG's additions imported
- [ ] **J13** user and pilot department: gold set written (needed for item 10)

## 1. Schema part 1, ingestion (#2, first)
Refs: [data-model](../platform/data-model.md) §1.2, §2.1, §2.2, §3.2, §9, §10; [connectors §6.0](../ingestion-worker/design-connectors.md)
- [x] `config`: `Settings`, `DataProtectionPolicies`, `EmbeddingProfiles` (ceiling per README §8 O1), `Collections`, `Sources`, `Pipelines`, `PipelineVersions` (#76)
- [x] `content`: `NodeManifest`, `WorkerNodeSupport`, `CrawlHosts`, `SiteProfiles`, `SiteProfileVersions`, `CrawlFrontier` (per source), `CrawlRuns`, `ConnectorCheckpoints`, `Uploads`, `DeletionProposals`, `QuarantinedFiles` (#76)
- [x] `Documents`, `NodeOutputs`, `ChunkSets`, `Chunks` (partial GIN, per-profile halfvec HNSW), `EmbeddingCache`, `Runs`, `ParseObservations`, `ParseProfiles`, `BlobTombstones` (#34) (#76)
- [x] Grants; `DatabaseRoleTests` extended; #22 recorded (#76)
- [x] **Hand-off J2:** migrations merged; issue to stream A (#76, #85)

## 2. Schema part 2, serving (#3)
Refs: [data-model §2.1, §2.2](../platform/data-model.md), [weakness review §2](../weakness-review.md)
- [x] `Assistants`, `AssistantVersions`, view `PublishedAssistantVersions`, `AssistantAudiences`, `GatewayModels` (this PR)
- [x] `EvaluationSets`, `EvaluationQuestions`, `EvaluationRuns` (`Variant`, coverage fields), `EvaluationResults`, `RetrievalStats` (this PR)

## 3. Retrieval core (#11)
Refs: [knowledge §6–§8, §16.1–§16.2](../knowledge-api/design.md)
- [x] Statements K, V, H+D; exact/HNSW rule; RRF; dedup and cap (this PR)
- [x] Coverage (vector side alone until the keyword floor is calibrated); `IReranker` no-op seam; query normaliser (this PR)
- [x] **Done:** §16.1, §16.2 tests; #4, #5, #6, #7, #19 recorded (this PR; the knowledge role's parts of §16.1 — section assembly, download grants, cache keys, request table — are item 12, age decay and `demote` P1b, Keycloak P2)
- [x] **Hand-off J5:** issue to stream A (#120)

## 4. Gateway client, release-1 side (#13)
Refs: [gateway-changes §13.4](../gateway-changes/design.md), [admin §4.5, §4.10](../admin/design.md), [identity §4.7](../identity/design.md)
- [x] Per-level keys (`Eu` for every P1a call); `GatewayModels` sync with residencies (this PR)
- [x] V8 `/v1/models` check on assistant publish (this PR: `GatewayModelCheck`; called from publish and level/audience saves in item 14)
- [x] Pilot embedding profile seeded with its canary; `GET /api/gateway/status` (this PR: `EmbeddingProfile:*` seeded by the migrator, canary recorded by the admin)
- [ ] Status green against the real gateway (after J10) — **live**

## 5. Knowledge role (#12)
Refs: [knowledge §2–§5, §9, §10, §16.3, §16.6](../knowledge-api/design.md)
- [x] `KnowledgeService`, in-process `IRetriever`; query embedding at the profile's ceiling, in-process cache, circuit, degrade (#127)
- [x] Section expansion; citations; download grants and `/content` (#146, this PR; `URB2` decryption waits for #145)
- [x] `/v1/retrieve`, `/v1/collections`, `/v1/documents/{id}` for `internal` clients; Scalar (this PR)
- [ ] **Done:** §16.3, §16.6 tests; #95, #102, #162 recorded (part 4b: §16.6 and the events, #95/#102/#162 recorded with #95 open for the browser check and #102 not met until the stop list; left in §16.3: evaluator == API on the smoke collection (item 17's collection; stream A adds the comparison), roles split (chat role); tampered ciphertext → aborted response and `integrity` metric done in the #163 follow-up)

## 6. Admin API set-up surface (#14)
Refs: [admin §4.1–§4.7, §12](../admin/design.md), [weakness review §1](../weakness-review.md)
- [x] OpenAPI and Scalar on `rag-admin`; `AdminClientAddresses` with `Settings.TrustedProxies` (#32, #38) (item 14a; settings, data protection and templates (read) of the third box are in it too)
- [x] Collections (publish, retrieval settings), sources (sync now, upload batches), runs and crawl report, `cancel-run` (#46) (#176, #178, #180; #32 recorded with its live part open)
- [x] Site profiles (edit, roll back), templates (read), clients and keys, settings, data protection (history `none` only) (#167, #185, #188)
- [x] Assistants: create, draft, publish, audience = client; evaluation questions and runs; member groups by hand (#189, #190)
- [x] **Done:** the pilot set-up works through Scalar alone; audited; `PermissionMatrix` covers each endpoint; #32 recorded (this PR: `PilotSetupTests`; #32 in #180 with its live part open)
- [x] **Hand-off J7:** issue to stream A and the user (#193)

## 7. Chat API (#15)
Refs: [chat §3–§10, §15.1, §18](../chat-api/design.md), [identity §5](../identity/design.md)
- [ ] `/v1/models`; `/v1/chat/completions` stream and JSON; conversational check; condense (600 ms)
- [ ] Coverage handling and not-found texts; token budget; prompt frame; residency check before the first byte
- [ ] Marker renumbering; Open WebUI `source` events and `url_citation`; output filter; attachments refused
- [ ] Open WebUI user-JWT trust; `open-webui` resource in the AppHost
- [ ] **Done:** a FakeGateway answer cites a fixture document in Open WebUI; #42, #47, #48, #50, #103, #105, #136, #154, #164, #165 recorded
- [ ] **Hand-off J9:** issue to stream A

## 8. Realm additions (#16)
Refs: [identity §11](../identity/design.md), Decisions table "P1 pilot sign-in", issue #47
- [x] `deploy/keycloak/` additions: clients, `rag-*` roles, `departmentCodes`, full scope off, no test users, password policy, brute force, admin OTP (this PR: `rag-additions.json`, the separable `rag-realm-settings.json`, `apply-additions.py`)
- [x] **Done:** imports next to a minimal `ume` realm; sign-in test; #83 rechecked (this PR: `RealmAdditionsTests`; #176, #177, #83 recorded)
- [x] **Hand-off J8:** issue to the user for the gateway session (#215)

## 9. Evaluation smoke (#17, your part)
- [x] `tests/evaluation/smoke` (fixture collection + 30 questions) and its test: recall@10 ≥ 0.8, every answer cited (this PR: `SmokeSet` in TestKit, `SmokeEvaluationTests`: recall@10 1.000, recall@3 1.000, 30/30 cited; the `evaluation-smoke` job is stream A's)

## 10. Gold set and thresholds (#21, after J13)
- [ ] Gold set entered as `human` questions through Scalar
- [ ] Thresholds saved from the three-way run; #143, #169 recorded

## 11. P1a done (#22)
- [ ] A pilot member's cited answer from umea.se content shown in Open WebUI
