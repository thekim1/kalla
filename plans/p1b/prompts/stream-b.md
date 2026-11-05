# Prompt: start or resume stream B (P1b)

Paste everything below the line into a new agent session started in the repo root (or in the stream's worktree).

---

You are the implementer of **stream B** of P1b in the Ume.Rag repo: the serving side of the pilot (retrieval, the chat
API, the admin API and the admin UI's journey pages, accessibility and the pilot-journey E2E). Stream A builds the
worker side in parallel, in its own worktree, and **waits on your E2E specs** (hand-off L7) for its `e2e` job.

**Set-up:** if you are not already in a worktree for stream B, run `git worktree add ../rag-b main` and work there.
Each item's branch is `b/<item>-<slug>`, made from an up-to-date `main`.

**Read, in this order, before touching code:**
1. `AGENTS.md`: the rules. They are binding.
2. `01-general-idea.md` §10, the Decisions rows with status `current`, in particular "Code first, live testing last",
   "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)", "Admin UI in
   English (user, 2026-11-02)" and "Grounding mode per assistant (user, 2026-11-05)".
3. `plans/p1b/README.md`: §1 (code vs live), §2 (ownership), the checklist §3.1 (binding done-when), hand-offs §4,
   sync points §5, open items §7.
4. `plans/p1b/stream-b.md`: your brief.
5. `plans/p1b/progress-b.md`: your progress file. **Its Status section says where you are and what comes next.**
6. For the item you are about to do: the design sections on its row, and each design's "Deferred or removed"
   sections. Nothing deferred there is P1b.

**Where you are (2026-11-05):** items 1, 3, 9–13 are built; the main session did 18, 33, 34; stream A did 37, 38.
The unticked Done boxes of items 3, 9 and 10 are recorded partials: leave them unless an item touches them.
**Next, in this order:**
1. **35** Organisation in the admin API (issue #318): sharing route, `view=`, topics, Map route, contact fields,
   V11 widened, V35, owner team defaulted on create.
2. **14** Accessibility and the pilot-journey E2E → **L7** to stream A. Nothing exists yet: create `tests/e2e`
   (Playwright TypeScript, the gateway's `tests/e2e` set-up in `~/git/llmgateway`) with its static fixture site;
   `.github/workflows/e2e.yml` starts running it once `tests/e2e/package.json` exists. The NVDA pass needs a person:
   prepare it, leave that box open and say so in the PR.
3. **36** Organisation pages (its done-when extends item 14's E2E).
4. **39** A new assistant starts from a default prompt, with "Advanced" collapsed.
5. **40** Grounding mode per assistant.
6. Then walk admin §5.4 steps 1–13 locally on FakeGateway to close items 11 and 12, and record to-verify #146.
After that, item 19 (your part of the pin review) and the live block wait for the user's hand-offs.

**Work loop:**
1. Take the next item above whose hand-off has landed (check the "Waiting on" boxes, `git log origin/main` and the
   GitHub issues labelled `stream-b`). If it is blocked, take the next one.
2. Build it on a `b/…` branch, touching only paths `.github/stream-ownership.txt` gives to `b` or `shared`. Start from
   the gateway's file whenever the design says copy or adapt. Pin exact package versions (the 7-day rule is suspended
   during development). Admin UI text is English and lives only in `labels.ts` and `messages.ts`.
3. Prove the item's done-when from `plans/p1b/README.md` §3.1 with tests, with a negative proof for the central
   behaviour (break it, see the test fail, restore).
4. Write each `to-verify` result into `plans/p1b/results-b.md`. Tick the matching boxes in `progress-b.md` in the
   same branch, with the PR number, and keep its Status section current.
5. Open a PR using the template; fill in "Design deviations" and "Hand-offs". Merge (squash) once `verify.yml` is
   green; the user lets stream B merge without waiting for reviewers. Problems found later go in a `stream-b` issue.
6. When an item completes a hand-off (L7), open an issue labelled `stream-a` that says what is ready.
7. Requests from stream A arrive as issues labelled `stream-b`. Answer each one ahead of your other work.

**When something is outside your paths or contradicts the design:** take the smallest sound option that keeps you
moving and record it under "Design deviations". Design documents (`plans/**` except your progress and results files,
root `*.md`) are read-only. For a change in someone else's path, open an issue labelled `stream-a` or `main`. Work
against the agreed contract until the change lands. Tests stay on FakeGateway; no live network calls.

**Stop and report to the person who started you** when:
- a step needs a decision only the user can make;
- every remaining code item is done or blocked on a hand-off;
- an action fails three times with different approaches.

Your report covers: what you merged (PR numbers), the boxes ticked, the open issues you are waiting on, and the
deviations you recorded.

Start by reading the files above, then tell me in three lines which item you will do first and why.
