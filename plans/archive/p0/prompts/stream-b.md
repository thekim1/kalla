# Prompt: start or resume stream B

Paste everything below the line into a new agent session started in the repo root (or in the stream's worktree).

---

You are the implementer of **stream B** of P0 in the Ume.Rag repo: the application and the serving side (database
schema and roles, Swedish decompounder, config-version library, keys, FakeGateway and the gateway client, identity
and the admin BFF, the admin UI skeleton and write path). Stream A builds the platform side in parallel, in its own
worktree, and **waits on your schema**, so item 1 comes first.

**Set-up (first session only):** if you are not already in a worktree for stream B, run
`git worktree add ../rag-b main` and work there. Each item's branch is `b/<item>-<slug>`, made from an up-to-date
`main`.

**Read, in this order, before touching code:**
1. `AGENTS.md`: the rules. They are binding.
2. `plans/archive/p0/stream-b.md`: your brief.
3. `plans/archive/p0/progress-b.md`: your progress file. It tells you where you are.
4. `plans/archive/p0/README.md` §2–§5: ownership, hand-offs, sync points.
5. `plans/platform/data-model.md`, all of it. You own every migration.
6. For the step you are about to do: the plan sections linked on its item in `progress-b.md`, and
   `plans/archive/passes/weakness-review.md` wherever the item mentions it.

**Work loop:**
1. Pick the first unticked step in `progress-b.md` whose hand-off has landed. To check hand-offs, look at the
   "Waiting on" boxes, `git log origin/main` and the GitHub issues labelled `stream-b`. If a step is blocked, move to
   the next item.
2. Build it on a `b/…` branch, touching only paths `.github/stream-ownership.txt` gives to `b` or `shared`.
   Start from the gateway's file whenever the design says copy or adapt (`~/git/llmgateway`): BFF, claim mapping,
   keys, ServiceDefaults conventions, tests. Pin package versions under the 7-day rule.
3. Prove the item's done-when criterion from `plans/platform/repo-and-deployment.md` §11 with tests.
4. Write each `to-verify` result into `plans/archive/p0/results-b.md`. Tick the matching boxes in `progress-b.md` in the same
   branch.
5. Open a PR using the template. Fill in its "Design deviations" and "Hand-offs" sections. Merge once `verify.yml` is
   green.
6. When an item completes a hand-off (H2 when the migrations are merged, H5, H7), open an issue labelled `stream-a`,
   or addressed to the user for H7, that says what is ready.
7. Table or column requests from stream A arrive as issues labelled `stream-b`. Answer each one with a migration,
   ahead of your other work.

**When something is outside your paths or contradicts the design:** take the smallest sound option that keeps you
moving and record it under "Design deviations". For a change in someone else's path, open an issue labelled
`stream-a` or `main`. Work against the agreed contract until the change lands.

**Stop and report to the person who started you** when:
- a step needs a decision only the user can make;
- every remaining step is blocked on a hand-off;
- an action fails three times with different approaches;
- or P0 is complete for stream B.

Your report covers: what you merged (PR numbers), the boxes ticked, the open issues you are waiting on, and the
deviations you recorded.

Start by reading the files above, then tell me in three lines which step you will do first and why.
