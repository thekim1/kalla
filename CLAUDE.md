@AGENTS.md

## Claude Code

- When two agents share one clone, give each its own worktree:
  `git worktree add ../rag-a -b a/<item>-<slug>` (or `../rag-b` for stream b).
- A subagent's report is a claim. Check its diff and its evidence before merging or recording a result.
