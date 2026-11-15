# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.5.1`** (2026-11-13); the user's VM runs 0.5.0. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every
commit; HTTPS through `gh auth git-credential`, handbook *Environment notes*).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. **[`plans/roadmap.md`](plans/roadmap.md): the plan ahead.** Done by release, the critical path to go-live with each
   step's input and owner, work that needs no input, and the open calls. HANDOVER doesn't repeat it.
2. [`plans/decisions.md`](plans/decisions.md): the decisions in force; every entry binds. Record a decision by changing
   or adding an entry, then a dated line under *Later decisions* in `plans/archive/decisions-log.md`.
3. **[`plans/work/main-handbook.md`](plans/work/main-handbook.md): how we work (the user's preferences, binding), the
   local stack, lessons, environment notes.** Read it at the start of a session; add a lesson there when you learn one.
4. `plans/work/`: the queues `stream-a.md`, `stream-b.md`, `main.md` (open items only; finished ones are in
   `plans/archive/work/`), results in `results-a.md` and `results-b.md`.
5. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and doesn't bind.

## Start here (session 38)

1. **GitHub first.** `gh pr list`, then the latest comments on **#667** (the M-16 session, which builds M-17 and the
   #694 fixes for the user, GitHub `thekim1`) and **#580** (stream b and gateway decisions). The user has the main
   session approve and merge ready PRs: update a stale branch first (`gh pr update-branch`), review the
   security-relevant parts, and settle a PR's *Design deviations* on `main` after it merges (the design is main's).
2. **Review #727** (open, green): M-17 C7 and the Portainer stack's gateway pin to **v0.10.2**. The gateway's migrations
   create Ume.Rag's three credential keys from `UME_INTEGRATION_MANIFEST`, `rag-bootstrap` copies them to the services
   (a set `UME_RAG_*_GATEWAY_KEY` still wins), and rag-admin joins the gateway's `edge` network for
   `https://adminapi:8443/integration/v1`. Security-relevant: where the key secrets land (their own volume, not
   `ume-certs`, per the gateway's #87 decision), file modes, and who can read them. C12 and C11 follow from the M-16
   session in their own PRs, then 0.6.0.
3. **#726 needs a design answer** (from the user, for thekim1/supportbot): `ume.general_knowledge` per chat request,
   capped by the assistant's level, so one assistant serves three strictness levels. It touches chat §3 (the `ume`
   object), V36 (a public assistant with general knowledge) and the test panel. Present it as four options with a
   recommendation (handbook; memory *Decisions: four alternatives*).
4. **Review #694's fixes** as the M-16 session posts them. Still open: high #696 (gateway calls under the config lock),
   #697 (Portainer bootstrap re-run test, shellcheck); medium #700, #702, #703, #705–#710; small #711 (stream a) and
   #712 (stream b; item 8 decided); #720 (a misleading `gateway_key_model` text).
