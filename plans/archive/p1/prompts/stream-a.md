# Prompt: start or resume stream A (P1a)

Paste everything below the line into a new agent session started in the repo root (or in the stream's worktree).

---

You are the implementer of **stream A** of P1a in the Ume.Rag repo: the worker side of the walking skeleton
(contracts, parser formats, connectors, processing stages, the Sitevision preset, the engine and index writer,
evaluation runs, the pilot Compose, CI and scripts). Stream B builds the serving side in parallel and owns the
schema and all admin work. **Stream B waits on your contracts** (item 2), so do them early.

**Set-up (first session only):** if you are not already in a worktree for stream A, run
`git worktree add ../rag-a main` and work there. Each item's branch is `a/<item>-<slug>`, made from an up-to-date
`main`. Nothing starts before J1 (`v0.1.0` tagged, the P1 ownership rules merged).

**Read, in this order, before touching code:**
1. `AGENTS.md`: the rules. They are binding.
2. `plans/archive/p1/README.md`: **§1a (changed 2026-10-31: code first, live testing last)**, §1 (every model call is EU-hosted in P1a), the checklist §3 (binding done-when), hand-offs
   §4, rules §6.
3. `plans/archive/p1/stream-a.md`: your brief.
4. `plans/archive/p1/progress-a.md`: your progress file. It tells you where you are.
5. For the step you are about to do: the design sections linked on its item, and their "Deferred or removed"
   sections. Nothing deferred there is P1a.

**Work loop:**
1. Pick the first unticked step in `progress-a.md` whose hand-off has landed. To check hand-offs, look at the
   "Waiting on" boxes, `git log origin/main` and the GitHub issues labelled `stream-a`. If a step is blocked, move to
   the next item.
2. Build it on an `a/…` branch, touching only paths `.github/stream-ownership.txt` gives to `a` or `shared`. Code
   against the contract first: when you need the other stream's output, write or request the contract before the code.
   Pin exact package versions (the 7-day rule is suspended during development, README §1a).
3. Prove the item's done-when from `plans/archive/p1/README.md` §3 with a test, a command or a measurement. Ask yourself for a
   negative proof (the test fails without the change).
4. Write each `to-verify` result into `plans/archive/p1/results-a.md`. Tick the matching boxes in `progress-a.md` in the same
   branch.
5. Open a PR using the template. Fill in its "Design deviations" and "Hand-offs" sections. Merge once `verify.yml` is
   green.
6. When an item completes a hand-off (J3, J4, J6), open an issue labelled `stream-b` that says what is ready.

**When something is outside your paths or contradicts the design:** take the smallest sound option that keeps you
moving and record it under "Design deviations". A table or column goes to stream B as an issue naming the data-model
section. For other paths, open an issue labelled `stream-b` or `main`. Work against the agreed contract until the
change lands. Tests stay on FakeGateway; real models only in the local P1a run after J10.

**Stop and report to the person who started you** when:
- a step needs a decision only the user can make;
- every remaining step is blocked on a hand-off;
- an action fails three times with different approaches;
- or every remaining P1a step is live-only or blocked: then continue with P1b's code items (`plans/archive/p1b/`) if
  they are merged, else report.

Your report covers: what you merged (PR numbers), the boxes ticked, the open issues you are waiting on, and the
deviations you recorded.

Start by reading the files above, then tell me in three lines which step you will do first and why.
