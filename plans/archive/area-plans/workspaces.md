# Ask-a-file workspaces: plan

**Later (P2–P3).** Nothing here is built in P1; P1's upload source (one fixed `files` pipeline per collection) is what workspaces later reuse, and the E1 and W3 spikes that gate them run in P1b.

Self-service knowledge bases: a person or a team uploads a set of files and gets Q&A, chat and whole-document tasks
(summarise, overview, compare) over them, with the same Swedish search, classification, residency, personal-data
checks, citations and audit as department collections. A workspace is a **light kind of collection** that reuses the
whole pipeline; nothing here is a second RAG stack. Decided by the user on 2026-10-30 (§14; Decisions table row
"Ask-a-file workspaces"). Builds on knowledge only through direct links, one assistant type and versioned assistants (reasoning:
[`../passes/simplification-pass.md`](../passes/simplification-pass.md) §15); phase **P2**.

## 1. Scope

**Does:** personal and team workspaces; one-step creation, upload, processing status, sharing and deletion in a small
Swedish area "Mina kunskapsbaser" in `rag-admin`; one assistant per workspace, listed as a model to its members only;
per-document summaries made on-prem at indexing; answer modes for passages, named documents, a files overview and
full text; quotas, expiry and owner-leaves handling; full deletion.

**Does not:** a chat UI (Open WebUI and other chat-API clients), new connectors (uploads only; SharePoint/OneDrive
pickers are a later idea), per-workspace pipeline editing, evaluation sets or publish reports, `public` or `sensitive`
workspaces, agent loops.

## 2. What a workspace is (reuse map)

| Object | Workspace form | Reused from |
|---|---|---|
| Collection | `Kind = workspace` (department collections: `Kind = department`); `Classification = internal` always (never `public`; `sensitive` refused until its separate deployment exists, register T1); `PublishedAt` set at creation | data-model §2.1 |
| Source | exactly **one** `upload` source on the platform `files` pipeline with `summaries` on (§6.1), created with the collection; no schedule, no preview, no parameter edits | connectors §9, engine §13.1 |
| Assistant | the ordinary assistant type (no separate kind; mode derived: no skills, so always the RAG path), created automatically and **published as version 1**: name "Fråga: *workspace name*", slug `ws-<8 base32>`, description = the workspace's, the Swedish workspace template, the workspace as its **only** direct link, answer modes of §6.3 | data-model §2.1, chat design §7 |
| Files | upload flow and limits, ClamAV fail-closed, sniffed types, parser routing, chunking, personal-data detection (recorded, not blocking: no `public` rule applies), embedding on-prem, flat chunks | connectors §9, §11; processing design |
| Search | the knowledge API unchanged; a workspace stays ≤ 10,000 chunks (§5.2), so retrieval always takes the **exact scan** path (knowledge plan §3) | knowledge design §6 |
| Chat | chat API path, residency check before the first byte, citations, history mode and 90-day retention of the `internal` level, bucket-key encryption of everything chat-related | chat plan §4–§5 |
| Audit | admin write path (change + outbox + audit + jobs in one transaction), `oid` as actor | admin design §4.4 |

