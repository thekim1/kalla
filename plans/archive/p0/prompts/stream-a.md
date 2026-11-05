# Prompt: start or resume stream A

Paste everything below the line into a new agent session started in the repo root (or in the stream's worktree).

---

You are the implementer of **stream A** of P0 in the Ume.Rag repo: the platform and the ingestion side (Postgres
image, parser, ClamAV, CI, Compose with backups and the restore drill, worker dispatcher, scale seams, Entra Bicep,
capacity scripts, docs, release). Stream B builds the application side in parallel, in its own worktree.

**Set-up (first session only):** if you are not already in a worktree for stream A, run
`git worktree add ../rag-a main` and work there. Each item's branch is `a/<item>-<slug>`, made from an up-to-date
`main`.

**Read, in this order, before touching code:**
1. `AGENTS.md`: the rules. They are binding.
2. `plans/archive/p0/stream-a.md`: your brief.
3. `plans/archive/p0/progress-a.md`: your progress file. It tells you where you are.
4. `plans/archive/p0/README.md` §2–§5: ownership, hand-offs, sync points.
5. For the step you are about to do: the plan sections linked on its item in `progress-a.md`, and
   `plans/archive/passes/weakness-review.md` wherever the item mentions it.

**Work loop:**
1. Pick the first unticked step in `progress-a.md` whose hand-off has landed. To check hand-offs, look at the
   "Waiting on" boxes, `git log origin/main` and the GitHub issues labelled `stream-a`. If a step is blocked, move to
   the next item.
2. Build it on an `a/…` branch, touching only paths `.github/stream-ownership.txt` gives to `a` or `shared`.
   Start from the gateway's file whenever the design says copy or adapt (`~/git/llmgateway`). Pin package versions
   under the 7-day rule.
3. Prove the item's done-when criterion from `plans/platform/repo-and-deployment.md` §11 with a test, a command or a
   measurement.
4. Write each `to-verify` result into `plans/archive/p0/results-a.md`. Tick the matching boxes in `progress-a.md` in the same
   branch.
5. Open a PR using the template. Fill in its "Design deviations" and "Hand-offs" sections. Merge once `verify.yml` is
   green.
6. When an item completes a hand-off (H3, H4, H7), open an issue labelled `stream-b`, or addressed to the user for H7,
   that says what is ready.

**When something is outside your paths or contradicts the design:** take the smallest sound option that keeps you
moving and record it under "Design deviations". For a change in someone else's path, open an issue labelled
`stream-b` or `main`. Work against the agreed contract until the change lands.

**Stop and report to the person who started you** when:
- a step needs a decision only the user can make;
- every remaining step is blocked on a hand-off;
- an action fails three times with different approaches;
- or P0 is complete for stream A.

Your report covers: what you merged (PR numbers), the boxes ticked, the open issues you are waiting on, and the
deviations you recorded.

Start by reading the files above, then tell me in three lines which step you will do first and why.
