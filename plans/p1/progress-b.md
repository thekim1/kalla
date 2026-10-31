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
- [ ] `Assistants`, `AssistantVersions`, view `PublishedAssistantVersions`, `AssistantAudiences`, `GatewayModels`
- [ ] `EvaluationSets`, `EvaluationQuestions`, `EvaluationRuns` (`Variant`, coverage fields), `EvaluationResults`, `RetrievalStats`

## 3. Retrieval core (#11)
Refs: [knowledge §6–§8, §16.1–§16.2](../knowledge-api/design.md)
- [ ] Statements K, V, H+D; exact/HNSW rule; RRF; dedup and cap
- [ ] Coverage (vector side alone until the keyword floor is calibrated); `IReranker` no-op seam; query normaliser
- [ ] **Done:** §16.1, §16.2 tests; #4, #5, #6, #7, #19 recorded
- [ ] **Hand-off J5:** issue to stream A

## 4. Gateway client, release-1 side (#13)
Refs: [gateway-changes §13.4](../gateway-changes/design.md), [admin §4.5, §4.10](../admin/design.md), [identity §4.7](../identity/design.md)
- [ ] Per-level keys (`Eu` for every P1a call); `GatewayModels` sync with residencies
- [ ] V8 `/v1/models` check on assistant publish
- [ ] Pilot embedding profile seeded with its canary; `GET /api/gateway/status`
- [ ] Status green against the real gateway (after J10)

## 5. Knowledge role (#12)
Refs: [knowledge §2–§5, §9, §10, §16.3, §16.6](../knowledge-api/design.md)
- [ ] `KnowledgeService`, in-process `IRetriever`; query embedding at the profile's ceiling, in-process cache, circuit, degrade
- [ ] Section expansion; citations; download grants and `/content`
- [ ] `/v1/retrieve`, `/v1/collections`, `/v1/documents/{id}` for `internal` clients; Scalar
- [ ] **Done:** §16.3, §16.6 tests; #95, #102, #162 recorded

## 6. Admin API set-up surface (#14)
Refs: [admin §4.1–§4.7, §12](../admin/design.md), [weakness review §1](../weakness-review.md)
- [ ] OpenAPI and Scalar on `rag-admin`; `AdminClientAddresses` with `Settings.TrustedProxies` (#32, #38)
- [ ] Collections (publish, retrieval settings), sources (sync now, upload batches), runs and crawl report, `cancel-run` (#46)
- [ ] Site profiles (edit, roll back), templates (read), clients and keys, settings, data protection (history `none` only)
- [ ] Assistants: create, draft, publish, audience = client; evaluation questions and runs; member groups by hand
- [ ] **Done:** the pilot set-up works through Scalar alone; audited; `PermissionMatrix` covers each endpoint; #32 recorded
- [ ] **Hand-off J7:** issue to stream A and the user

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
- [ ] `deploy/keycloak/` additions: clients, `rag-*` roles, `departmentCodes`, full scope off, no test users, password policy, brute force, admin OTP
- [ ] **Done:** imports next to a minimal `ume` realm; sign-in test; #83 rechecked
- [ ] **Hand-off J8:** issue to the user for the gateway session

## 9. Evaluation smoke (#17, your part)
- [ ] `tests/evaluation/smoke` (fixture collection + 30 questions) and its test: recall@10 ≥ 0.8, every answer cited

## 10. Gold set and thresholds (#21, after J13)
- [ ] Gold set entered as `human` questions through Scalar
- [ ] Thresholds saved from the three-way run; #143, #169 recorded

## 11. P1a done (#22)
- [ ] A pilot member's cited answer from umea.se content shown in Open WebUI
