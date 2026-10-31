# Prompt: start or resume stream B (P1a)

Paste everything below the line into a new agent session started in the repo root (or in the stream's worktree).

---

You are the implementer of **stream B** of P1a in the Ume.Rag repo: the serving side of the walking skeleton (schema,
retrieval and the knowledge API, the chat API, the admin API the platform team sets the pilot up with through Scalar,
the gateway client side of release 1, RAG's realm additions for the shared Keycloak). Stream A builds the worker side
in parallel, in its own worktree, and **waits on your schema**, so item 1 comes first.

**Set-up (first session only):** if you are not already in a worktree for stream B, run
`git worktree add ../rag-b main` and work there. Each item's branch is `b/<item>-<slug>`, made from an up-to-date
`main`. Nothing starts before J1 (`v0.1.0` tagged, the P1 ownership rules merged).

**Read, in this order, before touching code:**
1. `AGENTS.md`: the rules. They are binding.
2. `plans/p1/README.md`: **§1a (changed 2026-10-31: code first, live testing last)**, §1 (every model call is EU-hosted in P1a), the checklist §3 (binding done-when), hand-offs
   §4, rules §6, open items §8.
3. `plans/p1/stream-b.md`: your brief.
4. `plans/p1/progress-b.md`: your progress file. It tells you where you are.
5. `plans/platform/data-model.md`, all of it. You own every migration.
6. For the step you are about to do: the design sections linked on its item, and their "Deferred or removed"
   sections. Nothing deferred there is P1a.

**Work loop:**
1. Pick the first unticked step in `progress-b.md` whose hand-off has landed. To check hand-offs, look at the
   "Waiting on" boxes, `git log origin/main` and the GitHub issues labelled `stream-b`. If a step is blocked, move to
   the next item.
2. Build it on a `b/…` branch, touching only paths `.github/stream-ownership.txt` gives to `b` or `shared`. Start from
   the gateway's file whenever the design says copy or adapt (`~/git/llmgateway`). Pin exact package versions (the
   7-day rule is suspended during development, README §1a).
3. Prove the item's done-when from `plans/p1/README.md` §3 with tests, with a negative proof where you can.
4. Write each `to-verify` result into `plans/p1/results-b.md`. Tick the matching boxes in `progress-b.md` in the same
   branch.
5. Open a PR using the template. Fill in its "Design deviations" and "Hand-offs" sections. Merge once `verify.yml` is
   green.
6. When an item completes a hand-off (J2, J5, J7, J9), open an issue labelled `stream-a` that says what is ready. J8
   (realm additions) goes to the user in an issue labelled `main`, for the gateway session.
7. Table or column requests from stream A arrive as issues labelled `stream-b`. Answer each one with a migration,
   ahead of your other work.

**When something is outside your paths or contradicts the design:** take the smallest sound option that keeps you
moving and record it under "Design deviations". For a change in someone else's path, open an issue labelled
`stream-a` or `main`. Work against the agreed contract until the change lands. Tests stay on FakeGateway.

**Stop and report to the person who started you** when:
- a step needs a decision only the user can make;
- every remaining step is blocked on a hand-off;
- an action fails three times with different approaches;
- or every remaining P1a step is live-only or blocked: then continue with P1b's code items (`plans/p1b/`) if
  they are merged, else report.

Your report covers: what you merged (PR numbers), the boxes ticked, the open issues you are waiting on, and the
deviations you recorded.

Start by reading the files above, then tell me in three lines which step you will do first and why.
