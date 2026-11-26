# Upgrade guide and release checklist

Each release says whether it supports a **rolling update**: the previous version keeps running against the new schema
while its containers are replaced. The pilot runs **one slot** (one `kalla-api`, one `kalla-admin`), so every pilot
upgrade is a short, announced maintenance window whatever the answer. With two slots (`init-deployment.sh --rolling`) a
"yes" release is updated without downtime by `deploy/rolling-update.sh` ([Updates without
downtime](zero-downtime-updates.md)). Until the first production deployment releases may break compatibility (Decisions,
"No backward compatibility before production"), so check the answer every time; from then on migrations are
expand/contract only and "no" is the exception.

**A "no" release needs no manual stop on one slot.** From 0.8.0, `kalla-migrate` itself keeps the
services a migration can't run beside out of the database until their new version starts: its log says *Migration …
stops the services of role …*, the old container answers errors for the seconds until it is replaced, and the new one
gets its access back when it starts. With two slots or on Kubernetes, stop or scale down those services first, as
before. **An upgrade may skip releases** (from 0.5.1 on; each release tests it by booting an earlier release's
stack, named in its section, and applying the new file): read each skipped release's section for what it asks of you,
and do the update once.

`dc` below stands for `docker compose --env-file /etc/kalla/.env`, run from the directory that holds `deploy/`.

A pull request does not edit *Unreleased* below: it adds `changes/<branch>.upgrade.md` ([`changes/`](../changes/README.md)),
and the release preparation folds the notes in here and writes the release's *Rolling update* line.

## Unreleased

## 0.11.0

- **Rolling update: yes** from 0.10.0: three migrations, each a new table (`chat."AssistantDailyOutcomes"`,
  `chat."AssistantDailyGaps"`, `chat."DocumentDailyFeedback"`), whose grants the migration service applies at start; no
  variable changes. Paste this release's stack file (or set the image variables to 0.11.0's) and update the stack once.
  From 0.9.2 or earlier, first to 0.10.0 by its section below.
- **The gateway stays at Fors 0.13.0**: nothing to do on the gateway side.

## 0.10.0

- **Rolling update: yes** from 0.9.2: three migrations, all additive (new tables, nullable columns and one column with
  a default, wider check constraints), no variable changes. Paste this release's stack file (or set the image variables
  to 0.10.0's) and update the stack once. From 0.9.1 or earlier, straight to 0.10.0 by the 0.9.2 section below.
- **The gateway first, or together: Fors 0.13.0** (the Portainer stack pins it; rolling update yes from Fors 0.12.2; a
  separate gateway follows Fors's guide, *0.13.0*). With it the admin UI shows the organisation's logo; with an older
  gateway it keeps the built-in mark.
- **The stack's TLS proxy** (profile `proxy`): kalla-bootstrap writes its new nginx.conf at the update, but the running
  `kalla-proxy` reads it only at start. After the update, restart `kalla-proxy` (Portainer: *Containers*, select,
  *Restart*; Compose: `dc restart kalla-proxy`). Without that, users can still get `502 Bad Gateway` after signing in
  until they clear their cookies.
- **Kubernetes with ingress-nginx:** add `nginx.ingress.kubernetes.io/proxy-buffer-size: 32k` to the annotations of
  the `kalla-admin` Ingress (as in `deploy/kubernetes/example/ingress.yaml`) and apply it.
- **A proxy of your own in front of Källa or the gateway:** add `proxy_buffer_size 32k; proxy_buffers 8 32k;
  proxy_busy_buffers_size 64k; large_client_header_buffers 4 32k;` at its `http` level (Nginx Proxy Manager: each
  proxy host's *Advanced* tab, or `/data/nginx/custom/http_top.conf`), then reload it. An F5 or another proxy: allow
  response headers of at least 32 KB. **Check:** sign in with a browser that still has old cookies for the host; the
  proxy's log has no `upstream sent too big header`.
- **The nightly answer evaluation is on after the upgrade.** From the first night every published, active assistant
  whose knowledge bases have test questions answers up to 100 of them, and the judge model rates each answer: up to 100
  answers and 100 judge calls per assistant and night, charged to the assistant's gateway key. Nothing runs while no
  judge model is set. To run fewer, or none, set *Questions per assistant and night*, or switch *Run the nightly answer
  evaluation* off, in *Settings › Evaluation* (`Settings.EvaluationAnswerQuestions`, 0 = off).

## 0.9.2

- **Rolling update: yes** from 0.9.1: no migration, no variable changes, the same gateway (Fors 0.12.2). Paste this
  release's stack file (or set the image variables to 0.9.2's) and update the stack once. From 0.9.0 or earlier, straight
  to 0.9.2 by the 0.9.1 section below.
- **Sources that render JavaScript**: on 0.9.0 and 0.9.1 their pages failed with `render.unavailable` and went
  back to the queue. Nothing to do but update: the queued fetch jobs run once the new worker is up. A source whose fetch
  mode was set to plain HTTP as a workaround can be set back to the browser mode.
- **Topics:** a topic rule (*Edit processing* › *Topics*) whose `match` was written as an address
  (`https://www.umea.se/…/`) tagged nothing until now. From 0.9.2 it matches; the topics appear when the pages are next
  processed again (publish the processing, or reprocess the source).

## 0.9.1

- **Rolling update: no** from 0.9.0, though there is no migration and no variable changes: the update recreates
  `kalla-postgres` (its socket moves into the volume `kalla-postgres-socket`) and, with Fors 0.12.2 in the Portainer
  stack, the gateway's `postgres`: a short restart of each database. Update the stack once. From 0.8 or earlier, straight to 0.9.1 by the
  0.9.0 section below, without its role rename by hand.
- **The gateway first, or together: Fors 0.12.2** (the Portainer stack pins it; a separate gateway follows Fors's
  guide, *0.12.2*).
- **From 0.8 or earlier: no role rename by hand.** The one-shot `kalla-roles` renames the database roles `ume_rag_*` to
  `kalla_*` before `kalla-migrate` signs in, and on every later start changes nothing. The 0.9.0 section's steps that
  stop the services and paste `deploy/upgrade/0.9-rename-roles.sql` into a console are gone:
  - **Portainer** (with Fors 0.12.2 in the stack, whose `roles` renames the gateway's roles the same way): back up,
    look through and rename the variables, paste this release's stack file and update it once, with *Re-pull image and
    redeploy* and *Prune services* ([portainer.md](portainer.md), *Upgrade*). Nothing is stopped first. A stack that
    already renamed the roles by hand, or updated to 0.9.0 before renaming them, is updated once as well: what is done
    is skipped. If `kalla-roles` fails, its log says why; the statements by hand on `kalla-postgres` are the fallback
    (same section).
  - **Compose:** the same: `kalla-roles` renames the roles in the data that `deploy/upgrade/upgrade-to-0.9.sh` carried
    over (the script no longer signs in to the old `rag-postgres`).
  - **Kubernetes:** unchanged, by hand (0.9.0, step 2): the migration Job has the migrator's credentials only, and the
    superuser signs in only over the database's own socket.
- **New volume `kalla-postgres-socket`** (Compose and Portainer): `kalla-postgres`'s Unix socket, in memory
  (`tmpfs`), shared with `kalla-roles`. `kalla-postgres` is recreated once by the update, a restart of the database.
  Nothing to set.

## 0.9.0

- **Rolling update: no** (the rename, below). The migrations `CollectionSlugUniqueAmongLive` and `AssistantsDeletedAt`
  limit the slugs' unique indexes to rows that aren't deleted and add `config."Assistants"."DeletedAt"`;
  `PurgeDeletedSourcesAndCollections` gives the nightly retention its new steps. `kalla-migrate` runs them once the
  roles are renamed. From 0.5.x or later in one update, as for 0.8.0: read the 0.8.0 section too when you skip it.
  This release tests the update from 0.6.0's Portainer stack, with its variables renamed as below (Portainer:
  [portainer.md](portainer.md), *Upgrade*).
- **The gateway first, or together:** Källa 0.9.0 needs **Fors 0.12.1** or later. The Portainer stack holds both and
  updates them together; elsewhere, upgrade the gateway first, from 0.11 straight to 0.12.1.
- **The rename:** every name from before the rename to Källa changes, together with the gateway's rename to
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
- **Compose:** from the new release's directory, with the old installation still there, run
  `./deploy/upgrade/upgrade-to-0.9.sh` first without options: it prints its plan and changes nothing. It finds the old
  project by its containers' Compose labels (the service `rag-postgres` or `postgres`; `--old-project` when there are
  two) and where each volume's data is, from `docker inspect`. Read the plan, then run it again with `--apply`:
  - it renames the variables in `deploy/.env` (keeping `.env.pre-0.9`);
  - it stops and removes the old project's containers (`docker compose -p <old> down`; the volumes stay);
  - it copies each named volume `<old>_rag-*` into `<project>_kalla-*`. A run that stopped part-way is safe to repeat:
    a volume whose copy did not finish is copied again;
  - data on a host disk (a volume backed by a bind, or a direct bind) is not copied: the new volume is a bind-backed
    volume of the same path. A backup directory that `KALLA_BACKUP_DIR` names is left as it is;
  - it refuses an old `postgres-data`, `certs`, `secrets` or `blobs` that is missing or empty, and a new volume that
    already holds other data.

  The database roles are renamed by the release's `kalla-roles` when it starts, as in the Portainer stack. Then run
  `./deploy/init-deployment.sh` and start the release as usual. Remove the copied old volumes once it runs, never the
  host paths the new volumes point at.