**Editing the assistant** uses the same draft → publish versioning as every assistant in
a simplified form: managers change name, description and a short "extra instruktioner" field (≤ 1,000 characters,
inside the locked frame); a save is a draft, **Publicera** makes it live, "Tidigare versioner" restores one (a new
version). No test panel, test-question set or judge run (the owner's own chat is the test); the chat role serves the
published version only. Links, model, modes and history settings are not editable: a workspace assistant links only
its workspace, never skills. Owner-team scoping (§15.4) does not apply: workspace edit rights are its managers'.

Quality is shown as **facts per file** instead of evaluation: state, pages, text-layer vs OCR pages, tables, chunks,
tokens, language, personal-data types found (counts only), warnings ("ingen text hittades", "skannad: text via OCR",
"dubblett", "format stöds inte än", "virus") from `Uploads`, `RunItems`, `Documents` and `ChunkSets`. Workspaces are
excluded from nightly retrieval evaluation and the answer-level judge.

## 3. Ownership, access and clients

- **Personal workspace**: members = the owner (Entra `oid`). **Team workspace**: members = one Entra group (group claim
  value); the creator must be in that group at creation; managers = the creator plus named co-managers (§8).
- Access is the existing `IAccessPolicy` (identity design §4.3): assistant audience rows of kind `user` (new, the owner's
  `oid`) or `group`, the client ceiling (Open WebUI `internal`, so workspaces are visible there), the data-handling step.
  Application subjects (keys, client-credentials tokens) match only `client` rows, so **no key ever reaches a
  workspace**. `CollectionAudiences` mirror the assistant's rows for P2 token callers of `/v1/retrieve` and `/mcp`
  (MCP clients stay at ceiling `public` by default, so they see no workspace unless an admin raises the client).
- **Clients**: Open WebUI (each workspace appears as a model to its members), the own chat client (P2, delegated
  token), later Teams (P3).
- **Attribution**: gateway calls go `x-ume-on-behalf-of` a team (delegation arrives with gateway release 2, P2, before
  P2a). P2a proposal: one platform team
  `Settings.Workspaces.TeamId` with a central budget, the matching department as `Collections.DepartmentId` and
  `Jobs.DepartmentId` (one fairness bucket for all workspaces); per-department charge-back is question 2.
- **Department assistants linking a workspace** (direct links, P2): only a **team** workspace, only after a manager
  shares it with that department (`CollectionShares` row written from the area; personal workspaces are never
  shareable this way); the department assistant then reaches it in `passages` mode; deleting or expiring the
  workspace shows the impact notice ("påverkar 2 assistenter") and use through those assistants counts as activity
  (question 12).
- **Department pages never list workspaces** (`Kind = department` filter) except as linkable shared items; platform admins see workspace metadata only
  (owner, size, state, expiry) in a generic list; file names and content only through an audited support action
  (§9).

**Dependency (gate for P2a).** Open WebUI users are `OwuiUser` subjects matched by `client` rows only until **E1** (a
real Entra token through Open WebUI, identity design §5.4, §9.4) passes; e-mail is never the basis of a grant. User and
group audiences in Open WebUI therefore need E1. Second gate: Open WebUI v0.12.5 keeps **one process-wide model
registry** (`app.state.MODELS`) overwritten by whichever user last listed models (`backend/open_webui/utils/models.py:484-499`),
and chat resolves the model from it (`main.py:1153-1167`), so per-user model lists can fail with "Model not found"
when two users with different lists are active. The per-user list cache itself is keyed by user
(`routers/openai.py:709-715`). This affects every P2 department/group audience, not only workspaces (question 4).
The spikes on E1 and on the W3 workaround (Open WebUI group permissions, register W3) run in late P1
(P1b), so this gate is known before P2 starts. Departments beyond the first get one Open WebUI instance each (its own
client, ceiling and audience) until W3 and E1 are solved; that fallback does not serve personal or team workspaces,
which still wait for both gates.

## 4. Self-service area "Mina kunskapsbaser"

Served by `rag-admin` (same BFF, CSP, sign-in, Swedish texts, WCAG 2.2 AA, admin design §2.3, §10.1). Today every
`/api` call without a `rag-*` role is 403 and `ume-rag-admin` has *assignment required*: a new app role
**`rag-workspace-user`** assigned to an all-staff group (or the Open WebUI users group) and a policy `Workspaces` open
the area; the route group `/api/me/workspaces` is scoped by the caller's `oid` and groups, never by department.

| Page | Content |
|---|---|
| Översikt | my workspaces and team workspaces I am a member of: name, kind, files, size vs quota, last used, expiry date, "Öppna i chatten" (`Clients.PortalUrl`) |
| Ny kunskapsbas | one step: name, description, personal or team (group picker: groups from the caller's claim; search by name with the P2 Graph lookup), confirmation "inget sekretessbelagt eller känsligt", then the file drop zone |
| Kunskapsbas | file list with live state (poll 2 s while processing), facts and warnings per file, replace, delete, restore within 14 days; usage vs quota; members and managers; rename; delete workspace |

| Method | Path | Purpose |
|---|---|---|
| `GET`, `POST` | `/api/me/workspaces` | list; create (collection + source + assistant + audience rows in one transaction) |
| `GET`, `PATCH`, `DELETE` | `/api/me/workspaces/{id}` | detail; rename, description, managers, group; delete (managers) |
| `POST` | `/api/me/workspaces/{id}/upload-batches`, `…/files`, `…/commit` | the connectors §9.1 flow behind a quota check (§5.2) |
| `GET` | `/api/me/workspaces/{id}/files` | files with state and facts |

A workspace id outside the caller's membership is 404, as elsewhere. Team members may upload and delete files;
managers also rename, change the group and delete the workspace.

## 5. Upload, processing and quotas

### 5.1 Interactive priority

Goal: a 30-page text-layer PDF is searchable in **about a minute** (p50 ≤ 60 s, p95 ≤ 2 min, *to measure*) while a
nightly crawl runs. Today the dispatcher orders by class, then `Priority` and age (engine §9.3); per-department
fairness, batched dequeue and coalesced `NOTIFY` are P2; the parser has a preview-first gate (engine §9.5). Smallest
mechanism, no new lane or class:

1. Workspace upload syncs are enqueued with `Priority = Settings.Workspaces.InteractivePriority` (20); continuation
   and child jobs (`fetch`, `parse`, `embed`, `llm`) **inherit the parent's priority** (one rule in the enqueue helper).
   Only the first `InteractiveMaxPages` (300) pages of a batch get it; the rest run at 0, bulk loads stay at −5.
2. **One over-commit slot per class** for jobs with `Priority ≥ 20` (`Worker:Concurrency:interactive`, default 1), so an
   interactive job is dequeued even when its class is full (the pilot parser has one slot, held by a crawl's parse
   job for up to a 10-minute slice).
3. At the parser the existing **priority gate** admits interactive jobs right after previews, so they wait at most one
   range of the running job (≤ 60 s).
4. When P2 fairness arrives, interactive jobs are tried **before** the per-department loop (one extra indexed query).

Scanned PDFs take longer (OCR ≈ 4 s per page per slot, parser §14: ≈ 2 min for 30 pages); the page shows an
estimate. Searchability never waits for the summary (§6.1).

### 5.2 Quotas (proposed, `Settings.Workspaces`, platform admins may override per workspace)

| Limit | Personal | Team |
|---|---|---|
| Workspaces | 10 per person | 20 per group |
| Files per workspace | 200 | 500 |
| Size per workspace / total | 500 MB / 1 GB per person | 2 GB / 5 GB per group |
| Chunks per workspace | 10,000 (≈ 6,000 PDF pages, *estimate*; keeps the exact-scan path) | 10,000 |
| Per file | the upload limits: ≤ 50 MB, ≤ 100 files and 500 MB per batch; ≤ 500 pages (bounds summary cost) | same |

Files and bytes are checked by the admin API at batch create and per file (`Uploads` + the pending batch); chunks by
the worker at index (over quota → file outcome `refused` `workspace.quota_chunks`, nothing written). Usage counters
live in `content` (§8), never in `config`, so uploads do not bump the config version.

## 6. Whole-document tasks

### 6.1 Per-document summaries (on-prem, at indexing)

- A P2 stage **`summarise`** after `index` in the `files` template, switched on by the parameter `summaries` (off for
  department collections, on for workspaces); it is the planned "LLM summary" P2 stage. It runs in a new job class
  **`llm`** (concurrency 2), so summaries never hold `embed` slots and a file is searchable before its summary exists.
- Model `Settings.Workspaces.SummaryModel` (default: the gateway's on-prem general chat model), always with the
  **explicit ceiling `OnPrem`** whatever the level (stricter than engine §7.7's `Eu` for P2 LLM stages of `internal`
  documents; the user decided "on-prem"), on behalf of the workspace's team, `x-ume-step: summarise`.
- Output per chunk set: a **short** summary (≤ 60 words, for the overview) and a **long** one (≤ 400 words: purpose,
  main points, dates, amounts, decisions, sections), in Swedish, untrusted-text framing as for sources. Documents above
  the model's input budget are summarised map-reduce over sections (≈ 8k tokens each) and merged.
- Stored with the chunk set (`content."DocumentSummaries"`, §8): swapped, rolled back and purged with it; cached as a
  stage output by input hash, so re-uploading identical bytes costs nothing. Failure or budget refusal → retried with
  back-off; the file shows "sammanfattning saknas" and the overview skips it.

### 6.2 Files overview and full text

- **Files overview**: the short summaries of all live documents of the workspace, in file-name order, built at query
  time (no stored artefact), capped at 6,000 tokens; above the cap the 40 most recently changed files and a note.
- **Full text**: each document rebuilt from its chunks in order with overlap stripped (as section expansion does,
  knowledge design §9), with page markers. `TextTokens` per chunk set is stored at index time.

### 6.3 Answer modes (deterministic, no agent loop)

The chat role picks one mode per question, in this order, and records it on the message (`AnswerMode`, metric
`rag.chat.answer_mode`). Every mode is one model call; misroutes degrade gracefully because modes 2–3 also carry
passages.

| # | Mode | Chosen when | Prompt sources | Not-found gate |
|---|---|---|---|---|
| 1 | `full-text` | workspace `TextTokens` ≤ min(`FullTextMaxTokens` 24,000, `FullTextShare` 0.5 × the answer model's `GatewayModels.ContextWindow`) | every document in full, stable order (provider prompt caching can reuse the prefix across turns) | no (the frame tells the model to say when the files do not answer) |
| 2 | `documents` | the question names 1–3 files (normalised file-name stem or title match, ≥ 4 characters); covers "sammanfatta X", "jämför X och Y" | long summaries of the named files + passages filtered to them (top 5 each) | no |
| 3 | `overview` | whole-set wording from a fixed sv/en list (`sammanfatta`, `översikt`, `alla filer`, `vad handlar`, `jämför`, `skillnad`, `summarize`, `overview`, `compare`…; `Settings.Workspaces.OverviewTerms`) | files overview + top 5 passages | no |
| 4 | `passages` | otherwise | standard RAG (condense, retrieve top 10, section expansion) | yes, as today |

Workspace assistants use `Settings.Workspaces.AnswerModel` with `answer.contextTokens` 32,768 by default; in modes 1–3
the sources budget is the mode's budget instead of chat design §6.3's 8,000 cap, history keeps its 4,000. Effective
level = max(`internal`, client ceiling) = `internal` → ceiling `Eu`, so full text may reach an EU model, as any
`internal` passage does. **Citations**: passages as today (page links); full text and summaries cite the document
(label "Sammanfattning av *fil*" for summary sources, download-grant link), not the page.

## 7. Lifecycle

| Event | Rule (proposed) |
|---|---|
| Activity | latest of chat use (`chat."AssistantDailyTotals"`), upload, or change in the area (audit); no stored `LastUsedAt` in `config` |
| Expiry | **180 days** without activity → deleted; warnings at 30 and 7 days shown in the area and on the Översikt page (an e-mail channel is question 7); any activity resets it |
| Owner leaves (personal) | weekly Graph check of owners inactive ≥ 30 days (`accountEnabled = false` or 404; needs an application permission in package 2) → state `owner-left`, assistant hidden, **deleted after 30 days**; without the permission, expiry covers leavers |
| Owner leaves (team) | workspace persists; if no manager remains, any group member may take over (audited); group deleted → frozen, deleted after 30 days unless a platform admin transfers it |
| Delete (manager, expiry, owner-left) | one transaction: assistant deactivated (gone from `/v1/models` at the next config version), collection and source soft-deleted, a `command` job soft-deletes documents; chunk sets and summaries purged after 14 days (restorable until then), upload blobs tombstoned and purged after `BlobPurgeDelayDays` (35, = backup retention), so no copy outlives 35 days; conversations keep their own retention |

## 8. Data model sketch (columns only)

| Table | Change |
|---|---|
| `config."Collections"` | `Kind` (`department`/`workspace`), `OwnerKind` (`person`/`group`), `OwnerSubjectId varchar(64) NULL` (`oid`), `OwnerGroupValue varchar(256) NULL`, `Description`, `LifecycleState` (`active`/`owner-left`/`frozen`), `ExpiryWarnedAt`, `LifecycleChangedAt`, `QuotaOverride jsonb NULL`; written only on transitions, never per use |
| `config."WorkspaceManagers"` (new) | `CollectionId`, `SubjectId` (`oid`), `AddedBy`, `AddedAt` |
| `config."Sources"` | none (one `upload` row; the workspace pipeline row is a platform `Pipelines` row of template `files` with `summaries` on) |
| `config."Assistants"`, `AssistantVersions` | a workspace is a collection, so department assistants link it through the version's `CollectionIds` snapshot like any collection; `WorkspaceCollectionId uuid NULL` (marks the automatic assistant; not a second assistant type); `QuerySteps.workspace` (`fullTextMaxTokens`, `overview`) in its versions; instructions from a locked platform assistant template plus the owner's extra instructions; excluded from the 90-day "unused" attention item (§7 expiry applies) |
| `config."AssistantAudiences"`, `CollectionAudiences` | `Kind` gains `user` with a new `UserValue varchar(64)` (`oid`); check: exactly one of `SubjectId`/`GroupValue`/`UserValue` |
| `config."Settings"` | `Workspaces jsonb`: quotas, `ExpiryDays` 180, `WarnDays` [30, 7], `OwnerLeftGraceDays` 30, `TeamId`, `SummaryModel`, `AnswerModel`, `FullTextMaxTokens`, `FullTextShare`, `OverviewTerms`, `InteractivePriority` 20, `InteractiveMaxPages` 300 |
| `content."DocumentSummaries"` (new) | `ChunkSetId` PK (FK Cascade), `DocumentId`, `Classification`, `ShortText`, `LongText`, `Model`, `PromptVersion`, `CostUsd`, `CreatedAt` |
| `content."ChunkSets"` | `TextTokens int` (full-text budget) |
| `content."WorkspaceUsage"` (new, worker) | `CollectionId` PK, `Files`, `Bytes`, `Chunks`, `TextTokens`, `PendingFiles`, `UpdatedAt` |
| `content."Jobs"` | class `llm` in the check constraint |
| `chat."Messages"` | `AnswerMode varchar(20) NULL` |

## 9. Security events and audit

- Audit actions (admin design §9.2 scheme): `workspace.create|update|share|managers|delete|restore|takeover|transfer|quota`,
  `assistant.publish|restore` for the workspace assistant's versions,
  `upload.batch|delete|restore` on the workspace's source; system actor `system:workspaces` for expiry, owner-left and
  frozen transitions.
- New events in the existing ranges (identity design §10): **6008** `rag.workspace.shared` (admin, Information: group
  or user audience added or removed; `Actor`, `WorkspaceId`, `AudienceKind`, `Count`); **6009**
  `rag.workspace.admin_action` (admin, Warning: a platform admin opened files of, transferred or deleted someone else's
  workspace, with a stated reason; `Actor`, `WorkspaceId`, `Action`, `Reason`); **5106** `rag.workspace.quota_refused`
  (worker, Information, once a minute per workspace with `Count`). Denials stay 5002/5005 (`audience_mismatch`, 404).

## 10. Archive and data protection

Files uploaded to a workspace and the answers given over them may be **allmänna handlingar** (inkomna or upprättade),
and expiry or owner-left deletion may need a gallringsbeslut. This extends register **A1** (for chat
logs; the pilot runs history mode `none` until it is answered) to workspace files and summaries; until answered, P2a must not ship with automatic deletion switched on (question
8). Personal data: detection runs and is shown per file; a personnummer gives a warning in the area, not a block
(question 9). Open WebUI's own chat copies (register W2) apply to workspace chats too.

## 11. Capacity (estimates, *to measure*)

| Item | Estimate |
|---|---|
| Summary per 30-page PDF | ≈ 18k input + ≈ 600 output tokens on-prem; ≈ 10–30 s per file; one L4/A10-class GPU ≈ 1,000–2,000 files/hour (prefill-bound) |
| Pilot-scale P2a | 100 users × 20 files = 2,000 files ≈ 36M input tokens once, then only changed files |
| Embedding | ≈ 600 tokens per PDF page (vs 3,600 per web page, engine §18): a 30-page PDF ≈ 2 s at 10k tokens/s |
| Full-text turn | ≤ 24k prompt tokens per message vs ≈ 4–9k in passage mode; the answer-model prefill line of [performance pass](../passes/performance-pass.md) §2.2 grows accordingly; per-user rate limit `MessagesPerMinutePerUser` (20) unchanged |
| Blobs | 1 GB per person cap; 1,000 active users at ≈ 100 MB ≈ 100 GB uploads plus parse outputs; backed up within 15 minutes as all uploads |
| Config churn | creates, shares and deletes are config writes (serialised, data-model §5); fine at a few per minute, snapshot reload per entity type to measure with ~5,000 assistants |

## 12. Alternatives considered

- **Open WebUI's built-in Knowledge** (per-user knowledge bases with grants, full-context mode): rejected. It bypasses
  Swedish search (`simple` config), classification and client ceilings, residency checks, personal-data detection, our
  audit and retention, and stores a second copy of every file and chunk in Open WebUI (`../../../platforms/open-webui.md`
  §3, §5, §9). It stays switched off.
- **Eneo's personal and shared spaces** (`../../../platforms/eneo.md` §6): the same idea (personal space per user, shared
  spaces with user/group members), but everything is scoped to a space and loaded whole per message. We copy the
  ownership idea, not the scoping. NotebookLM-style notebooks are the reference for whole-document tasks.
- **Chat attachments (C1, chat design §15.2)** stay for one-off use: chat-scoped, conversation retention, no
  summaries. A later "Spara i kunskapsbas" action could copy an attachment into a workspace (needs the original file,
  which attachments do not keep: the client re-uploads it).
- **A separate service or schema for workspaces**: rejected; the collection, source and assistant rows already carry
  everything but ownership and lifecycle.

## 13. Milestones

| Phase | Deliverable |
|---|---|
| P2a (gates: E1 passed, the Open WebUI model-registry issue solved, A1 answered for workspaces) | personal and team workspaces, "Mina kunskapsbaser", interactive priority, facts per file, `summarise` stage and `llm` class, the four answer modes, quotas, expiry and delete, events and audit, perf check of §5.1 in `perf.yml` |
| P2b | owner-left via Graph, takeover and transfer, e-mail warnings (if decided), sharing with named colleagues (if decided), `/v1/retrieve` and `/mcp` for members |
| Later | "Spara i kunskapsbas" from chat attachments, SharePoint/OneDrive file picker as a second source, Teams client (P3) |

## 14. Answered questions (user, 2026-10-30)

1. ~~Phase?~~ **Answered**: **P2**, after the pilot.
2. ~~Owners?~~ **Answered**: **teams and individuals**: personal workspaces and team workspaces shared with a team or
   Entra group.
3. ~~Where are they created and managed?~~ **Answered**: in a **small self-service area in our app**, Swedish
   "Mina kunskapsbaser", served by `rag-admin`'s BFF and sign-in, open to all staff (create, upload, status, share,
   delete). Chat in Open WebUI, where each workspace is a model to its members; other chat-API clients can use it too.
4. ~~Whole-document tasks?~~ **Answered**: **from the start**: per-document summaries made on-prem at indexing, a
   full-text mode when the whole workspace fits the answer model's context, passage Q&A with citations as usual.

## 15. Questions for the detail round

1. Share a personal workspace with **named colleagues** (audience rows of kind `user`), or only via groups?
2. **Attribution**: one central platform team and budget (proposal), or the owner's department team?
3. **Groups**: only groups in the creator's claim; a size cap (needs the Graph lookup)? Who may share with very large
   groups such as all staff?
4. **Open WebUI model registry**: upstream fix, a pinned patched build, or fewer per-user models (e.g. one model
   "Mina kunskapsbaser" plus a workspace choice)? Also blocks P2 department/group audiences in Open WebUI.
5. If **E1** fails: workspaces only in the own chat client, or a small chat page in the area?
6. Quotas and the 24k full-text threshold: accept the proposals?
7. **Warning channel** for expiry: in-app only, or e-mail through the municipal relay (needs an address lookup)?
8. **Archive (A1)**: are workspace files, summaries and answers allmänna handlingar; may expiry and owner-left deletion
   run without a gallringsbeslut?
9. Personnummer or other personal data found in a file: warn (proposal) or refuse?
10. **Feedback and review** of workspace conversations: owner sees counts only; may `rag-reviewer` read them at all?
11. Summary and answer models: which on-prem model summarises Swedish well enough; may the answer model be EU for
    full text?
12. **Department assistants linking a team workspace**: allowed only when a manager shares it with the department
    (proposal); do such assistants also get the whole-document modes, and who is told when the workspace is deleted?

## 16. To verify

| Item | When | How |
|---|---|---|
| Open WebUI process-wide `app.state.MODELS` with per-user lists (`utils/models.py:484-499`, `main.py:1153-1167`) at the P2 version | late P1 (P1b spike; `../../to-verify.md` #174) | two users with different lists, interleaved chats |
| E1: `system_oauth` token forwarding and refresh with our audience | late P1 (P1b spike; `../../to-verify.md` #175) | as identity design §9.4 |
| Interactive latency: 30-page text PDF searchable p50 ≤ 60 s during a crawl and an OCR batch | P2a | `perf.yml` scenario |
| Swedish summary quality of the on-prem model (20 municipal PDFs, judged by two staff) and its tokens/s | P2a | sample run |
| Config snapshot reload cost with ~5,000 assistants and frequent workspace writes | P2a | load test |
| Prompt-cache reuse of the full-text prefix at the gateway's providers | P2a | gateway usage headers (`CachedInputPerMillionUsd`) |
| Group-claim overage (> 200 groups) for team workspaces | P2a | with the Graph lookup |

## 17. Changes to other documents

Not applied yet (owners apply them in the detail round):

| Document | Change |
|---|---|
| `../../platform/data-model.md` §2.1–§2.3, §4.1, §8 | §8 columns and tables; `llm` class; retention rows for `DocumentSummaries` (with the chunk set) and workspace deletion |
| `../../ingestion-worker/design-engine.md` §9.1, §9.3, §9.5, §13.1, §7.7 | `llm` class; priority inheritance and the interactive over-commit slot; parser gate admits interactive jobs; `files` parameter `summaries`; `OnPrem` ceiling for `summarise` |
| `../../ingestion-worker/design-processing.md`, `design-connectors.md` §9 | `summarise` stage spec; quota checks in the upload flow; personal data recorded, not blocking, for workspaces |
| `../../knowledge-api/design.md` | full-document text and summaries for the chat role (`IRetriever`); workspaces in `GET /v1/collections` for members only |
| `../../chat-api/design.md` §6.3, §7, §9.3, §15.2 | answer modes and budgets; summary citations; `AnswerMode`; link to "Spara i kunskapsbas" |
| `../../identity/design.md` §4.3, §10, §12 | audience kind `user`; app role `rag-workspace-user` and policy `Workspaces`; events 5106, 6008, 6009; package 2: Graph permission for owner-left |
| `../../admin/design.md` §3, §5, §9.2 | "Mina kunskapsbaser" pages and routes incl. the simplified draft/publish form; department pages filter `Kind = department` and show shared team workspaces as linkable; audit actions |
| `../../postponed-decisions.md` | A1 extended to workspace files; new item for the Open WebUI model registry (question 4) |
| `../passes/performance-pass.md` §2 | summary and full-text rows |
| `../../README.md`, `../../../01-general-idea.md` §10 | P2 scope line; a detail-round Decisions row when §15 is answered |

**Conflicts found in the existing design**: (1) Open WebUI users match only `client` audiences until E1, so
workspaces in Open WebUI depend on E1; (2) Open WebUI's shared model registry breaks per-user model lists; (3) the
admin app requires a `rag-*` role and *assignment required*, so "open to all staff" needs a new role; (4) config
writes were assumed rare (data-model §5), and workspaces make them user-driven; (5) engine §7.7 sends P2 LLM stages of
`internal` documents at `Eu`, while workspace summaries are `OnPrem` by decision; (6) chat design §6.3 caps sources at
8,000 tokens, below the full-text budget; (7) `CollectionShares` are written by the owning department's `Manage`
(admin design §3.2), while a team workspace is shared by its managers, who hold no department role; (8) assistant
versioning assumes a test panel and test-question set on publish, which the workspace
assistant skips.
