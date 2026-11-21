- **From 0.8 or earlier: no role rename by hand.** The one-shot `kalla-roles` renames the database roles `ume_rag_*` to
  `kalla_*` before `kalla-migrate` signs in, and on every later start changes nothing. The 0.9.0 section's steps that
  stop the services and paste `deploy/upgrade/0.9-rename-roles.sql` into a console are gone:
  - **Portainer** (with Fors 0.12.2 in the stack, whose `roles` renames the gateway's roles the same way): back up,
    look through and rename the variables, paste this release's stack file and update it once, with *Re-pull image and
    redeploy* and *Prune services* ([portainer.md](portainer.md), *Upgrade*). Nothing is stopped first. A stack that
    already renamed the roles by hand, or updated to 0.9.0 before renaming them, is updated once as well: what is done
    is skipped. If `kalla-roles` fails, its log says why; the statements by hand on `kalla-postgres` are the fallback
    (same section).
  - **Compose:** `deploy/upgrade/upgrade-to-0.9.sh` still renames the roles in the old `rag-postgres`; `kalla-roles`
    then finds nothing to rename.
  - **Kubernetes:** unchanged, by hand (0.9.0, step 2): the migration Job has the migrator's credentials only, and the
    superuser signs in only over the database's own socket.
- **New volume `kalla-postgres-socket`** (Compose and Portainer): `kalla-postgres`'s Unix socket, in memory
  (`tmpfs`), shared with `kalla-roles`. `kalla-postgres` is recreated once by the update, a restart of the database.
  Nothing to set.
