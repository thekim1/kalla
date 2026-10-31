# Handover

State of the Ume.Rag work for the next session. Updated 2026-10-31, session 9.
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's existing **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the
gateway and wants to do better than **Eneo** (`~/git/eneo`), which they find slow and a "kitchen sink" monolith.

This repo holds the design and, from P0, the code (decided session 5: no separate `ume-rag` repo). The design is **complete**: ten detail steps, a consistency pass, a performance pass
and (session 4) a **simplification pass**, all reviewed and pushed. Session 5 split P0 into **Stage 0 + two parallel streams** (`plans/p0/`) and added the
agent harness (`AGENTS.md`, `CLAUDE.md`, `.github/stream-ownership.txt`, PR template). Session 6 built **Stage 0**
(PR #1, `84dedbe`), ran **stream A** through local subagents and reviewed **stream B** (a remote agent on another
machine) over GitHub; the sync point after H2 is done. Session 7 merged the worker, ClamAV, Compose (part 1) and
blob-store PRs, fixed the red `main` (#29), and opened PRs for every remaining stream A item before the release
(Entra, capacity scripts, backups/restore, docs). **Next: see "Next step" at the end** (merge the open PRs in
order, open H7/H8, then the integration sync point).

Plain-language overview for stakeholders: `docs/stakeholder-report.html` (open in a browser; keep it in step with the
design), also published as a private artifact (share from its menu): https://claude.ai/artifact/GzKzra4RwKdf7xEWFc4EAM

## Reading order

1. `01-general-idea.md` §10 **Decisions table**: authoritative; later rows win over earlier ones. Then
   `plans/weakness-review.md` (session 5: five fixes, binding over the simplification pass and the detail documents). The rows from "Simplification
   pass" on (site profiles, visual builder, fewer containers, workspaces, many assistants) override much of what
   comes before. Sections 3–9 are the original idea.
2. `plans/simplification-pass.md`: the ten review changes in decided form, §11a conflicts settled during the
   rewrite, §13 umea.se findings and learned site profiles, §14 six pilot containers, §15 many assistants per department. Read
   this before any detail document.
3. `plans/README.md`: plan folders, phases P0–P3, links to the registers and passes.
4. `plans/architecture.md`: Mermaid diagrams (system overview, chat request, ingestion, agent tool call).
5. The detail documents for the area being worked on (map below). Each ends with "Changes to other documents",
   answered questions, "To verify" and "Deferred or removed (simplification pass)".

## Document map

| Area | First-level plan | Detail documents |
|---|---|---|
| Platform | `plans/platform/plan.md` (deployables, zones, residency matrix, data protection §8a) | `data-model.md` (schemas, roles and grants, `SearchText` + built-in `swedish`, per-source documents and chunk sets, unpartitioned `Jobs`, outbox, bucket keys, site profiles, `AssistantVersions` and test questions, `AdminSessions`, retention, blobs, Redis from P2); `repo-and-deployment.md` (repo layout, build, Aspire, FakeGateway, stock pgvector image, pgBackRest, Compose, networks, secrets, settings rule §3, CI, **P0 checklist §11**) |
| Identity | `plans/identity/plan.md` | `design.md` (BFF copied from the gateway, RAG's own Entra registrations, member groups from claims (Graph lookup P2), `RagPrincipal`, `IAccessPolicy`, explain access, Open WebUI JWT trust, keys, per-level gateway keys and explicit ceiling, security events, Keycloak realm) |
| Gateway changes | `plans/gateway-changes/plan.md` | `design.md` (one combined release: residency ceiling + `RequireMaxResidency`, delegation with key-only request policy, claims-only member groups, 503 vs 403, `/v1/models` filtered by the ceiling; P2 rerank endpoint), `adr-drafts.md` (018–020, accepted together) |
| Parser | `plans/parser/plan.md` | `design.md` (Docling sidecar, every PDF incl. the pypdfium2 text-layer path, Tesseract `swe` only, `/v1/inspect`, gated `/v1/ner`, `format_deferred` for legacy Office/RTF/email) |
| Ingestion | `plans/ingestion-worker/plan.md` | `design-engine.md` (fixed linear pipelines, per-stage cache keys, jobs, scheduler, publish = plan → re-process → report → keep/roll back, index writer with per-document swap); `design-connectors.md` (URL list, crawl, shared frontier, sitemap `lastmod`, learned site profiles §5.6, egress guard, uploads by slot, ClamAV, deletion guard); `design-processing.md` (HTML extraction by site profile, chunking with breadcrumbs, personal data on chunk sets, advisory evaluation) |
| Knowledge API | `plans/knowledge-api/plan.md` | `design.md` (`/v1/retrieve`, hybrid search in three statements, lexeme OR query, scalar `CollectionId`, coverage needing vector and keyword weak, `IReranker` seam, citations, download grants) |
| Chat API | `plans/chat-api/plan.md` | `design.md` (OpenAI-compatible API, Open WebUI without task generation, condense skip + 600 ms timeout, residency checks before the first byte, same level every turn, serves the published assistant version, bucket keys, feedback import via HTTP API) |
| Tools (P2) | `plans/tools/plan.md` | `design.md` (in-process tool hub, `hosted` servers only, `ume-mcp-kommun`, skills with served pointer + rollback, static small skills, sharing, third-party text, drift pre-registration, `/mcp` early) |
| Admin | `plans/admin/plan.md` | `design.md` (BFF + Swedish journey pages, generic forms for platform objects, test panel, nightly answer judge and skill gate in `rag-admin`, serialised config writes, assistant drafts/publish/versions/test questions/copy/templates, department map, cross-rules V1–V34 with V20/V27 retired (32 rules), site-profile pages; owner-team scoping P2) |
| Workspaces (P2) | `plans/workspaces/plan.md` (ask-a-file: personal and team knowledge bases from uploaded files, "Mina kunskapsbaser" in `rag-admin`, interactive priority, on-prem summaries, four answer modes, quotas, lifecycle; 12 questions for the detail round) | – (detail round next) |
| Cross-cutting | – | `plans/simplification-pass.md` (§14 six containers, §15 many assistants per department); `plans/consistency-pass.md`; `plans/performance-pass.md` (capacity model incl. umea.se numbers, triggers); `plans/postponed-decisions.md` (register); `plans/to-verify.md` (≈ 167 checks by topic and phase) |

Background: `02-architecture-review.md` (R1–R7; R2 "shared document store" is now sharing by cache, not by row),
`platforms/` (Eneo, Dify, RAGFlow, Onyx, Open WebUI, Kernel Memory, OpenSearch, Bifrost; synthesis
`00-comparison.md`). Reference clones: `~/git/llmgateway`, `~/git/eneo`,
`~/git/rag-platforms/{dify,ragflow,onyx,open-webui,kernel-memory,bifrost}`.

## Key decisions (full list: Decisions table)

- **Pilot containers (6)**: `rag-api`, `rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`, `postgres` (pgBackRest
  inside the image); no Redis until the first multi-host deployment (P2): admin sessions in Postgres, rate limits and
  the query-embedding cache in process (`simplification-pass.md` §14).
- **Assistants**: one type (agent loop only when a linked skill carries tools), knowledge only through directly linked
  collections (skills carry know-how and tools), versioned from P1 (draft, test questions, publish, history, restore);
  owner-team edit scoping in P2. Many assistants per department share knowledge banks.
- **Services**: `Ume.Rag.Api` (roles chat, knowledge, mcp), `Ume.Rag.AdminApi` + Swedish admin UI (also hosts the
  nightly answer judge and, in P2, the skill gate), ingestion worker (`Worker:Classes` per instance; retrieval
  evaluation only, no tools), Python Docling parser (all PDFs, OCR, gated NER), ClamAV; renderer only in P2 if a
  source needs JavaScript; tool hub in-process in P2. Postgres (stock pgvector + pgBackRest), and Redis from P2, separate
  from the gateway.
- **Ingestion**: documents and chunk sets per source, reuse by content-addressed caches; fixed linear pipelines
  (template + parameters), no canvas; **learned site profiles** per host (Sitevision preset) set the HTML stages,
  auto-applied with guardrails; sitemap `lastmod` incremental runs; advisory evaluation, no gates.
- **Gateway**: every model call goes through it, with a per-level delegating key and an explicit
  `x-ume-max-residency`; gateway release 1 before the pilot, release 2 in P2; embeddings EU-hosted through the gateway until on-prem
  hardware exists (register M2), then on-prem BGE-M3 (re-embeds every chunk).
- **Classification**: `public` / `internal` / `sensitive`; residency public any, internal EU or on-prem, sensitive
  on-prem; `sensitive` isolation = a separate deployment when needed (no tier machinery).
- **Data protection**: chat content encrypted under (level, expiry month) bucket keys; destroying bucket keys waits
  for A1; pgBackRest PITR, write-once blobs, escrowed long-lived keys, `public` blobs unencrypted.
- **Pilot (P1)**: URL lists, umea.se crawl (≈ 14,100 HTML + PDF URLs; source `maxPages` ≥ 20,000), uploads;
  HTML, PDF, modern Office, ODF, images, text; RAG assistants in Open WebUI for one department; feedback; limited
  review by `rag-reviewer`.
- **Settings rule**: deployment knobs are `Section:Key`; run-time values are `Settings.<Section>.<Key>`.

## How we work (user preferences)

- **Ask before each level of detail**, with the question tool: up to 4 questions per round, recommended option
  first. Record every answer in the relevant document ("Answered") and in the Decisions table.
- **Verify a premise before putting it in a question** (and in a spec): in session 4 the Open WebUI feedback import
  turned out to carry no chat snapshots, which changed change 5 before it was briefed.
- **One subagent writes one document from a precise brief and does not commit.** The main session reviews, checks
  claims against the gateway and real package source, fixes problems and asks the user only what they must decide.
  A second subagent round carries the "Changes to other documents" rows into the other files.
- Wide passes run several subagents in parallel, each owning separate files and reporting cross-document issues
  instead of editing them; the main session settles conflicts in a binding follow-up brief (session 4:
  `simplification-pass.md` §11a). A Workflow needs the user's explicit opt-in.
- **Stay compatible with the gateway**: copy its conventions, files, claims, config keys, key format and
  security-event scheme; when something is missing, add it to both systems (`gateway-changes`).
- **Check library and site claims against real sources**: packages downloaded into the scratchpad and read, never
  executed; real pages fetched with `curl` (session 4: umea.se). Running external npm tools needs the user's
  permission (mermaid-cli was refused by the permission classifier).
- A formatter in the user's editor sometimes realigns markdown tables: **re-read a file before editing it**.
- Commit at the end of each reviewed step, then push to `origin main`.
- The global agent-memory instructions (`.agent/`) do not apply: this repo has no `.agent/` directory.
- **Running P0 (session 6):**
  - The main session runs **stream A** by delegating one item (or half an item) at a time to an `implementor`
    subagent in its own worktree (`../rag-aN`, branch `a/<item>-<slug>` from `origin/main`). The `implementor` agent
    type **cannot commit**: it leaves work uncommitted and reports; the main session re-runs its proofs (build, tests,
    image tests), reviews the diff, then commits, pushes and opens the PR (template filled in).
  - **Stream B is a remote agent on another machine** (GitHub user `thekim1`). Talk to it **only through GitHub**:
    issues labelled `stream-b` (requests to it), `stream-a`, `main`, and PR reviews. **Check GitHub once before
    starting each new piece of work** (open PRs, issues, recent comments); no continuous polling (too frequent).
  - The user authorised the main session to **review stream B's PRs and answer its issues autonomously** (post the
    review as a PR comment; open an issue for a bug, with evidence). Stream B merges its own PRs.
  - **Merging stream A's PRs needs the user's explicit go-ahead** ("merge #N"): the permission classifier blocks
    unapproved merges. **Force-push is blocked too**: to update a branch with `main`, merge `origin/main` into it and
    push normally (a PR with a merge conflict gets no CI run at all).
  - Repo rule: shared files (`Directory.Packages.props`, `UmeRag.slnx`, …) go in a small PR of their own, merged first.
- **Learned in session 7:**
  - The user wants **parallel work**: up to three stream A agents at once, each in its own worktree (`../rag-aN`), on
    items that touch different paths. Brief each one fully (it sees nothing else), and require it to re-run its proofs
    and give the PR body.
  - **`plans/p0/results-a.md` conflicts on every concurrent PR** (all append rows at the end). Resolve by merging
    `origin/main` into the branch and keeping both blocks (`main`'s rows first). A conflicting PR gets no CI at all,
    so check `mergeable` when a PR shows no checks.
  - `gh pr checks --watch` can return before the checks register; wait about 90 s first, or watch twice.
  - Merges stay one explicit "merge #N" per PR, even when the user says "keep working autonomously". "Merge when
    green" for a named PR counts as that go-ahead.
  - Closing a `stream-b` issue needed the user's explicit go-ahead (the classifier refused it once).
  - `pkill -f <pattern>` in a Bash call can match its own shell and kill the call; use `pgrep` and a PID instead.
  - The implementor agent verifies well when the brief asks for a **negative proof** (the test fails without the
    change). Every agent this session found at least one real problem that way or by measuring:
    GC hex values, `File.Move` races, default-route egress, step order.
- **Learned in session 8:**
  - A PR's checks compare against the base commit recorded when the push happened. A stacked PR pushed *before* its
    retarget to `main` keeps the old base, so `ownership` blames it for files from `main` (#46). A re-run reuses that
    base and doesn't help; a new push (or close + reopen) does. Retarget first, then push.
  - Merging `origin/main` into a branch that carries an unsquashed copy of a squash-merged PR can duplicate a whole
    YAML job without a conflict marker (PyYAML accepts duplicate keys). Diff the resolved file against both sides.
  - The permission classifier reads a merge go-ahead per PR: "merge #49" did not cover retargeting and merging #50.
  - Don't `git stash` in the main clone while agents edit files in it.

- **Learned in session 9:**
  - Branch names can't start with `main/` (a ref `refs/heads/main` exists); the main session's PR branches use a
    plain name (`p1-ownership`).
  - `Agent` with `isolation: "worktree"` makes its own worktree; when the brief names a prepared worktree, leave
    `isolation` unset.
  - The classifier refused merging #57 even though the user had said "do 3" (merge the ownership rules): it needs
    "merge #N" literally.

## Progress

| Step | Document | Commit |
|---|---|---|
| Research, general idea, review, first-level plans | `01-general-idea.md`, `02-architecture-review.md`, `platforms/`, `plans/*/plan.md` | `8160fcd` |
| 1–10. Detail steps | `plans/*/design*.md`, `data-model.md`, `repo-and-deployment.md` | `d889cd9` … `ec29ea9` |
| 11. Consistency pass + to-verify register | `plans/consistency-pass.md`, `plans/to-verify.md` | `55b6113` |
| Architecture diagrams | `plans/architecture.md` | `74f8b61` |
| Performance pass | `plans/performance-pass.md` | `3b79baa` |
| Overall review → simplification pass (10 changes) + umea.se site profiles | `plans/simplification-pass.md`, every plan document | `6f0602f` |
| Six pilot containers; many assistants per department (§15); ask-a-file workspaces plan | `simplification-pass.md` §14–§15, `plans/workspaces/plan.md` | `0e91d81`, `00e531c` |
| P0 split into Stage 0 + streams A/B; agent harness | `plans/p0/`, `AGENTS.md`, `CLAUDE.md`, `.github/` | (this session) |
| Weakness review: five fixes (P1a/P1b, gold set + go-live gate, freshness, history mode `none` + DPIA, gateway release split + P0 capacity) | `plans/weakness-review.md`, every plan document | `f9a12b2` … `abbc3d7` |
| Progress files (checkboxes) and stream prompts | `plans/progress.md`, `plans/p0/progress-*.md`, `plans/p0/prompts/` | (this session) |
| P0 Stage 0: solution, health, AppHost, TestKit, `verify.yml` + ownership check, labels | `plans/p0/progress-stage-0.md` | `84dedbe` (PR #1) |
| P0 stream A: Postgres image (#5), parser image + contract (#12/#13), CI | `plans/p0/progress-a.md`, `results-a.md` | `f3b82ce` (#5), `95c2ad0` (#9), `6c3035a` (#16) |
| P0 stream B (remote): schema + roles (H2), decompounder, config-version library, keys, FakeGateway + ceiling handler | `plans/p0/progress-b.md`, `results-b.md` | `8583b1b` (#7), `f6a3cf0` (#8), `dfd9743` (#13), `4520f53` (#17), `1a79a69` (#18) |
| Sync point after H2: results folded into `to-verify.md`, deviations settled in the design; decisions recorded | `to-verify.md`, `data-model.md`, `repo-and-deployment.md`, `parser/design.md`, Decisions table | `2bcf459`, `f558e9c`, `0e03ac7`, `c85a023`, `487154b` |
| P0 session 7: worker follow-up (#30, fixes #29), ClamAV (#26), dispatcher (#20), Compose part 1 (#31), blob store (#33); design fixes (GC `0x4B`, `query-embed`, audit `Service`) | `plans/p0/progress-a.md`, `results-a.md`, `repo-and-deployment.md`, `performance-pass.md`, `admin/design.md` | `d59f435`, `7155d09`, `0ae6a4c`, `adbf1a9`, `61c08f1`, `4656dc4`, `53370e2`, `bce6817` |
| P0 session 7: Entra Bicep (#36), capacity scripts (#37), Compose part 2 + backups (#40), docs (#42); P1a plan | `plans/p1/`, Decisions table | `a9590dd` … `4ace26b` |
| P0 session 8: #49, #50 merged; integration sync point for stream A (register, deviations in the design, README/CHANGELOG, stakeholder report) | `to-verify.md`, detail documents, Decisions table "P0 integration, stream A" | (this session) |
| P0 session 9: #56 answered; stream B's deviations settled (P0 integration review done); identity design reworked for the Keycloak pilot; PRs #57 (P1 ownership), #58 (runbook), #60 (release) | Decisions table "P0 integration, stream B", `identity/design.md`, `progress.md` | `edc85b6`, `cd5c3e5` |
| **P0 ends**: merge #57, #58, #60; #27; dry run; `v0.1.0` (= J1) | below | **next** |

## Open items

**Waiting on people** (`plans/postponed-decisions.md`):
- **A1** archive law: are chat logs allmänna handlingar, is a gallringsbeslut needed? No longer a pilot blocker (pilot
  runs history mode `none`, weakness review §4); gates stored chat content; owner
  to be named. Also gates destroying chat bucket keys.
- **G1** DPIA with the data protection officer: starts in P0, finished before go-live; covers Open WebUI's store as the
  primary copy and decides W2 (weakness review §4).
- **M2** on-prem model capacity: decided before P1 if the P0 measurement (#22) falls short (weakness review §5).
- Every phase-blocking item needs a named person and a date before the phase starts; P0 ends only when A1, G1, W1,
  W2, N1, U1 and P2 have both.
- **W2** Open WebUI chat copies: v0.12.5 cannot prune other users' chats without `ENABLE_ADMIN_CHAT_ACCESS`; decide
  with A1 before the pilot.
- **W1** existing AI chat on umea.se (`chat.umea.se`, `ume-app-aichat-prod`): what it is and how it overlaps.
- **N1** product name, before the pilot.
- **U1** crawler about page and robots.txt check with the web editors, before the first production crawl.
- **P2** licences of models, OCR data, dictionaries and the `sv_SE` word list, before the pilot.
- **D1** Postgres streaming standby, before production beyond the pilot.
- **R1** self-hosted GitHub runner; **S2** object store server (P2).
- **W3** Open WebUI per-user model lists: one process-wide model registry in v0.12.5 breaks per-user lists; gates
  the first P2 group/user audience in Open WebUI (workspaces included).
- **C2** visual pipeline builder: P3, built only when its trigger fires (editors need flows that template
  parameters cannot express).

**Measured or checked during implementation**: `plans/to-verify.md` (rows up to #167), including the decompounder
against Postgres `ispell`, the stock pgvector image tag, pgBackRest with that image, the umea.se site profile and
acceptance page, the cost of the per-document swap, pool 12 / 4 searches, the Postgres admin session store, and the
assistant test-run cost.

## Next step: P0 ends (start of session 10)

Start with `plans/p0/prompts/main-session.md` and this section. First check GitHub (open PRs, issues, comments).

**Done in session 9:**
- #56 answered: H6 and H8 don't block J1 (test-host deploy and drill are P1b since 2026-10-30); stream B may start
  P1a item 2 (schema part 1) as a **draft PR merged only after `v0.1.0`**.
- Stream B's design deviations (#23–#25, #32, #35, #38, #39, #45, #46, #53–#55) settled in the design (`edc85b6`);
  no new `results-b.md` rows. Integration review and stream B ticked in `plans/progress.md`.
- `identity/design.md` reworked for the shared-Keycloak pilot, Entra labelled P2 (`cd5c3e5`). It added **nine open
  questions** (§16 "Open for P1": realm administrator, how RAG's additions are imported, realm-wide settings in whose
  file, OTP scope, Open WebUI sign-in gate, account lifecycle, identity continuity on LDAP/Entra, token-caller parts,
  E1 provider) and §14 rows 11–18 (changes other documents need: plan, admin, chat-api §4.3, repo-and-deployment §4.2/§7.1,
  data-model §2.3, to-verify, gateway session prompt §2).
- PRs open, all green or running, **waiting on the user's "merge #N"**: #57 (P1 ownership rules, part of J1), #58
  (`docs/runbook.md`, item 10; the done-when "reviewed by the operations owner" is the user's), #60 (`release.yml`,
  bundle, smoke test, SBOMs; item 11 minus the tag; H5 evidence in its body). #58 and #60 both append to
  `results-a.md`/`progress-a.md`: merge `origin/main` into the second one (keep both blocks).
- #59 to stream B: copy the gateway's `PublishAdminUi` (release.yml copies `dist` into `wwwroot` meanwhile).

**Next:**
1. Merge #57, #58, #60 (user's go-ahead each). Tick H5 in `progress-a.md` from #60's evidence; remove worktrees
   `../rag-a14`, `../rag-a15`.
2. #27 (ClamAV `1.4.6_base`) on or after 2026-11-02 through an implementor.
3. Before the tag: decide #91 (Syft lists neither pgvector nor ClamAV itself; recommended: accept and say so in the
   release notes); `## 0.1.0` in `CHANGELOG.md`; reword `docs/production-setup.md` ("until v0.1.0 there is no release
   bundle") and `docs/upgrade-guide.md`; a `workflow_dispatch` dry run of `release.yml` on GitHub (untested there:
   ghcr push with `GITHUB_TOKEN`, gha cache, runner disk, freshclam from a runner). Then the user approves pushing
   tag `v0.1.0` → **J1**; tell stream B (#56) it may merge schema part 1.
4. Carry identity §14 rows 11–18 into the other documents (one subagent round); take §16's questions 1–4 and 6 to
   the gateway session prompt and the rest to the user.
5. Found by the runbook work: `deploy/compose.openwebui.yaml:85`, `:90` still name Entra (P1a item 18); **no script
   backs up Open WebUI's data directory**, although it is the primary copy (G1/W2); with the pilot settings no admin can
   export another user's chat at v0.12.3 (to-verify #173, runbook "Public-records requests"); the design cites Open WebUI
   v0.12.5 but the pin is v0.12.3. Also `chat-api/design.md` citations outside §4.3 still point at v0.12.5.
6. Unchanged from session 8: RAG's realm additions (stream B P1 item 16), `--oidc-authority`/Open WebUI OIDC pointed
   at the shared Keycloak (`init-deployment.sh` still shows Entra), `BlobTombstones` (#34).

**Gateway work** runs in a separate session in `~/git/llmgateway`, started from
`plans/gateway-changes/prompt-gateway-session.md`. It reports back with an issue labelled `main`. Not started yet.

**Still needed from the user:** "merge #57/#58/#60"; the operations-owner review of the runbook; start the gateway
session; the platform's licence (EUPL-1.2 suggested); names and dates for A1, G1, N1, U1, W2, P2 and the gold-set owner;
a browser check of the admin UI.

**Open issues:** #27 (ClamAV 1.4.6, stream A), #34 (`BlobTombstones`, B, P1), #41 (`rag-blobs` root-owned, A, P1), #47
(Keycloak pilot, B), #48 (H8), #51/#52 (licence notices), #56 (stream B's J1 question; answered), #59 (`PublishAdminUi`, B).

**Worktrees:** `../rag-a14` (#60), `../rag-a15` (#58); remove each with `git worktree remove` once merged. The Aspire CLI
is at `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).

**CI facts:** parser jobs (own `parser.yml`) take about 4.2 min when the parser changed and are skipped otherwise. The
hosted runner needs the "Free runner disk" step for the parser image. Testcontainers needs locally built images to carry
a registry part with a dot (`ume-rag.local/...`).
