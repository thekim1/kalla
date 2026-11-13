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

- **One gateway credential key per service (#580, B-37, A-30).** rag-api, rag-admin and rag-worker each read only
  their credential key of the gateway integration *Ume.Rag*, the secret file `Gateway__Key`; the per-level keys
  (`Gateway__Keys__<level>`) are no longer read. **Apply:** a gateway admin issues the three keys
  (production-setup.md, *Gateway credential keys*); re-run `init-deployment.sh` with your options, replacing every
  `--gateway-key-file` with `--gateway-credential-key-file api=FILE`, `admin=FILE` and `worker=FILE`. It writes the new
  files, deletes the per-level ones and names them: ask the gateway admin to revoke those keys. Then restart api, admin
  and worker. Kubernetes: `create-secrets.sh` again (the old keys leave the secrets), then `kubectl rollout restart`.
  Portainer: set `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY`, remove the
  `UME_RAG_*GATEWAY_KEY_<level>` variables, update the stack with the new stack file (`rag-bootstrap` deletes the old
  files), then restart rag-api, rag-admin and rag-worker. **Check:** no service's log shows `gateway_key_missing` or a
  gateway `401`.
- **The gateway's Data API is no longer used (#580, B-35, A-30).** rag-admin syncs the organisation, its gateway keys
  and the models from the gateway's `GET /v1/ume/context` and `GET /v1/models` with its credential key, which needs the
  scope `organisation.read` (production-setup.md, *Gateway credential keys*). The Data API key
  (`secrets/admin/OrgSync__DataApiKey`), `UME_RAG_ORG_SYNC_URL` and rag-admin's `OrgSync__BaseUrl` are gone, and
  `init-deployment.sh` refuses `--org-sync-url` and `--org-sync-key-file`. **Apply:** have the gateway admin give the
  admin's credential key the scope `organisation.read` if it lacks it. Re-run `init-deployment.sh` with your options
  minus those two: it deletes the Data API key file and names it; ask the gateway admin to revoke that Data API key.
  Then `dc up -d --wait` (rag-admin is recreated without `OrgSync__BaseUrl`); the host firewall drops the Data API from
  `llm` on its next run (`sudo systemctl start rag-firewall.service` for now). Kubernetes: remove `OrgSync__BaseUrl`
  from your overlay's `ume-rag-admin-config`, re-run `init-deployment.sh` as above, then `create-secrets.sh` (the key
  leaves `ume-rag-admin-secrets`) and `kubectl rollout restart deployment/rag-admin`; the Data API's port can leave
  your egress rules. Portainer: remove `UME_RAG_ORG_SYNC_KEY` (and `UME_RAG_ORG_SYNC_URL` if set), drop `data` from
  `COMPOSE_PROFILES` unless the gateway's Data API serves something else, and update the stack with the new stack file
  (`rag-bootstrap` deletes the key file and logs it; the network `rag-dataapi` is no longer in the file); then restart
  rag-admin and ask the gateway admin to revoke the Data API key. **Check:** `secrets/admin/OrgSync__DataApiKey` is
  gone, and the admin UI's Gateway page (`GET /api/gateway/status`) shows a recent successful organisation sync with
  no `degraded` reason.
