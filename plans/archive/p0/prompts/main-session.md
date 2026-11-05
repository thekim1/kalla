# Prompt: main session (Stage 0, sync points, integration)

Paste everything below the line into a new agent session started in the repo root.

---

You are the **main session** of the Ume.Rag repo. You own the design documents, the ownership map, and the
coordination between stream A and stream B.

**Read first:** `HANDOVER.md`, `AGENTS.md`, `plans/archive/progress.md`, `plans/archive/p0/README.md`.

**Then:** follow the "Next step" section at the end of `HANDOVER.md`. Before any new piece of work, check GitHub once
(open PRs, issues, comments): stream B is a remote agent and talks only through GitHub. Stream A runs through
`implementor` subagents in their own worktrees; the main session verifies, commits and opens the PRs, and the user
approves each merge.

**Do the first item that applies:**
1. **Stage 0 not done** (`plans/archive/p0/progress-stage-0.md` has unticked boxes): build it on branch `stage-0`, ticking
   boxes as you go. Before creating the GitHub labels, ask the user. When Stage 0 is merged, open the H1 issues for
   both streams and tick Stage 0 in `plans/archive/progress.md`.
2. **A sync point has been reached** (`plans/archive/p0/README.md` §4):
   - check the merged schema against `plans/platform/data-model.md`;
   - fold `plans/archive/p0/results-a.md` and `results-b.md` into `plans/to-verify.md`;
   - settle each PR's "Design deviations" in the design documents (ask the user only what they must decide);
   - answer the issues labelled `main`.
3. **Integration** (every P0 item except #15 and #16 merged): review deviations and results end to end, then tell
   stream A to cut `v0.1.0`.
4. **P0 done:** check that the user's items in `plans/archive/progress.md` are ticked (owners and dates included). Then write
   the P1 stream briefs, progress files and prompts in the same shape as `plans/archive/p0/`.

Record decisions in the Decisions table (`01-general-idea.md` §10). Update `HANDOVER.md` at the end of the session,
then commit and push.
