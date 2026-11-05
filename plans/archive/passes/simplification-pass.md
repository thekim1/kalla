# Simplification pass

> **Folded into the detail documents on 2026-11-01. Not binding; kept for the reasoning.** Every rule is now stated in the detail document it changes; the ledger at the end lists each item and where it went. Where this text and a detail document or the Decisions table differ, the detail document or the table wins.

Cross-cutting pass after the performance pass (2026-10-30). An overall review of the finished design (five independent
cluster reviews plus a read of the core documents) found the shape sound but the density too high: nearly every
component was designed to P2/P3 rigour and pulled into P0/P1, while robustness gaps sat in unglamorous places
(backups, key loss, all-at-once failure, residency by header discipline). The user adopted all ten changes below on
2026-10-30. This document is the **decided form**; the other documents were rewritten to match it (§12). Where this
document and an older passage differ, this document wins; Decisions table rows of 2026-10-30 (simplification pass)
record it.

Principle for the rewrite: **cut, do not annotate**. Removed mechanisms are deleted from the designs and listed once
in the document's "Deferred or removed (simplification pass)" section with where they went (later phase, trigger, or
dropped).

## 1. Thin P0 and P1 (scope)

**P0** = a skeleton that builds, deploys, is observable and can be restored:

- Keep (P0 checklist, repo-and-deployment §11): repo, projects, `verify.yml`, ServiceDefaults and health, migrations,
  config-version library (with the grace window, §9), FakeGateway, Keycloak realm, keys, admin UI skeleton and write
  path, AppHost, parser contract, single-host Compose, `release.yml`, deploy to the test host, Entra package 1 (RAG's
  own registrations, §7), gateway release agreed (§7), docs, worker dispatcher skeleton.
- Change: the Postgres image item becomes "stock pgvector image + .NET decompounder test" (§3); backup becomes
  pgBackRest with WAL archiving and a **point-in-time restore drill** (§4).
- Scale seams kept in P0 (cheap, avoid rewrites): pooler-safe SQL rule and scan test; `IBlobStore` with the write-once
  contract (§4); `IJobQueue` as a plain interface over the Postgres queue; `Worker:Classes` and
  `Worker:Concurrency:<class>`.
- Moved to **P2 (first multi-host deployment)**: `content."LeaderLeases"`, `connection-budget.sh`, role
  `ume_rag_monitor`, batched dequeue, coalesced and debounced `NOTIFY`, Redis per-host crawl in-flight leases, the
  fleet-wide embedding lease, a/b slots (the single-host pilot runs **one slot** with a short announced maintenance
  window).
- Workflows in P0: `verify.yml`, `release.yml`, the image test job. `perf.yml` (R1 test) and nightly evaluation move
  to P1 (before the pilot); `flake-hunt.yml`, `scale.yml`, parser fuzzing (Atheris) move to P2.

**P1 (pilot)** = one department using an assistant in Open WebUI:

- Sources: URL list, website crawl, uploads. Fixed pipeline (§2).
- Formats: HTML, PDF (text layer and OCR), modern Office (`.docx`/`.xlsx`/`.pptx`), OpenDocument, images (OCR), plain
  text, Markdown, CSV. **Moved to P2** (refused as `unsupported_format` in P1 unless the pilot source inventory taken
  at P1 start shows them common): legacy `.doc`/`.xls`/`.ppt` (LibreOffice), RTF, email `.eml`/`.msg` with child
  documents. Picture crops dropped until a describe-pictures node exists.
- OCR: **Tesseract `swe` only** in the parser image; EasyOCR and the bake-off are dropped. Acceptance check in P1: å/ä/ö
  recall ≥ 95 % on a 20-page Umeå sample; EasyOCR is reconsidered only if that fails (register P1 closed).
- Renderer: **P2**, and only if a P1-start check shows that pilot sites need JavaScript (umea.se pages server-rendered
  → not needed). `IRenderer` stays as a seam. Document identity is the final URL with no `origin`/`rendered` variants;
  when rendering arrives, a source either renders or not and the fetch mode is a document attribute. Short pages are an
  info note in the crawl report, never `degraded` because rendering is off.
- Personal data: patterns in .NET for every document; **names (NER) only when they can change the flag**: the document
  is in a source of a `public` collection, is not already flagged by another rule, and has ≥ 5 contact values. The
  separate `ner` job class and `NER_LANES` sizing are removed; the gated `/v1/ner` call runs inside the processing job
  (parser endpoint unchanged).
- Evaluation: advisory harness (§8), not a blocking gate.
- Chat: Open WebUI title, tag and follow-up generation are **switched off**; `owui-tasks`, `Clients.TaskModel` and the
  task heuristic are removed; the only task model is `Settings.Chat.CondenseModel`. No direct SQL against Open WebUI's
  database (feedback import and pruning use its HTTP API or its own settings).
- Admin UI: Swedish pages only for the department journey (§10).
- Gateway: one combined release before the pilot (§7).

## 2. Share by cache, not by row; fixed pipelines (ingestion)

- **Documents and chunk sets belong to one source.** A document is (source, identity); a chunk set is (document,
  pipeline version, embedding profile). A source belongs to exactly one collection (data-model `Sources`), so
  `Chunks."CollectionId"` is a **scalar** with a B-tree index; `CollectionIds` arrays, their GIN index, follow runs,
  `RunIds` arrays, the shared-chunk rule and cross-department set revival are removed. Membership changes no longer
  rewrite chunks.
- **Reuse is by content-addressed caches** (the shared crawl frontier is **P2**, [weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer); in P1 each source has its
  own frontier): the shared crawl frontier still makes one request per URL per freshness
  window for every source that wants it (politeness unchanged); raw content blobs are keyed by content hash; parse
  output is cached by (content hash, parser settings hash, parser build); embeddings by (text hash, profile). Every
  per-department copy after the first is cheap, and nothing couples departments.
- **Per-stage cache keys** = hash(input hash, stage settings, stage code version). No whole-graph Merkle hashes.
- **Uploads**: identity is (source, upload slot); the content hash is the version. Replacing a file keeps the document
  id, so evaluation questions, feedback and citation history survive revisions.
- **Fixed pipelines.** Each source type has one linear pipeline: fetch → scan (ClamAV, files) → parse (auto-parse
  rules) → clean → chunk → personal data → embed → index. A pipeline version is (template id, parameter set) as
  versioned JSON; publish computes the re-processing plan from which parameters changed (effect tags kept; **P2**, [weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):
  P1 publish re-processes every document of the collection, unchanged stages free from the per-stage caches).
  `IPipelineNode` stays as the internal way to add stages in code. Removed: the DAG executor, typed ports, user-added
  nodes, locked nodes, the Vue Flow canvas (a **P3 item behind a trigger**, register C2, decided by the user 2026-10-30: built only when
  editors ask for a flow that template parameters cannot express, seen in at least two departments or one recurring platform-team request). The *pipeline editor* right becomes "edit template parameters".
- **Parse rules, no auto-apply learning of parse strategies.** Built-in rules plus admin-made parse rules; parse
  observations are still recorded; from P2, parse-profile **proposals** are shown to platform admins and need
  approval. Removed: confidence threshold, trial runs, automatic rollback, the kill switch for auto-apply.
  **Exception, added on the user's request (§13): learned site profiles** auto-configure the HTML stages of crawl
  sources; they are deterministic, previewed and checked, not trial-and-score learning.