5. **Main's next items, no input needed:**
   - The 16 rows of `plans/to-verify.md` *Before go-live* (sorted this session): local tests and source readings such
     as Cronos DST (#16), Open WebUI behaviour (#47, #50, #134, #165), the PDF viewer CSP (#95), licences (#109,
     #110), the OTel Postgres receiver (#123). #69–#71 belong with #702's LibreOffice work.
   - A faster `Ume.Rag.AdminApi.Tests` (stream b's project; ask on #580 first).
   - M-19's two cosmetic leftovers (`plans/work/main.md`): a Live card's numbers in link colour, *Hide* on its own line
     on a phone.
6. **Small design debts:** repo-and-deployment's Portainer section needs a line for the `proxy` profile (C8); two
   minor points from the C8 review are open on #690 (the trusted-proxy address changes when `rag-proxy` is recreated;
   Open WebUI's site sends no HSTS).

## State

- **Release:** `v0.5.1` is the last (2026-11-13). The user deployed **0.5.0** with the Portainer stack, `openwebui` and
  `clamav` on. Their Open WebUI client was made by hand, so their next upgrade follows the upgrade guide's C4 note.
- **Gateway v0.10.2** (2026-11-14) released all four of our requests: #87 (manifest provisioning, ADR 022), #88 (the
  Ume.Rag preset, `deploy/integration-presets/ume-rag.json` is ours), #89 (`organisation_name`), #90's replacement
  (`integration_api_url` in the context). M-17 C7, C11 and C12 are unblocked; C7 is #727.
- **M-17 (deploy configuration):** merged C0–C6, C8–C10; open C7 (#727), C11, C12, C13.
- **M-19 (admin UI review) done** (#724, decided by the user 2026-11-15, UI1 B, UI2 B, UI3 A, UI4 B):
  - Home has a Live card per published assistant and a Health block, and folds published checklists.
  - The knowledge base and assistant lists have more columns, and every list row is a real link.
  - The audit log shows names linked to their objects, events in plain words, and pickers.
  - The assistant page's wording says what saving does.
  - Not done: Settings placeholders (the API doesn't return the value in force) and personal-data document links (no
    document page). As-built details are in admin §5.5.1, §5.5.2, §5.5.4, §9.1.
- **Upload gaps measured** (#722, `proposals/upload-quality-review.md` *Measurement 1*): U1 (pictures) is the largest
  loss. On 14 public IT guides, 18 % of the text is inside screenshots, 19–76 % in step-by-step guides, and none of it
  is kept. U5 and U7 cost little. `scripts/upload-gaps/measure.py` can run on DoIT's files at T6. U1 still needs a
  Decisions entry (keep the OCR text, or a vision description).
- **`plans/to-verify.md` is sorted by when a check can run** (#721): live block by roadmap step (43), before go-live
  (16), P2 and later (53); 12 rows closed.
- **Every stream-queue code item is done.** Stream b (remote) announces on #580, the M-16 session on #667.
- **The test chatbot (T2–T6) is the user's team's**: don't plan or prepare it; fix what they report.
- **Local stack:** the user secrets still block the AppHost with the real gateway. A FakeGateway stack on a copy of
  the dev volume works for UI checks (session 37, *Lessons* below). Nothing is running; no worktrees are left.

## Next release (0.6.0), when the user asks

- **CHANGELOG** *Unreleased* is current through #724 (#725): add what merges after (#727, C11, C12). At the release,
  its *Rolling update* line is **no** for rag-admin (`RemoveWorkerConcurrency`: stop the old rag-admin first).
- **The upgrade guide's** *Unreleased* has notes for C1–C6, C8–C10 and M-18; C7 adds one. C9's note is **to do before
  the update**: give rag-api's and rag-admin's credential keys `OnPrem` and `Eu`.
- The repository setting *Allow GitHub Actions to create and approve pull requests* must be on for C0's pin PR.
- The full CI set plus a release dry run (handbook *Lean CI*), the browser tests included: #724 changed
  `tests/e2e/specs/pilot.spec.ts` (a run row's first cell is a link now). Move the queues' ticked items to
  `plans/archive/work/to-v0.6.0.md` (handbook *Plan files*).

## Session 37 (2026-11-15)

- **to-verify triage** (#721): the open rows are sorted into live block, before go-live, and P2 and later. Closed:
  #60, #63, #68, #72, #91 (resolved by tests and `release.yml`); #36–#38, #147 (the gateway's own checks); #130.
- **Upload gaps measured** (#722), above.
- **Admin UI review:**
  - A browser walk through 25 screens on a copy of the dev data.
  - Three bugs fixed (#723): Documents by link, archived assistants, tabs in `?tab=`.
  - The proposal was decided by the user, then built as M-19 by four implementor agents in parallel worktrees, merged
    on one branch and checked in the browser (#724). The CHANGELOG is in #725.
- **GitHub:** found the gateway's v0.10.2 and unblocked C7/C11/C12 on #667. No PRs from others came in until #727.
- **Lessons:**
  - Two implementor agents refused to commit (their rules say the user commits). Plan to commit their work yourself
    after reading the diff.
  - Parallel agents running test suites push the load to ~37, and vitest then fails at random in untouched specs.
    Rerun alone before calling a failure real.
  - The Playwright MCP browser rejects the dev certificate. Use a Node script with `tests/e2e/node_modules/playwright`
    and `ignoreHTTPSErrors`, logging in from the user secret inside the script (never on the command line).
  - Copy the dev volume (`ume-rag-m1-postgres`) to a throwaway volume with `alpine cp -a` for a realistic stack. Remove
    it after.
  - `uv run` in `src/parser` creates `.venv`, which breaks `check-doc-refs.py`. Delete it after use.

## Session 36 (2026-11-14 to 15)

- Reviewed and merged M-17 C5, C6, C8–C10 and #715/#716; built #692 (the proxy re-resolves its upstreams), #717
  (#704's release caches), #718 and #719 (CHANGELOG, upgrade note).
- Review #694 filed (18 issues, #695–#712) by four parallel reviewers, each finding checked against the code.
- The user decided #704 (tag releases read no shared cache) and #712 item 8 (the migrator seeds the platform's own
  clients). Grading dry run done on a FakeGateway stack.
