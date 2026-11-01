# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-01, end of session 17 (second half included). Earlier sessions are in the
git history (`git log -- HANDOVER.md`).
Repo `~/git/ume-rag-platform`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

## Where we are

- **The demo runs** (`plans/demo-path.md` "Result"): crawl → retrieve → cited answer in Open WebUI from the `turkos`
  assistant (`claude-haiku-5-5`), over the user's interim gateway at `Eu`. P0 released (`v0.1.0`); P1a code complete;
  P1b in progress.
- **Direction (user):** demo first, then details; **more focus on the admin UI** (`plans/demo-path.md` "Next focus",
  issue #278); no more crawling at this stage. Don't over-engineer tests; merge on green CI.
- **Admin UI:** Översikt, Organisation, Kunskapsbanker (B, #279), **Plattformsobjekt** = generic forms for clients,
  keys, settings, data protection, site profiles, gateway models (A, #281; `src/admin-ui/src/platform/**` is stream
  A's). B builds the journey pages: **#285** (Lägg till källa) approved, B merges; **#288** (source and run page)
  conflicts, waits for B's rebase.
- **Stream A's queue is empty:** P1 item 19 (EU-route capacity, #286), P1b item 17's importer settings (#289),
  `Gateway:MaxResidency` and Open WebUI notes (#290), the evaluator's stop list (#291) are merged. Left for stream A:
  P1b item 15 `e2e` (after B's L7), the go-live pin review, the live items (17's import run, 21–26).
- **Design** is in sync with everything merged (design syncs `d0f246b`, `0ee7bc4`, `4adfc63`, `b742f27`): two layers,
  Decisions table (rows `current` bind) > detail documents. **Gate every docs commit on `scripts/check-doc-refs.py`
  printing `0 misses`** (one push slipped through with a miss this session).
- **Decided this session:** Decisions row "Condense on the EU route" (`gpt-6-luna`, `reasoning_effort: none`, no
  `temperature`, 1,200 ms; B builds it in #287); `temperature` sent only when set (#282, folded); main-session calls on
  the design sync (either-of policies stay: #283; 5002 `conversation_not_owned` stays: #284; admin reads
  `PublishedAssistantVersions`; `Gateway:MaxResidency` implements M2); pipeline publish copies on write for departments,
  publication id = plan job id (admin §6.6, #256).
- **Measured (P1 item 19):** EU-route embedding 18–20k tokens/s (key-limited), query embedding p95 156 ms, answer TTFT
  p99 3.1 s; all of umea.se (≈ 15,400 URLs, ≈ 8,100 PDFs) ≈ 2–3 h (fetch-bound) and ≈ $6–22 in embeddings
  (`plans/p1/results-a.md`).
- `main` = `6c05377`, CI green.

## Local stack (this machine, AppHost running)

- Started with `Seed__Enabled=false`; dashboard `https://localhost:17470`; admin UI `https://localhost:5174`
  (Plattformsobjekt at `/plattform`); admin API `https://localhost:7402`; api `https://localhost:7401` (http 5401);
  Open WebUI = the `open-webui` resource's http port (was `http://localhost:33237`; check `aspire describe`).
- **Pilot data (built through the admin API in session 17):** department DoIT (SQL seed, no Data API key); collection
  `turkos` (public, published, `coverageMaxDistance` 0.55 for the demo); crawl source **"umea.se"** = whole host,
  **capped at `maxPages` 2,500, `**.pdf` excluded, no schedule**, 2,323 HTML pages live (10,071 chunks, 676 tokens per
  page); site profile www.umea.se (sitevision); `Settings.Crawl` (`municipalHosts`, www.umea.se 2 req/s, 2 connections);
  `PiiOwnDomains`; clients "Open WebUI" (`open-webui`), "Pilot retrieval (checks)" (`internal`), "Pilot grading"
  (`integration`); `GatewayModels` rows (manual) `claude-opus-5-5`, `gpt-6.1-sol`, `claude-haiku-5-5`; assistant
  **`turkos` v3 on `claude-haiku-5-5`** (the user found Opus unnecessary), audience Open WebUI + Pilot grading.
- **Open WebUI:** `DEFAULT_MODEL_METADATA` (no builtin tools) is in the AppHost since B's #280; a local model override
  on `turkos` does the same. First account (admin) `demo@umea.se`; the user's logins (Open WebUI and the admin UI's dev
  `rag-platform-admin`) are in `~/ume-rag-demo-login.txt` (mode 600, outside the repo).
- User secrets hold `OpenWebUi:ClientId` and `Parameters:openwebui-api-key` (the Open WebUI key). The retrieval and
  grading keys were in the session scratchpad only: reveal (`POST /api/keys/{id}/reveal`, audited) or issue new ones.
- **Draft gold sets:** session 17's 40 questions over the demo slice (`~/ume-rag-gold-draft-questions.csv`,
  `~/ume-rag-gold-draft-answers.csv`, outside the repo: 33/33 answerable cited their page, 6/7 off-topic not-found);
  session 15's 199-row draft in `plans/p1/gold-set/` has document ids of the other computer's database (match by node id
  `4.<id>` in the URL).
- **Crawl caution:** a source's scope change re-syncs it by itself (trigger `config-change`); cancel with
  `POST /api/runs/{id}/cancel`. The user said no more crawling at this stage.

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind). Recent rows: "Code first, live testing last",
   "7-day rule in development", "No backward compatibility before production", "Interim gateway for P1a", "Personal
   data on the municipality's own published site", "Pilot set-up inputs", "Condense on the EU route".
2. `plans/demo-path.md` (the demo, its result, the admin-UI focus, the deferred list).
3. `plans/p1/README.md`, `plans/p1b/README.md` (items, hand-offs, done-when); progress `plans/progress.md`,
   `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results `plans/p1/results-{a,b}.md`.
4. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). The passes
   (`plans/*-pass.md`, `weakness-review.md`) don't bind; read them for the reasoning.

## How we work (user preferences, binding)

- **Demo first, details after; admin UI focus now.** No extra negative proofs, probes or flake hunts unless a change
  needs one; merge on green CI. Keep adversarial review of stream B's security-relevant parts, proportionate.
- **Approve and merge without asking (user, 2026-11-02):** approve PRs judged ready for merge (reviewed, checked against
  the live stack where relevant, CI green) and merge PRs that should be merged; tell the user afterwards. Real decisions
  (design, scope, anything destructive) still go to the user. Branch deletes on GitHub are still blocked by the
  auto-mode check.
- **Stream B** is a remote agent (GitHub `thekim1`); talk only through GitHub (labels `stream-b`, `stream-a`, `main`).
  **B merges its own PRs**, usually fast. Nudging B is allowed (user, session 17). `gh` acts as `ume-nichlas`.
- **Main session** (this session) owns design documents (`plans/**`, root `*.md`, `.github/stream-ownership.txt`):
  commit directly to `main`. Stream-A work goes through branches `a/<item>-<slug>` and PRs; a PR that must touch B's
  files for coordination uses a non-stream branch name (ownership isn't checked) and says so.
- **Subagents:** worktrees `../rag-aN`; brief fully; their report is a claim: check the diff (headings unchanged,
  present tense, no PR history in designs, ref check, spot-check against the code).
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): own small
  PR, append-only.
- Verify claims against real sources. Secrets (gateway address and key, API keys, passwords) never go into the repo, a
  log or a chat: read them into variables, write them to mode-600 files.

### Lessons

- **Scripting the admin API** (recreate `adm.py` each session): `/bff/login` → Keycloak form (user `rag-platform-admin`,
  password = user secret `Parameters:dev-user-password`, file has a UTF-8 BOM) → the `form_post` page (upper-case
  `<FORM`) posted to `/signin-oidc`; writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN` (cookie
  `RAG-XSRF-TOKEN` from `/bff/user`); send bodies as UTF-8 bytes (`requests` sends `str` as Latin-1, breaking `å`);
  `/openapi/v1.json` (Admin) gives every request schema. Publish an assistant with `version` (assistant) and
  `draftVersion`; a source scope change needs `POST /api/sources/{id}/impact` first (its `impactToken`).
- **The interim gateway:** refuses `temperature` for Claude models (fixed by #282, needs the new api code); caps an
  embedding request at 8,192 tokens in total; reasoning models spend small `max_tokens` on hidden reasoning; its front
  end returns 403 to Python's default User-Agent (send one); reports no cost; rate-limits embeddings (429, requeued).
- Open WebUI 0.12.3 adds builtin tools to every browser chat (`DEFAULT_MODEL_METADATA` turns them off); an API test
  without a browser `session_id` doesn't see it.
- A cancelled run writes no counts (`Added` 0); six umea.se node-id URLs (`/4.<id>.html`) answered 404.
- Read the *first* real failure; don't overlap more than two full Worker.Tests runs; flakes are often real (trace before
  widening a bound); timing assertions sample `clock_timestamp()`, not `now()`.
- MTP: `--filter-class` values are OR'd; grep with `NO_COLOR=1`. CI's shellcheck is 0.9.0. Compose changes: check every
  `compose config` step in `verify.yml`/`release.yml`.
- A branch carrying an unsquashed copy of a squash-merged PR: recreate it from `origin/main`.
- `psql -c "A; B"` is one transaction; `pgrep -f` matches its own command line.
- Admin UI tests locally: `npx vitest run --pool=threads` (the forks pool times out under WSL); a worktree needs
  `node_modules` (symlink the main checkout's, remove the link before committing).

## Code map (on `main`)

| Area | Where | State |
|---|---|---|
| Contracts | `src/Ume.Rag.Pipelines/`, `contracts/` | done; site profile `contracts/site-profiles/v1/www.umea.se.profile.json` |
| Parser | `src/parser/`, `src/Ume.Rag.Worker/Parsing/` | done |
| Connectors | `src/Ume.Rag.Worker/Connectors/` | done; crawl keeps and fetches the path form (`FetchUrl`, #268) |
| Processing, engine, commands, preview | `src/Ume.Rag.Worker/` | P1a complete, P1b items 4–8 done |
| Evaluation | `src/Ume.Rag.Worker/Evaluation/` | done incl. source demote (#270) and the stop list in every variant (#291); generation/judge at `OnPrem` only |
| Admin API (B) | `src/Ume.Rag.AdminApi/` | P1b item 9 parts a–f merged; `/openapi/v1.json` serves the generic forms |
| Knowledge + chat (B) | `src/Ume.Rag.{Retrieval,Answering,Api}/` | chat with feedback, conversation ids; `temperature` only when set (#282) |
| Admin UI | `src/admin-ui/` | Översikt, Organisation, Kunskapsbanker (B); Plattformsobjekt `src/platform/` (A) |
| AppHost (B) | `src/Ume.Rag.AppHost/` | `open-webui` with a configured client and key, no builtin tools (#280) |
| Deploy (A) | `deploy/` | Compose incl. `DEFAULT_MODEL_METADATA` (#274), importer settings (#289), `Gateway__MaxResidency` (#290) |
| Scripts (A) | `scripts/{capacity,grading,dev,audit}/` | capacity run on the EU route done; grading script run live (#277) |

## Next step

1. **Check the stack and GitHub:** `aspire describe` (if down: `export Seed__Enabled=false; aspire start --apphost
   src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive`); open PRs, new issues, CI on `main`.
2. **Review B's PRs as they come**, against the live admin API as for #279/#285: #288 after its rebase; then whatever B
   opens for #278 (journey pages), #283, #284, #287, item 9c (pipeline publish, now unblocked by #256's decision).
   Approve only with the user's OK.
3. **When #287 lands:** set `Settings.Chat.CondenseModel = gpt-6-luna` and `CondenseReasoningEffort = none` on the local
   stack (Plattformsobjekt → Inställningar → Chatt) and check a follow-up question is condensed (`rag.chat.condense
   {outcome=used}`); fold #287 into chat §5 if it deviates.
4. **Open decision (user + main):** LLM test-question generation and the judge run at ceiling `OnPrem` only
   (`plans/ingestion-worker/design-processing.md` §13.1, "Model"), so they can't run before on-prem models exist (M2).
   Allowing `Eu` under M2 is a design change and needs the evaluation client's fixed temperatures (0.3, 0) dropped or
   made optional (Claude models refuse them). The human gold set needs neither.
5. Then, user decides: the gold set with DoIT and the three-way evaluation with threshold calibration (item 21; the demo
   runs with `coverageMaxDistance` 0.55 and a weak hit can still get a soft "the sources don't say" answer; draft
   questions in `~/ume-rag-gold-draft-*.csv` and `plans/p1/gold-set/`); P1b item 15 `e2e` (after B's L7); pin review
   and Aspire 13.6.4 (shared/B); more of umea.se only when the user wants it.

**Open issues to stream B:** #278 (admin UI order), #283 (either-of policies), #284 (5002 `conversation_not_owned`),
#287 (condense), #256 (decided, B closes it with item 9c), plus the older hand-off issues #52, #75, #80, #90, #102, #129,
#132, #246. **For stream A:** #48 (H8 on-prem capacity run), #51 (licence notices), #193, #210 (hand-offs received).
**For the main session:** #100 (L8). **For the user:** #215 (J8).

**Not folded into the design yet (need a call):** #217's fired-types list in the PII report; `UrlCanonicalizer` →
Pipelines (#178); identity §16 Q2–4, Q8 (gateway session); admin design says `GET /api/departments`/`/api/teams`, the
code has `/api/organisation/departments` with teams nested.

**Waiting on the user:** a Data API key (`catalog.read`) for the org and model syncs (DoIT and the model rows are local
seeds until then); the gateway session (release 1 incl. the embedding-batch fix, a 4xx for policy blocks, the shared
Keycloak realm `ume` with J8 #215, identity §16 Q1–Q4/Q6); handing `docs/gateway-host-setup.md` to Hermes; **O7**
(go-live host); identity §16 Q5, Q7–Q9; runbook review by the operations owner; U1 with the web editors; names and dates
for A1, G1, N1, U1, W2, P2 and the gold-set owner (DoIT); DPIA G1; the evaluation-model decision (step 4).

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; gates stored chat content: the chat schema allows history `none` only), G1 (DPIA, before go-live), M2
(triggered: EU-hosted models until on-prem hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page /
robots.txt), P2 (licences), D1 (standby), R1 (self-hosted runner), S2 (object store), W3 (per-user model lists), C2
(visual builder, P3). Every phase-blocking item needs a named person and a date.

## Environment notes

- `rag_key.txt` and `rag_gateway_url.txt` in the repo root hold the interim-gateway key and address (mode 600, excluded
  in `.git/info/exclude`, also in the AppHost user secrets). Never commit, print or move them.
- **Git over HTTPS:** git's credential manager opens a dialog nobody answers; use `git -c credential.helper= -c
  'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH);
  `aspire describe`, `aspire resource <r> restart` (rebuild the project first, e.g. `dotnet build src/Ume.Rag.Api`).
- Postgres in the AppHost: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…>
  psql -h localhost -U postgres -d ragdb`.
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci` (Testcontainers needs
  a dotted registry part). `172.30.0.0/16` is taken on this machine.
- Self-hosted runners `ume-rag` and `ume-rag2` (the user's other machine); a runner can fail with "MSBuild child node
  exited prematurely" under load: re-run the job.
- Scratchpad helpers (`adm.py`, `psql.sh`, keys) don't survive a session; re-create what you need.
