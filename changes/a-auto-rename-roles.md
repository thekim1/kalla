- **The database roles are renamed by the stack:** the new one-shot `kalla-roles` runs before every `kalla-migrate`
  (Compose and the Portainer stack) and renames the roles `ume_rag_*` from before the rename to Källa to `kalla_*`
  (`deploy/upgrade/0.9-rename-roles.sql`, now also ending a session still signed in under an old name), so the upgrade
  from 0.8 or earlier needs no console and no SQL. Where the roles have the new names it changes nothing. It signs in
  as the Postgres superuser over `kalla-postgres`'s Unix socket (by peer, no password, no network), which now lives in
  the in-memory volume `kalla-postgres-socket`. With Fors 0.12.2, whose own one-shot renames the gateway's roles, the
  Portainer upgrade from 0.5.x or later is: back up, rename the variables, paste the stack and update it once.
- **Fixed:** `deploy/upgrade/upgrade-to-0.9.sh` and the upgrade tests' `deploy/tests/rename-0.9.sh` wrote a variable
  they add onto the last line of an env file that did not end in a newline (`FORS_INTEGRATION_MANIFEST=KALLA_OIDC_...`).
