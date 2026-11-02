# Progress: stream B (the serving side, P1b)

Owner: stream B. Brief: [`stream-b.md`](stream-b.md). Rules: [`../../AGENTS.md`](../../AGENTS.md). Checklist and binding
done-when criteria: [`README.md`](README.md) §3.

**How to use:** start once your P1a code items are done or blocked on a live hand-off. Work top to bottom; item 1
first, because stream A waits on it. Take the first unticked step whose hand-off has landed; if blocked, take the next
item and come back. Code items (§3.1) all come before the live block (§3.2). Tick each box in the PR that does the
work and put the PR number after it (`[x] … (#12)`). Record every `to-verify` row in `results-b.md` before ticking it
here.

## Status (2026-11-02, session 20)

- Items 1, 3 (Done open: K for 10–30 % terms, question on #248), 9 and 10 are built and merged; their Done boxes wait on
  to-verify rows 23 and 20 (partly, see `results-b.md`). Hand-offs L2 #234, L3 #249, L4 #300, L6 #273 filed.
- The admin UI is first (user, #278). Built: Kunskapsbanker (#279); every part of Källa (#285, #288, #303, #306, #309,
  #310); Publicering (#305); Assistenter with Versioner, Lagring and Arkivera (#301, #304); generic platform forms by
  the main session (#281). Item 11's Done (§5.4 steps 1–8 locally on FakeGateway) is still to be checked.
- Next, in order: item 12's Testfrågor och kvalitet with the evaluation report, then Sökinställningar; item 12's
  Översikt setup checklist and Granskningslogg; item 13 (Återkoppling); then #297 (stream-b test flakes). Known: run
  counters stay 0 until a run ends (#288 review), so the run page polls for the end.
- Merged without waiting for reviewers (user, 2026-11-02); problems go in a `stream-b` issue.

## Waiting on (tick when it lands)
- [ ] P1b ownership rules (README §2) and these briefs merged
- [ ] **L1** A's contracts part 2 (then validate the new source fields and job payloads against them)
- [ ] **L5** A's preview, publish, evaluation runs and commands (then the pages show real results)
- [ ] **L9** gateway session: release 1 deployed for the pilot (needed for item 27)
- [ ] **L12** user: G1 finished (needed for item 32)
- [ ] P1a's J10, J11, J13 (live block, `../p1/progress-b.md`)

## Code items

### 1. Schema part 3 (first)
Refs: [data-model §2.3, §8](../platform/data-model.md), [weakness review §3, §4](../weakness-review.md)
- [x] `chat`: `Conversations`, `Messages`, `MessageSources`, `Feedback`, `FeedbackImportState`, `AssistantDailyTotals`; `DataKeys`, `ToolCalls` created, unused (#223)
- [x] Freshness columns P1a's item 2 did not create (`Sources.DemoteRules`, `CrawlRuns.SeedsFailed`/`Complete`) (`DemoteRules`, `Incremental`, `RemovalCheck` in #219; `SeedsFailed`/`Complete` were already in P1a's schema; plus #219's parse-rule key, evaluation and chunk columns)
- [x] Grants; `DatabaseRoleTests` extended (#219, #223)
- [x] **Hand-off L2:** issue to stream A (#234)

### 3. Age decay and `demote`
Refs: [knowledge §6.5, §16.1](../knowledge-api/design.md), [weakness review §3](../weakness-review.md)
- [x] `recency` multiplier after fusion; `demote` lowest factor; uploads never demoted (#235, #241)
- [x] `website` template default set on a collection's first `website` source (#235)
- [x] Keyword stop list: lexemes in > 30 % of the scope's chunks dropped from K's query (#248); K ≤ 30 ms p95 met for frequent terms, not for terms in 10–30 % of the 100k scope (`results-b.md` row 5, flagged to the main session)
- [ ] **Done:** §16.1 tests green; evaluator order == `/v1/retrieve` order
- [x] **Hand-off L3:** issue to stream A (#249)

### 9. Admin API for P1b
Refs: [admin §4, §6.6, §6.7, §12](../admin/design.md), [identity §4.9](../identity/design.md)
- [x] Sources: preflight, preview, impact, runs, deletion decisions, documents with PII view/clear, release hold, template parameters, `demote` (#254, #255, #299; impact, runs, template parameters and `demote` earlier)
- [x] Pipeline publish: plan → confirm (409 on change) → report → keep / roll back (#298, preview #299; report = the worker's publish-report)
- [x] Evaluation set and reports (gold set first, threshold proposal as numbers); retrieval settings with the on-demand comparison (#258, #259; retrieval settings and publish in #176)
- [x] Assistants: draft → publish, versions with restore, archive, description, contact; test panel in `rag-admin`; audiences; retention; impact lines (#260, #261; draft, publish and audiences earlier in P1a)
- [x] Audit list; explain access (#257)
- [ ] **Done:** audited, config version bumped, `PermissionMatrix` covers each endpoint; #23 recorded
  — all parts merged and covered by `PermissionMatrixTests`; #23 recorded as partly in `results-b.md` (lock-wait measurement and `rag.admin.config_lock.wait` open)
- [x] **Hand-off L4:** issue to stream A (#300)

### 10. Chat: history `none`, feedback
Refs: [chat §11–§14](../chat-api/design.md)
- [x] Issued conversation ids; `ConversationWriter` (metadata only, daily totals) (#262; with `chat.rag_retention_chat_step`)
- [x] Conversation endpoints, user delete, 90-day metadata retention (#263; retention in #262)
- [x] `/v1/feedback` (ratings; `comment_not_stored`) (#264)
- [x] `FeedbackImporter` (ratings and reason categories, watermark, advisory lock) and feedback pruning (#267)
- [ ] **Done:** chat §18 tests for these parts green; #20, #51, #135 recorded
  — tests green; #51 and #135 recorded; #20 partly (the writer's batch is one transaction, not one `NpgsqlBatch` round trip)
- [x] **Hand-off L6:** issue to stream A with the configuration names (#273)

### 11. Journey pages, part 1
Refs: [admin §5.3–§5.5, §6.4–§6.7](../admin/design.md)
- [x] Kunskapsbanker (list, Översikt, Källor, Dokument with personuppgifter) (#279)
- [x] Källa (forms with template parameters, preflight, preview Före / Efter, impact, runs, crawl report, files, quarantine, deletions, held, Nedprioritera)
  — add-source drawer with crawl/URL-list parameters and Kontrollera (#285), source page with Hämta nu, runs and personal-data report, run page with crawl report (#288), preview step with Före / Efter and stage outputs in the drawer (#303); edit with impact and Nedprioritera (#306), Filer (upload source, upload, replace, delete) and Karantän (#309); deletion decision on the run page and Behållna on the source page (#310)
- [x] Publicering (plan, progress, report, Behåll / Återställ) — API in #298 (plan, confirm, rollback); page and the source page's Bearbetning card (parameters → Publicera…) in #305
- [ ] **Done:** admin §5.4 steps 1–8 work locally on FakeGateway

### 12. Journey pages, part 2
Refs: [admin §5.3, §5.8, §7](../admin/design.md)
- [x] Testfrågor och kvalitet and the evaluation report; Sökinställningar (#312)
- [x] Assistenter: draft, Testa, Publicera, Versioner with restore, Målgrupp, retention, archive
  — #301: list, create, Utkast (model, instructions, collections, warnings), Testa, Publicera, Målgrupp; #304: Versioner with Återställ, Lagring (V10 within the level's policy), Arkivera / Ta fram igen
- [ ] Översikt setup checklist; Granskningslogg; explain access
  — in part: setup checklist Kom igång on Översikt and the collection page (#313, #316); Granskningslogg (audit log page, #321); still missing: explain access
- [ ] Generic forms for platform objects (incl. parse rules; data protection `none` only); #146 recorded
  — in part: the main session built the generic forms in #281; #146 not recorded
- [ ] **Done:** admin §5.4 steps 9–13 work locally

### 13. Feedback page
Refs: [admin §8.1](../admin/design.md), [chat §14.4](../chat-api/design.md)
- [ ] Ratings, reasons' categories, assistant, time, cited documents, outcome, import health; department-scoped

### 14. Accessibility and pilot journey E2E
Refs: [admin §10.1, §11.4](../admin/design.md)
- [ ] axe, target-size, reflow specs; keyboard-only pilot journey against a static fixture site in `tests/e2e`
- [ ] One NVDA pass; `docs/accessibility-statement.md`; #96 recorded
- [ ] **Hand-off L7:** issue to stream A

### 18. W3 spike
- [ ] Two users, two groups, interleaved chats on the dev Keycloak and a local Open WebUI; #174 recorded

### 33. Organisation schema
Refs: [data-model §2.1](../platform/data-model.md), Decisions "Organisation" row
- [ ] `Collections.SharedWith`, `ContactName`, `ContactEmail`; `CollectionShares`; `AssistantVersions.CollectionLinks`; view and snapshot
- [ ] **Hand-off L13:** issue to stream A
- [ ] **Done:** migrations on an empty and the development database; `DatabaseRoleTests`

### 34. Topics in retrieval
Refs: [knowledge §4.2, §6.2](../knowledge-api/design.md)
- [ ] Search scope with links; topic predicate in K and V; chat and test panel pass links
- [ ] **Hand-off L14:** issue to stream A
- [ ] **Done:** topic, whole-collection and `filters.tags` retrieval tests

### 35. Organisation in the admin API
Refs: [admin §3.2, §4.5, §4.6](../admin/design.md)
- [ ] Sharing route, `view=`, topics, Map route, contact fields, owner-team default
- [ ] V11 widened, V35
- [ ] **Done:** `PermissionMatrixTests`; shared summary visible, sources 404; V35 refusal lists assistants

### 36. Organisation pages (after #314)
Refs: [admin §5.2–§5.5](../admin/design.md)
- [ ] Menu regrouped; Knowledge bases tabs *Ours* / *Shared with us* / *All*
- [ ] Sharing and Topics tabs; knowledge picker with topics; "Who can use it"; Map table; no team field
- [ ] **Done:** vitest per page; the E2E shares and links with a topic by keyboard

### 19. Code complete (your part)
- [ ] Pin review: NuGet and npm pins under 7 days old reviewed; #113 recorded; realm callback for the Open WebUI pin (README §7 O8)
- [ ] Condense residency: the chat condense step runs at `Eu` until on-prem hardware exists (#204 design deviation 1, M2); switch it to `OnPrem` before go-live once that route exists

## Live block

### 20. P1a's live items
- [ ] As `../p1/progress-b.md` (items 4's real-gateway check, 10, 11 there)

### 25. Freshness and index checks
- [ ] Decay and `demote` on the temporal questions; #171 recorded
- [ ] HNSW recall vs exact on the pilot collection; #117 recorded

### 27. Gateway release 1 switch (after L9)
- [ ] Pilot-team keys with `RequireMaxResidency`; status green against release 1; #41 (release-1 part) recorded

### 28. Feedback import live
- [ ] A pilot rating on the Feedback page; pruning of old Open WebUI feedback; #49, #135 rechecked live

### 30. E1 spike
- [ ] #175 recorded, or moved to P2 by the user (README §7 O3)

### 31–32. Go-live gate and pilot live
- [ ] Graded gold-set answers meet every threshold (with A and the user)
- [ ] Pilot live
