# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-02, end of session 18 (last update after #332 and `2eba7de`). Earlier sessions are in the
git history (`git log -- HANDOVER.md`).
Repo `~/git/ume-rag-platform`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

## Where we are

- **The demo runs:** crawl → retrieve → cited answer in Open WebUI from the `turkos` assistant (`claude-haiku-5-5`)
  over the user's interim gateway at `Eu`; follow-up questions are condensed (`gpt-6-luna`, logged `condense
  Rewritten`). P0 released (`v0.1.0`); P1a code complete; P1b in progress, admin UI first (#278).
- **Admin UI is English** (B's #320) and covers the setup journey: knowledge bases with sources, preview, publish,
  test questions and quality, search settings; assistants with draft/test/versions/audience (incl. check access, #324);
  feedback, audit log, organisation, platform objects. Fixed in session 18 (#329): the Draft tab starts from the
  published version, department names on the start page, knowledge-base facts, run counts in words.
- **Search quality:** the first generated test set for `turkos` (100 questions, Opus writes, GPT-6.1 judges) gives
  **recall@10 0.91, MRR 0.70**. Analysing the misses found a **degraded HNSW index** (exact best 0.246 at rank 1, the
  index's best 0.454; `REINDEX` fixed it). Now the worker checks index recall against exact nightly at 03:00 and after
  large syncs and rebuilds below 0.95 (#330, first live check: recall 1.0); the worker holds `MAINTAIN` and migrations
  drop invalid `_ccnew` leftovers (#328); the dev database gets the HNSW defaults (#326). The 9 remaining misses
  (analysed 2026-11-02): 2 near-duplicate förskola pages; 1 question generated from a Finnish page (the Swedish page
  ranks 1); 2 answered by other pages that the label doesn't name (the 2050 goal, Word templates); **4 buried by fusion**:
  vector rank 1–2, absent from the keyword list, so RRF puts them at 16–21 behind pages that are mediocre in both lists.
  On-demand runs on the same set: hybrid 0.91/0.70, **vector only 0.96/0.79**, keyword weight 0.5 → 0.97/0.74,
  **0.25 → 0.98/0.77**, 0.1 → 0.97/0.81 (recall@10/MRR). Hybrid still wins on exact names (property designations,
  named places). Generated questions favour vectors; the human gold set decides (Weakness review 2).
- **Architecture plan** (`architecture_plans.md`, root, a proposal): Crotalus wrote it, the main session checked it
  against the code and rewrote it as v2 (`5d4387a`, `0525053`), then **Crotalus rewrote it again** (`2eba7de`, 17:25:
  "knowledge, assistants and models", 9 items, model roles, placements, quality). **The main session has not reviewed
  that version.** Item 3 (one guided assistant setup with three fixed presets) still **changes a decision** ("Weakness
  review 1" put assistant templates in P2) and needs the user's yes or no.
- `main` = `635a5db`. `verify.yml` runs on pull requests and manual dispatch only, not on push: `main` has no run per
  merge by design (the listed `main` runs are manual). Every session-18 PR was green before merging.

## Decided in session 18 (user)

- **Admin UI in English**, Swedish later as a translation; every UI string in `labels.ts`/`messages.ts`, no i18n
  library yet (Decisions row, `724f1c7`).
- **Evaluation models at the collection's ceiling** (`6bfe67d`, #317): generation and the judge run at `Eu` under M2
  (`sensitive` stays `OnPrem`), no `temperature` (Claude refuses it). Local settings: `EvaluationModel`
  `claude-opus-5-5`, judge `gpt-6.1-sol`. Test questions skip documents detected as another language (#327; translations
  stay searchable: Umeå is in the Sami and Finnish administrative areas).
- **Organisation** (`5529c65`; reasoning and wireframes in `plans/admin/organisation-proposal.md`): departments own
  knowledge bases; sharing *only our department* / *selected departments* / *whole municipality*, read-only through
  assistants; topics = path-rule tags, chosen per assistant link (`CollectionLinks`); contact person on knowledge bases;
  teams hidden in create forms; the Map as a table in P1b; municipality-wide content owned by the department that runs
  it; the W3 spike now. New V35. P1b items 33–36 (B, #318), 37–38 (A, after L13/L14). **B hasn't started.**
- **Index health** (knowledge §6.4, data-model §3.2): the rules above, decided by the main session from the measurement.

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker Desktop must be up and its WSL integration connected: `docker info`). After merges:
  `dotnet build UmeRag.slnx`, then `aspire resource <r> restart` for `migrations`, `api`, `adminapi`, `worker`.
- Dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); admin API
  `https://localhost:7402`; api `https://localhost:7401`; Open WebUI = the `open-webui` resource's port (33747 after the
  last start; check `aspire describe`).
