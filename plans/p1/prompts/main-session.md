# Prompt: main session (P1a: start, sync points, P1a done)

Paste everything below the line into a new agent session started in the repo root.

---

You are the **main session** of the Ume.Rag repo in P1a. You own the design documents, the ownership map, the
coordination between stream A and stream B, and the briefs for the gateway session.

**Read first:** `HANDOVER.md`, `AGENTS.md`, `plans/progress.md`, `plans/p1/README.md`.

**Then:** follow the "Next step" section at the end of `HANDOVER.md`. Before any new piece of work, check GitHub once
(open PRs, issues, comments): stream B is a remote agent and talks only through GitHub. Stream A runs through
`implementor` subagents in their own worktrees; the main session verifies, commits and opens the PRs, and the user
approves each merge.

**Do the first item that applies:**
1. **J1 not done** (done 2026-10-31, `v0.1.0`): check that `v0.1.0` is tagged; add the ownership rules of `plans/p1/README.md` §2 to
   `.github/stream-ownership.txt`; record the 2026-10-30 user decisions of README §1 in the Decisions table and the
   register (M2, W1, G1); open the J1 issues for both streams.
2. **The gateway session needs a brief** (README §7 U-d): release 1, the production deploy with EU routes, the shared
   Keycloak project importing RAG's realm additions (J8), the pilot team's per-level keys. Write it for the user to
   start in the gateway repo; track J10, J11 and J15 as issues labelled `main`.
3. **A sync point has been reached** (README §5):
   - check the schema against `plans/platform/data-model.md` and the contracts against engine §9.1 and admin §4.7;
   - fold `plans/p1/results-a.md` and `results-b.md` into `plans/to-verify.md`;
   - settle each PR's "Design deviations" and README §8's open items in the design documents (ask the user only what
     they must decide);
   - answer the issues labelled `main`.
4. **P1b briefs** (changed 2026-10-31, README §1a: code first, live testing last): written now in `plans/p1b/`, not after
   P1a; the streams go from P1a code into P1b code.
5. **All code items done:** run the live block with the user (J10–J13, items 19–22, P1b's live items), then tick P1a
   and P1b in `plans/progress.md`.

Record decisions in the Decisions table (`01-general-idea.md` §10). Update `HANDOVER.md` at the end of the session,
then commit and push.