- **Migration `UploadScannedAt` (#535, B-47).** It adds `content."Uploads"."ScannedAt"` and lets the upload state
  be `unscanned`. Additive: the previous release runs against it. Files uploaded before the migration have no
  `ScannedAt`, although they were scanned; once the admin UI shows *Not scanned for malware* (later in this
  release), set it once after the migration: `UPDATE content."Uploads" SET "ScannedAt" = "UploadedAt" WHERE
  "ScannedAt" IS NULL AND "State" IN ('clean', 'indexed');`
- **Malware scan optional (#535, A-36, A-37).** The worker's `ClamAv:Mode` is `on` (default) or `off`, and
  `rag-clamav` now runs only with the Compose profile `clamav`, which `init-deployment.sh` writes into
  `COMPOSE_PROFILES` unless `--clamav off` (production-setup.md, *Malware scan*). An existing `deploy/.env` has no
  `clamav` profile yet: **without a re-run, `rag-clamav` does not start after the update, and since the worker is still
  `on` and fails closed, every upload and fetched file waits with `clamav.unavailable`.** **Apply (Compose):** re-run
  `init-deployment.sh` with your usual options (it adds the profile and `UME_RAG_CLAMAV_MODE=on`; add `--clamav off`
  only to store files without a malware scan, then also `dc rm -sf rag-clamav`), then `dc up -d --wait`. Later re-runs
  keep the mode in `deploy/.env`. Kubernetes: `rag-clamav` moved from `components/ingestion` to `components/clamav`;
  add `- ../components/clamav` to your overlay's components (as in `example/`), or, to run without the scan, set
  `ClamAv__Mode=off` in its `ume-rag-worker-config` and delete the old resources (kubernetes.md, *Malware scan*).
  Portainer: add `clamav` to `COMPOSE_PROFILES`, or set `UME_RAG_CLAMAV_MODE=off`, and
  update the stack with the new stack file. **Check:** `dc ps rag-clamav` is healthy (or absent with `--clamav off`,
  where the worker logs that the scan is off at its start).
- **The gateway's cost and budget read-back (#580, B-45, A-35), optional:** a gateway admin adds the scope `usage.read`
  to rag-admin's credential key, and the new `UME_RAG_GATEWAY_INTEGRATION_API_URL` in `deploy/.env` (rag-admin's
  `Gateway__IntegrationApiUrl`; Kubernetes: that key in `ume-rag-admin-config`; Portainer: the variable) names the
  gateway's Admin API; unset, there is no read-back (production-setup.md, *Cost and budget read-back*).
- **Rolling update: no**, because of `DailyTotalsWithoutCost` below. Five more migrations since 0.4.0, besides
  `UploadScannedAt` above; `rag-migrate` applies them and the grants in `app-roles.sql`, with no superuser steps:
  - `GatewayKeys` adds `config."GatewayKeys"`, the mirror of the integration's assigned keys that rag-admin syncs from
    the gateway's context (#580, B-35, B-36). Additive.
  - `GatewayModelLimitsSource` adds `config."GatewayModels"."LimitsSource"` (`catalog` | `manual`, default `manual`):
    the limits of a `catalog` row come from the gateway and can't be edited (B-35). Additive.
  - `DailyUsage` adds `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"`, the tokens and cost of every
    model call per day and step (B-38, A-31), and adds the first table to the chat retention step (3 years). Additive.
  - `ObjectGatewayKeys` adds `config."Assistants"."GatewayKeyId"` and `config."Collections"."GatewayKeyId"` (B-36), the
    key each one is charged to. Additive; an existing assistant or knowledge base has none until someone picks one.
  - `DailyTotalsWithoutCost` drops `CostUsd`, `InputTokens` and `OutputTokens` from `chat."AssistantDailyTotals"`
    (B-38): tokens and cost are only in `AssistantDailyUsage` now, and the totals of days before the upgrade lose them.
    The previous release's rag-api still writes those columns, so **stop rag-api and rag-admin before `rag-migrate`**
    and start the new release after it.

## 0.4.0

- **Rolling update: yes** from 0.3.0. Three migrations, all of which 0.3.0 runs against:
  - `ProfileIndexReset` adds `content.rag_reset_profile` and `content.rag_lock_profile_if_unchanged` (editable
    embedding profiles, #554).
  - `AnswerLengthFromModel` adds `config."GatewayModels"."MaxOutputTokens"` and removes `answer.maxOutputTokens` and
    `answer.contextTokens` from every assistant version's `QuerySteps`, published ones included. 0.3.0 then uses its
    defaults (16,384 and 1,024 tokens).
  - `KeywordStatistics` adds `content."KeywordStatistics"`.

  The new grants are in `app-roles.sql`, which `rag-migrate` applies. There are no superuser steps and no role changes.
- **Answer length now follows the model (B-31).** After the upgrade every assistant answers up to its model's output
  limit, in a context of the model's window. A model with no limit on the mirror gets no `max_tokens`, so the
  provider's default applies. **Apply:** enter *Context window* and *Longest output* for each chat model on Platform →
  Gateway models. Optionally, set a ceiling for every assistant under Settings → *Longest answer, all assistants*.
  Re-enter an override on an assistant's Draft tab only where one is wanted.
- **PostgreSQL settings (A-22):** `postgresql.conf` sets `jit = off`, `random_page_cost = 1.1` and
  `effective_io_concurrency = 200`. The file is bind-mounted, so postgres reads the new one only after a restart:
  `dc restart postgres` in the window (a short outage of every service), unless `dc up` recreates postgres for a new
  `POSTGRES_IMAGE`. On a two-slot installation it can wait for the next window: nothing depends on it.
  **Check:** `dc exec -T postgres psql -U postgres -d ragdb -Atc 'SHOW jit'` says `off`.
- **Keyword statistics (A-29):** the worker writes `content."KeywordStatistics"` after each finished run and in the
  nightly maintenance, and rag-api reads them (no `ts_stat` in rag-api any more, #499). The first check of a large
  collection reads its search vectors once, on the maintenance session. The worker checks every collection once when
  it starts, so after the upgrade each collection has its stop list within minutes (until then K runs with every
  term). Nothing to configure. **Check:** `SELECT count(DISTINCT "CollectionId") FROM content."KeywordStatistics"`
  is above 0 a few minutes after `dc up` on an installation with collections of at least 100 chunks.
- **New knobs**, all with defaults: `UME_RAG_ORGANISATION_NAME` (`Admin:OrganisationName`, default `Umeå kommun`),
  `Crawl:UserAgent` (default `UmeaKommunBot/1.0`), and `Settings.Chat.prompts` (prompt texts, built-in when absent).
- **Apply (Compose):** as for 0.3.0. Replace `deploy/` with the bundle's (keep `.env`, `certs/`, `secrets/`), set the
  image pins, re-run `init-deployment.sh` with your options (plus `--render` with `browser` sources, below), then
  `dc up -d --wait`, or `./deploy/rolling-update.sh --pull` with two slots. Then follow the notes below.
- **Check:** `dc ps` all healthy; `/version` of rag-api and rag-admin says 0.4.0; `dc logs rag-migrate` lists the three
  migrations; a question in an assistant's test panel shows the answer length it sent.

- **Telemetry and health checks:** traces are now sampled at 10 % of the requests that start at a service
  (`UME_RAG_OTEL_TRACES_SAMPLER_ARG`, `always_on` for all); logs and metrics are unchanged. The .NET containers' health
  probe runs every 30 s (every 5 s while starting) instead of every 15 s, and a container is unhealthy after 3 failed
  probes (90 s) instead of 5 (75 s). Nothing to configure.

- **Renderer behind a profile (#505):** `rag-renderer` (Chromium) now runs only with the Compose profile `render`, and
  the worker gets its `Render__Endpoint` only then; without them sources with fetch mode `browser` wait with
  `render.not_configured`. **Apply:** an installation with `browser` sources re-runs `init-deployment.sh` with its
  options plus `--render` (it keeps every secret, adds `COMPOSE_PROFILES=render` to `deploy/.env` and keeps
  `secrets/worker/Render__Endpoint`), then `dc up -d --wait`. Without `browser` sources, re-run it without `--render`
  (it removes the endpoint), then `dc up -d --wait`, `dc restart rag-worker` (it reads its secret files at start) and
  `dc rm -sf rag-renderer` (`up` leaves a running container of a profile that is now off alone). Kubernetes: `create-secrets.sh --renderer` now needs `init-deployment.sh --render`
  first. **Check:** `dc ps rag-renderer` is healthy with the profile, absent without it.

- **Idle job lanes (#505):** an idle worker no longer probes the queue every 2 s per job class: a lane waits for a
  `rag_jobs` notification, its class's next due job, or at most `Worker:IdleWaitSeconds` (30 s). `Worker:PollSeconds`
  is gone (a `Worker__PollSeconds` you set is ignored). A requeued job (a transient failure, a release, a reclaim) now
  notifies `rag_jobs` too. Nothing to configure.

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
- **Gateway credential keys and the OIDC client secret:** run `init-deployment.sh` again, with all your original options
  and the new `--gateway-credential-key-file SERVICE=FILE` or `--oidc-client-secret-file`; it rewrites those files.
  Restart the services that read them. A credential key is rotated in the gateway with its 24-hour grace period first
  (production-setup.md, *Gateway credential keys*).
- **Never regenerate** the pepper, the Data Protection certificate, the blob master key or the pgBackRest passphrase:
  existing data or backups would become unreadable. There is no rotation procedure for them yet; after any future
  rotation, escrow again (runbook, *Key escrow*).
- A re-run without `--renew-certs` rewrites `deploy/.env` and keeps the old one as `deploy/.env.bak`: carry over the
  lines you added by hand (image pins, `UME_RAG_OTEL_ENDPOINT`).