- **Pilot data:** department DoIT (hand-made, code `1000`); collection `turkos` (public, published,
  `coverageMaxDistance` 0.55); crawl source "umea.se" (whole host, `maxPages` 2,500, `**.pdf` excluded, no schedule;
  2,532 documents, 2,324 with content, 10,074 chunks); site profile www.umea.se; clients "Open WebUI", "Pilot retrieval
  (checks)", "Pilot grading"; `GatewayModels` (manual) `claude-opus-5-5`, `gpt-6.1-sol`, `claude-haiku-5-5`; assistant
  `turkos` v3; generated test set of 100 (2 of them from Finnish pages: generated questions can't be retired by hand).
- DoIT's code is `1000` (set by hand, session 18), the first of the dev users' codes (`dev/keycloak/ume-realm.json`:
  1000/3000/5000), so the start page names it; 3000 and 5000 show as unknown departments.
- **Logins:** Open WebUI and the admin UI's dev user are in `~/ume-rag-demo-login.txt` (mode 600). Never put passwords
  in a chat; point the user to the file.
- **Draft gold sets:** `~/ume-rag-gold-draft-{questions,answers}.csv` (40 questions, session 17) and
  `plans/p1/gold-set/` (199 rows, other machine's ids; match by node id `4.<id>`).
- **Crawl caution:** a source's scope change re-syncs it (trigger `config-change`); cancel with
  `POST /api/runs/{id}/cancel`. No more crawling at this stage (user).

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind). Newest: "Admin UI in English", "Evaluation
   models at the collection's ceiling", "Organisation: department-owned, municipality-shared knowledge bases with topics".
2. `plans/demo-path.md`; `plans/admin/organisation-proposal.md` (adopted).
3. `plans/p1b/README.md` (items 1–38, hand-offs L1–L14), progress `plans/p1b/progress-{a,b}.md`, `plans/progress.md`.
4. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). The passes don't bind.

## How we work (user preferences, binding)

- **The platform should be good and easy to use** (user, session 18): look at pages in the browser, not only tests;
  fix friction or file it.
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one; merge on green CI.
  Keep a proportionate adversarial review of B's security-relevant parts.
- **No tests that depend on timing or machine speed** (user): assert behaviour; "wait until X, fail after 60 s" is fine.
- **Approve and merge without asking** (user): PRs judged ready (reviewed, checked live where relevant, CI green); tell
  the user afterwards. Design, scope and destructive actions still go to the user. GitHub branch deletes are blocked by
  the auto-mode check.
- **Stream B** is a remote agent (GitHub `thekim1`); talk only through GitHub (labels `stream-b`, `stream-a`, `main`).
  B merges its own PRs, often without waiting for review. Nudging is allowed. `gh` acts as `ume-nichlas`.
- **Main session** owns design documents (`plans/**`, root `*.md`, `.github/stream-ownership.txt`): commit to `main`.
  Stream-A work: branches `a/<item>-<slug>`, PRs. When the user asks the main session to fix something in B's paths:
  claim the issue with a comment, use a `main-session/<slug>` branch, say so in the PR (#326, #328, #329).
- **Subagents:** own worktree (`../rag-aN`, `../rag-mN`), full brief, Docker-bound tests named; their report is a claim:
  check the diff and the CI logs (a green check can still have skipped the tests).
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): own small
  PR, append-only.
- Verify claims against real sources. Secrets (gateway address and key, API keys, passwords) never go into the repo, a
  log or a chat.

### Lessons

- **Admin API scripting** (recreate `adm.py` each session): `/bff/login` → Keycloak form (`rag-platform-admin`,
  password = AppHost user secret `Parameters:dev-user-password`, file has a UTF-8 BOM) → `form_post` to
  `/signin-oidc`; writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN` (cookie `RAG-XSRF-TOKEN`); send
  bodies as UTF-8 bytes. Screenshots: Playwright in the scratchpad, log in through the Keycloak form
  (`#username`, `#password`, `#kc-login`), cached Chromium `~/.cache/ms-playwright/chromium-1248`.
- **Chat through Open WebUI** for tests: `POST /api/v1/auths/signin` then `/api/chat/completions` (the api itself needs
  the Open WebUI user JWT).
- **The interim gateway:** Claude models refuse `temperature`; embeddings capped at 8,192 tokens per request; 403 for
  Python's default User-Agent; no cost reported; 429 on embeddings (requeued).
- **Retrieval checks:** `POST /api/collections/{id}/test-retrieval` (`topK`, `mode` Hybrid/Vector/Keyword); compare
  with an exact SQL ranking (`"Embedding" <=> query`) when a page is missing. Collections over 10,000 chunks use HNSW.
- The session scratchpad is wiped on restart (scripts, Playwright, `secretsid`).
- Read the *first* real failure; don't overlap two full Worker.Tests runs; flakes are often real.
- MTP: `--filter-class` values are OR'd. CI's shellcheck is 0.9.0. Compose changes: check every `compose config`.
- `psql -c "A; B"` is one transaction; `pgrep -f` matches its own command line.
- Admin UI tests: `npx vitest run --pool=threads`; a worktree needs `node_modules` (symlink, remove before commit).