- **Personal-data state lives on the chunk set** (flag and counts from that set's personal-data stage output); the
  `public`-collection membership rule reads the set's flag. Every execution that produces a chunk set, preview and
  re-processing included, produces its state.
- **Alert** when a source has had N (default 3) runs without a complete traversal (deletions never run, content goes
  stale silently).

## 3. Swedish decompounding in .NET, stock Postgres (platform)

- Compounds are split in a .NET `SwedishDecompounder` (in `Ume.Rag.Retrieval` or a small `Ume.Rag.Text`), driven by
  the `sv_SE` Hunspell word list with linking-morpheme rules (`s`, vowel elision), longest match, minimum part length.
  The worker stores `Chunks."SearchText"` = text plus the parts; the generated `SearchVector` uses the **built-in
  `swedish`** configuration over it. The query side runs the same decompounder before building the query.
- **Keyword query** is built from lexeme arrays (`to_tsvector` → lexemes → `to_tsquery` with `|`), never by replacing
  `' & '` in the text form of `plainto_tsquery`.
- Postgres runs the **stock `pgvector/pgvector` image** (tag for 0.8.7 on PG 17, to verify). Managed Postgres is allowed
  where pgvector ≥ 0.8 and the needed settings are available. Supersedes the "Postgres hosting: own container
  everywhere" decision. Register H1 is closed (no dictionary in Postgres backends).
- Tests: the compound cases (`bygglovsansökan` → `bygglov`, `ansökan`) become .NET unit tests plus one integration test
  that a part matches through Postgres; a P0 comparison of the decompounder against Postgres `ispell` on a word sample
  records any recall difference (to-verify).
- Licence of the word list stays in register P2.

## 4. Backups, recovery and key escrow (robustness)

- **Postgres**: pgBackRest with continuous WAL archiving (`archive_timeout` 60 s) and point-in-time restore; weekly
  full, daily differential, 35-day retention (proposed). Targets: RPO ≤ 5 min, RTO ≤ 4 h for the pilot. The restore
  drill (P0) is a point-in-time restore. Major upgrades use `pg_upgrade` where possible.
- **Standby**: a streaming replica is an **availability** requirement before production beyond the pilot (P2), not a
  performance trigger.
- **Blobs are write-once**: keys are never overwritten (new content = new key), deletion = tombstone + delayed purge.
  Uploads (the only content that cannot be fetched again) are copied to the backup target within 15 minutes; derived
  blobs (raw fetches, parse outputs) are re-creatable and backed up nightly or not at all.
- **Two key classes**: long-lived keys (blob master keys per level, chat bucket key wrapping keys that are not being
  shredded) are **escrowed** offline with retention ≥ the data they protect; shred-bucket keys follow the shred rule
  (§5). Key backups no longer share one ≤ 30-day rule.
- **`public` blobs are not envelope-encrypted** (encrypted disk only); `internal` blobs are.

## 5. Data protection proportional to the pilot (chat, platform)

- **Chat content keys**: one data key per (level, expiry month) **bucket**, wrapped by that level's master key; each
  message is encrypted under the bucket of its own expiry month. Removed: per-conversation data keys, 7-day epochs,
  epoch races and tombstones, in-process conversation key caches. A user deleting a conversation deletes its rows.
- **Crypto-shredding waits for A1.** The format supports it, but destroying bucket keys (which reaches backups) is
  switched off until the registrar answers A1; until then retention deletes rows only. If A1 allows deletion, bucket
  destruction is switched on by a setting.
- Chat history storage is unchanged (turns are stored, encrypted; the Open WebUI feedback import does not carry chat
  snapshots, so feedback review needs our stored turns).
- **Tier machinery removed**: no composite `(Id, Classification)` keys and foreign keys, no `DataTiers` state, no
  move-tier command, no cross-tier fusion. Rows keep a plain `Classification` column and retrieval keeps its level
  filter. If `sensitive` ever needs isolation it becomes a **separate deployment** reached over the existing HTTP seam
  (`Retrieval:Mode=Http`); register T1 is restated so (K2 stays with it).

**Later change ([weakness review §4](weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)).**
The pilot assistant runs history mode `none` until A1 is answered (metadata and ratings only). The chat bucket keys,
content encryption, crypto-shredding and conversation review above are not built in P1; the data model keeps them for
when A1 allows stored content. Escrow in P0/P1 covers the blob keys and the pgBackRest passphrase. Feedback review
therefore has ratings only; comments stay in Open WebUI, whose store is the primary copy covered by the DPIA (G1).

## 6. Residency enforced by structure (security)

- **Gateway keys per level**: delegating keys `OnPrem`-only, `Eu`-max and open, each issued per deployable that needs
  it, with `AllowedIpRanges`. The worker never holds the open key. The key layer becomes a real second control.
- **Explicit ceiling on every call**: a `DelegatingHandler` in the gateway client refuses any request without an
  explicit `x-ume-max-residency` (`OnPrem`, `Eu` or `External`; `public` now sends `External`) and checks the key
  matches the level; unit-tested for every call site (chat, condense, judge, evaluation generation, NER/LLM stages).
- **Gateway changes** (in the combined release, §7): key flag `RequireMaxResidency` (the gateway refuses a request
  without the header); delegated requests apply **only the key's request policy**, never the effective team's (key
  flag `ApplyTeamPolicy`, default false for delegating keys; budgets and attribution unchanged); when targets within
  the ceiling exist but are drained or circuit-open the answer is **503 with `Retry-After`**, and `403
  max_residency_not_met` only when no configured target is within the ceiling.
- **Same effective level on every turn** for clients that keep history: max(assistant level, client ceiling) from the
  first message, so model and residency do not change between turn 1 and turn 2.

## 7. One gateway release, simpler identity

- **One combined gateway release** before the pilot: residency ceiling (old B), delegation (old A) and member groups
  in **claims-only** mode (old C), with the §6 flags. ADR drafts 018–020 stay, scheduled together. The **interim
  per-team linked keys (`GatewayKeyLinks`) are removed**: development and test use the FakeGateway, and the pilot waits
  for the release.
- **Graph-based member-group lookup moves to P2** (with data-plane audiences and approval package 2):
  `checkMemberGroups`, `GroupMember.Read.All`, the membership cache, the circuit, its security events, the "failed"
  banner and `MaxAgeHours`. In P1 department scope comes from groups in the claim.
- **RAG gets its own Entra registrations** (`ume-rag-admin`, same code as the gateway's); RAG's Bicep owns only RAG's
  registrations and never the gateway's. Users get `rag-*` roles on RAG's registration. The audit actor is **`oid`**
  (fallback `sub`).
- **Dropped**: Open WebUI e-mail as the basis for grants (`ResolveUsersByEmail`); group audiences through Open WebUI
  wait for E1.
- **Added**: an admin "explain access" endpoint (client, subject, assistant → the failing policy step and reason),
  cheap because `IAccessPolicy` is pure; and model validation against the gateway's `/v1/models` at the level's
  ceiling when an assistant is saved.
- Consistency fixes: one guest rule in both systems; no MSAL in the RAG Graph client; no GIN index on the RAG mirror
  of member groups; `Gateway:RequireDelegationEcho` setting decides when a missing echo fails closed.

**Later change ([weakness review §5](weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)).**
The combined release is split. Release 1, before the pilot: the residency ceiling with `RequireMaxResidency`, the
503/403 split, `/v1/models` filtered by the ceiling (ADR 018) and FakeLlm `dimensions`. Release 2, in P2: member
groups from claims (ADR 019) and delegation (ADR 020), before the first second-department or group audience. The
"no interim per-team keys" rule is reversed for the one pilot team: until release 2 the pilot is attributed with that
team's own per-level gateway keys (no `x-ume-on-behalf-of`; the gateway is unchanged). Department scope from groups
in the claim therefore reaches production with release 2. A second department in Open WebUI gets its own Open WebUI
instance until W3 and E1 are solved; the E1 and W3 spikes move to late P1.

## 8. Answer quality and evaluation

- **Not-found gate**: coverage is `low` only when the best vector distance is above the threshold **and** the keyword
  list is empty or below its floor. Short conversational messages (no content words) skip the gate and go to the
  model with a "no sources" frame. A condense failure or timeout never produces `low`: retrieval runs on the raw
  question. Coverage outcomes and near-threshold distances are logged as metrics (never query text) to calibrate the
  threshold on real traffic.
- **Reranker earlier**: an `IReranker` seam in P1 (FakeGateway implementation); a gateway rerank endpoint for
  `bge-reranker-v2-m3` on-prem is a gateway change for P2 (the gateway has none today); the evaluator compares
  with/without as soon as it exists; enabled per assistant when the evaluation shows a gain; coverage then uses the
  reranker score. Rerank moves from P3 to P2.
- **Condense**: skipped when the question is self-contained (heuristic: length and no anaphora); otherwise a hard
  timeout of 600 ms, falling back to the raw question.
- **Evaluation is an advisory harness**: per collection a set of generated (judge-filtered) and human questions, plus
  rewritten feedback questions; runs on the **full collection** after each publish and nightly; recall@10 and MRR with
  counts, compared with the previous run, shown on the publish screen. Removed: blocking gates, shadow sub-corpora,
  escalation 50 → 100, Wilson/bootstrap/McNemar, the `building` state with its 0.70 floor and the platform-admin
  override. A collection goes live when its owner publishes it. A gate is reconsidered only when measured run-to-run
  variance shows it can tell versions apart.
- **Publish** = plan → re-process (new chunk sets written next to the live ones, swapped per document, §9) →
  evaluation report → keep or roll back; previous chunk sets kept 14 days for one-click rollback.
- **Answer-level evaluation** (moved to **P2**, [weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)): a nightly on-prem judge over the evaluation sets through
  `Ume.Rag.Answering` measuring faithfulness and citation precision; the `uncited_answer` rate per assistant is a
  metric.
- **Breadcrumbs**: the page breadcrumb (`BreadcrumbList` or breadcrumb nav) is put in front of the heading path in the
  chunk prefix; document date and type stay metadata.
- `MarkCited` receives only the ranks actually cited.
- Interactive gateway header timeout lowered (proposed 15 s) so a stuck model gives an error, not a long spinner.

**Later change ([weakness review §2](weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch)).** A **pilot gold set** (100–150 answerable, 30–50
unanswerable, ≥ 10 temporal questions, written with the pilot department, stored as `human` questions) is built in
P1a; reports lead with it and generated questions are a regression signal only. A one-time **go-live gate** graded by
people (four starting thresholds) decides the pilot's go-live; publish stays advisory. Until `coverageMinKeywordScore`
is calibrated, `low` is decided by the vector side alone; both thresholds are set from answerable and unanswerable
questions together. In P1a the gold set is run vector-only, hybrid, and hybrid without the decompounder; hybrid and
the decompounder stay only if they beat vector-only.

## 9. Degrade gracefully

- **Config grace window**: security-relevant decisions use the last confirmed snapshot for up to
  `Config:MaxUnconfirmedSeconds` (deployment setting, default 90 s); beyond that, keyed requests get 503. Notifications
  still apply changes (revocations included) at once when they arrive. `/health/ready` does not fail on staleness
  within the window, so one stalled database does not drain every instance at the same moment.
- **No untrusted binary parsing in the worker**: every PDF goes to the parser; its text-layer fast path uses pypdfium2
  (already used by `/v1/inspect`) next to Docling and OCR in the parser. PdfPig and the worker/parser page stitching are
  removed; the worker parses HTML, text, Markdown and CSV only. A reclaim after an instance died does not count as an
  attempt; a job reclaimed 3 times is dead-lettered as a poison suspect.
- **Serialised config writes**: every admin config transaction first takes `SELECT … FOR UPDATE` on
  `config."ConfigVersion"`, then runs `ConfigRules`, so two concurrent saves cannot each pass cross-rules (write skew).
- **`Jobs` unpartitioned**: a partial unique index on `DedupKey WHERE State IN ('queued','running')` replaces the
  advisory-lock dedup; terminal jobs are deleted nightly in batches (7 days). `RunItems` stays day-partitioned.
- **Short transactions for chunk writes**: a new chunk set is written as not-live in batches of ≤ 1,000 chunks, then
  one short swap transaction (fence, swap, mark the old set deleted); an orphan sweep removes sets never swapped.
- **Smaller pools**, starting values measured in P0: `rag-api` 12 per instance (`Retrieval:MaxConcurrentSearches` 4
  = ⌊(pool − 3) ÷ 2⌋, since a search may hold two connections), `rag-admin` 5, `rag-worker` 10, `max_connections`
  100; Postgres container memory limit sized from the P0 measurement.
- **Redis-down behaviour stated** for every Redis use; rate limits fall back to per-instance in-memory limits.

## 10. A smaller, more useful P2; admin P1 UI for the journey (tools, admin)

- **Tool hub in-process** in P2: `IToolHub` runs in the chat role with bulkheads (per-server concurrency, timeout,
  circuit); `rag-tools` as a separate service (mTLS hop, own DB role, snapshot consumer) comes only with the first
  external, OBO or write tool.
- **Hosting kind `hosted` only** in P2; `municipal`/`external`, the `none`/`api-key` resolvers, per-call connections and
  `tools-egress` move to P3. The OpenAPI → MCP generator is dropped (none of the first tools has an OpenAPI source).
  The first tools live in **one** server `ume-mcp-kommun` (one container, one registration), starting with the mock
  fee calculator.
- **Skill lifecycle**: served pointer plus rollback; SemVer labels stay for display. Removed: pin/follow, MAJOR
  auto-pin, weekly re-gate. Per-skill MCP endpoints, `ask_assistant` conversations, prompts and resources move to P3.
- **`/mcp` early in P2** with `search_knowledge` and `list_collections` (client ceiling `public`), ahead of the hub.
- **Agent loop**: when an assistant's skills total ≤ about 4k tokens they are included statically and their tools are
  visible from step 1; progressive `load_skill` only above that; `search_knowledge` is prefetched with the user
  question as on the RAG path.
- **Sharing**: owners may share a skill or a collection with named departments (share list); the
  own-department-only link rule is replaced.
- **Third-party text**: sources and tool servers carry `ContainsThirdPartyText` (default true, set at approval); it
  drives the untrusted labelling and the lethal-trifecta check, which in P2 is a **hard refusal** (risk acceptance
  comes with write/OBO tools); imported external skills need a human platform-admin approval.
- **Drift**: owners pre-register the definition hash before a deploy (one approver); drift polling runs under one
  leader; `hosted` servers are verified on connect; two-person re-approval only for unexpected drift.
- **Skill gate** runs on the assistant's linked models, end to end against read-only tools.
- Agent cost limit **fails closed** when a price is unknown (hard-cap estimate).
- **Admin P1 UI**: Swedish pages for the department journey only (collection, source, preview, publish with the
  evaluation report, assistant, test panel, audience, feedback, limited review). Platform-admin objects (settings,
  data protection, clients, templates, embedding profiles, gateway models) use generic schema-driven forms and the
  OpenAPI UI in P1. Removed from P1: node editor, version diff and rebase, crawl hierarchy tree, coverage histogram,
  audit diff viewer (the audit list stays). The test panel runs from the same release as `rag-api`.
- Consistency fixes: one count for the cross-rules; an owner for the skill gate → served transition; a request flow
  for tool servers or the "(+ request)" right removed.

## 11. Effect on registers

| Register item | Change |
|---|---|
| H1 Hunspell memory | Closed: no dictionary in Postgres (§3) |
| P1 OCR engine | Closed: Tesseract `swe`, P1 acceptance check (§1) |
| T1 / K2 separate tier | Restated: separate deployment behind the HTTP seam (§5) |
| A1 archive law | Now also gates enabling bucket-key destruction (§5) |
| Z1 | Joined by legacy Office, RTF and email (P2) |
| New: D1 standby | Streaming replica before production beyond the pilot (§4) |
| New: W1, W2 | Existing chat on umea.se; Open WebUI chat-copy retention (§11a) |
| B1 benchmark | Unchanged; feeds the advisory harness (later: starts with the pilot gold set, weakness review §2) |
| O1, S2, R1, N1, U1, P2, V1, S1, M1, E1, K1 | Unchanged (K1 key backups follow §4's two key classes) |

## 11a. Settled during the rewrite

Conflicts between the rewritten documents, settled by the main session:

| # | Topic | Decision |
|---|---|---|
| 1 | Chunk-set visibility | A chunk is searchable iff `DeletedAt IS NULL`. New sets (`writing`) are written deleted in batches of ≤ 1,000; the swap is one short transaction per document (`Documents."LiveChunkSetId"`, un-delete the new set's chunks, delete the old set's); `held` sets stay deleted until the document is cleared; `superseded` sets kept 14 days for rollback (the same swap back). No candidates are lost to non-live sets |
| 2 | Pools | `rag-api` 12 per instance, `MaxConcurrentSearches` 4 (§9) |
| 3 | Answer-level work | Skill gate (P2) and nightly answer judge run in `rag-admin`; the worker runs retrieval evaluation only, holds no tool credentials and is not on network `mcp` |
| 4 | Names | Evaluation kinds `publish-report`/`nightly`/`on-demand`/`answers` (+ `skill-gate` in P2; `rag-admin` writes `answers` and `skill-gate` runs); set states `writing`/`live`/`held`/`superseded`; site-profile commands `approve-`/`reject-`/`edit-`/`rollback-site-profile`; `content."SiteProfiles"` + `SiteProfileVersions` per (level, host) |
| 5 | Deferred formats | Refused as `format_deferred` |
| 6 | Gateway key secrets | `Gateway__Keys__OnPrem`/`Eu`/`External`; the worker gets `OnPrem` and `Eu` only |
| 7 | Open WebUI chat copies | v0.12.5 cannot prune other users' chats without `ENABLE_ADMIN_CHAT_ACCESS`; register item W2, decided with A1 before the pilot |
| 8 | Crawl page limit | Default 10,000 stays; the preflight warns when the sitemap is larger; the umea.se source needs ≥ 20,000 |
| 9 | Rerank endpoint | A separate P2 gateway change, not in the combined release |
| 10 | Explain access | `POST /api/access/explain` for platform admins and department admins (own department) |
| 11 | Personal-data clearance | A decision on the document; flags and counts on the chunk set |
| 12 | New register items | D1 (Postgres standby), W1 (existing chat on umea.se), W2 (Open WebUI chat copies) |

## 12. Documents changed

All plan and design documents were rewritten to this form in one pass: platform (`plan.md`, `data-model.md`,
`repo-and-deployment.md`), ingestion worker (`plan.md`, `design-engine.md`, `design-connectors.md`,
`design-processing.md`), parser, knowledge API, chat API, identity, gateway changes, tools, admin, and the
cross-cutting documents (`../../README.md`, `../../architecture.md`, `performance-pass.md`, `../../postponed-decisions.md`,
`../../to-verify.md`, `../../../01-general-idea.md`, `../../../HANDOVER.md`).

## 13. umea.se findings and learned site profiles (user request, 2026-10-30)

The user asked that the parser and crawler design be checked against the real umea.se and that the generic system
handle such pages easily, **preferably by auto-learning and a dynamically set-up pipeline**. Reconciled with §2: the
pipeline stays linear, but its HTML stage parameters are set per host by a **learned site profile**, and each document
is routed dynamically (HTML extraction in the worker; PDF fast path, Docling or OCR in the parser by `/v1/inspect`).

### 13.1 What umea.se looks like (fetched 2026-10-30, example page `…/digitalomstallningochit.4.88db09b1724ee319ca45d.html`)

| Fact | Observed | Consequence |
|---|---|---|
| CMS | **Sitevision** (`sv-*` classes, `/sitevision/` resources, `svid…` ids). Widely used by Swedish municipalities | Ship a built-in **Sitevision preset**; it covers many municipal sites, not only Umeå |
| Rendering | Server-rendered: all main text is in the HTML | Renderer not needed for umea.se (confirms §1: renderer P2) |
| Change headers | No `ETag`, no `Last-Modified`, `Cache-Control: no-store`, session cookies | Conditional GETs save nothing; change detection = **sitemap `lastmod`** + content hash; never store or send cookies |
| Sitemap | `robots.txt` → `sitemapindex.xml` → one `sitemap1.xml.gz` with **24,184 URLs, all with `lastmod`**: 6,159 HTML, 7,959 PDF, ≈ 8,900 images, 233 video. The example page's `lastmod` (2026-01-15T09:24) equals its visible "Sidan uppdaterades 2026-01-15" | **Incremental runs by `lastmod` are the default** for this host (a daily run fetches only changed URLs); PDFs are the larger half of the corpus, so the parser path matters more than HTML; images and video are excluded by default (alt text and captions come from the pages) |
| Identity | Sitevision node id in the URL (`.4.88db09b1724ee319ca45d.html`); `/4.88db09b1724ee319ca45d.html` serves the same page (200, no redirect); the short alias `/doit` also serves it with 200; `rel=canonical` points to the full path | A page moved in the tree keeps its node id. The profile may set an **identity rule** (node id) once verified: the id-only URL gives the same output hash (`OutputHash` of the extracted text) on sampled pages; on umea.se the raw content hashes differ by per-request noise (connectors design §5.6, PR #104). Aliases without redirects are folded by canonical **only when verified** the same way (the "never trust canonical" rule becomes "trust only when verified") |
| Main content | `<main>` holds ≈ 1.3 KB text of a 107 KB page; header, 4 `nav`s, footer, cookie banner (`GomCookieBanner`), crisis-message script outside it | Main-content root = `main` |
| Boilerplate inside `main` | Share widget ("Dela Facebook Twitter LinkedIn Mail Stäng dela"), short-URL line, "Undersidor" child-link list | Template-repeated blocks inside the root are stripped by the profile; the child-link list is kept as links for discovery, not as chunk text |
| Breadcrumb | `nav[aria-label="Brödsmulor"] ol.sol-breadcrumbs`, with screen-reader text "nivå i brödsmulenavigeringen" in each link | Breadcrumb selector, `sr-only` text removed; feeds the chunk prefix (§8) |
| Date | No header date; body text "Sidan uppdaterades YYYY-MM-DD"; sitemap `lastmod` | Date pattern per profile → `SourceModifiedAt` (fallback `lastmod`) |
| Metadata | `meta description`, `keywords`, `og:*`; no JSON-LD | Description and keywords stored as document metadata |
| Contact block | "Kontakt" with phone, visiting/postal address, opening hours, a named manager with phone and e-mail | Kept as a section (answers "vem kontaktar jag?" need it); personal-data rules apply as designed (3 contact values here, below the name-check threshold) |
| robots.txt | Disallows `*.html.printable`, `?sv.state=`, `?contactUserId=`, `;jsessionid=` etc. | Honoured as designed; the profile also normalises away session parameters |
| Existing chat | The CSP allows `chat.umea.se` and `ume-app-aichat-prod.azurewebsites.net` | umea.se already runs an AI chat: find out what it is before the pilot (scope and overlap, register N1/U1 owners) |

### 13.2 Learned site profiles

- A **site profile** belongs to a host (shared by every source crawling it, editable by platform admins) and is
  versioned JSON: CMS fingerprint, main-content root selector, boilerplate selectors and repeated phrases to strip,
  breadcrumb selector, date pattern, identity rule (verified), URL normalisation (session parameters), excluded URL
  types (images, video), change signal (`lastmod` reliable or not), JavaScript needed or not, link-list blocks kept for
  discovery only.
- **Learning** (deterministic, no LLM needed): on the first run of a crawl source, the worker samples 30–50 pages from
  the sitemap (spread over sections) and derives the profile: CMS fingerprint → preset (Sitevision first; WordPress,
  Episerver/Optimizely later); main root by landmark (`main`, `article`, `role=main`) or text density; boilerplate =
  DOM blocks and phrases repeated on ≥ 60 % of sampled pages inside the root; breadcrumb by `aria-label`
  (Brödsmulor/breadcrumb), `BreadcrumbList` or class name; date by matching visible dates against `lastmod`; identity
  rule by fetching id-only URLs for a few pages and comparing content hashes; `lastmod` reliability by comparing
  `lastmod` with visible dates and content-hash changes across runs; JavaScript need by raw text yield.
- **Applied automatically** with guardrails: on a new source the learned profile is applied and shown in the source
  form preview (before/after text of sample pages). A **re-learned** profile for a host in use is applied
  automatically only when its check passes on the sample: no page loses more than 10 % of its main text words, no page
  ends up empty, and the boilerplate share does not rise; otherwise it becomes a proposal for a platform admin. Every
  application is versioned, audited and can be rolled back in one click; each document records the profile version
  that extracted it.
- **Re-learning** runs when extraction quality drops (empty-root rate, boilerplate share or text-yield change above a
  threshold in a run report, e.g. after a CMS redesign) and at most weekly.
- **Dynamic routing per document** stays in the fixed pipeline: the profile decides the HTML extraction; `/v1/inspect`
  decides the PDF path (pypdfium2 text layer, Docling layout, OCR per page). Editors never see a graph.
- Built-in presets live in code next to `IPipelineNode`; a profile learned for a host overrides the preset.
- Acceptance test (P1): the umea.se example page extracts to exactly its main text without share widget, short URL
  line and child-link list, with breadcrumb "Kommun och politik › Kommunens organisation › Förvaltningar,
  verksamheter › Teknik och fastighet" (path to verify on the live page) and date 2026-01-15; a fixture set of ≈ 20
  saved umea.se pages (HTML and PDF) is checked in as a regression corpus.

**Later change ([weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)).** Learning and automatic re-learning move to **P2**. In P1 (P1a) the umea.se site
profile is a **hand-checked Sitevision preset**: a manual profile JSON with the same schema, versioned like any config;
learning adds to such profiles later. The acceptance test above stays in P1a.

**Later change ([weakness review §3](weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)).** The
`lastmod` reliable flag is set only after a sample of 500 random umea.se URLs has been fetched and their content
hashes compared with `lastmod`; until then the umea.se source runs full traversals without the `lastmod` skip. A
traversal is complete when ≥ 99 % of seeds are resolved (no longer "no seed failed"); URLs that leave the sitemap get a
removal check. Ranking gets per-collection age decay (on for PDFs in the `website` template) and `demote` URL patterns
for archive sections.

## 14. Fewer containers in the pilot (user, 2026-10-30)

The user asked whether the pilot's eight long-running containers could be fewer. Decided: **six** (`rag-api`,
`rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`, `postgres`), plus the one-shot `rag-migrate`.

- **No Redis in P1.** In the single-slot pilot Redis held only admin sessions, rate limits and the query-embedding
  cache. Replacements:
  - **Admin sessions**: the BFF's ticket store (and Data Protection key ring, if the gateway keeps it in Redis) in
    Postgres, owned by `rag-admin`, expired rows deleted by the admin's retention step.
  - **Rate limits**: in-process per instance (exact with one slot).
  - **Query-embedding cache**: in-process memory cache with the same HMAC key, value format and size cap.
  - Each sits behind a small seam (`IRateLimitStore`, `ISharedCache`) with an in-memory implementation and a Redis
    implementation; **Redis returns with the first multi-host deployment (P2)**, where rate limits, crawl and
    embedding in-flight leases and the cache must be shared. A deployment enables it by configuring
    `ConnectionStrings:redis`; without it, the in-memory implementations are used.
- **pgBackRest inside the Postgres image.** `ume-rag/postgres` = the pinned stock `pgvector/pgvector` image plus the
  PGDG `pgbackrest` package and its configuration (a derived image of a few lines, tested in CI);
  `archive_command = pgbackrest archive-push %p`; scheduled backups run `pgbackrest backup` inside the `postgres`
  container from a host timer (`docker compose exec`), so there is no long-running sidecar and no WAL spool volume.
  The `postgres` container joins the `backup` network, whose firewall allows only the backup target.
- Considered and rejected: merging `rag-api` and `rag-admin` (an admin deploy would restart chat, and the nightly
  answer judge and test panel would share CPU with live chat); merging ClamAV into the parser (ClamAV needs egress for
  signature updates, the parser must have none); merging the worker into `rag-api` (ingestion must never compete with
  chat).

## 15. Many assistants per department (user, 2026-10-30)

Analysis of a department or team building several chatbots and agents over websites and files: the chain source →
knowledge bank (collection) → assistant → audience is sound and reusable, but P2 offered several ways to attach
knowledge, two assistant kinds, and no versioning or tests per assistant. Decided:

1. **Knowledge only through direct links.** An assistant = instructions + the knowledge banks (and, P2, workspaces)
   it links directly + skills. **Skills carry know-how and tools only, never collections**: `SkillVersionLinks` of kind
   `collection` are removed, and so are the rules and retrieval scopes that added a skill's collections. One place
   shows what an assistant knows.
2. **One assistant type, mode derived.** No `rag`/`agent` choice: an assistant with no tool-bearing skills runs the
   fixed RAG path; linking a skill with tools switches it to the agent loop (P2), shown as a label ("Använder
   verktyg"). `Assistants.Mode` becomes a computed value, not an editable field.
3. **Light assistant versioning in P1.** Edits are saved to a **draft**; the test panel runs the draft; **Publicera**
   makes it the live version; version history with a diff and one-click restore (restore = a new version). Each
   assistant has a small **test-question set** (question, expected behaviour as free text and optional expected
   source/phrase), re-run on publish through `Ume.Rag.Answering` in `rag-admin` with the answer judge, **advisory**
   like collection evaluation. The chat role serves the published version only.
4. **Owner-team scoping in P2.** Assistants, collections and (P2) skills keep their owner team; from P2 a department may
   limit `Edit` to members of the owner team (department admins still see and edit all).
5. **Smaller additions** (main session, low risk): copy an assistant; assistant templates (instructions, not-found
   texts, settings) maintained by the platform team; a department map (sources → knowledge banks → assistants →
   audiences) and an impact notice on saving a source or knowledge bank ("påverkar 3 assistenter"); an attention item
   for assistants without use in 90 days (suggest archiving); every assistant has a description and a contact owner,
   shown where clients can display them (Open WebUI model description, to verify).

**Later change ([weakness review §1](weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)).** Of item 3, only draft → publish with history and restore stays in P1 (P1b); the
version diff and the per-assistant test-question set (with the answer judge) move to **P2**. Of item 5, copy,
assistant templates, the department map and the unused-assistant attention item move to **P2**.


## Ledger (2026-11-01)

Every item of this document, in document order, and where it went. Kinds: *rule* (stated in the detail document named), *risk* (kept in a register), *rationale* (binds nothing), *reference* (stays here). Statuses: *folded* (written into the target), *already there* (the target already stated it), *obsolete* (superseded; the Decisions row or "as built" is named), *not folded* (reason given).

| ID | Pass § | Item | Kind | Status | Target |
|---|---|---|---|---|---|
| S-1 | intro ¶1 | Review history: five cluster reviews, density too high, user adopted ten changes 2026-10-30; this document wins over older passages | rationale | rationale: binds nothing | – |
| S-2 | intro ¶2 | "Cut, do not annotate": removed mechanisms deleted and listed once in each doc's "Deferred or removed (simplification pass)" section | rule | already there | chat plan.md §9, chat §23, data-model §13, engine §24, every detail doc's "Deferred or re… |
| S-3 | §1 ¶1 | P0 = skeleton that builds, deploys, is observable and can be restored | rule | already there | platform-plan §11, repo-and-deployment §11 |
| S-4 | §1 Keep | P0 keep list: repo, projects, verify.yml, ServiceDefaults, migrations, config-version lib with grace window, FakeGateway, Keycloak realm, k… | rule | already there | repo-and-deployment §11 |
| S-5 | §1 Change | Postgres image item = stock pgvector image + .NET decompounder test | rule | already there | repo-and-deployment §5.2, §11 |
| S-6 | §1 Change | P0 backup = pgBackRest with WAL archiving and a point-in-time restore drill | rule | already there | repo-and-deployment §7.4, §11 |
| S-7 | §1 Seams | P0 scale seams: pooler-safe SQL rule + scan test; `IBlobStore` write-once; `IJobQueue` plain interface; `Worker:Classes`, `Worker:Concurren… | rule | already there | data-model §9, engine §9.1, engine §9.3, repo-and-deployment §11 (#21) |
| S-8 | §1 Moved P2 | To P2 (first multi-host): `LeaderLeases`, `connection-budget.sh`, `ume_rag_monitor`, batched dequeue, coalesced/debounced NOTIFY, Redis cra… | rule | already there (repo-and-deployment §13); folded (data-model §1.1; §2.2; §13) | data-model §1.1; §2.2; §13, repo-and-deployment §13 |
| S-9 | §1 Moved P2 | Pilot runs one slot with a short announced maintenance window; a/b slots P2 | rule | already there | platform-plan §2, repo-and-deployment §6, §7.3 |
| S-10 | §1 Workflows | P0 workflows: verify.yml, release.yml, image test job | rule | already there | repo-and-deployment §8.1 |
| S-11 | §1 Workflows | `perf.yml` (R1) and nightly evaluation in P1 before the pilot; `flake-hunt.yml`, `scale.yml`, Atheris fuzzing P2 | rule | already there (repo-and-deployment §8.1, §8.3); folded (parser §15.4) | parser §15.4, repo-and-deployment §8.1, §8.3 |
| S-12 | §1 P1 | P1 (pilot) = one department using an assistant in Open WebUI | rule | folded (platform-plan §2); obsolete: Weakness review 1: P1 scope (general-idea §1) | general-idea §1, platform-plan §2 |
| S-13 | §1 P1 Sources | P1 sources: URL list, website crawl, uploads; fixed pipeline | rule | already there | connectors §1, plan §1 |
| S-14 | §1 P1 Formats | P1 formats: HTML, PDF (text + OCR), docx/xlsx/pptx, ODF, images (OCR), text, Markdown, CSV | rule | already there | parser §3, processing §3.2 |
| S-15 | §1 P1 Formats | Legacy .doc/.xls/.ppt, RTF, .eml/.msg to P2, refused in P1 unless the P1-start inventory shows them common | rule | folded | connectors §9.2, parser §3, §7.5 |
| S-16 | §1 P1 Formats | Picture crops dropped until a describe-pictures node exists | rule | already there | parser §7.3, §20 |
| S-17 | §1 OCR | Tesseract `swe` only; EasyOCR and bake-off dropped | rule | already there | parser §9.2, §20 |
| S-18 | §1 OCR | P1 acceptance: å/ä/ö recall ≥ 95 % on 20-page Umeå sample; EasyOCR only if it fails; register P1 closed | rule | already there | parser §9.3 |
| S-19 | §1 Renderer | Renderer P2 and only if a P1-start check shows JS needed; `IRenderer` seam stays | rule | already there | connectors §8, connectors §8.1, connectors §8.2 |
| S-20 | §1 Renderer | Identity = final URL, no `origin`/`rendered` variants; fetch mode a document attribute when rendering arrives | rule | already there | connectors §3, connectors §8.4 |
| S-21 | §1 Renderer | Short pages = info note in crawl report, never `degraded` because rendering is off | rule | already there | connectors §5.5 |
| S-22 | §1 Personal data | PII patterns in .NET for every document; NER only for `public`-collection source, not already flagged, ≥ 5 contact values | rule | already there | processing §9.1, processing §9.3 |
| S-23 | §1 Personal data | `ner` job class and `NER_LANES` sizing removed; gated `/v1/ner` inside the processing job | rule | already there | data-model §4.2, engine §9.1, parser §7.8, §20, processing §10, processing §9.3 |
| S-24 | §1 Evaluation | Evaluation is an advisory harness, not a blocking gate | rule | already there | processing §14 |
| S-25 | §1 Chat | Open WebUI title/tag/follow-up generation off; `owui-tasks`, `Clients.TaskModel`, task heuristic removed; only task model `Settings.Chat.Co… | rule | already there | chat §4.2, chat §4.3, data-model §2.1 |
| S-26 | §1 Chat | No direct SQL against Open WebUI's database; feedback import and pruning via its HTTP API or settings | rule | already there | chat §13.3, chat §14.2 |
| S-27 | §1 Admin | Admin UI: Swedish pages only for the department journey | rule | already there | admin §5.3 |
| S-28 | §1 Gateway | One combined gateway release before the pilot | rule | obsolete: Weakness review 5 | gateway §13, identity §2 |
| S-29 | §2 ¶1 | Document = (source, identity); chunk set = (document, pipeline version, embedding profile); source in exactly one collection | rule | already there | data-model §2.2, engine §1, engine §2 |
| S-30 | §2 ¶1 | `Chunks.CollectionId` scalar with B-tree; `CollectionIds` arrays, GIN, follow runs, `RunIds`, shared-chunk rule, cross-department revival r… | rule | already there | data-model §2.2; §3.2; §13, engine §24 |
| S-31 | §2 ¶2 | Reuse by content-addressed caches: raw blobs by content hash; parse output by (content hash, parser settings hash, parser build); embedding… | rule | already there | data-model §9, engine §1, engine §8 |
| S-32 | §2 ¶2 | Shared crawl frontier is P2; P1 one frontier per source | rule | already there | connectors §6.0 |
| S-33 | §2 ¶3 | Per-stage cache key = hash(input hash, stage settings, stage code version); no Merkle hashes | rule | already there | engine §3.3, engine §8 |
| S-34 | §2 ¶4 | Upload identity = (source, upload slot); content hash is the version; replace keeps document id | rule | already there | connectors §3.4, connectors §9.4 |
| S-35 | §2 ¶5 | One linear pipeline per source type: fetch → scan → parse → clean → chunk → personal data → embed → index | rule | already there | engine §1, engine §2.1 |
| S-36 | §2 ¶5 | Pipeline version = (template id, parameter set) as versioned JSON | rule | already there | data-model §2.1, engine §3.1 |
| S-37 | §2 ¶5 | Publish computes re-processing plan from changed parameters (effect tags) — P2; P1 publish re-processes the whole collection, caches make u… | rule | folded (engine §12.2); already there (connectors §12.3) | connectors §12.3, engine §12.2 |
| S-38 | §2 ¶5 | `IPipelineNode` stays; DAG executor, typed ports, user-added/locked nodes, Vue Flow canvas removed | rule | already there | admin §6.2, §6.8, engine §24, engine §5 |
| S-39 | §2 ¶5 | Canvas = P3 item behind trigger C2 (≥ 2 departments or one recurring platform-team request) | rule | folded (general-idea §5a); already there (admin §6.8) | admin §6.8, general-idea §5a |
| S-40 | §2 ¶5 | Right "pipeline editor" = edit template parameters | rule | already there | admin §3.1, §6.1 |
| S-41 | §2 ¶6 | Built-in + admin parse rules; observations recorded; P2 parse-profile proposals need approval; no auto-apply, trial runs, threshold, auto r… | rule | already there | processing §14.6, processing §22, processing §3.5 |
| S-42 | §2 ¶6 | Exception: learned site profiles auto-configure HTML stages | rule | obsolete: as built (manual profiles in P1; learning P2) | connectors §5.6 |
| S-43 | §2 ¶7 | Personal-data state on the chunk set; `public` membership rule reads the set's flag; every execution incl. preview produces it | rule | already there | data-model §2.2, processing §9.4, processing §9.5 |
| S-44 | §2 ¶8 | Alert when a source has N (default 3) runs without a complete traversal | rule | already there | connectors §16, connectors §5.4, engine §10.3 |
| S-45 | §3 ¶1 | .NET `SwedishDecompounder` (sv_SE Hunspell list, linking `s`, vowel elision, longest match, min part length); `SearchText` = text + parts;… | rule | already there | data-model §3.1, knowledge §6.2 |
| S-46 | §3 ¶2 | Keyword query from lexeme arrays OR'd, never by replacing `' & '` in `plainto_tsquery` text | rule | already there | data-model §3.3, knowledge §6.2 |
| S-47 | §3 ¶3 | Stock `pgvector/pgvector` image (0.8.7 on PG 17); managed Postgres allowed if pgvector ≥ 0.8; supersedes "own container everywhere" | rule | already there | repo-and-deployment §5.2, §10 |
| S-48 | §3 ¶3 | Register H1 closed | rule | already there (register) | – (postponed-decisions) |
| S-49 | §3 ¶4 | Tests: .NET unit tests for compounds (`bygglovsansökan`), one Postgres integration test; P0 comparison vs `ispell` recorded | rule | already there | data-model §3.1; §14 |
| S-50 | §3 ¶5 | Word-list licence stays in register P2 | rule | already there (register) | – (postponed P2) |
| S-51 | §4 ¶1 | pgBackRest, continuous WAL archiving `archive_timeout` 60 s, PITR; weekly full, daily diff, 35-day retention (proposed) | rule | already there | repo-and-deployment §7.4, §6.2 |
| S-52 | §4 ¶1 | Targets RPO ≤ 5 min, RTO ≤ 4 h (pilot); P0 restore drill is a PITR; major upgrades via `pg_upgrade` | rule | already there | repo-and-deployment §7.4, §7.5, §14 |
| S-53 | §4 ¶2 | Streaming standby = availability requirement before production beyond the pilot (P2) | rule | folded | repo-and-deployment §7.4 |
| S-54 | §4 ¶3 | Blobs write-once: never overwrite, delete = tombstone + delayed purge | rule | already there | data-model §9 |
| S-55 | §4 ¶3 | Uploads copied to backup target within 15 min; derived blobs nightly or not at all | rule | already there | repo-and-deployment §7.4 |
| S-56 | §4 ¶4 | Two key classes: long-lived keys (blob master keys, non-shredded chat wrapping keys) escrowed offline; shred-bucket keys follow shred rule | rule | already there (repo-and-deployment §7.4); folded (data-model §7) | data-model §7, repo-and-deployment §7.4 |
| S-57 | §4 ¶5 | `public` blobs not envelope-encrypted; `internal` blobs are | rule | already there | data-model §7; §9 |
| S-58 | §5 ¶1 | Chat data key per (level, expiry month) bucket wrapped by level master key; per-conversation keys, epochs, key caches removed | rule | already there | chat §12.3, data-model §2.3; §7 |
| S-59 | §5 ¶1 | User deleting a conversation deletes its rows | rule | already there | chat §13.2 |
| S-60 | §5 ¶2 | Crypto-shredding off until A1; retention deletes rows only; switched on by a setting | rule | already there | chat §13.4, data-model §7; §8 |
| S-61 | §5 ¶3 | Chat history storage unchanged: turns stored encrypted; feedback review needs our stored turns | rule | obsolete: Weakness review 4: data protection | chat §11.2, chat §14 |
| S-62 | §5 ¶4 | Tier machinery removed: no composite keys, `DataTiers`, move-tier, cross-tier fusion; plain `Classification` column + level filter | rule | already there | data-model §6; §13 |
| S-63 | §5 ¶4 | `sensitive` isolation = separate deployment over HTTP seam (`Retrieval:Mode=Http`); T1 restated, K2 with it | rule | already there | data-model §6, platform-plan §6 |
| S-64 | §5 Later | Later change: history mode `none`, no bucket keys/encryption/shredding/review in P1; escrow covers blob keys + pgBackRest passphrase; feedb… | rule | already there | chat §11.2, chat §12.3, chat §14, repo-and-deployment §7.4 |
| S-65 | §6 ¶1 | Gateway keys per level (delegating `OnPrem`-only, `Eu`-max, open), per deployable, `AllowedIpRanges`; worker never holds the open key | rule | already there | identity §4.7, repo-and-deployment §6.3 |
| S-66 | §6 ¶2 | `DelegatingHandler` refuses any call without explicit `x-ume-max-residency`; checks key matches level; `public` sends `External`; unit-test… | rule | already there (engine §7.7, chat §8.1, chat §18.5); folded (knowledge §5.2) | chat §18.5, chat §8.1, engine §7.7, knowledge §5.2 |
| S-67 | §6 ¶3 | Gateway key flag `RequireMaxResidency` | rule | already there | gateway §3.1 |
| S-68 | §6 ¶3 | Delegated requests apply only the key's request policy (`ApplyTeamPolicy` default false) | rule | already there | gateway §5.3 |
| S-69 | §6 ¶3 | 503 + `Retry-After` when in-ceiling targets are drained/circuit-open; 403 `max_residency_not_met` only when none configured | rule | already there | gateway §3.3 |
| S-70 | §6 ¶4 | Same effective level every turn: max(assistant level, client ceiling) from the first message for history-keeping clients | rule | already there | chat §4.1, chat §8.2, identity §4.7 |
| S-71 | §7 ¶1 | One combined release (B, A, C claims-only) with ADRs 018–020 together | rule | obsolete: Weakness review 5 | gateway §13.1 |
| S-72 | §7 ¶1 | Interim per-team linked keys (`GatewayKeyLinks`) removed; dev/test use FakeGateway; pilot waits for the release | rule | folded (gateway §13.4, §17); obsolete: Weakness review 5 (identity §14) | gateway §13.4, §17, identity §14 |
| S-73 | §7 ¶2 | Graph member-group lookup (`checkMemberGroups`, `GroupMember.Read.All`, cache, circuit, events, banner, `MaxAgeHours`) to P2; P1 scope from… | rule | folded (gateway §4.4, §17); already there (identity §2.4, §17) | gateway §4.4, §17, identity §2.4, §17 |
| S-74 | §7 ¶3 | RAG gets own Entra registrations (`ume-rag-admin`); RAG Bicep owns only RAG's; users get `rag-*` roles on RAG's registration | rule | already there | identity §1.6, §12 |
| S-75 | §7 ¶3 | Audit actor is `oid` (fallback `sub`) | rule | already there | admin §9.2, data-model §2.4, identity §3 |
| S-76 | §7 ¶4 | Dropped: Open WebUI e-mail as grant basis (`ResolveUsersByEmail`); group audiences via Open WebUI wait for E1 | rule | already there | identity §5.4, §17 |
| S-77 | §7 ¶5 | Admin "explain access" endpoint (client, subject, assistant → failing step + reason) | rule | already there | admin §4.5, identity §4.9 |
| S-78 | §7 ¶5 | Model validated against gateway `/v1/models` at the level's ceiling when an assistant is saved | rule | already there | admin §4.6, identity §4.7 |
| S-79 | §7 ¶6 | One guest rule in both systems | rule | already there | gateway §4.6, identity §1.7 |
| S-80 | §7 ¶6 | No MSAL in the RAG Graph client | rule | already there | identity §2.4 |
| S-81 | §7 ¶6 | No GIN index on RAG mirror of member groups | rule | already there | data-model §2.1, identity §2.1 |
| S-82 | §7 ¶6 | `Gateway:RequireDelegationEcho` decides when missing echo fails closed | rule | already there | identity §4.7, §10 |
| S-83 | §7 Later | Later change: release split; pilot attributed by pilot team's per-level keys (no `x-ume-on-behalf-of`); one Open WebUI per department; E1/W… | rule | folded (gateway §13.4); already there (chat §4.3, identity §4.7) | chat §4.3, gateway §13.4, identity §4.7 |
| S-84 | §8 ¶1 | Not-found: `low` only when vector distance above threshold AND keyword list empty/below floor | rule | already there | chat §10.1, chat §6.2, knowledge §7 |
| S-85 | §8 ¶1 | Short conversational messages skip the gate, go to model with "no sources" frame | rule | already there | chat §5, chat §6.2 |
| S-86 | §8 ¶1 | Condense failure/timeout never produces `low`; retrieval on raw question | rule | already there | chat §5, chat §6.2 |
| S-87 | §8 ¶1 | Coverage outcomes and near-threshold distances logged as metrics, never query text | rule | already there | knowledge §15 |
| S-88 | §8 ¶2 | `IReranker` seam P1 (FakeGateway impl); gateway rerank (`bge-reranker-v2-m3`) P2; per-assistant enable on evaluated gain; coverage then use… | rule | already there | knowledge §6.7, parser (n/a) |
| S-89 | §8 ¶3 | Condense skipped when self-contained (length, no anaphora); else hard 600 ms timeout → raw question | rule | already there | chat §5 |
| S-90 | §8 ¶4 | Advisory harness: generated (judge-filtered) + human + rewritten feedback questions; full collection after each publish and nightly; recall… | rule | already there | admin §7.2, processing §13, processing §14 |
| S-91 | §8 ¶4 | Removed: blocking gates, shadow sub-corpora, 50 → 100 escalation, Wilson/bootstrap/McNemar, `building` state 0.70 floor, override; collecti… | rule | already there | processing §14, processing §22 |
| S-92 | §8 ¶4 | Gate reconsidered only when measured run-to-run variance can tell versions apart | risk | n/a (risk) | processing §14.2 |
| S-93 | §8 ¶5 | Publish = plan → re-process (sets side by side, swapped per document) → report → keep/roll back; previous sets kept 14 days | rule | already there | engine §12.1, engine §12.3 |
| S-94 | §8 ¶6 | Answer-level evaluation (nightly on-prem judge via `Ume.Rag.Answering`, faithfulness, citation precision) P2; `uncited_answer` rate metric | rule | already there | chat §17, chat §18.8 |
| S-95 | §8 ¶7 | Page breadcrumb (`BreadcrumbList` or nav) before heading path in chunk prefix; date and type stay metadata | rule | already there | processing §6.2, processing §7 |
| S-96 | §8 ¶8 | `MarkCited` receives only cited ranks | rule | already there | chat §4.1, chat §9.5, knowledge §2.2 |
| S-97 | §8 ¶9 | Interactive gateway header timeout lowered (proposed 15 s) | rule | already there | chat §8.3 |
| S-98 | §8 Later | Later change: gold set, go-live gate, vector-side-alone `low`, three-way comparison | rule | folded (processing §13.4, processing §14); already there (knowledge §7) | knowledge §7, processing §13.4, processing §14 |
| S-99 | §9 ¶1 | Config grace window `Config:MaxUnconfirmedSeconds` (default 90 s), then 503 for keyed requests; notifications apply at once; `/health/ready… | rule | already there | data-model §5, platform-plan §4 |
| S-100 | §9 ¶2 | No untrusted binary parsing in the worker: every PDF to parser; pypdfium2 fast path; PdfPig and page stitching removed; worker parses HTML,… | rule | folded (parser §3); already there (processing §3.2, processing §3.7) | parser §3, processing §3.2, processing §3.7 |
| S-101 | §9 ¶2 | Reclaim after instance death is no attempt; reclaimed 3 times → dead-letter as poison suspect | rule | already there | engine §7.3, engine §9.4 |
| S-102 | §9 ¶3 | Serialised config writes: `SELECT … FOR UPDATE` on `config."ConfigVersion"` first, then `ConfigRules` | rule | already there | admin §4.4, data-model §5 |
| S-103 | §9 ¶4 | `Jobs` unpartitioned; partial unique index on `DedupKey` where queued/running; terminal jobs deleted nightly in batches (7 days); `RunItems… | rule | already there | data-model §4.1; §4.4 |
| S-104 | §9 ¶5 | New chunk set written not-live in batches ≤ 1,000, one short swap transaction; orphan sweep | rule | already there | engine §14 |
| S-105 | §9 ¶6 | Pools: `rag-api` 12, `MaxConcurrentSearches` 4, `rag-admin` 5, `rag-worker` 10, `max_connections` 100; Postgres memory limit from P0 measur… | rule | already there (repo-and-deployment §6.2, knowledge §6.6); folded (data-model §1.1) | data-model §1.1, knowledge §6.6, repo-and-deployment §6.2 |
| S-106 | §9 ¶7 | Redis-down behaviour stated per use; rate limits fall back to per-instance in-memory | rule | already there | data-model §11 |
| S-107 | §10 ¶1 | Tool hub in-process in the chat role with bulkheads; `rag-tools` service only with first external/OBO/write tool | rule | already there | tools §2.2, §4 |
| S-108 | §10 ¶2 | Hosting kind `hosted` only in P2; `municipal`/`external`, `none`/`api-key`, per-call connections, `tools-egress` P3; OpenAPI → MCP generato… | rule | already there | tools §4.3, §8.2, §8.3 |
| S-109 | §10 ¶2 | First tools in one server `ume-mcp-kommun`, starting with mock fee calculator | rule | already there | tools §8.5 |
| S-110 | §10 ¶3 | Skill lifecycle: served pointer + rollback, SemVer for display; pin/follow, MAJOR auto-pin, weekly re-gate removed; per-skill MCP endpoints… | rule | folded | tools §3.5, §7.4, §7.5 |
| S-111 | §10 ¶4 | `/mcp` early in P2 with `search_knowledge`, `list_collections` (client ceiling `public`) | rule | already there (tools §7.2, §7.3); folded (knowledge §14) | knowledge §14, tools §7.2, §7.3 |
| S-112 | §10 ¶5 | Agent loop: skills ≤ ~4k tokens included statically; progressive `load_skill` above; `search_knowledge` prefetched | rule | already there | tools §3.9, §6.1 |
| S-113 | §10 ¶6 | Owners may share a skill or collection with named departments (share list); own-department-only link rule replaced | rule | already there (tools §3.6, data-model §2.1); folded (admin §3.2) | admin §3.2, data-model §2.1, tools §3.6 |
| S-114 | §10 ¶7 | `ContainsThirdPartyText` (default true, set at approval) drives untrusted labelling and lethal-trifecta hard refusal in P2; imported extern… | rule | already there | tools §3.11, §5.4 |
| S-115 | §10 ¶8 | Drift: owners pre-register definition hash (one approver); polling under one leader; `hosted` verified on connect; two-person re-approval o… | rule | already there | tools §4.4 |
| S-116 | §10 ¶9 | Skill gate runs on the assistant's linked models, end to end against read-only tools | rule | folded | tools §3.7 |
| S-117 | §10 ¶10 | Agent cost limit fails closed when price unknown | rule | already there | tools §6.5 |
| S-118 | §10 ¶11 | Admin P1 UI: Swedish pages for department journey only; platform objects via generic forms + OpenAPI UI | rule | already there | admin §5.3, §5.8 |
| S-119 | §10 ¶11 | Removed from P1: node editor, version diff/rebase, crawl hierarchy tree, coverage histogram, audit diff viewer | rule | already there | admin §6.2, §6.5, §16 |
| S-120 | §10 ¶11 | Test panel runs from the same release as `rag-api` | rule | already there | admin §4.9, §2.1 |
| S-121 | §10 ¶12 | One count for cross-rules; owner for skill gate → served; tool-server request flow / "(+ request)" right removed | rule | already there | admin §4.6, §3.3, tools §3.5 |
| S-122 | §11 H1 | Register H1 closed | rule | already there (register) | – (postponed) |
| S-123 | §11 P1 | Register P1 (OCR) closed | rule | already there | parser §9.3 |
| S-124 | §11 T1/K2 | T1/K2 restated: separate deployment behind HTTP seam | rule | already there (register) | – (postponed) |
| S-125 | §11 A1 | A1 also gates enabling bucket-key destruction | risk | registered: postponed A1:17 (gates stored chat content) | – |
| S-126 | §11 Z1 | Z1 joined by legacy Office, RTF, email (P2) | rule | already there (register) | – (postponed) |
| S-127 | §11 D1 | New D1 standby before production beyond the pilot | risk | registered: postponed D1:32 | – |
| S-128 | §11 W1/W2 | New W1 (existing chat on umea.se), W2 (Open WebUI chat-copy retention) | risk | registered: postponed W1:20 (answered), W2:21 | – |
| S-129 | §11 B1 | B1 unchanged; feeds advisory harness; starts with gold set | rule | already there (register) | – (postponed) |
| S-130 | §11 others | O1, S2, R1, N1, U1, P2, V1, S1, M1, E1, K1 unchanged; K1 follows two key classes | rationale | rationale: binds nothing | – |
| S-131 | §11a #1 | Chunk searchable iff `DeletedAt IS NULL`; `writing` sets written deleted in ≤ 1,000 batches; swap via `Documents.LiveChunkSetId`; `held` st… | rule | already there | data-model §2.2, engine §12.3, engine §14 |
| S-132 | §11a #2 | Pools `rag-api` 12, `MaxConcurrentSearches` 4 | rule | already there (knowledge §6.6); folded (data-model §1.1) | data-model §1.1, knowledge §6.6 |
| S-133 | §11a #3 | Skill gate (P2) and nightly answer judge in `rag-admin`; worker runs retrieval evaluation only, no tool credentials, not on network `mcp` | rule | already there | admin §2.2, processing §14, repo-and-deployment §6.1 |
| S-134 | §11a #4 | Names: evaluation kinds `publish-report`/`nightly`/`on-demand`/`answers` (+`skill-gate`); set states `writing`/`live`/`held`/`superseded`;… | rule | already there | admin §4.5, §4.7, data-model §2.2 |
| S-135 | §11a #5 | Deferred formats refused as `format_deferred` | rule | already there (parser §3, §7.5); folded (connectors §9.2) | connectors §9.2, parser §3, §7.5 |
| S-136 | §11a #6 | Secrets `Gateway__Keys__OnPrem`/`Eu`/`External`; worker gets `OnPrem` and `Eu` only | rule | already there | repo-and-deployment §6.3, §7.1 |
| S-137 | §11a #7 | v0.12.5 cannot prune others' chats without `ENABLE_ADMIN_CHAT_ACCESS`; W2 decided with A1 before the pilot | risk | n/a (risk) | chat §13.3 |
| S-138 | §11a #8 | Crawl `maxPages` default 10,000; preflight warns when sitemap larger; umea.se source needs ≥ 20,000 | rule | folded | connectors §5.2 |
| S-139 | §11a #9 | Rerank endpoint = separate P2 gateway change, not in the combined release | rule | folded | gateway §7.4 |
| S-140 | §11a #10 | `POST /api/access/explain` for platform admins and department admins (own department) | rule | already there | admin §4.5, identity §4.9 |
| S-141 | §11a #11 | Personal-data clearance is a decision on the document; flags and counts on the chunk set | rule | already there | data-model §2.2, processing §9.5 |
| S-142 | §11a #12 | New register items D1, W1, W2 | rationale | rationale: binds nothing | – |
| S-143 | §12 | Documents changed: all plan/design docs rewritten in one pass | rationale | rationale: binds nothing | – |
| S-144 | §13 ¶1 | User request: auto-learning + dynamic pipeline; reconciled as linear pipeline with per-host profile and per-document routing | rationale | rationale: binds nothing | – |
| S-145 | §13.1 CMS | umea.se is Sitevision; ship a built-in Sitevision preset | rule | already there | connectors §5.6 |
| S-146 | §13.1 Rendering | umea.se server-rendered; renderer not needed | rationale | rationale: binds nothing | – |
| S-147 | §13.1 Headers | No ETag/Last-Modified; change detection = sitemap `lastmod` + content hash; never store or send cookies | rule | already there (connectors §4); folded (connectors §12.1) | connectors §12.1, connectors §4 |
| S-148 | §13.1 Sitemap | Incremental runs by `lastmod` default for umea.se; images and video excluded by default | rule | already there (connectors §5.1, connectors §5.2); obsolete: Weakness review 3 (lastmod default only after sample); exclusion already there (connectors §5.4) | connectors §5.1, connectors §5.2, connectors §5.4 |
| S-149 | §13.1 Identity | Identity rule (node id) only once verified by `OutputHash` on sampled pages; aliases folded by canonical only when verified | rule | already there | connectors §3.1, connectors §3.3, connectors §5.6 |
| S-150 | §13.1 Main | Main-content root = `main` | rule | already there | connectors §5.6 |
| S-151 | §13.1 Boilerplate | Template-repeated blocks inside root stripped; "Undersidor" child-link list kept for discovery, not chunk text | rule | already there | connectors §5.6, processing §3.7 |
| S-152 | §13.1 Breadcrumb | Breadcrumb selector `nav[aria-label="Brödsmulor"]`, `sr-only` text removed; feeds chunk prefix | rule | already there | connectors §5.6, processing §7 |
| S-153 | §13.1 Date | Date pattern "Sidan uppdaterades" → `SourceModifiedAt`, fallback `lastmod` | rule | already there | connectors §5.6 |
| S-154 | §13.1 Metadata | Description and keywords stored as document metadata | rule | already there | processing §7 |
| S-155 | §13.1 Contact | Contact block kept as a section; PII rules apply as designed | rule | folded | processing §9.4 |
| S-156 | §13.1 robots | robots.txt honoured; profile normalises away session parameters | rule | already there | connectors §3.1, connectors §5.1 |
| S-157 | §13.1 Existing chat | umea.se already runs an AI chat: find out what it is before the pilot | risk | registered: postponed W1:20 (answered) | – |
| S-158 | §13.2 ¶1 | Site profile per host, shared, editable by platform admins; versioned JSON with listed fields (CMS, root, boilerplate, breadcrumb, date, id… | rule | already there | connectors §5.6, data-model §2.2 |
| S-159 | §13.2 ¶2 | Learning: sample 30–50 pages, CMS → preset, main root by landmark/density, boilerplate ≥ 60 %, breadcrumb, date, identity, `lastmod`, JS by… | rule | already there | connectors §5.6 |
| S-160 | §13.2 ¶3 | Auto-apply guardrails: new source applied with preview; re-learned applied only if no page loses > 10 % words, none empty, boilerplate shar… | rule | already there | admin §4.5, connectors §5.6 |
| S-161 | §13.2 ¶4 | Re-learning on quality drop, at most weekly | rule | already there | connectors §5.5, connectors §5.6 |
| S-162 | §13.2 ¶5 | Dynamic routing per document: profile for HTML, `/v1/inspect` for PDF path; editors never see a graph | rule | already there | processing §3.1, processing §3.2 |
| S-163 | §13.2 ¶6 | Built-in presets in code next to `IPipelineNode`; learned host profile overrides preset | rule | already there | connectors §5.6 |
| S-164 | §13.2 ¶7 | P1 acceptance: umea.se example page extracts to exact main text, breadcrumb, date 2026-01-15; ≈ 20 saved pages as regression corpus | rule | already there | connectors §18, connectors §5.6 |
| S-165 | §13.2 Later 1 | Later change: learning P2; P1a hand-checked Sitevision manual profile JSON | rule | already there | connectors §5.6 |
| S-166 | §13.2 Later 2 | Later change: 500-URL `lastmod` sample; ≥ 99 % completeness; removal check; age decay; `demote` | rule | already there | connectors §5.4, connectors §5.6, knowledge §6.5 |
| S-167 | §14 ¶1 | Pilot has six long-running containers + one-shot `rag-migrate` | rule | already there | platform-plan §2, repo-and-deployment §6, §6.2 |
| S-168 | §14 Redis | No Redis in P1; admin sessions (ticket store, DP key ring) in Postgres owned by `rag-admin`, expired rows deleted by retention | rule | already there | admin §2.3, data-model §2.1; §8 |
| S-169 | §14 Redis | Rate limits in-process; query-embedding cache in-process with same HMAC key, format, cap | rule | already there | identity §6.2, knowledge §5.4 |
| S-170 | §14 Redis | Seams `IRateLimitStore`, `ISharedCache` (in-memory + Redis); Redis returns in P2, enabled by `ConnectionStrings:redis` | rule | already there (repo-and-deployment §1, §4.1); folded (data-model §11) | data-model §11, repo-and-deployment §1, §4.1 |
| S-171 | §14 pgBackRest | `ume-rag/postgres` = stock pgvector + PGDG `pgbackrest`; `archive-push`; backups via host timer `docker compose exec`; no sidecar or spool… | rule | already there | repo-and-deployment §5.2, §6.1, §7.4 |
| S-172 | §14 Rejected | Rejected merges: api+admin, ClamAV+parser, worker+api, with reasons | rationale | n/a (rationale) | repo-and-deployment §6 |
| S-173 | §15 intro | Analysis of a department building several assistants | rationale | rationale: binds nothing | – |
| S-174 | §15 #1 | Knowledge only through direct links; skills never carry collections (`SkillVersionLinks` kind `collection` removed) | rule | already there | admin §4.5, data-model §2.1, tools §3.2 |
| S-175 | §15 #2 | One assistant type; mode derived from tool-bearing skills; label "Använder verktyg"; `Assistants.Mode` computed | rule | already there | admin §1, §4.5, chat §2.2, data-model §2.1 |
| S-176 | §15 #3 | Draft → test panel → Publicera → history with restore (restore = new version); chat serves published only | rule | folded (data-model §2.1); already there (admin §4.5, §4.9) | admin §4.5, §4.9, data-model §2.1 |
| S-177 | §15 #3 | Version diff and per-assistant test-question set re-run on publish with answer judge | rule | already there | admin §7.4 |
| S-178 | §15 #4 | Owner-team edit scoping in P2 | rule | already there | admin §3.2 |
| S-179 | §15 #5 | Copy assistant, assistant templates, department map, unused-assistant (90 days) attention item | rule | already there | admin §1 row 24, §5.3 |
| S-180 | §15 #5 | Impact notice on saving a source/knowledge bank ("påverkar 3 assistenter") | rule | already there | admin §1 row 24, §4.5 |
| S-181 | §15 #5 | Every assistant has description and contact owner, shown where clients can display them | rule | already there | chat §3.1, data-model §2.1 |
| S-182 | §15 #5 | Open WebUI model description display of the contact owner, to verify | risk | n/a (risk) | chat §3.1 |
| S-183 | §15 Later | Later change: only draft → publish with history/restore in P1b; diff, test-question set, copy, templates, map, attention item to P2 | rule | already there | admin §1, §7.4 |
