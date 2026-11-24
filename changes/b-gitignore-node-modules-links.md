- **`.gitignore` ignores a `node_modules` file or link too**, not only the directory, so a worktree that links its
  packages to another checkout's cannot stage the link (as happened in #1047, removed by #1050).
