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

## Start here (session 37)

1. **GitHub first.** `gh pr list`, then the latest comments on **#667** (the M-16 session, which builds M-17 and the
   #694 fixes for the user, GitHub `thekim1`) and **#580** (stream b and gateway decisions). Review each new PR's
   security-relevant parts. The user has the main session approve and merge ready PRs: update a stale branch first
   (`gh pr update-branch`) so CI runs on the current `main`, and settle a PR's *Design deviations* on `main` after it
   merges (the design is main's).
2. **The M-16 session works down review #694** in its suggested order. Its next fix is **#702** (LibreOffice's output skips
   the parser's zip-bomb check). Open from #694:
   - high: #696 (gateway calls under the config lock) and #697 (Portainer bootstrap re-run test, shellcheck);
   - medium: #700, #702, #703, #705–#710;
   - small fixes: #711 (stream a) and #712 (stream b; its item 8, seeding `admin-console`, is decided).
   - Also open: #720 (a misleading `gateway_key_model` text, from the grading dry run).
3. **Main's own next item** (no input needed): sort the 124 open rows of `plans/to-verify.md` by phase (live block,
   before go-live, P2), closing what no longer applies; 23 of them have their context only in archived passes. After
   that, the roadmap's other *Meanwhile* items: measure the upload gaps U1–U7, and a faster `Ume.Rag.AdminApi.Tests`.
4. **Small design debts:**
   - repo-and-deployment's Portainer section needs a line for the `proxy` profile (C8).
   - Two minor points from the C8 review are open on #690: the trusted-proxy address changes when `rag-proxy` is
     recreated (docs), and Open WebUI's site sends no HSTS.

## State

- **Release:** `v0.5.1` is the last (2026-11-13). The user deployed **0.5.0** with the Portainer stack, `openwebui` and
  `clamav` on ("deployed fine"). Their Open WebUI client was made by hand, so their next upgrade follows the upgrade
  guide's C4 note.
- **M-17 (deploy configuration):**
  - **Merged:** C0–C6 and C8–C10 (#674, #683, #682, #684, #687, #689, #690 with the proxy fix #692, #691, #693). Their
    design is on `main`.
  - **Left:** C7, C11 and C12 wait on the gateway's releases. Its answers on #580: #89 accepted; #87 accepted as
    create-only with its own volume; #88 a generic add with a preset file Ume.Rag owns; #90 declined, so the context
    carries `integration_api_url` instead. The proposal's rows and `plans/work/main.md` follow them. **C13** (the
    `init-deployment.sh` and docs clean-up) needs nothing.
- **M-18** merged (#686).
- **Review #694** (2026-11-14, the successor to #513, which is closed): 18 issues, #695–#712.
  - Merged: #713 (#695), #714 (#699), #715 (#698), #716 (#701).
  - #717 (#704) was done by main.
- **Decided by the user on 2026-11-15:**
  - **#704:** a tag release reads no shared cache. Built in #717; its tag path first runs at 0.6.0. Before go-live,
    `release.yml` and `entra.yml` move to a runner of their own under a separate OS user (roadmap step 7). This settles
    the old "CI runners A/B" call.
  - **#712 item 8:** the migrator seeds the platform's own clients in every deployment, `admin-console` now and
    `rag-internal` from P2 (identity §4.1, Decisions "Deploy configuration"). The code is stream b's.
- **Grading dry run done** (2026-11-15, FakeGateway stack):
  - `answer`, a graded CSV and `totals` worked against a live rag-api with a real integration key.
  - The set-up the real run needs is in the roadmap and on #100, which stays open until the gold set (J13) exists.
- **Every stream-queue code item is done.** Stream b (remote) announces on #580, the M-16 session on #667.
- **The test chatbot (T2–T6) is the user's team's** (2026-11-14): don't plan or prepare it; fix what they report.
- **Local stack:** the user secrets still block the AppHost with the real gateway. A FakeGateway stack on a throwaway
  volume works (handbook *Local stack*; the scripting notes are under *Lessons*). Nothing is running and no worktrees
  are left.

## Next release (0.6.0), when the user asks

- **CHANGELOG** *Unreleased* is current through #717 (#718): add what merges after. At the release, its *Rolling
  update* line is **no** for rag-admin (`RemoveWorkerConcurrency`: stop the old rag-admin first).
- **The upgrade guide's** *Unreleased* has notes for C1–C6, C8–C10 and M-18. C9's note is **to do before the update**: give
  rag-api's and rag-admin's credential keys `OnPrem` and `Eu`.
- The repository setting *Allow GitHub Actions to create and approve pull requests* must be on for C0's pin PR.
- The full CI set plus a release dry run (handbook *Lean CI*). Move the queues' ticked items to
  `plans/archive/work/to-v0.6.0.md` (handbook *Plan files*).

## Session 36 (2026-11-14 to 15)

- **Reviews:**
  - Reviewed C5 (#687), C6 (#689), C8 (#690, after merge), C9 (#691), C10 (#693), #715 and #716, then merged the
    approved ones.
  - In C6, rag-api's new secrets directory could override any of its configuration; it now takes only the feedback key.
  - C9's first context read could throw a bare cancellation into a request; that is fixed and tested.
  - My #687 nit about a create race was wrong: config writes already serialise on the version row. I corrected it on #688.
- **Built:**
  - **#692:** the proxy re-resolves its upstreams. Proved locally against the pinned nginx: 502 before the fix, 200
    after, once another container held the old address.
  - **#717:** #704's release caches.
  - **#718 and #719:** the CHANGELOG and the upgrade note.
- **Design on `main`:** C5, C6, C9 (identity §4.7), C10 (identity's group table and claim mapping, admin §5.10,
  data-model §2.1), chat §12.2 and §17 (#698), and the two decisions above.
- **Review #694:** four parallel reviewers covered deploy, the admin API, the worker and chat, and the admin UI. Each
  finding was checked against the code before it was filed.

## Session 35 (2026-11-14)

- **M-17 C0** built and proven by a release dry run (#674); **C1** built on its branch.
- **Parser audit fixed** (#678): the lock's `exclude-newer` moved to 2026-11-07, `opentelemetry-api` 1.45.2.
- This machine's Docker has no free address pool for the stack's networks: `portainer-boot.sh --compose-file` with fixed
  subnets (10.210.N.0/24) works. Port 5000 is taken here (a local registry must use another port). The `gh` token has
  no `read:packages`, so Ume.Rag's released images can't be pulled here: build them into a local registry instead.

## Session 34 (2026-11-13 to 14)

- Deploy-configuration plan written and decided (#662); gateway issues llmgateway#87–#90 filed for the user.
- Clients and keys page built with two implementor agents against a contract in admin §5.9 (#663); A-41 (#665); #684
  reviewed (C4: rag-api trusts only the deployment's Open WebUI client); its upgrade note (#685).
- The deploy was reported fine; the test chatbot moved to the user's team.
