# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.5.0`** (2026-11-13). Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
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

## State (2026-11-13, end of session 33)

- **`v0.5.0` released** (2026-11-13, run 43427657518, 11 assets). Every code item is done except A-41 (#654, small, no
  input needed); the rest is the live block, whose step 0, **the interim gateway on v0.10.1 with three credential
  keys**, blocks every live check (roadmap *Now*).
- **Plan files restructured** (M-14, #655, #656): the roadmap is the plan ahead, queues hold open items only, designs
  hold the specification (history in `plans/archive/design-history/`), proposals in `plans/proposals/`. How they are
  kept: handbook *Plan files*.
- **Stream b** (remote, GitHub only) has its own main session (design PRs on `main-session/` branches). It works fast,
  splits items into parts, answers review findings with PRs, takes stream-a paths on `a/` branches when the user asks,
  and announces its work on #580. B-42–B-46 are merged and unticked until their live checks.
- **Local user secrets** still block the AppHost (handbook *Environment notes*): the user moves `Gateway:Keys:Eu` to
  `Gateway:Services:<service>:Key` and drops `ClamAv:Enabled`.

## Next

1. **GitHub:** review and answer new stream-b PRs and issues. **#642** (stream b): the read-back's reason is in no
   view, the shared-key note shows on every key, `messages.ts` lacks `gateway.key_unavailable` and
   `gateway.delegation_not_applied`, and `gateway.budget_exhausted` still blames "the platform's budget". **#580**
   stays open until the live checks of phases 2–3 pass (the gateway owner was asked about a `runId` filter on the
   request list); **#513** until #506, #507 and #511 close; **#550** until stream b acknowledges.
2. **A-41** (#654): the read-back on the platform's Gateway view and the cost runbook (texts on a `main-session/` branch).
3. The roadmap's *Meanwhile* list, as queue items, when the user agrees; the open product call on the support-assistant
   track (roadmap *Now*).

## Session 33 (2026-11-13)

- **CI review:** `backend` was 312 of 418 runner-minutes, and its long pole `Ume.Rag.AdminApi.Tests` (40 classes serial
  in one collection). #648 split it into four database collections (AdminApi 4–5 min → 3:03, `backend` ~6.2 → ~4.5
  min); #647 skips `backend` on docs- and deploy-only PRs (confirmed live on #652). Not done on purpose: per-project
  test selection, evaluation-smoke. Open: one `WebApplicationFactory` per class (suggested to stream b on #580); the
  runner layout, after re-measuring queue waits.
- **#645** fixed in #650 (the quiet-rule render test's page gets 15 s and names the reason on failure; reopen with it).
- **V39 confirmed** by the user (#651). CHANGELOG #649; release 0.5.0 #652 and the tag.
- **Plan review** and the restructuring (M-14, #655, #656); stream b fixed #642 in #653 (reviewed), A-41 queued for #654.
