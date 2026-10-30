# Simplification pass

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
- **Reuse is by content-addressed caches**: the shared crawl frontier still makes one request per URL per freshness
  window for every source that wants it (politeness unchanged); raw content blobs are keyed by content hash; parse
  output is cached by (content hash, parser settings hash, parser build); embeddings by (text hash, profile). Every
  per-department copy after the first is cheap, and nothing couples departments.
- **Per-stage cache keys** = hash(input hash, stage settings, stage code version). No whole-graph Merkle hashes.
- **Uploads**: identity is (source, upload slot); the content hash is the version. Replacing a file keeps the document
  id, so evaluation questions, feedback and citation history survive revisions.
- **Fixed pipelines.** Each source type has one linear pipeline: fetch → scan (ClamAV, files) → parse (auto-parse
  rules) → clean → chunk → personal data → embed → index. A pipeline version is (template id, parameter set) as
  versioned JSON; publish computes the re-processing plan from which parameters changed (effect tags kept).
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
- **Answer-level evaluation** from late P1: a nightly on-prem judge over the evaluation sets through
  `Ume.Rag.Answering` measuring faithfulness and citation precision; the `uncited_answer` rate per assistant is a
  metric.
- **Breadcrumbs**: the page breadcrumb (`BreadcrumbList` or breadcrumb nav) is put in front of the heading path in the
  chunk prefix; document date and type stay metadata.
- `MarkCited` receives only the ranks actually cited.
- Interactive gateway header timeout lowered (proposed 15 s) so a stuck model gives an error, not a long spinner.

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
| B1 benchmark | Unchanged; feeds the advisory harness |
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
cross-cutting documents (`README.md`, `architecture.md`, `performance-pass.md`, `postponed-decisions.md`,
`to-verify.md`, `../01-general-idea.md`, `../HANDOVER.md`).

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
| Identity | Sitevision node id in the URL (`.4.88db09b1724ee319ca45d.html`); `/4.88db09b1724ee319ca45d.html` serves the same page (200, no redirect); the short alias `/doit` also serves it with 200; `rel=canonical` points to the full path | A page moved in the tree keeps its node id. The profile may set an **identity rule** (node id) once verified: the id-only URL returns the same content hash on sampled pages. Aliases without redirects are folded by canonical **only when verified** the same way (the "never trust canonical" rule becomes "trust only when verified") |
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
  line and child-link list, with breadcrumb "Kommun och politik › Kommunens organisation › Förvaltningar och
  verksamheter › Teknik och fastighet" (path to verify on the live page) and date 2026-01-15; a fixture set of ≈ 20
  saved umea.se pages (HTML and PDF) is checked in as a regression corpus.
