- **Rolling update: no.** Every name from before the rename to Källa changes, together with the gateway's rename to
  Fors 0.12.1 (its own guide, "0.12.0" and "0.12.1"; from Fors 0.11 go straight to 0.12.1, not through 0.12.0, which
  loses the stored provider credentials and sign-in client secret, thekim1/fors#116): stop the old version, rename the
  database roles, start the new one. Back up the database and the files a restore needs first, and escrow the keys
  again afterwards.
- **Renamed without a fallback:**
  - **Variables:** `UME_RAG_*` becomes `KALLA_*` (`UME_RAG_API_HOST` → `KALLA_API_HOST`, …; the rest of each name is
    unchanged), `RAG_*_IMAGE` becomes `KALLA_*_IMAGE`, and the gateway's `UME_*` becomes `FORS_*` (`UME_DOMAIN` →
    `FORS_DOMAIN`). Rename `UME_RAG_*` before `UME_*`, so that none becomes `FORS_RAG_*`.
  - **Services, volumes and networks:** `rag-*` becomes `kalla-*` (`kalla-api`, `kalla-postgres-data`,
    `kalla-edge`, …). The internal certificates are issued again for the new host names on the first start.
  - **Database roles:** `ume_rag_migrator`, `ume_rag_api`, `ume_rag_admin` and `ume_rag_worker` become `kalla_*`
    (`deploy/upgrade/0.9-rename-roles.sql`, as the Postgres superuser).
  - **Images:** `ghcr.io/thekim1/kalla/*` (`postgres`, `migrations`, `api`, `admin`, `worker`, `parser`, `clamav`,
    `renderer`); the release bundle is `kalla-X.Y.Z.tar.gz`.
  - **Defaults:** the admin's sign-in client is `kalla-admin` (was `ume-rag-admin`). A deployment that declares its
    sign-in and relied on the old default sets `KALLA_OIDC_CLIENT_ID=ume-rag-admin` (the Compose script does that); one
    set in the setup wizard keeps its client. The backup repository's default path is `/kalla` (was `/ume-rag`): an
    `s3://` target without a path gets `/ume-rag` appended, so the existing backups stay in use.
  - **Keycloak:** Källa's realm files name the realm `$(env:FORS_KEYCLOAK_REALM:-fors)`, as Fors 0.12's own. A Keycloak
    kept from before the rename keeps realm `ume`: set `FORS_KEYCLOAK_REALM=ume` where its import runs (Fors's
    upgrade script does that for the gateway's bundled Keycloak; do the same for Källa's standalone realm).
- **What stays readable:** everything an installation has encrypted (the encrypted uploads, the sign-in client secrets
  saved in the setup wizard, the stored key secrets). The names that are part of the ciphertext keep their names from
  before the rename. The public host names (`rag.`, `rag-admin.`, `chat.<domain>`) stay too.
- **What keeps working:** keys issued before the rename (`ume-rag-sk-…`, `ume-rag-pk-…`), and the gateway credential
  keys' files under `Ume.Rag/` in the integration secrets (read when `Kalla/` has none). In the gateway, rename the
  integration `Ume.Rag` to `Kalla` under *Integrations* before provisioning from Fors 0.12's manifest, or a second
  integration is created (Fors's guide, *Integrations*).
- **Compose:** from the new release's directory, with the old installation running, run
  `./deploy/upgrade/upgrade-to-0.9.sh`. It renames the variables in `deploy/.env` (keeping `.env.pre-0.9`), stops the
  old project `ume-rag` except Postgres, renames the roles, removes the old containers and copies the volumes
  `ume-rag_rag-*` to `kalla_kalla-*` (the backup volumes too). Then run `./deploy/init-deployment.sh` and start the
  release as usual. Remove the old volumes once it runs.
- **Portainer** (one stack with the gateway):
  1. **Stop the stack**, and rename its variables: every `UME_RAG_*` to `KALLA_*`, every `RAG_*_IMAGE` to
     `KALLA_*_IMAGE`, then every `UME_*` to `FORS_*` (with Fors's own defaults to keep, its guide, *Portainer*). A
     stack that declares its sign-in (`KALLA_OIDC_CLIENT_SECRET`) and names no client adds
     `KALLA_OIDC_CLIENT_ID=ume-rag-admin`.
  2. **Update the stack** with the release's `compose.portainer.yaml`, with *Prune services* on. `kalla-bootstrap`
     copies the volumes `rag-certs`, `rag-secrets`, `rag-blobs`, `rag-openwebui-data` and `rag-postgres-data` into the
     `kalla-*` volumes once (its log says so); it stops instead when the old database is still open. `kalla-migrate`
     then fails to sign in, because the roles do not have their new names yet.
  3. **Rename the roles:** open a console (`/bin/sh`) on `kalla-postgres` and run
     `PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" psql -U postgres -d postgres -f -`, then paste
     the content of `deploy/upgrade/0.9-rename-roles.sql` and end with Ctrl-D. Do the same for the gateway's `postgres`
     (Fors's guide), then **redeploy the stack**.
  4. Remove the old `rag-*` volumes once the stack runs.
- **Kubernetes:** the Secrets, ConfigMaps, Deployments and Services `rag-*` become `kalla-*`. Scale the old
  Deployments to zero, run `deploy/upgrade/0.9-rename-roles.sql` as the database's superuser, create the secrets again
  with `create-secrets.sh` (it reads the existing certificates and secrets), apply the new manifests and delete the
  old objects.