- **Portainer** (one stack with the gateway; every step in [portainer.md](portainer.md), *Upgrade*): rename the roles
  first, then update once.
  1. **Stop the services, not the databases:** in *Containers*, every container of the stack except `postgres` and
     `rag-postgres` (a role that is connected cannot be renamed).
  2. **Rename the roles in both databases**, in a console (`/bin/sh`) on the old containers: on `rag-postgres` run
     `PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" psql -U postgres -d postgres -f -`, paste the
     content of `deploy/upgrade/0.9-rename-roles.sql` and end with Ctrl-D; on `postgres` the same with Fors's
     statement (its guide, *0.12.0*, *Portainer*, step 3). From here the old stack no longer starts: going back is the
     backup, or renaming the roles back.
  3. **Stop the stack**, and rename its variables: every `UME_RAG_*` to `KALLA_*`, every `RAG_*_IMAGE` to
     `KALLA_*_IMAGE`, then every `UME_*` to `FORS_*`. Write the old defaults you relied on as values:
     `KALLA_OIDC_CLIENT_ID=ume-rag-admin` for a stack that declares its sign-in (`KALLA_OIDC_CLIENT_SECRET`) and names
     no client, `FORS_OIDC_CLIENT_ID=ume-admin` where `FORS_OIDC_AUTHORITY` is set and names no client, and
     `FORS_DATA_AUDIENCE=ume-data-api` unless it is set.
  4. **Update the stack once** with the release's `compose.portainer.yaml`, with *Prune services* on. The gateway's
     bootstrap copies `ume-certs` and `ume-secrets` into `fors-certs` and `fors-secrets`, and `kalla-bootstrap` copies
     the volumes `rag-certs`, `rag-secrets`, `rag-blobs`, `rag-openwebui-data` and `rag-postgres-data` into the
     `kalla-*` volumes once (its log says so); it stops instead when the old database is still open. If the copy fails
     part-way (a full disk), free the space and update the stack again: the copy is done again from the start.
  5. Remove the old `rag-*`, `ume-certs` and `ume-secrets` volumes once the stack runs.

  **If you updated before renaming the roles** (the gateway's own guide has that order, right for a gateway alone):
  the gateway's `migrations` fails to sign in, and `kalla-bootstrap` and `kalla-postgres` wait for it. Rename the
  gateway's roles on `postgres`, update again, rename Källa's on `kalla-postgres`, and update a third time.
- **Kubernetes** (read through, not run: no Kubernetes installation has been upgraded yet). The Secrets, ConfigMaps,
  Deployments, Services, Ingresses, NetworkPolicies and the migration Job `rag-*` and `ume-rag-*` become `kalla-*`.
  The data stays where it is: the blob claim keeps its name `rag-blobs`, and the StatefulSet `postgres` keeps its name
  and so its claims `data-postgres-0` and `pgbackrest-postgres-0`.
  1. **Keep your namespace.** The example overlay now says `kalla`; the claims are in the namespace you installed in
     (`ume-rag` by the old example) and cannot move. Take the new `example/` as your overlay, carry your changes over
     (the ConfigMaps and patch targets have new names) and set your namespace in `kustomization.yaml` and
     `namespace.yaml`.
  2. Scale the old Deployments to zero (`rag-api`, `rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`,
     `rag-renderer`), then rename the roles as the database's superuser. With the `datastores` component:
     `kubectl -n <namespace> exec -i statefulset/postgres -- psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres
     < deploy/upgrade/0.9-rename-roles.sql`.
  3. Create the secrets again under their new names: `create-secrets.sh` with the options of the installation and
     `--namespace <namespace>`. It reads the certificates and secrets in `deploy/certs` and `deploy/secrets` and
     generates nothing that exists.
  4. With the `datastores` component: `kubectl -n <namespace> delete statefulset postgres`. A StatefulSet's selector
     cannot change, so the new one cannot be applied over the old; deleting the StatefulSet stops Postgres and keeps
     its two claims, which the new StatefulSet of the same name takes over. Do not delete the claims.
  5. Run `deploy.sh` with the new overlay.
  6. Delete the old objects, by name: the Deployments, Services, Ingresses and PodDisruptionBudgets `rag-*`, the Job
     `rag-migrate`, the NetworkPolicies, ConfigMaps and Secrets `ume-rag-*`, and the claim `rag-clamav-db` (the new
     `kalla-clamav-db` downloads the signatures again). **Never `rag-blobs`, `data-postgres-0` or
     `pgbackrest-postgres-0`.**
- **Host units** (`deploy/host/install.sh`): the units keep their `rag-*` names, but their state directory is
  `/var/lib/kalla` (was `/var/lib/ume-rag`). After installing the new units, point node_exporter's textfile collector
  (or the collector that reads `backup.prom`, `upload-backup.prom` and `certs.prom`) at the new directory and remove
  the old one, whose files are no longer updated. `backup-last-run` starts anew at the next backup.
- **Telemetry and the SIEM:** security events are logged under the category `Kalla.Security` (was
  `Ume.Rag.Security`), and the meter and trace source are named `Kalla` (was `Ume.Rag`). Change the collector's
  filter that forwards security events (`deploy/otel/collector-siem.example.yaml`) and any SIEM rule or collector
  filter that names the old category or source. The metric names (`rag.*`) are unchanged.

## 0.8.0

- **Rolling update: no** (the migration `DropMessageParentId`, below); on one slot nothing to do, `rag-migrate` stops
  the previous rag-api and rag-admin itself. The migration `ContinueRuns` only adds. From 0.5.x or 0.6.0 too, in one
  update (Portainer: [portainer.md](portainer.md), *Upgrade*). This release tests the update from 0.6.0's stack, not
  0.5.1's: one of 0.5.1's images is no longer on ghcr.io (a 0.5.x stack that has its images keeps working).
- **Before the update:** back up as always. Compose and Kubernetes: check the escrow with the release you run now
  (`escrow-keys.sh --verify`). Portainer: a 0.7.x or earlier stack has no escrow set yet (that release's
  `escrow-keys.sh` has no `--project`), so back up the three files of its *First deploy* instead
  ([portainer.md](portainer.md), *Upgrade*, step 1). A Compose installation made with the files of 0.7.0 or earlier
  is installed again (*Compose: no in-place upgrade*, below).
- **After the update:** escrow the keys again with the new `escrow-keys.sh` and verify the new set (*Escrow the keys
  again*, below); read the end of `rag-bootstrap`'s log.

- **Migration `DropMessageParentId`** drops `chat."Messages"."ParentMessageId"` (never written or read), which the
  previous rag-api and rag-admin still map. On one slot (Compose, Portainer) there is nothing to do: `rag-migrate` keeps
  both out of the database until their new version starts. With two slots or on Kubernetes, stop or scale down rag-api
  and rag-admin before the migration. No new variable.
- **`rag-migrate` stops what a migration can't run beside** (above). Nothing to do: the first run creates the table
  `public.ume_rag_service_fences` and the function `public.ume_rag_admit` in ragdb. **Rolling back** after a release
  marked *Rolling update: no*: the previous version is refused at start (*The database's schema is newer than this
  version can run beside*), as it should be; go back by restoring the backup, as before.
- **Portainer: `UME_INTEGRATION_API_URL` has a default,** the stack's own `adminapi`
  (`https://adminapi:8443/integration/v1`), so the cost and budget read-back needs no variable. A stack that sets it
  keeps its value; set and empty turns the read-back off.
- **Portainer: one upgrade section.** A stack on 0.5.x or 0.6.0 moves to this release in one update
  ([portainer.md](portainer.md), *Upgrade*).
- **Compose:** `init-deployment.sh` asks for no sign-in value: sign-in is set in the setup wizard
  (`https://<admin host>/setup`, the setup code in rag-admin's log), as on Portainer. Only with `--hardened` does an
  interactive run (no `--yes`) ask for the sign-in provider's address when neither `--oidc-authority` nor `deploy/.env`
  gives one: leave it empty to keep the provider in the setup wizard, or type it so the host firewall lets rag-admin
  reach it. New and optional: `--gateway-secrets-dir`, which reads the three credential keys from a gateway on the same
  host ([quick start](compose-quick-start.md)).
- **Kubernetes:** `create-secrets.sh` now generates the certificates and secrets itself, with the bootstrap of the
  Compose and Portainer stacks, and no longer reads what `init-deployment.sh` wrote. Run it on your existing
  `deploy/certs` and `deploy/secrets` with your host names (`--domain`, or `--api-host` and `--admin-host`): it keeps
  the CA, the passwords, the keys, the pepper, the Data Protection certificate and the credential keys, and issues
  the five service certificates again from the same CA (have `deploy/certs/ca.key` in place), so restart the
  deployments afterwards. The options that `init-deployment.sh` took for Kubernetes are now its own
  ([Kubernetes](kubernetes.md), *Install*); with your own database, pass `--postgres-host` instead of editing the
  connection strings.
- **The prerequisite was stated too low:** the Compose files need Docker Compose 2.33.1 or later (`gw_priority`), as
  they have since they pinned each service's default route; the guides said 2.24.
- **Open WebUI is off unless asked for.** `stack.env.example` no longer has `openwebui` in `COMPOSE_PROFILES`, and
  without the profile `rag-bootstrap` generates nothing for Open WebUI and removes rag-api's `OpenWebUi__JwtSecret`. **A
  stack that has the profile keeps it: nothing to do.** A stack that ran without the profile changes in one way:
  rag-api no longer has a key to check Open WebUI's user tokens with, which nothing used. Turning the profile on later
  generates what is missing; turning it off keeps Open WebUI's store and keys for the next time.
- **Portainer: the stack file changes nothing you set.** It is generated from `deploy/compose.rag.yaml` now, with the
  same services, volumes, networks and variables; update the stack as for any release.
- **Compose: no in-place upgrade; install again.** Compose runs `deploy/compose.rag.yaml` with the Portainer stack's
  names (`rag-postgres`, the volumes `rag-postgres-data`, `rag-certs`, `rag-secrets`, `rag-config`, the `RAG_*_IMAGE`
  variables), and `rag-bootstrap` keeps the certificates and secrets in volumes instead of `deploy/certs` and
  `deploy/secrets`. Its published ports bind `0.0.0.0` by default, as the Portainer stack's (before: `127.0.0.1`).
  No Compose installation exists outside tests, so there is no migration. To carry an installation's data over, keep
  before the new install: the database volume `ume-rag_postgres-data` with the role passwords that
  open it (`deploy/secrets/postgres/*_password`), `deploy/secrets/.pepper`, `deploy/secrets/keys/rag-blob-internal`
  and `deploy/certs/data-protection.pfx` (without these three the database is unusable), and the `ume-rag_rag-blobs`
  volume. Then run the new `init-deployment.sh` (`--help`) and, before the first `up`, copy the old database volume's
  contents into `ume-rag_rag-postgres-data` and the kept files to the same paths in the `rag-secrets` volume
  (`.pepper`, `postgres/`, `keys/`) and the `rag-certs` volume (`data-protection.pfx`), owned as `rag-bootstrap` leaves
  them; `up` then runs `rag-bootstrap`, which keeps them and copies them to each service. **This carry-over
  is written from how `rag-bootstrap` treats existing files and has not been run**: try it on a copy first.
- **Escrow the keys again** after the update, with the new `deploy/escrow-keys.sh` (Portainer: `--project <stack>`,
  from the release bundle), then `--verify` it. The new set also holds the pepper, the database roles' passwords, the
  conversation id keys, the Data Protection certificate and the internal CA, in a new layout (`rag-secrets/…`,
  `rag-certs/…`). `--verify` fails on a set made by an earlier release, so keep the old set until the new one prints
  `PASS`. The pepper and certificate copies kept separately until now are in the set, and can go once it is sealed.

## 0.7.0

- **Rolling update: yes** from 0.6.0. Migrations `IdentityOidcSettings`, `SetupMode` and `OpenWebUiSignInSettings` only
  add; Open WebUI is recreated once. The Portainer stack moves the gateway to `v0.11.0`, also rolling.
- **Before the update:** nothing must be added or changed. A deployment that sets its authority and
  `UME_RAG_OIDC_CLIENT_SECRET` never sees a setup wizard. Only a Portainer stack **without**
  `UME_INTEGRATION_MANIFEST` (keys issued by hand) decides first whether the gateway may create three new credential
  keys from the stack file's default, or sets the variable empty (below, and `docs/portainer.md`, *Upgrade*).
- **After the update:** read the end of `rag-bootstrap`'s log (a retired variable the stack still sets is named
  there), and check that you sign in as before.
- **`UME_DOMAIN` names Ume.Rag's hosts (setup wizard S0).** Rolling update, no migration. Portainer: `UME_DOMAIN`
  gives rag-api `rag.<domain>`, its admin `rag-admin.<domain>` and Open WebUI `chat.<domain>`; each `UME_RAG_*_HOST`
  still overrides its own name. Compose: `init-deployment.sh --domain DOMAIN` does the same for `--api-host`,
  `--admin-host` and, when another `--openwebui-*` option asks for Open WebUI, `--openwebui-host`. **Apply:** nothing; a stack that sets `UME_RAG_API_HOST` (and the others)
  without `UME_DOMAIN` keeps its names. To switch, set `UME_DOMAIN` and remove the host variables whose value is the
  default. **Check:** `rag-bootstrap`'s certificates and the proxy's names are unchanged (no `issuing certificate` for
  rag-api or rag-admin in its log unless a name changed).
- **Sign-in is set in the setup wizard (identity design §19).** rag-admin starts without an authority and,
  on a first run, opens its setup wizard at `https://<admin host>/setup` with a setup code on its output: the wizard
  shows the redirect URIs, takes the client secrets (stored encrypted with Data Protection) and saves only after a test
  sign-in by a platform admin. A value the deployment sets wins and is locked in the wizard; an empty one is unset.
  Open WebUI's start wrapper (`deploy/openwebui/start-with-sign-in.py`) asks rag-api for its sign-in settings and
  restarts Open WebUI within a minute when they change. The stack files no longer pass `Oidc__*` defaults, and
  `stack.env.example` has no secret left. Migrations `IdentityOidcSettings`, `SetupMode` and `OpenWebUiSignInSettings`
  add columns and a table and widen a check: **rolling update: yes** for the schema (Open WebUI is recreated).
  - **An existing deployment keeps working and never sees the wizard** when it has its authority and client secret in
    its variables (Portainer: `UME_OIDC_AUTHORITY` or `UME_RAG_OIDC_AUTHORITY`, and `UME_RAG_OIDC_CLIENT_SECRET`;
    Compose: `--oidc-authority` and `--oidc-client-secret-file`; Kubernetes: `Oidc__Authority` and the secret file):
    rag-admin counts setup as done at its first start.
  - **A token-shape variable you changed** (`UME_RAG_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`,
    `_GROUP_CLAIM`, `_EXTRA_SCOPE`, `_TENANT_ID`, `_REFUSE_GUESTS`, `_CLIENT_ID`): keep the line. The stack files pass
    each only when it is set, so the code's defaults (the old defaults; `email` is always requested) apply otherwise, or
    the wizard's values. `UME_RAG_OIDC_REFUSE_GUESTS` now reaches rag-admin as a file: Portainer's `rag-bootstrap` writes
    it; Compose passes `--oidc-refuse-guests true|false` to `init-deployment.sh` instead of the variable in
    `deploy/.env`; Kubernetes keeps its `Oidc__RefuseGuests` literal, which the base no longer sets.
  - **Open WebUI's variables** (`UME_RAG_OPENWEBUI_OIDC_*`) now feed rag-api and rag-admin, which serve them to Open
    WebUI's start; they keep working as deployment values. `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=open-webui` may go (it is
    the default; set, it locks the wizard's field). Without a client secret Open WebUI signs in as a public client
    (PKCE). A confidential client's secret stays in the stack, or is entered in the wizard's Open WebUI step after a re-open.
  - **Portainer apply:** step by step in [portainer.md](portainer.md#upgrade). In short: paste the new `compose.portainer.yaml` and update the stack (`rag-bootstrap` writes
    `admin/Setup__Secret`, and Open WebUI's secret to rag-api's and rag-admin's secret files). A leftover
    `UME_RAG_OIDC_CLIENT_SECRET=CHANGE` declares a wrong secret and keeps the wizard closed: `rag-bootstrap`'s checklist
    names it; remove it.
  - **Compose apply** (an existing 0.6.0 deployment; steps 1–4 and 8–10 of *Upgrade procedure (Compose host)* below
    stay as they are):
    1. Replace `deploy/` with the release's, keeping `deploy/.env`, `deploy/certs` and `deploy/secrets`, and set the
       image pins.
    2. Re-run `init-deployment.sh` with your options. The `--oidc-*` options are optional and, in 0.7.0, never prompted; a re-run
       without `--oidc-authority` or `--oidc-client-id` keeps the value in `deploy/.env`, and the client secret file
       in `secrets/admin` stays, so the sign-in stays declared and the wizard stays closed. It writes
       `secrets/admin/Setup__Secret`. With the host firewall, keep `--oidc-authority`: rag-admin reaches only that
       provider.
    3. With Open WebUI: pass **`--openwebui-oidc-client-secret-file` again**. The secret moved to `secrets/api` and
       `secrets/admin/OpenWebUi__Oidc__ClientSecret`; the old `OAUTH_CLIENT_SECRET` line in `secrets/openwebui.env` is
       no longer read and may be deleted. Pass `--openwebui-oidc-client-id` and `--openwebui-oidc-provider-url` only
       if they differ from the defaults. A provider on another host than the authority needs `--allow openwebui=HOST`
       for the host firewall.
    4. If you used `UME_RAG_OIDC_REFUSE_GUESTS` in `deploy/.env`: pass `--oidc-refuse-guests true|false` instead.
    5. `dc up -d --wait`, then `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d`.
  - **Kubernetes apply:** `create-secrets.sh` after the re-run of `init-deployment.sh` (it carries
    `admin/Setup__Secret`), then `deploy.sh`. The base no longer sets `Oidc__ClientId`, the claims, `Oidc__ExtraScopes__0`
    or `Oidc__RefuseGuests`: add to your overlay any of them you had changed.
  - **To move a declared deployment to the wizard** (and the secrets out of the variables): remove the client secret
    (Portainer: `UME_RAG_OIDC_CLIENT_SECRET`, and Open WebUI's `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, which is then
    entered in the wizard's Open WebUI step; Compose: delete
    `secrets/admin/Oidc__ClientSecret`; Kubernetes: delete the key from `ume-rag-admin-secrets`), set
    `UME_RAG_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) to a new value, update the stack, then enter the values in the
    wizard with the code from rag-admin's log. Until you finish, sign-in uses the stored values, which are now
    incomplete: do it in one go. Keep the authority variable if the gateway shares it (it stays locked).
  - **Check:** a declared deployment's rag-admin logs *The deployment declares the sign-in* once and signs in as
    before; a new one prints `rag-admin: setup mode is open until … Setup code: …`. rag-openwebui's (or open-webui's)
    log has `rag-openwebui: starting with OIDC sign-in from rag-api's sign-in settings`, and Open WebUI's sign-in page
    shows the button. `UME_RAG_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) stays empty until you want to open the
    wizard again; remove it before restoring a database.
- **A leftover variable is named.** Rolling update, no migration, no new variable to set. A Portainer stack that
  still sets a variable this version no longer reads (the ones the sections below say to remove, back to 0.5.0's; the
  per-level `UME_RAG_*GATEWAY_KEY_<level>` variables of 0.4.0 and before are not named) works
  as before; `rag-bootstrap`'s checklist now names each with what replaced it, and Home in the admin UI shows platform
  admins a notice with the names. **Apply:** nothing is required; to clear the notice, remove the named variables from
  the stack's variables and update the stack. **Check:** `rag-bootstrap`'s log has no line *is set and no longer read*.
- **One typed value in the Portainer stack, `UME_DOMAIN`, and the gateway's own setup wizard.** The stack pins
  the gateway's `v0.11.0`: **rolling update: yes**, its migrations `SignInSettings` and `SetupMode` add tables,
  columns and grants (the gateway's upgrade guide, *Portainer*). No variable has to be added and none is retired (the
  gateway's new `UME_SETUP_REOPEN` is optional).
  - **Host names:** the gateway's two default under `UME_DOMAIN` as Ume.Rag's three do (`llm.<domain>`,
    `llm-admin.<domain>`), and its public URL to `https://<gateway host>`. rag-admin links to the gateway's admin
    (among them the setup wizard's link to the gateway's wizard) at `UME_ADMIN_PUBLIC_URL`, else
    `https://<gateway admin host>`. Each `UME_*_HOST` and public URL that is set still wins.
  - **Sign-in:** `stack.env.example` no longer sets `UME_OIDC_AUTHORITY`. Unset, the gateway's admin opens its own
    setup wizard (the code is in `adminapi`'s log; `UME_SETUP_REOPEN` opens it again) and rag-admin has no declared
    authority: its wizard suggests the provider saved in the gateway's. So a new stack sets up the gateway's wizard
    first, then Ume.Rag's. Set, `UME_OIDC_AUTHORITY` declares the provider for both, as before, and the gateway's
    wizard stays closed; `UME_OIDC_CLIENT_SECRET` is used only together with it.
  - **The integration manifest:** the stack file gives the gateway's `UME_INTEGRATION_MANIFEST` a default,
    `deploy/portainer/integration-manifest.json` (the integration `Ume.Rag` and its three credential keys, every key
    limited to `OnPrem` and `Eu`, no owner). The gateway places an integration without an owner in a team and
    department of its own, code `ume-rag`, which a gateway admin moves to another team with its keys. Unset, the
    default applies; set, the stack's own manifest wins (0.6.0's, with an owner: nothing changes); set and empty,
    nothing is provisioned.
  - **Portainer apply:** step by step in [portainer.md](portainer.md#upgrade). A stack that sets
    `UME_OIDC_AUTHORITY` and `UME_INTEGRATION_MANIFEST` (every 0.6.0 stack made from its `stack.env.example`) changes
    nothing: paste the new file and update the stack. **A stack without `UME_INTEGRATION_MANIFEST`** (keys issued by
    hand, kept from 0.5.x) gets the default from now on: the gateway creates the keys `rag-api`, `rag-admin` and
    `rag-worker` in the integration `Ume.Rag` where they don't exist (and the integration, in a team `ume-rag`, if none
    has that name), writes their secrets to `<stack>_integration-secrets`, and changes no existing key. The services
    keep the keys their `UME_RAG_*_GATEWAY_KEY` variables set, which win, so the new keys stay unused. To create none,
    add `UME_INTEGRATION_MANIFEST` with an empty value before the update; or revoke the three afterwards (a revoked key
    is not created again). Optional afterwards: set `UME_DOMAIN` and remove the host and public URL lines whose value
    is its default.
  - **Compose and Kubernetes:** nothing. This change touches only the Portainer stack; a gateway deployed with its own
    Compose bundle or on Kubernetes follows the gateway's upgrade guide for `v0.11.0`, which keeps a deployment
    that sets the authority as it is.
  - **Check:** the gateway's `bootstrap` log has `bootstrap: sign-in to the admin console is declared by the
    stack's variables (UME_OIDC_AUTHORITY), …` (an existing stack) or `… is set in the setup wizard: …` (a new one);
    `adminapi`'s log has no `Setup code` line for a declared stack; `migrations`' log says
    `… 'Ume.Rag' applied, 0 of 3 credential keys created` for a stack whose keys exist.

## 0.6.0

- **Rolling update: no** from 0.5.x. Migration `RemoveWorkerConcurrency` drops a column the old rag-admin still reads.
  With 0.6.0's or 0.7.0's own `rag-migrate`, stop the old rag-admin before it runs; a later release's `rag-migrate`
  does it itself (the top of this guide). rag-api and rag-worker are not affected.
- **Before the update:** give rag-api's and rag-admin's credential keys the residencies `OnPrem` and `Eu` in the
  gateway (C9, below). **Portainer:** paste the `v0.6.0` release's `compose.portainer.yaml` (gateway v0.10.3, Ume.Rag 0.6.0) and add
  `UME_INTEGRATION_API_URL` (C11); keep your `UME_RAG_*_GATEWAY_KEY` variables unless you move to the manifest (C7).
- **After the update:** create the embedding profile if none exists (C5) and check *Models* shows `act-as-key`.

- **Fewer Portainer variables.** The stack sets Open WebUI's feedback address while the
  `openwebui` profile is on and the malware-scan mode from the `clamav` profile, so `UME_RAG_OPENWEBUI_BASE_URL` and
  `UME_RAG_CLAMAV_MODE` are no longer read; `UME_RAG_API_PUBLIC_URL` defaults to `https://<UME_RAG_API_HOST>` and is
  needed only for a public URL with a port. **Apply:** nothing is required (a leftover value is ignored); remove the two
  variables when you paste the new stack file. **Check:** `rag-bootstrap`'s checklist names nothing about them.
- **Open WebUI's client is set up by the deployment.** rag-migrate creates the client *Open WebUI*
  (*Managed by the deployment*) from a key the deployment generates, and rag-api trusts Open WebUI's users **only**
  for that client. An Open WebUI client made by hand stays in the registry but is no longer trusted, so until the
  assistants' audiences move, Open WebUI users see no models. **Apply:**
  1. Before the update, note which assistants the hand-made client offers (*Apps and keys* → its drawer, or each
     assistant's *Who can use it*), and its information class, history and feedback days.
  2. Portainer: paste the new stack file and remove `UME_RAG_OPENWEBUI_API_KEY` and `UME_RAG_OPENWEBUI_CLIENT_ID`
     from the variables; Compose: re-run `init-deployment.sh` with your options, without `--openwebui-api-key-file`
     and `--openwebui-rag-client-id` (both are unknown options now). Update the stack.
  3. In *Apps and keys*, open *Open WebUI* (managed): set the class, history and feedback days to the old client's
     (it starts as `internal`, keeps history, 90 days), and tick the same assistants. Save.
  4. Disable the hand-made client and revoke its key.
  **Check:** a test user signs in to Open WebUI and sees the assistants; chat answers no `401 user_jwt_invalid`
  (what the hand-made client's requests get now). A new deployment key later: delete `.openwebui-rag-api-key` in the secrets volume
  (Compose: `secrets/.openwebui-rag-api-key`) and update the stack; rag-migrate revokes the old one.
- **The embedding profile is created in the admin UI.** rag-migrate no longer seeds it, and
  `UME_RAG_EMBEDDING_MODEL`, `_DIMENSIONS`, `_REQUEST_DIMENSIONS`, `_MAX_INPUT_TOKENS` and `_MAX_RESIDENCY` (Compose,
  Portainer) and `EmbeddingProfile__*` with the `ume-rag-migrations-config` map (Kubernetes) are gone. An existing
  profile stays as it is. **Apply:** remove those variables from the stack or `deploy/.env`; Kubernetes: take the new
  base and drop the `ume-rag-migrations-config` entry from your overlay. A new deployment: a platform admin creates the
  profile under *Models → Embedding profiles → Create profile* ([portainer.md](portainer.md) step 9).
- **The Open WebUI feedback key is minted by the stack.** A one-shot job (Portainer: `rag-openwebui-setup`;
  Compose: `open-webui-setup` in `compose.openwebui.yaml`) mints the feedback service account's key after Open WebUI
  starts, with password sign-in off, into `openwebui-feedback/OpenWebUi__AdminApiKey`, which rag-api now reads
  (`Security__ExtraSecretsDirectory`). `UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `UME_RAG_OPENWEBUI_PASSWORD_AUTH` and
  `deploy/openwebui-feedback-key.sh` are gone; `rag-bootstrap` and `init-deployment.sh` delete the old
  `api/OpenWebUi__AdminApiKey`. **Apply:** Portainer: paste the new stack file, remove the two variables, update the
  stack. Compose: re-run `init-deployment.sh` with your options (it creates `secrets/openwebui-feedback`), update the
  main stack, then `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d`. Until the job has
  minted the new key (a minute after Open WebUI starts), the import has none and skips its run. **Check:** the job's log says *minted*, and rag-api's next feedback import
  runs (within 15 minutes).
- **Optional TLS proxy in the Portainer stack.** `proxy` in `COMPOSE_PROFILES` starts `rag-proxy`, nginx
  with your certificate (`UME_RAG_PROXY_CERTS_DIR`) in front of the five host names ([portainer.md](portainer.md#tls-proxy)).
  Nothing changes without the profile.
- **The residency ceiling comes from the gateway.** Each service's ceiling is its credential key's allowed
  residencies in the gateway's context; `UME_RAG_GATEWAY_MAX_RESIDENCY` (`Gateway__MaxResidency`, Kubernetes
  `ume-rag-config`) is gone. **Apply before the update:** in the gateway's admin, give rag-api's and rag-admin's credential
  keys the residencies `OnPrem` and `Eu` only while there is no on-prem model hardware (the worker's already are); a key that allows every
  residency would otherwise let a public assistant answer at `External`. Then remove the variable. **Check:** each
  service logs *the credential key's ceiling: Eu* at start.
- **Role groups are a setting.** Migration `IdentityRoleGroups` adds `config."Settings"."IdentityRoleGroups"`;
  *Settings → Role groups* in RAG's admin maps sign-in groups to roles, read at each sign-in.
  `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER` and `_VIEWER` are gone;
  `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` stays as the break-glass and is merged with the setting. **Apply:** if you set
  any of the four, enter the same groups on that page right after the update and remove the variables; until then, a
  user who had a role only through one of those groups doesn't have it. With the shared Keycloak the roles come in the token and nothing changes.
- **The organisation's name comes from the gateway.** RAG's admin UI names the organisation from its gateway
  context's `organisation_name` (the gateway admin console's branding; gateway v0.10.2, feature `organisation-name`).
  `UME_RAG_ORGANISATION_NAME` (Compose), `UME_ORGANISATION_NAME` (Portainer, for rag-admin) and `Admin__OrganisationName`
  (Kubernetes, `ume-rag-admin-config`) are no longer read. **Apply:** set the organisation's name in the gateway's admin
  console (branding) if it isn't, and remove the variable. With an older gateway, or an empty name, the texts name no
  organisation. **Check:** RAG's sign-in page names the organisation.
- **The Integration API's address comes from the gateway.** rag-admin reads the address of the gateway's
  Integration API (the cost and budget read-back) from its gateway context's `integration_api_url` (gateway v0.10.2,
  feature `integration-api-url`). `UME_RAG_GATEWAY_INTEGRATION_API_URL` (Compose, Portainer) and
  `Gateway__IntegrationApiUrl` (Kubernetes, `ume-rag-admin-config`) are no longer read. **Apply:** if you used the
  read-back, the gateway admin sets the gateway's `UME_INTEGRATION_API_URL` to the Admin API's address as rag-admin
  reaches it, **ending in `/integration/v1`** (your old value plus `/integration/v1`); then remove the variable. Compose
  with the host firewall: the variable no longer adds the host to `llm`, so run `docker-user-firewall.sh` with
  `--allow llm=<gateway admin host>:<port>` (a drop-in for `rag-firewall.service` that adds it to `ExecStart`). Without the gateway's address there is no
  read-back. **Check:** `GET /api/gateway/status` has `integration.configured: true` within 5 minutes of the update.
- **Migration `RemoveWorkerConcurrency`** drops `config."Settings"."WorkerConcurrency"`, which nothing
  read; per-instance concurrency stays `Worker__Concurrency__<class>`. The old rag-admin still selects the column
  (its settings row through EF), so its settings pages fail between the migration and its own update; stop it first.
  rag-api and rag-worker name their columns and are not affected.
- **The `admin-console` client is set up by the deployment.** rag-migrate creates the test panel's client
  `admin-console` (type `internal`, *Managed by the deployment*) in every deployment; one made by hand becomes the managed
  one, its settings kept and any key of it revoked. Its keys can no longer be made or changed in the admin. Migration
  `ClientsManagedPerName` makes the managed clients unique per type and name instead of per type, and rag-migrate finds
  them by both, so the name of a client managed by the deployment (*Open WebUI*, `admin-console`) can no longer be
  changed in the admin. **Apply:**
  nothing. **Check:** *Apps and keys* shows `admin-console` as managed; rag-migrate's log says "The admin test panel's
  client admin-console: Created" (or `Adopted`, then `Unchanged`).
- **Migration `ChunkSetsEmbeddingProfileIndex`** adds an index on `content."ChunkSets"."EmbeddingProfileId"`
  for the admin's embedding-profile *in use* checks. Nothing else changes; the old services run against it.
- **The Portainer stack pins the gateway's `v0.10.3`.** Against `v0.10.2` (next entry): rolling update, no migration,
  no new variable; the only change is the gateway's Ume.Rag preset (*Integrations → Add from manifest*), whose `rag-api` and `rag-admin` keys now
  allow only `OnPrem` and `Eu`, as the stack's `UME_INTEGRATION_MANIFEST` already does. Keys made from the `v0.10.2`
  preset keep their residencies: set them to `OnPrem` and `Eu` in the gateway's admin (0.6.0, *Before the update*).
- **The gateway creates Ume.Rag's credential keys (the gateway's `v0.10.2`, in the stack's `v0.10.3`).**
  The gateway's own upgrade notes for 0.10.2 apply (rolling update, no migration; a new volume
  `<stack>_integration-secrets`; a read grant on `Branding.OrganisationName`). The gateway's `migrations` now applies
  `UME_INTEGRATION_MANIFEST` (in `stack.env.example`): it creates the integration `Ume.Rag`, its owner team and the keys
  `rag-api`, `rag-admin` and `rag-worker` where they don't exist, by name, and never changes one that does;
  `rag-bootstrap` copies their secrets to the services. `UME_RAG_*_GATEWAY_KEY`, set, still wins. `rag-bootstrap` now
  waits for the gateway's `migrations`, and rag-admin joins the gateway's `edge` network to reach `adminapi`.
  **Apply** to a stack whose keys were made by hand: paste the new stack file and keep your three `UME_RAG_*_GATEWAY_KEY`
  variables; add `UME_INTEGRATION_API_URL=https://adminapi:8443/integration/v1` (the cost read-back's address, handed
  to rag-admin in the gateway's context). Add `UME_INTEGRATION_MANIFEST` only if your integration is named `Ume.Rag` and
  its keys `rag-api`, `rag-admin` and `rag-worker` (then nothing is created, and the existing keys' secrets are written
  to the volume, audited as a reveal), or once you want the gateway to issue new keys under those names; set its
  `teamCode` to the integration's owner team. A new stack: keep both from `stack.env.example`, change `departmentCode`.
  **Check:** the gateway's `migrations` log says *'Ume.Rag' applied*; `rag-bootstrap`'s checklist names no gateway key;
  *Models* in RAG's admin shows `act-as-key`.

- **Kubernetes NetworkPolicies are on by default.** `base` now carries default-deny and every flow the stack
  needs; `components/networkpolicies` is gone and the Ingress controller's rule is `components/ingress-controller`
  (namespace `ingress-nginx`). The worker's and freshclam's 80/443 egress reaches public addresses only. **Apply:** in
  your overlay replace `../components/networkpolicies` with `../components/ingress-controller` (change its namespace
  with the example's patch if yours differs); add the example overlay's egress rules for a gateway at a private address
  or on 8443, each `Crawl__AllowedPrivateRanges` range, and pgBackRest's S3 store. **Check:** with an enforcing CNI,
  sign in to the admin UI, ask a question, crawl a page and upload a file ([kubernetes.md](kubernetes.md), *Network
  policies*).
- **Certificate expiry is watched.** **Apply (Compose):** run `deploy/host/install.sh` again to install and
  enable `rag-cert-expiry.timer` (daily; it fails below 60 days and writes `certs.prom`). Portainer: nothing; the
  checklist names expiring certificates on every deploy. **Check:** `systemctl status rag-cert-expiry.timer`.

## 0.5.1

- **Rolling update: yes** from 0.5.0: no migration, no configuration change for Compose or Kubernetes.
- **Portainer:** paste the new `deploy/portainer/compose.portainer.yaml` over the stack (it pins the gateway's and
  Ume.Rag's images and adds the optional `openwebui` profile) and update it. From this release a changed gateway key or
  Open WebUI feedback key applies without restarts; the end of `rag-bootstrap`'s log lists what is still missing
  ([portainer.md](portainer.md)).

## Before 0.5.0

A deployment older than 0.5.0 first moves to 0.5.1 by the sections for 0.2.0 to 0.5.1 in an older copy of this guide,
[`docs/upgrade-guide.md` at `v0.7.0`](https://github.com/thekim1/kalla/blob/v0.7.0/docs/upgrade-guide.md), as
[portainer.md](portainer.md) (*Upgrade from before 0.5.0*) does. From 0.5.x, one update to this release.

## Upgrade procedure (Compose host)

The one procedure for a host that runs the bundle with `docker compose` (one slot; with two, [Updates without
downtime](zero-downtime-updates.md); the Portainer stack, [portainer.md](portainer.md#upgrade)). A release's own section
above says what that release adds to these steps (the older ones still repeat some of them). `deploy/doctor.sh` comes
first and last: it is read-only, and exits 1 on what blocks an upgrade.

1. **Check the host** with the new release's doctor. Unpack the release's bundle beside the live one (here
   `~/kalla-<version>/`) and point its doctor at the live `/etc/kalla/.env`; 0.11.0 and older have no doctor of their
   own, and without `--env-file` the doctor reads the unpacked bundle's `.env`, which doesn't exist:

   ```bash
   sudo ~/kalla-<version>/deploy/doctor.sh --env-file /etc/kalla/.env --escrow-dir /media/<escrow>
   ```

   It names the project, the services' versions, where each volume's data is on the host and its free space, the
   overlays and profiles, the pgBackRest stanza, the WAL backlog and the newest backups, the newest key set, and which
   deploy directory the host units run from (run from the unpacked bundle, it warns that they run from another
   directory: expected until step 5). Fix every `BLOCK` line before going on, and read the `warn` lines: each one that
   applies is in *Your host differs from the bundle* below. Keep the output for step 9.
2. **Read** this guide's section for every release between yours and the target: *Rolling update*, migrations, new
   grants, superuser steps, settings. Review the release's image digests, SBOM and advisories.
3. **Announce** the window. `kalla-api` and `kalla-admin` are replaced together: the admin's test panel and answer judge
   run the same answering code as the api.
4. **Back up and check the escrow:**

   ```bash
   sudo deploy/backup-postgres.sh --type full --start-fast
   sudo deploy/escrow-keys.sh --verify /media/<escrow>
   ```

   Note the time: with the WAL archive, any later moment is restorable too (runbook, *Restore*).
5. **New files and images.** Replace `deploy/` with the release's, or unpack it beside the old one. What is yours is
   outside it (the internal certificates and the generated secrets are in the Docker volumes):
   - the env file and the gateway's CA are in the state directory, `/etc/kalla` (Decisions "Compose host layout and
     upgrades"). Point the new `deploy/` at them with its own `sudo deploy/init-deployment.sh` (no options: it keeps
     every value and writes `COMPOSE_FILE` with the new directory), and remove or update any image pins you set
     (*Image pins* below). A host from before keeps its env file in the old `deploy/.env`: copy that file into the new
     `deploy/` first, and this run moves it to `/etc/kalla/.env`;
   - any other directory a `KALLA_*_DIR` names (your own trust, certificate or gateway-secrets directory): keep it
     outside `deploy/`. Their binds are not created when missing, so `up` fails without them;
   - your own Compose overlays. Keep them outside `deploy/` (for example in `/etc/kalla/`), named by an absolute path
     in `COMPOSE_FILE`, so the next upgrade doesn't replace them. Don't drop one from `COMPOSE_FILE` to make `up` start:
     an overlay that puts the data on a host disk would then start on empty volumes.

   Re-run `sudo deploy/host/install.sh` from the new `deploy/`: the units then act on it, through
   `/etc/kalla/host.env`. `sudo deploy/host/install.sh --check` says whether they do.
6. **Superuser steps**, only if the release lists them (below), all before step 7. Role caps and per-role settings
   run against the running database. With a new `KALLA_POSTGRES_IMAGE`, first replace kalla-postgres alone with
   `dc up -d --wait kalla-postgres` (a short outage of every service), then update the extension.
7. **Migrate and replace.** Nothing is stopped first, also for a release marked *Rolling update: no*: `up` runs the
   migration while the old containers are still running, `kalla-migrate` keeps the ones that can't run beside it out of
   the database, and they are replaced right after.

   ```bash
   dc up -d --wait
   ```

   Compose recreates every container whose image or settings changed. `kalla-bootstrap` runs first, then
   `kalla-migrate`; api, admin and the worker start only after it has finished successfully, so no service runs against
   an older schema. The worker is stopped before its replacement starts (its leases make the pause harmless; an
   overlap would exceed its role's connection limit). The parser and ClamAV are recreated only if their image changed.
8. **Verify:** `dc ps` all healthy, `/health/ready` and `/version` of api and admin, `dc logs kalla-migrate` shows the
   expected migration, a test question in the admin's test panel, and the backup-status metrics are back to normal
   within a few minutes.
9. **Check the host again**, now with the live `deploy/doctor.sh --escrow-dir /media/<escrow>` (as root): the new
   versions, the same project, volumes and host paths as in step 1, the units warning of step 1 gone, and no `BLOCK`
   line.
10. **Record** the release, the image digests, the schema migration, both doctor outputs and the test evidence in
    change management.

> **Your host differs from the bundle** when step 1 shows any of these. Each one changes a step above.
>
> - **Another project name** than `kalla` (a `warn` line under *Project*): every `dc` command needs it. Set
>   `COMPOSE_PROJECT_NAME=<name>` in `/etc/kalla/.env` once, or Compose starts a second, empty deployment named `kalla`
>   beside yours. With two projects on the host, pass `--project <name>` to the doctor.
> - **Data on a host disk** (a *bind-backed volume* or a *bind* under *Volumes and free space*): this procedure leaves
>   the data where it is. Keep the overlay or the volume definitions that put it there (step 5): the bundle's
>   `compose.host-paths.yaml` with `KALLA_DATA_ROOT` (`init-deployment.sh --data-root`), or an overlay of yours outside
>   `deploy/`. Check in step 9 that the same host paths are listed. An upgrade script that carries volumes over
>   (0.9.0's `upgrade-to-0.9.sh`) points the new volumes at the same host paths instead of copying; read its plan
>   before `--apply`.
> - **Changed ports or a hand-edited Compose file**: carry the change over in step 5 as an overlay of your own outside
>   `deploy/`, not as an edit of the release's files, which step 5 replaces. Compare `dc config --services` and
>   `dc config --images` before and after (plain `dc config` prints the gateway credential keys from `/etc/kalla/.env`).
> - **Host units from another directory or for another project** (a line under *Host units*): they run the old
>   bundle's scripts (or none, when that directory is gone), or back up a project that isn't this one. Units from before
>   0.12.0 have no `host.env` and act on `deploy/.env`'s project. Re-run `sudo deploy/host/install.sh` from the new
>   `deploy/` in step 5; with a project other than `kalla`, set `COMPOSE_PROJECT_NAME` in `/etc/kalla/.env` first, or
>   pass `--project <name>`.
> - **No backups configured** (no `compose.backup.yaml`, a `BLOCK` line under *Backups*): there is nothing to restore
>   from if step 7 fails, step 4's `backup-postgres.sh` fails, and step 8's backup metrics don't exist. Set up a backup
>   target first ([production-setup](production-setup.md)). To go on without one (a test host you can rebuild), pass
>   `--no-backups-ok` to the doctor in steps 1 and 9, and replace step 4 with a copy of the data:
>   `dc stop`, then copy each path the doctor lists under *Volumes and free space* (the host path of a bind-backed
>   volume, or the volume's `_data` directory) with `sudo cp -a` to a disk of its own, and `dc start`. Rolling back is
>   then copying it back with the services stopped.

**Rolling back.** An image rollback is safe only when the previous version supports the new schema (a release with
*Rolling update: yes*). Never run a destructive down-migration: restore the backup of step 4 in place
(`restore-pitr.sh`, runbook) and start the previous images.

## Image pins

Production runs images pinned by digest. A release bundle (from `v0.1.0`) has each digest written into the Compose
files' `${KALLA_X_IMAGE:-…}` defaults by `deploy/build-release-bundle.sh`, which refuses an unpinned image; in a clone
the defaults of `deploy/compose.rag.yaml` are the last release's pins. To run images from a registry or built on the
host, or to override one, set the variable in `/etc/kalla/.env` (`init-deployment.sh --set
KALLA_API_IMAGE=<image>@sha256:…`; `--set KALLA_API_IMAGE=` removes it again):

| Variable | Image |
|---|---|
| `KALLA_POSTGRES_IMAGE` | `kalla/postgres` (pinned `pgvector/pgvector` plus pgBackRest) |
| `KALLA_MIGRATIONS_IMAGE`, `KALLA_API_IMAGE`, `KALLA_ADMIN_IMAGE`, `KALLA_WORKER_IMAGE` | the .NET services |
| `KALLA_PARSER_IMAGE` | the parser |
| `KALLA_CLAMAV_IMAGE` | `kalla/clamav` |
| `KALLA_RENDERER_IMAGE` | `kalla/renderer` |
| `OPENWEBUI_IMAGE` | Open WebUI, the profile `openwebui` (pinned in `deploy/compose.rag.yaml`) |
| `KALLA_PROXY_IMAGE` | nginx of the TLS proxy, the profile `proxy` (pinned in `deploy/compose.rag.yaml`) |

Every pin, image or package, was released at least 7 days before it was pinned.

## Superuser steps

The migrator (`kalla_migrator`) owns ragdb but is not a superuser and cannot create or alter roles, so two kinds of
change need the bootstrap superuser. They run inside the kalla-postgres container over its socket, as the
`postgres` user (peer authentication):

```bash
dc exec -T kalla-postgres psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c '<statement>'
```

**Role caps and per-role settings.** Connection limits, timeouts and the `hnsw.*` defaults of the service roles are
set only by `deploy/postgres/init.sql`, which runs once, when the database is created. `app-roles.sql`, which the
migration service applies on every run, re-grants table privileges but cannot change a role. A release that changes a
cap or a per-role setting lists the statements here; run them on existing databases before `dc up`, for example:

```sql
ALTER ROLE kalla_api CONNECTION LIMIT 26;
ALTER ROLE kalla_api IN DATABASE ragdb SET statement_timeout = '5s';
```

New sessions pick up a per-role setting; restart the service to make all its connections use it. Check the current
values with `SELECT rolname, rolconnlimit FROM pg_roles WHERE rolname LIKE 'kalla_%'` and `\drds`.

**Extensions.** A new `KALLA_POSTGRES_IMAGE` with a newer pgvector needs `ALTER EXTENSION vector UPDATE;` once the new
image runs (`dc up -d --wait kalla-postgres`) and before the migrations. Recreating kalla-postgres restarts the
database: a short outage of every service.

## Change types

| Change | Procedure |
|---|---|
| pgvector minor | new `KALLA_POSTGRES_IMAGE`; extension step above |
| Postgres major (17 → 18) | not rolling: `pg_upgrade` where possible (else `pg_dump`/`pg_restore` into a new volume), then a new pgBackRest stanza and a full backup |
| Word list or decompounder | changes the stored search text: the release ships a worker command that recomputes it in batches; the evaluation smoke set is compared before and after |
| Parser image (Docling, models) | the parse cache key includes the parser build, so old outputs stay valid; re-parsing is an opt-in publish |
| Embedding profile | never a migration: a background re-embed with the evaluation report |

None of these has happened yet; the first release that brings one adds its commands to its section above.

## Certificate and secret rotation

- **Internal certificates** (825 days): `kalla-bootstrap` re-issues a service certificate at the first `dc up -d` within
  30 days of its expiry (it needs `ca.key` in the `kalla-certs` volume), and the end of its log names what to restart:
  `dc restart kalla-postgres kalla-parser`. A new internal CA: delete `ca.pem` and `ca.key` in `kalla-certs`,
  `dc up -d --wait`, then `dc restart` (production-setup.md, *Internal TLS*).
- **PKI certificates** of the public listeners: put the new `api.crt` and `api.key`, `admin.crt` and `admin.key` in
  the directory `KALLA_PKI_DIR` names and run `dc up -d`: `kalla-bootstrap` copies them at every start. kalla-api and
  kalla-admin serve theirs through Kestrel, which watches the files; if a client still sees the old certificate, restart
  them.
- **Gateway credential keys and the OIDC client secret:** run `init-deployment.sh` with only the new
  `--gateway-credential-key-file SERVICE=FILE` or `--set-file KALLA_OIDC_CLIENT_SECRET=FILE` (it keeps the rest of
  `/etc/kalla/.env`), then `dc up -d`: `kalla-bootstrap` rewrites those files. Restart the services that read them. A credential key is rotated in the gateway with its 24-hour grace period first
  (production-setup.md, *Gateway credential keys*).
- **Never regenerate** the pepper, the Data Protection certificate, the blob master key or the pgBackRest passphrase:
  existing data or backups would become unreadable. There is no rotation procedure for them yet; after any future
  rotation, escrow again (runbook, *Key escrow*).
- A re-run of `init-deployment.sh` changes only what its options name and keeps every other variable in
  `/etc/kalla/.env` (image pins and `FORS_OTEL_ENDPOINT` included); only comments and empty values are dropped.
