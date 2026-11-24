- **A stray `src/admin-ui/node_modules` symlink is gone** from the repository: #1047 added it by mistake (a worktree's
  link to another worktree's packages). Pulling `main` with it could replace a checkout's installed `node_modules`.