## Code map (on `main`)

| Area | Where | State |
|---|---|---|
| Contracts | `src/Ume.Rag.Pipelines/`, `contracts/` | done; `check-hnsw-index` command added (#330) |
| Parser, connectors, processing, engine | `src/parser/`, `src/Ume.Rag.Worker/` | P1a complete, P1b items 4–8 done |
| Evaluation | `src/Ume.Rag.Worker/Evaluation/` | generation and judge at the collection's ceiling, no temperature (#317); no questions from other-language pages (#327) |
| Index health | `src/Ume.Rag.Worker/Engine/HnswIndexHealth.cs`, `HnswCheckScheduler.cs` | nightly + after-sync check, concurrent rebuild, audit `hnsw-reindex` (#330) |
| Admin API (B) | `src/Ume.Rag.AdminApi/` | item 9 done incl. pipeline publish/preview (#298, #299); setup checklist (#313); sources list carries documents and last run (#329) |
| Knowledge + chat (B) | `src/Ume.Rag.{Retrieval,Answering,Api}/` | chat with condense on the EU route (#293) |
| Admin UI | `src/admin-ui/` | English (#320, platform pages #332); journey pages, feedback, audit log, check access (B); platform objects `src/platform/` (A) |
| Migration service (B) | `src/Ume.Rag.MigrationService/` | dev HNSW defaults (#326); invalid rebuild indexes dropped (#328, in `RagDatabaseMigrator`) |
| Deploy | `deploy/` | worker `MAINTAIN` on `content."Chunks"` (#328) |

## Next step

1. **Check GitHub and the stack**: open PRs, issues; `docker info`, then `aspire describe` (start it as above if down).
2. **Review `architecture_plans.md` as of `2eba7de`** against the code and the Decisions table (check its claims as for
   v2: the Open WebUI JWT has no department codes; access settings live outside the assistant's versions), then ask the
   user which items to adopt; with a yes, write the Decisions row, the design rules and P1b items.
3. **Organisation (#318):** nudge B to start item 33 (schema → L13), or offer the user that the main session builds 33
   to unblock 34–36. Review each PR live. Stream A follows with 37–38 after L13/L14.
4. **Keyword weight (user decides):** lower `keywordWeight` on `turkos` (proposal 0.25) from the analysis above, and
   confirm it on the human gold set (item 21) before it becomes the template default.
5. **User decides:** the gold set with DoIT and threshold calibration (item 21); P1b item 15 `e2e` (after B's L7); pin
   review and Aspire 13.6.4.

**Open issues — stream B:** #278, #314 (English UI, done in #320; B closes), #318, #52. **Stream A:** #300
(L4, information), #48, #51, #193, #210. #319 done in #332 (B switches the two form texts, then closes). **Main session:** #100 (L8). **User:** #215 (J8).

**Not folded into the design yet (need a call):** #217's fired-types list in the PII report; `UrlCanonicalizer` →
Pipelines (#178); identity §16 Q2–4, Q8 (gateway session); a connection pooler needs a direct connection for the HNSW
rebuild (as `ragdb-listen`); multi-collection assistant scopes aren't measured as one index-health scope.

**Waiting on the user:** a Data API key (`catalog.read`) for the org and model syncs (DoIT and the model rows are local
seeds until then); the gateway session (release 1, a 4xx for policy blocks, the shared Keycloak realm with J8 #215,
identity §16 Q1–Q4/Q6); `docs/gateway-host-setup.md` to Hermes; **O7** (go-live host); identity §16 Q5, Q7–Q9; runbook
review by the operations owner; U1 with the web editors; names and dates for A1, G1, N1, U1, W2, P2 and the gold-set
owner (DoIT); DPIA G1.

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; stored chat content: history `none` only), G1 (DPIA, before go-live), M2 (triggered: EU-hosted models
until on-prem hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page / robots.txt), P2 (licences),
D1 (standby), R1 (self-hosted runner), S2 (object store), W3 (per-user model lists; spike now), C2 (visual builder, P3).
Every phase-blocking item needs a named person and a date.

## Environment notes

- `rag_key.txt` and `rag_gateway_url.txt` in the repo root hold the interim-gateway key and address (mode 600, excluded
  in `.git/info/exclude`, also in the AppHost user secrets). Never commit, print or move them.
- **Git over HTTPS:** use `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Postgres in the AppHost: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…>
  psql -h localhost -U postgres -d ragdb`.
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci` (Testcontainers needs
  a dotted registry part). `172.30.0.0/16` is taken on this machine.
- Self-hosted runners `ume-rag` (this machine; needs Docker Desktop up) and `ume-rag2` (the user's other machine); a
  runner can fail with "MSBuild child node exited prematurely" under load: re-run the job.
