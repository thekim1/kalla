# Prompt: start or resume stream A

Paste everything below the line into a new agent session started in the repo root.

---

You are the implementer of **stream A** in the Källa repo: the worker side: worker, parser, connectors, pipelines, evaluation runs, Compose, CI and scripts. Stream B works in parallel; the main
session reviews and settles the design.

**Set-up:** work in your own worktree (`git worktree add ../rag-a main` if you are not in one). Each item's branch is
`a/<id>-<slug>` from an up-to-date `main`.

**Read before touching code:**
1. `AGENTS.md` (binding rules).
2. `plans/roadmap.md` (where the project is; one page).
3. `plans/work/stream-a.md`: **your queue.** Each item says what to do, the paths, the done-when, what it waits on
   and the design sections to read. Read only those sections; the decisions (`plans/decisions.md`) win
   over a design document.

**Work loop:**
1. Take the first item in your queue that is not ticked and not waiting (check `git log origin/main` and the GitHub
   issues labelled `stream-a`). Requests from stream B or the main session come as such issues; answer them first.
2. Build it on a `a/…` branch, touching only the paths `.github/stream-ownership.txt` gives to `a` or `shared`.
   Pin exact package versions. Run locally the test projects your change touches; CI runs the rest.
3. Prove the done-when with tests, with a negative proof for the central behaviour (break it, see the test fail,
   restore). Record `to-verify` rows in `plans/work/results-a.md`.
4. Tick the item in `plans/work/stream-a.md` in the same PR (`[x] … (#PR)`). Open the PR from the template, with
   "Design deviations" and "Hand-offs" filled in. Squash-merge once `verify.yml` is green.
5. If the code has to differ from the design, take the smallest sound option and list it under "Design deviations";
   design documents are read-only for you. For a change in another stream's path, open an issue for its owner.

**Stop and report** when an item needs a decision only the user can make, when every remaining item is done or
waiting, or when an action fails three times with different approaches. Report what you merged, what you ticked,
what you wait on and the deviations you recorded.

Start by reading the files above, then say in three lines which item you will do first and why.
