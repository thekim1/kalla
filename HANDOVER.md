# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.5.1`** (2026-11-13); the user's VM runs 0.5.0. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every
commit).

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

## Start here (session 34, updated 2026-11-14)

- **M-17** (deploy configuration, decided: [`plans/proposals/deploy-configuration.md`](plans/proposals/deploy-configuration.md),
  `plans/work/main.md` M-17) is built by **the M-16 session on stream b's machine** (GitHub `thekim1`), by the user's
  choice, one step at a time and announced on #667. **C0–C4 are merged** (#674, #683, #682, #684). This session reviews
  the security-relevant steps and builds none unless the user says so. C7, C11 and C12 wait on thekim1/llmgateway
  #87 (G1), #90 (G5), #89 (G4).
- **M-18** (remove `Settings.WorkerConcurrency` until P2, #677) is merged (#686). C5 (the embedding profile created in
  the admin) is on the M-16 session's branch `main-session/m17-c5-embedding-profile`.
- **Next release (0.6.0):** after the M-17 steps the user wants in it. Before tagging: CHANGELOG *Unreleased* needs
  M-16 (#668–#679, #681), #670, M-17 C0–C4 and M-18 (#686) (it lists only #663 now); `docs/upgrade-guide.md` *Unreleased* has the
  Open WebUI client move (#685); the repository setting *Allow GitHub Actions to create and approve pull requests* must
  be on for C0's pin PR.
- **The test chatbot (T2–T6) is the user's team's** (user, 2026-11-14): don't plan or prepare it; fix what they report.
  M-15 is withdrawn.

## State

- **`v0.5.1` released** (2026-11-13). The user **deployed 0.5.0 with the Portainer stack, `openwebui` and `clamav` on**
  (2026-11-13: "deployed fine"), so the stack and its Open WebUI profile have booted on a real VM. Their Open WebUI
  client was made by hand: the next upgrade follows the upgrade guide's *Unreleased* note (C4 trusts only the
  deployment's client).
- **Since 0.5.1 on `main`:** the clients page (#663), then M-16's *Apps and keys* (connect wizard with assistants and
  *How to connect*, keys under their app, last use), the side menu and the Settings page of sections (#668–#681); A-41
  (#665); M-17 C0–C4.
- **Decided in session 34:** D1–D10 of the deploy proposal (Decisions "Deploy configuration", "Residency per level",
  "Role groups as a setting"); clients and keys as a page (Decisions "Admin API and UI", admin §5.9); #677.
- **Every stream-queue code item is done.** Stream b (remote) announces on #580; the M-16 session on #667.
- **Local user secrets** still block the AppHost with the real gateway; a FakeGateway stack works (handbook *Local
  stack*).

## Next

1. GitHub: review M-17's PRs as they come (security-relevant parts), answer #667 and #580.
2. The 0.6.0 release when the user asks (checklist above).
3. Fix what the user's team reports from the VM.

## Session 34 (2026-11-13 to 14)

- Deploy-configuration plan written and decided (#662); gateway issues llmgateway#87–#90 filed for the user.
- Clients and keys page built with two implementor agents against a contract in admin §5.9 (#663; GitHub showed the
  PR *Closed* after a 502 on merge, the commit is on `main`); A-41 (#665); #684 reviewed (C4: rag-api trusts only the
  deployment's Open WebUI client); its upgrade note (#685).
- The deploy was reported fine; the test chatbot moved to the user's team.

## Session 35 (2026-11-14)

- **M-17 C0** built and proven by a release dry run (#674); **C1** built on its branch.
- **Parser audit fixed** (#678): the lock's `exclude-newer` moved to 2026-11-07, `opentelemetry-api` 1.45.2.
- This machine's Docker has no free address pool for the stack's networks: `portainer-boot.sh --compose-file` with fixed
  subnets (10.210.N.0/24) works. Port 5000 is taken here (a local registry must use another port). The `gh` token has
  no `read:packages`, so Ume.Rag's released images can't be pulled here: build them into a local registry instead.

## Session 33 (2026-11-13)

- **CI:** `backend` ~6.2 → ~4.5 min (#648 AdminApi tests in four database collections; #647 skips docs- and deploy-only
  PRs); #645 fixed (#650).
- **Releases `v0.5.0` and `v0.5.1`**; V39 confirmed (#651).
- **Plan files restructured** (M-14, #655, #656): the roadmap is the plan ahead, queues hold open items, designs hold
  the specification, history in `plans/archive/design-history/`, proposals in `plans/proposals/`.
- **Test chatbot before the umea.se pilot** decided (Decisions, roadmap *Now*).
- **Deploy with Portainer:** #657 (stack: pinned images, Open WebUI), #658 (`docs/portainer.md`), #659 (variables file,
  checklist, secrets reload). Stream b fixed #642 in #653 (reviewed); A-41 queued for #654.
