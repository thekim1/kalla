# Upgrade guide and release checklist

Each release says whether it supports a **rolling update**: the previous version keeps running against the new schema
while its containers are replaced. The pilot runs **one slot** (one `rag-api`, one `rag-admin`), so every pilot upgrade
is a short, announced maintenance window whatever the answer. With two slots (`init-deployment.sh --rolling`) a "yes"
release is updated without downtime by `deploy/rolling-update.sh` ([Updates without downtime](zero-downtime-updates.md)).
Until the first production deployment releases may break compatibility (Decisions, "No backward compatibility before
production"), so check the answer every time; from then on migrations are expand/contract only and "no" is the
exception, with its own steps.

`dc` below stands for `docker compose --env-file deploy/.env`, run from the directory that holds `deploy/`.

## Unreleased

- **Reverse proxy:** the api host (and Open WebUI's) now allows 2 MB request bodies, matching rag-api's chat route;
  with 1 MB, long conversations were refused by the proxy (SA-15). A generated `deploy/proxy/nginx.conf` doesn't
  change by itself: set `client_max_body_size 2m;` in its api (and Open WebUI) server block, or `proxy-body-size: 2m`
  on the `rag-api` ingress, then reload the proxy.

## 0.3.0

- **Rolling update: yes** from 0.2.0. No schema migration and no new grants or superuser steps; `rag-migrate` runs
  and finds nothing to apply.
- **Parser image:** legacy Word, Excel, PowerPoint and RTF uploads are converted with LibreOffice inside `rag-parser`
  (#526), so the image is larger. Nothing to configure.
- **New deployment options**, all opt-in; an existing single-slot Compose installation changes nothing:
  - Updates without downtime: `init-deployment.sh --rolling` and `deploy/rolling-update.sh`
    ([zero-downtime-updates.md](zero-downtime-updates.md)). The first rolling update stops slots that had no drain
    configured, so a few requests can fail at that moment.
  - Kubernetes: `deploy/kubernetes` ([kubernetes.md](kubernetes.md)).
  - Portainer, gateway and RAG in one stack: the release asset `compose.portainer.yaml` (runbook, *Portainer and other
    hosts without shell access*).
  - SIEM: `deploy/otel/collector-siem.example.yaml`; Keycloak setups: [keycloak.md](keycloak.md).
- **Apply (Compose):** replace `deploy/` with the bundle's (keep `.env`, `certs/`, `secrets/`), set the image pins, then
  `dc up -d --wait`, or `./deploy/rolling-update.sh --pull` with two slots.
- **Check:** `dc ps` all healthy; `/version` of rag-api and rag-admin says 0.3.0; an upload of a `.doc` file is
  indexed.

## 0.2.0

### Renderer (`rag-renderer`)

- **Rolling update: yes.** No migration; a new container in the ingestion zone and a new network.
- `compose.ingestion.yaml` adds `rag-renderer` (`images/renderer`, Chromium behind Playwright's `launch-server`) on
  the new internal network `render` (172.30.18.0/24), which `rag-worker` joins. `init-deployment.sh` generates the
  renderer's secret WebSocket path once (`secrets/.render-ws-path`) and writes `secrets/worker/Render__Endpoint` and
  `secrets/renderer.env`. The release bundle carries the renderer's seccomp profile as `images/renderer/seccomp.json`
  next to `deploy/`. The host firewall rejects the new subnet like the other internal ones.
- **Apply:** keep `172.30.18.0/24` free (production-setup.md, *Host prerequisites*); re-run `init-deployment.sh` with
  your options (it keeps every existing secret); a new `RENDERER_IMAGE` (or a built `ume-rag/renderer:latest`); then
  `dc up -d --wait` and `sudo systemctl start rag-firewall.service`.
- **Check:** `dc ps rag-renderer` is healthy (a restart loop with "No usable sandbox" in `dc logs rag-renderer`: the
  host restricts user namespaces, production-setup.md, *Renderer*), and the firewall's dry run
  (`deploy/host/docker-user-firewall.sh --dry-run`) rejects `172.30.18.0/24` in `UME-RAG-INPUT`.

### ClamAV 1.4.6

- **Rolling update: yes.** No migration and no configuration change.
- `images/clamav/Dockerfile` moves from `clamav/clamav-debian:1.4.5_base` to `1.4.6_base`, which fixes
  CVE-2026-20339, CVE-2026-20345 and CVE-2026-20346. Its only image was pushed on 2026-10-26; it is pinned for
  `v0.1.0` (2026-10-31, #27; the 7-day rule does not apply during development). With 1.4.5 freshclam logged "Your ClamAV
  installation is OUTDATED".
- **Apply:** a new `CLAMAV_IMAGE` (or a rebuilt `ume-rag/clamav:latest`), then `dc up -d --wait rag-clamav`. The
  signatures in the `clamav-db` volume are reused. Nothing is scanned while clamd restarts.
- **Check:** `dc ps rag-clamav` is healthy, and `dc logs rag-clamav` no longer warns that the installation is outdated.

### Open WebUI: the feedback service account

- **Rolling update: yes.** Only the optional Open WebUI stack changes; the main stack is untouched.
- `compose.openwebui.yaml` turns password sign-in off (`ENABLE_PASSWORD_AUTH`, on only while
  `deploy/openwebui-feedback-key.sh` runs) and gives every OIDC sign-in the role `user` (`DEFAULT_USER_ROLE`; it was
  `pending`). `init-deployment.sh --openwebui-host` adds the feedback service account to `secrets/openwebui.env`.
- **Apply** (only where Open WebUI runs): re-run `init-deployment.sh` with your options, then
  `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait`, then
  `sudo deploy/openwebui-feedback-key.sh` and `dc restart rag-api`. On a store that already had users the account is
  not created: production-setup.md, *Open WebUI*, says how to add it.
- **Check:** the script ends with `ok: password sign-in refused (403)`.

## Upgrade procedure (single slot)

1. **Read** this guide's section for every release between yours and the target: *Rolling update*, migrations, new
   grants, superuser steps, settings. Review the release's image digests, SBOM and advisories.
2. **Announce** the window. `rag-api` and `rag-admin` are replaced together: the admin's test panel and answer judge run
   the same answering code as the api.
3. **Back up and check the escrow:**

   ```bash
   sudo deploy/backup-postgres.sh --type full --start-fast
   sudo deploy/escrow-keys.sh --verify /media/<escrow>
   ```

   Note the time: with the WAL archive, any later moment is restorable too (runbook, *Restore*).
4. **New files and images.** Replace `deploy/` with the release's (keep `deploy/.env`, `deploy/certs` and
   `deploy/secrets`), and set the image pins in `deploy/.env` (below). Re-run `sudo deploy/host/install.sh` when
   `deploy/host/` changed.
5. **Superuser steps**, only if the release lists them (below), all before step 6. Role caps and per-role settings
   run against the running database. With a new `POSTGRES_IMAGE`, first replace postgres alone with
   `dc up -d --wait postgres` (a short outage of every service), then update the extension.
6. **Migrate and replace:**

   ```bash
   dc up -d --wait
   ```

   Compose recreates every container whose image or settings changed. `rag-migrate` runs first; api, admin and the
   worker start only after it has finished successfully, so no service runs against an older schema. The worker is
   stopped before its replacement starts (its leases make the pause harmless; an overlap would exceed its role's
   connection limit). The parser and ClamAV are recreated only if their image changed.
7. **Verify:** `dc ps` all healthy, `/health/ready` and `/version` of api and admin, `dc logs rag-migrate` shows the
   expected migration, a test question in the admin's test panel (once it exists), and the backup-status metrics are
   back to normal within a few minutes.
8. **Record** the release, the image digests, the schema migration and the test evidence in change management.

**Rolling back.** An image rollback is safe only when the previous version supports the new schema (a release with
*Rolling update: yes*). Never run a destructive down-migration: restore the backup of step 3 in place
(`restore-pitr.sh`, runbook) and start the previous images.

## Image pins

Production runs images pinned by digest. A release bundle (from `v0.1.0`) has each digest written into the Compose
files' `${X_IMAGE:-…}` defaults by `deploy/build-release-bundle.sh`, which refuses an unpinned image; in a clone the
defaults are the local `ume-rag/<service>:latest` images built on the host. To run images from a registry, or to override one, set the variable in `deploy/.env` to `<image>@sha256:…`:

| Variable | Image |
|---|---|
| `POSTGRES_IMAGE` | `ume-rag/postgres` (pinned `pgvector/pgvector` plus pgBackRest) |
| `MIGRATIONS_IMAGE`, `API_IMAGE`, `ADMIN_IMAGE`, `WORKER_IMAGE` | the .NET services |
| `PARSER_IMAGE` | the parser |
| `CLAMAV_IMAGE` | `ume-rag/clamav` |
| `OPENWEBUI_IMAGE` | Open WebUI, in the separate `compose.openwebui.yaml` project |

Every pin, image or package, was released at least 7 days before it was pinned.

## Superuser steps

The migrator (`ume_rag_migrator`) owns ragdb but is not a superuser and cannot create or alter roles, so two kinds of
change need the bootstrap superuser. They run inside the postgres container over its socket, as the
`postgres` user (peer authentication):

```bash
dc exec -T postgres psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c '<statement>'
```

**Role caps and per-role settings.** Connection limits, timeouts and the `hnsw.*` defaults of the service roles are
set only by `deploy/postgres/init.sql`, which runs once, when the database is created. `app-roles.sql`, which the
migration service applies on every run, re-grants table privileges but cannot change a role. A release that changes a
cap or a per-role setting lists the statements here; run them on existing databases before `dc up`, for example:

```sql
ALTER ROLE ume_rag_api CONNECTION LIMIT 26;
ALTER ROLE ume_rag_api IN DATABASE ragdb SET statement_timeout = '5s';
```

New sessions pick up a per-role setting; restart the service to make all its connections use it. Check the current
values with `SELECT rolname, rolconnlimit FROM pg_roles WHERE rolname LIKE 'ume_rag_%'` and `\drds`.

**Extensions.** A new `POSTGRES_IMAGE` with a newer pgvector needs `ALTER EXTENSION vector UPDATE;` once the new
image runs (`dc up -d --wait postgres`) and before the migrations. Recreating postgres restarts the database: a short
outage of every service.

## Change types

| Change | Procedure |
|---|---|
| pgvector minor | new `POSTGRES_IMAGE`; extension step above |
| Postgres major (17 → 18) | not rolling: `pg_upgrade` where possible (else `pg_dump`/`pg_restore` into a new volume), then a new pgBackRest stanza and a full backup |
| Word list or decompounder | changes the stored search text: the release ships a worker command that recomputes it in batches; the evaluation smoke set is compared before and after |
| Parser image (Docling, models) | the parse cache key includes the parser build, so old outputs stay valid; re-parsing is an opt-in publish |
| Embedding profile | never a migration: a background re-embed with the evaluation report |

None of these has happened yet; the first release that brings one adds its commands to its section above.

## Certificate and secret rotation

- **Internal certificates** (825 days): `sudo ./deploy/init-deployment.sh --renew-certs` with `ca.key` back on the
  host, then `dc up -d --force-recreate`, and move `ca.key` off again. `--renew-ca` replaces the CA as well.
- **PKI certificates** of the public listeners: run `init-deployment.sh` again, with all your original options and the
  new `--api-cert`, `--api-key`, `--admin-cert` and `--admin-key`, then recreate api and admin.
- **Gateway keys and the OIDC client secret:** run `init-deployment.sh` again, with all your original options and the
  new `--gateway-key-file` or `--oidc-client-secret-file`; it rewrites those files. Recreate the services that read
  them.
- **Never regenerate** the pepper, the Data Protection certificate, the blob master key or the pgBackRest passphrase:
  existing data or backups would become unreadable. There is no rotation procedure for them yet; after any future
  rotation, escrow again (runbook, *Key escrow*).
- A re-run without `--renew-certs` rewrites `deploy/.env` and keeps the old one as `deploy/.env.bak`: carry over the
  lines you added by hand (image pins, `UME_RAG_OTEL_ENDPOINT`).
