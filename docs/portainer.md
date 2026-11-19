# Deploying and upgrading with Portainer (0.8.x)

How to run Ume.Rag with the LLM gateway `v0.11.0` as one Portainer stack: a first deploy on a new VM, and the upgrade
of a `0.5.x` or later stack in one update (an older one: *Upgrade from before 0.5.0*). The stack is configured only through
environment variables, for hosts where `init-deployment.sh` can't run. Background and the full variable list: the
header of [`deploy/portainer/compose.portainer.yaml`](../deploy/portainer/compose.portainer.yaml), the runbook's
[*Portainer and other hosts without shell access*](runbook.md#portainer-and-other-hosts-without-shell-access), and
[production-setup.md](production-setup.md#portainer-gateway-and-rag-in-one-stack).

> **Each release has its own stack file.** From `v0.8.0` an upgrade from `0.5.x` or later takes only the new
> release's file, in one update (*Upgrade*); releases in between need not be installed. Up to `v0.7.0` an upgrade went
> one release at a time, with each release's own file.

## Which file

Use the **`compose.portainer.yaml` asset of the `v0.8.0` release** (or later), or the same file in the repository at
`deploy/portainer/compose.portainer.yaml` once the release's pin PR has merged. It pins the gateway's images
(`v0.11.0` in the `v0.8.0` and `v0.7.0` assets; `v0.10.3` in the `v0.6.0` asset; `v0.10.1` in the `v0.5.1` asset) and Ume.Rag's by digest, so no image variable is needed, and has Open WebUI as the profile `openwebui`. The `v0.5.0` asset predates both: it has
no Open WebUI and leaves `GATEWAY_IMAGE`, `ADMINAPI_IMAGE` and `MIGRATIONS_IMAGE` to be set by hand. The one typed
value, the two setup wizards and the default integration manifest of *First deploy* need a file built from the
gateway's `v0.11.0` or later: the `v0.6.0` asset has none of them.

## What you need

- A VM with Docker (Compose v2) and Portainer. Sizing: 4 vCPU, 16 GB RAM and 100 GB SSD for a pilot (floor 4 vCPU,
  8 GB, 50 GB; R1 sets the final numbers); ClamAV alone takes about 1 GB of RAM.
- **Registry access:** Ume.Rag's images on `ghcr.io/thekim1/ume-rag/*` are private. In Portainer, *Registries → Add
  registry → Custom*: `ghcr.io`, a GitHub user and a token with `read:packages`. The gateway's images are public.
- An **OIDC provider** (the shared Keycloak realm, AD FS or Entra ID) with clients for the gateway's admin
  (`ume-admin`) and Ume.Rag's admin (`ume-rag-admin`). The clients must exist
  before you set up sign-in. Their redirect URIs: `https://llm-admin.<domain>/signin-oidc` and
  `/signout-callback-oidc` for `ume-admin`, `https://rag-admin.<domain>/signin-oidc` and `/signout-callback-oidc` for
  `ume-rag-admin` (the two setup wizards show them,
  ready to copy). The client secrets are entered in the setup wizards, not in the stack.
- **Host names** and **real certificates** for the gateway API, the gateway admin, rag-api and Ume.Rag's admin. The
  services serve certificates from their own internal CAs, so something in front
  terminates TLS: your own reverse proxy (routes and body sizes:
  [production-setup.md](production-setup.md#reverse-proxy-and-pki)), or the stack's optional **proxy profile** (*TLS
  proxy* below).
- The **embedding and chat models** on the gateway, EU-hosted for now.
- **Open WebUI, the chat for users, is optional** and not part of the first deploy: an installation that only serves
  its API to your own applications never needs it. It takes one more client, host name and profile: *Adding Open
  WebUI*, below, at the first deploy or any time later.

## TLS proxy

With `proxy` in `COMPOSE_PROFILES` the stack runs `rag-proxy`: nginx on `UME_RAG_PROXY_BIND:UME_RAG_PROXY_PORT`
(`0.0.0.0:443`) in front of all five host names, with the routes and body sizes of production-setup.md. It re-encrypts
to the services and verifies them against their internal CAs, so nothing else needs to be published.

1. On the host, put one certificate for every name (four; five with Open WebUI; SAN or wildcard) in a directory, as `tls.crt` (with its chain)
   and `tls.key`, readable by uid 101 (`chown 101 tls.key && chmod 0400 tls.key`), and set
   `UME_RAG_PROXY_CERTS_DIR` to it (default `/etc/ume-rag/proxy-certs`). Without it `rag-proxy` doesn't start.
2. Give each service a host name of its own. `UME_DOMAIN` names them all: the gateway API `llm.<domain>`, the
   gateway's admin `llm-admin.<domain>`, rag-api `rag.<domain>`, its admin `rag-admin.<domain>` and, with the profile
   `openwebui`, Open WebUI `chat.<domain>` (`UME_GATEWAY_HOST`, `UME_ADMIN_HOST`, `UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST` and
   `UME_RAG_OPENWEBUI_HOST` override one each). A name shared with another service gets no route, and `rag-bootstrap`'s
   checklist says which. The public URLs are then plain `https://<host>`, which is their default: leave
   `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_PUBLIC_URL` and `UME_RAG_API_PUBLIC_URL` unset (the OIDC redirect URIs have no
   port).
3. Bind the other published ports to loopback (`UME_GATEWAY_BIND`, `UME_ADMIN_BIND`, `UME_RAG_API_BIND`,
   `UME_RAG_ADMIN_BIND` = `127.0.0.1`), so only the proxy is reachable.
4. After the first deploy, list the proxy as a trusted proxy in both admins (the gateway's *Settings → Trusted proxies*,
   Ume.Rag's *Settings → Network*): its address on the stack's `edge` and `rag-edge` networks (Portainer: *Networks*),
   or the clients' addresses are the proxy's.

**Renewing the certificate:** replace both files and restart `rag-proxy` (*Containers*, select, *Restart*).

## Internal certificates

`rag-bootstrap` keeps RAG's internal CA (10 years) and the service certificates (825 days) in `rag-certs`. On every
deploy it re-issues a service certificate that expires within 30 days, and the checklist at the end of its log:

- says **restart `rag-postgres` `rag-parser`** after re-issuing theirs (*Containers*, select, *Restart*). They read
  their certificates only at start, and a stack update recreates neither when its configuration is unchanged. rag-api,
  rag-admin and rag-worker serve theirs through Kestrel, which watches the files; if a client still sees the old
  certificate, restart those too. Without the restart Postgres keeps serving the old certificate, and once it
  expires every service's database connection fails.
- names **every certificate that expires within 60 days**. For a service certificate, update the stack once fewer than
  30 days remain (nothing needs to change): `rag-bootstrap` then re-issues it.

The same checklist names **every variable the stack still sets that this version no longer reads**, with what
replaced it (*… is set and no longer read (…): remove it from the stack's variables*). Such a variable has no effect;
Home in the admin UI shows platform admins the same names until they are removed and the stack is updated.

The checklist is written only when the stack is deployed. Update the stack at least once a year, or, with a shell on
the host, run `deploy/host/cert-expiry.sh --certs-dir /var/lib/docker/volumes/<stack>_rag-certs/_data` as root daily
(cron or a systemd timer): it names each certificate that expires within 60 days and then exits non-zero.

**Replacing the CA** (the checklist names it 60 days before it expires): delete `ca.pem` and `ca.key` in `rag-certs`
(`docker run --rm -v <stack>_rag-certs:/c alpine rm /c/ca.pem /c/ca.key`) and update the stack. `rag-bootstrap`
creates a new CA and re-issues every service certificate; then restart every Ume.Rag service and `rag-proxy`, which
read the CA only at start.

## First deploy

1. **Create the stack.** *Stacks → Add stack*, a name you will keep (the volumes are found by it), *Web editor*: paste
   the file. Pick the `ghcr.io` registry.
2. **Variables:** *Environment variables → Load variables from .env file* with
   [`deploy/portainer/stack.env.example`](../deploy/portainer/stack.env.example), then replace the one value its
   comments mark CHANGE, your domain. It is not a secret:

   ```text
   UME_DOMAIN=example.se        # llm., llm-admin., rag. and rag-admin.example.se
   ```

   `UME_DOMAIN` gives the host names: the gateway API `llm.<domain>` (its public URL `https://llm.<domain>`), the
   gateway's admin `llm-admin.<domain>`, rag-api `rag.<domain>` (its public URL `https://rag.<domain>`) and Ume.Rag's
   admin `rag-admin.<domain>`. To use another name for one, set `UME_GATEWAY_HOST`,
   `UME_ADMIN_HOST`, `UME_RAG_API_HOST` or `UME_RAG_ADMIN_HOST`; for a public address with a
   port, `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_PUBLIC_URL` (Ume.Rag's admin links to the gateway's admin there) or
   `UME_RAG_API_PUBLIC_URL`. The file lists each, commented out. A stack that sets the names and no `UME_DOMAIN` works
   as before: it needs `UME_GATEWAY_HOST` and `UME_RAG_API_HOST` then, and Ume.Rag's admin shares rag-api's name unless
   `UME_RAG_ADMIN_HOST` is set. `COMPOSE_PROFILES=clamav` (the malware scan) is already in the file; Open WebUI is one
   more profile (*Adding Open WebUI*).

   Leave `UME_INTEGRATION_MANIFEST` unset (the file has it commented out) and keep `UME_INTEGRATION_API_URL` as the file
   has it. The stack file then gives the gateway its default manifest,
   [`deploy/portainer/integration-manifest.json`](../deploy/portainer/integration-manifest.json): the gateway creates
   the integration `Ume.Rag` and its three credential keys, and hands rag-admin the Integration API's address (M-17 C7,
   C11). The manifest names no owner, so the gateway places the integration in a team and a department of its own, both
   named `Ume.Rag` with the code `ume-rag` (a team that already has the code `ume-rag` is used instead). You move it to
   your own team in step 7. Two other choices:
   - **Your own team at once:** set `UME_INTEGRATION_MANIFEST` to the default manifest on one line, with an `"owner"`
     in `"integration"`: `{"teamCode":…,"teamName":…,"departmentCode":…,"departmentName":…}`. The team is found by
     `teamCode`; when it doesn't exist, it is created in the department whose cost centre code is `departmentCode`
     (created too when absent).
   - **No integration from the stack:** set `UME_INTEGRATION_MANIFEST` with an empty value. Nothing is created; issue
     the keys by hand (step 8).

   The other optional variables (ports, binds, groups, the gateway's settings) are in the file's header. Sign-in is not
   a variable: you set it up in steps 4 and 5. To set it in the stack instead, see *Declaring sign-in instead* below.
3. **Deploy the stack.** The one-shot `bootstrap` (the gateway's) and `rag-bootstrap` create the internal CAs,
   certificates and secrets in the volumes; both databases are created and migrated; the gateway's `migrations`
   creates the integration `Ume.Rag` and its keys `rag-api`, `rag-admin` and `rag-worker` from the manifest and writes
   their secrets to the volume `<stack>_integration-secrets`, from which `rag-bootstrap` gives each service its key; the
   services start. With the default manifest, `migrations`' log has the warning
   `Integration 'Ume.Rag' names no owner in the manifest, so it is placed in the team 'Ume.Rag' (code 'ume-rag') …`
   (step 7 moves it). **The end of `rag-bootstrap`'s log lists what is still missing** (example values left in, Open
   WebUI's settings); it is printed again on every deploy, until it says every setting it checks is in place. Its last
   lines point at the setup wizard on every deploy, since `rag-bootstrap` can't see whether setup is done: skip that
   when you can already sign in. The end of the gateway's `bootstrap` log does the same for the gateway's wizard:
   `bootstrap: sign-in to the admin console is set in the setup wizard: https://llm-admin.example.se:9443/setup …`.
4. **Set up the gateway's sign-in first.** With no `UME_OIDC_AUTHORITY` in the stack, the gateway's admin opens its own
   setup wizard on its first start and prints a setup code. Do it before Ume.Rag's: rag-admin's wizard then offers the
   provider address you save here.
   - **Find the code:** *Containers → adminapi → Logs*, the line
     `adminapi: setup mode is open until 2026-11-20 14:05 UTC. Setup code: XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`. It is valid
     for one hour and is worth a gateway admin while setup is open: its holder chooses the gateway's sign-in provider.
     If the hour ends before you finish, restart adminapi: a setup that was never finished opens again with a new code.
   - **Open** `https://llm-admin.<domain>/setup` (with `:9443` when no proxy is in front) and enter the code.
   - **The wizard** shows the redirect URI and the post-logout redirect URI to register on the `ume-admin` client, then
     takes the provider address, the client ID (default `ume-admin`), the client secret (none for a public client) and,
     under *Claims and scopes*, the claim names. **Test sign-in** opens the provider in a window of its own: sign in
     there. **Save and close setup** is offered only after a passed test by someone who comes out as `gateway-admin`
     (the role in the token, or a group in `UME_OIDC_GROUPS_GATEWAY_ADMIN`).

   The gateway's own steps and failure cases:
   [its runbook](https://github.com/thekim1/llmgateway/blob/main/docs/runbook.md#first-start-without-an-authority-the-setup-wizard),
   *First start without an authority: the setup wizard*.
5. **Set up Ume.Rag's sign-in.** rag-admin opens its setup wizard on the first start and prints a setup code.
   - **Find the code:** *Containers → rag-admin → Logs*, the line
     `rag-admin: setup mode is open until 2026-11-20 14:05 UTC. Setup code: XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`. The code
     is valid for one hour. Treat it like a password: whoever holds it chooses the sign-in provider while setup is open.
     If the hour ends before you finish, restart rag-admin (*Containers*, select, *Restart*): a setup that was never
     finished opens again with a new code.
   - **Open** `https://rag-admin.<domain>/setup`, at the public address users will use: the wizard builds the redirect
     URIs from the address you open it at.
   - **The wizard's steps:**
     1. *Setup code*: paste the code.
     2. *Addresses*: the redirect URI and the post-logout redirect URI. Register both on the `ume-rag-admin` client at
        the provider now. The page also shows what the stack sets (the gateway's address, rag-api's public address, the
        platform admin groups).
     3. *Sign-in*: the wizard fills in the provider address saved in the gateway's wizard (*From the AI gateway*; you
        can change it), so the address is typed once for both. While the gateway has none, the wizard says so and links
        to the gateway's wizard: finish step 4 first. Where the stack sets `UME_OIDC_AUTHORITY` (or
        `UME_RAG_OIDC_AUTHORITY`), the address is locked instead (*Set by the deployment*). The
        client ID defaults to `ume-rag-admin`; type the client secret and, if you like, the date it expires. *Advanced*
        holds the claim names, with a preset for Keycloak and for Entra ID (the tenant ID). **Test sign-in** opens the
        provider in a new window: sign in there as yourself. The result shows who signed in, the roles, groups and
        departments the token carried, and whether that person is a platform admin.
     4. *Open WebUI*, shown only when the stack runs it (*Adding Open WebUI*): its client secret, or **Skip for now**.
     5. *Summary and finish*: the values to save and where each comes from. **Finish**, then **Sign in**.
   - **Who may finish:** only a test sign-in by a platform admin enables *Finish*: the role `rag-platform-admin` in the
     token's `roles` claim (Keycloak: that realm role, directly or through a group,
     [`deploy/keycloak/README.md`](../deploy/keycloak/README.md)), or a group named in
     `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` (only for an identity provider whose tokens carry groups instead). The wizard
     says which claim it read.

   After *Finish* setup is closed: the setup API answers 404 and the code is void. The sign-in provider, client and
   secret change only after a re-open (*Opening the setup wizard again*), never from an admin session.
6. **Open WebUI is not part of this path.** To give users the chat, now or later: *Adding Open WebUI*, below.
7. **In the gateway's admin**, as a gateway admin ([production-setup.md](production-setup.md#gateway-credential-keys)
   has every field): **move the integration to your own team** (skip it if your manifest names an owner): *Access →
   Integrations → Ume.Rag*, *Edit*, the owner team. The team must be active, in an active department. Every credential
   key of the integration that is not revoked moves with it; usage already recorded stays with the team `ume-rag`, and
   the new team's budgets count the keys from the move on. Then, under *Access → Integrations → Ume.Rag*, check the
   three credential keys (residencies `OnPrem` and `Eu`: Ume.Rag's ceiling, register M2), then create the **assigned
   keys** the assistants and knowledge bases will run on, one or more per department, in the integration with role
   *Assigned*. The manifest never changes a key that exists: change one here. **Rotate** a credential key here (with a
   grace period), then delete its file `Ume.Rag/rag-<service>` in `<stack>_integration-secrets` and update the stack;
   the gateway writes the new secret and the service picks it up within seconds.
8. **Keys issued by hand instead** (`UME_INTEGRATION_MANIFEST` set empty, or the gateway runs elsewhere): create the
   integration `Ume.Rag` and the three credential keys as production-setup.md says, set `UME_RAG_API_GATEWAY_KEY`,
   `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY` (set, a variable wins over the manifest's key) and
   update the stack. The services pick them up within seconds. **Address list** of a credential key: the services reach
   the gateway over the stack's internal network `llm`, whose subnet Docker chose
   (`docker network inspect <stack>_llm`); allow that subnet, or leave the list empty.
9. **Check** in Ume.Rag's admin (`https://rag-admin.example.se`): *Models* (side menu) shows the gateway's
   version and features with `act-as-key`, a recent organisation sync without a *degraded* reason, and the models.
   Then **create the embedding profile** (*Home* asks for it until one exists): *Models → Embedding profiles → Create
   profile*, pick the gateway's embedding model. The ceiling and the input limit come from the model; the test
   embedding measures its dimensions (or tick *Request dimensions* and give the size, for a model such as
   `text-embedding-3-large` that shortens its vectors) and is stored as the profile's canary. Every RAG credential key
   must allow the model. The first profile is the default, which new knowledge bases use.
10. **First content:** follow *Get started* on Ume.Rag's *Home* (a knowledge base, a source, the first fetch, test
    questions, an assistant). Your applications call rag-api with an app and its key from *Apps and keys*.
11. **Escrow the keys** on the Docker host, with `escrow-keys.sh` from the release bundle (`deploy/`): it writes one
    set with everything a restored database needs that no backup holds (the pepper, the blob key, the database passwords,
    the conversation id key, the Data Protection certificate and the internal CA; the runbook's *Key escrow*), and `--verify` reads it back:

    ```bash
    sudo deploy/escrow-keys.sh --project <stack> /media/<escrow>
    sudo deploy/escrow-keys.sh --project <stack> --verify /media/<escrow>     # PASS
    ```

    A database restored without the set is unusable, and the client secrets the wizard stored can only be read with
    the same Data Protection certificate. Escrow again after every rotation. The gateway's two are `ume-secrets/.pepper` and `ume-certs/data-protection.pfx`, which
    protects the client secret its wizard stored the same way. The stack has no WAL archiving: use `pg_dump` or volume
    snapshots.

Then the knowledge bases and assistants: each gets one of its department's assigned keys (V39); a published assistant
must have one once the gateway acts as keys.

### Declaring sign-in instead

A value set in the stack is the deployment's: it wins, and the wizard shows it locked (*Set by the deployment*). An
empty variable counts as unset. The gateway counts its setup as done at its first start when the stack sets
`UME_OIDC_AUTHORITY`; rag-admin, when the stack sets an authority and a client secret. Then that wizard never opens,
and a re-open shows those values locked.

- **The gateway:** `UME_OIDC_AUTHORITY` declares the provider of the gateway's admin and keeps the gateway's wizard
  closed. It is rag-admin's provider too, locked in both wizards, unless `UME_RAG_OIDC_AUTHORITY` names another one for
  rag-admin. Optionally `UME_OIDC_CLIENT_ID` (default `ume-admin`) and, for a confidential client,
  `UME_OIDC_CLIENT_SECRET`: the gateway's `bootstrap` writes the secret only together with the authority, and without
  one its log says `bootstrap: UME_OIDC_CLIENT_SECRET is left out: it is used only with UME_OIDC_AUTHORITY. …`. The
  claim names (`UME_OIDC_ROLE_CLAIM` and the others in the file's header) lock their fields in the gateway's wizard
  when set.
- **rag-admin:** `UME_OIDC_AUTHORITY` (or `UME_RAG_OIDC_AUTHORITY` for another provider than the gateway's) and
  `UME_RAG_OIDC_CLIENT_SECRET`; optionally `UME_RAG_OIDC_CLIENT_ID` (default `ume-rag-admin`) and the token's shape:
  `UME_RAG_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`, `_GROUP_CLAIM`, `_EXTRA_SCOPE`, `_TENANT_ID` (Entra ID)
  and `_REFUSE_GUESTS` (`true` or `false`). The secret and refusing guests reach rag-admin as files that
  `rag-bootstrap` writes on every deploy (and removes when the variable is emptied).
- **Open WebUI:** `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, and only to change the defaults
  `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID`, `UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL` and `UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME`.

The stack's variables are readable by everyone who may edit the stack; the wizards keep the secrets encrypted in the
databases instead.

## Adding Open WebUI

Open WebUI is the chat for users. It is optional: without the profile `openwebui` the stack creates nothing for it (no
secrets, no client in Ume.Rag's admin, no `chat.` host name in the TLS proxy) and Ume.Rag's admin has no Open WebUI
step in its setup wizard. The steps are the same at a first deploy and on a running installation.

1. **At the sign-in provider,** create the client `open-webui` (confidential, code + PKCE) with the redirect URI
   `https://chat.<domain>/oauth/oidc/login/callback`.
2. **A host name and its certificate:** `chat.<domain>` under `UME_DOMAIN`, or `UME_RAG_OPENWEBUI_HOST` for another
   name. With the stack's TLS proxy the certificate in `UME_RAG_PROXY_CERTS_DIR` must cover that name too; with your
   own proxy, route the name to `UME_RAG_OPENWEBUI_BIND` (`127.0.0.1`) `:` `UME_RAG_OPENWEBUI_PORT` (`3000`), plain
   HTTP.
3. **Add the profile:** `COMPOSE_PROFILES=clamav,openwebui` (keep the profiles you have), and update the stack.
   `rag-bootstrap` generates Open WebUI's secrets and its key for rag-api, `rag-migrate` registers it as the
   deployment's own client, and `rag-openwebui` starts. With the TLS proxy on, restart `rag-proxy` so it reads the new
   block.
4. **Its sign-in** is the setup wizard's *Open WebUI* step. At a first deploy it comes after the sign-in test. On a
   running installation setup is closed: open it again (*Opening the setup wizard again*), or declare the secret in
   the stack (`UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, *Declaring sign-in instead*).
   Open WebUI asks rag-api for its sign-in settings when it starts, and
   restarts by itself within a minute when they change. Its provider defaults to rag-admin's authority's
   `/.well-known/openid-configuration` and its client to `open-webui` (the wizard shows the redirect URI with a copy
   button). The step takes the client secret (leave the provider address empty to
   follow the admin's provider) and **Check** asks the provider whether it accepts the client: *accepted*, *refused*
   (a wrong client id or secret), or *could not be verified* (then a wrong secret shows when the first user signs in;
   saving is still allowed). **Save**, or **Skip for now** and open setup again later. Without a secret Open WebUI
   signs in as a public client (PKCE). Its log says what it started with:
   `rag-openwebui: starting with OIDC sign-in from rag-api's sign-in settings`.
5. **Choose its assistants:** Open WebUI connects by itself. Its client is shown in Ume.Rag's admin under *Apps and
   keys* as *Managed by the deployment*; open it there to choose the assistants it offers (an assistant's *Who can use
   it* works too). Users sign in through OIDC and get the role `user`.
6. **The feedback import** (thumbs and comments into Ume.Rag) needs nothing: the one-shot `rag-openwebui-setup` mints
   the feedback service account's key once Open WebUI runs (password sign-in stays off) and keeps it while it works,
   and rag-api finds Open WebUI in the stack by itself. Its log says *minted* or *kept*. A new key: delete
   `rag-secrets/openwebui-feedback/OpenWebUi__AdminApiKey` and update the stack.

**Taking it out again:** remove `openwebui` from `COMPOSE_PROFILES`, update the stack and remove the stopped
`rag-openwebui` container. Its store (the volume `rag-openwebui-data`) and its keys stay for the next time; the
managed client stays in *Apps and keys* with its key, unused: `rag-migrate` touches it only while the profile is on,
and rag-api accepts no Open WebUI user without the profile.

## Upgrade

From 0.5.x or later to this release, **in one update**: paste this release's stack file over the old one and update
the stack. No release in between has to be installed first, no variable has to be added, and nothing is stopped by
hand: where a migration can't run beside the old version of a service (0.6.0's `RemoveWorkerConcurrency` and the old
rag-admin), `rag-migrate` keeps that service out of the database until its new version starts. Plan a short window:
every service restarts once, and the gateway moves to `v0.11.0`. The release tests this path by booting 0.6.0's stack
with its variables and applying the new file over it. **Your stack keeps its variables and sees neither setup
wizard:** it sets `UME_OIDC_AUTHORITY`, so the gateway counts its setup as done at its first start, and with
`UME_RAG_OIDC_CLIENT_SECRET` beside it rag-admin does the same. Every change and why:
[upgrade-guide.md](upgrade-guide.md), and the gateway's
[upgrade guide](https://github.com/thekim1/llmgateway/blob/main/docs/upgrade-guide.md).

**Before the update**

1. **Back up** both databases, the gateway's two files (`ume-secrets/.pepper`, `ume-certs/data-protection.pfx`) and
   Ume.Rag's: from 0.7.x or earlier the three files its *First deploy* asked for (`rag-secrets/.pepper`,
   `rag-secrets/keys/rag-blob-internal`, `rag-certs/data-protection.pfx`); from 0.8.0 on, check its escrow set
   (`sudo deploy/escrow-keys.sh --project <stack> --verify /media/<escrow>` prints `PASS`).
2. **From 0.5.x, the gateway keys' residencies:** in the gateway's admin, give `rag-api`'s and `rag-admin`'s credential
   keys the residencies **`OnPrem` and `Eu` only** (the worker's already are). From 0.6.0 each service's ceiling is its
   key's residencies; a key that allows every residency would let a public assistant answer at `External`. Each
   published assistant also needs a gateway key that reaches both its chat model and its embedding model.
3. **From 0.5.x, if Open WebUI is on and its client was made by hand:** in Ume.Rag's admin, *Apps and keys*, open that
   client and note its assistants, information class, history and feedback days. After the update Open WebUI uses a
   client the deployment makes, and the hand-made one is no longer trusted.
4. **From 0.5.x,** if you set any of `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER` or
   `_VIEWER`, note their groups (with the shared Keycloak the roles come in the token and there is nothing to note).
5. Optional: set the organisation's name in the gateway's admin console (branding); Ume.Rag's header and sign-in page
   show it.
6. **Look through the stack's variables:**
   - `UME_OIDC_AUTHORITY` holds your provider's address, as it must in a working stack: it keeps the gateway's
     sign-in declared and is rag-admin's provider. Keep it and every other `UME_OIDC_*` you set;
   - `UME_RAG_OIDC_CLIENT_SECRET` and `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET` must hold the real secrets, as they do in
     a working stack. A leftover `CHANGE` declares a wrong secret and keeps the wizard closed;
   - a token-shape variable you changed (`UME_RAG_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`, `_GROUP_CLAIM`,
     `_EXTRA_SCOPE`, `_TENANT_ID`, `_REFUSE_GUESTS`, `_CLIENT_ID`): keep the line. The new file passes each only when
     it is set; unset, the code's default applies, which is the old file's default;
   - **`UME_INTEGRATION_MANIFEST` set** (a stack made from 0.6.0's `stack.env.example`, with an `owner`): keep it. A set
     value wins over the new stack file's default, and the integration and keys it made exist, so nothing is created
     or changed;
   - **`UME_INTEGRATION_MANIFEST` not set** (a stack upgraded from 0.5.x that kept its keys issued by hand): the new
     stack file's default manifest applies from this update on. The gateway looks for the integration by its name,
     `Ume.Rag`, and for each credential key by its name within it, creates what is missing and never changes what
     exists. Keys issued by hand under other names (0.5.0's `Ume.Rag api`, …) don't count, so the gateway creates
     three new credential keys `rag-api`, `rag-admin` and `rag-worker` in your integration `Ume.Rag` and writes their
     secrets to `<stack>_integration-secrets`; if your integration has another name, it also creates a second
     integration `Ume.Rag`, in a new team and department `ume-rag`. Your services keep their keys: a
     `UME_RAG_*_GATEWAY_KEY` that is set wins over the manifest's. The new keys stay unused. **To create nothing,** add
     `UME_INTEGRATION_MANIFEST` with an empty value now: set and empty, the default doesn't apply. Or let them be
     created and revoke them afterwards (*After the update*, step 14).

**The update**

7. **Edit the stack:** paste the new file over the old one (keep the stack name). **No variable has to be added or
   changed** (the empty `UME_INTEGRATION_MANIFEST` of step 6 is your choice; the Integration API's address for the
   cost and budget read-back is the stack's default). The host names stay as they are: `UME_DOMAIN` is for new
   stacks, or for the one-value form later (*Optional, afterwards*, below). Tidying, since a leftover is ignored and
   `rag-bootstrap`'s log names it: `UME_RAG_OPENWEBUI_API_KEY`, `UME_RAG_OPENWEBUI_CLIENT_ID`,
   `UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `UME_RAG_OPENWEBUI_PASSWORD_AUTH`, `UME_RAG_OPENWEBUI_BASE_URL`,
   `UME_RAG_CLAMAV_MODE`, `UME_RAG_EMBEDDING_MODEL` and the other `UME_RAG_EMBEDDING_*`,
   `UME_RAG_GATEWAY_MAX_RESIDENCY`, `UME_RAG_GATEWAY_INTEGRATION_API_URL`, `UME_ORGANISATION_NAME` and the four group
   variables of step 4 (`UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` stays).
8. **Update the stack** (*Re-pull image and redeploy* on). In order: the gateway's `bootstrap`, which also writes
   `adminapi/Setup__Secret`; the gateway's `migrations` (its migrations, then the integration manifest);
   `rag-bootstrap`, which writes `admin/Setup__Secret` and Open WebUI's client secret as files for rag-api and
   rag-admin; `rag-migrate` (from 0.5.x eight migrations, from 0.6.0 three; it creates the managed clients *Open
   WebUI*, while its profile is on, and `admin-console`); then the services. From 0.5.x `rag-migrate`'s log has
   *Migration …RemoveWorkerConcurrency stops the services of role ume_rag_admin*: the old rag-admin answers errors for
   the seconds until it is replaced. Open WebUI starts through a wrapper that asks rag-api for its sign-in settings,
   and `rag-openwebui-setup` mints the feedback key a minute after Open WebUI starts.

**After the update**

9. **Read the end of `rag-bootstrap`'s log:** it lists what is still missing and each variable that is no longer read,
   and says *sign-in to Ume.Rag's admin is declared by the stack's variables …, so the setup wizard stays closed*.
10. **Read the end of the gateway's `bootstrap` log:** `bootstrap: sign-in to the admin console is declared by the
    stack's variables (UME_OIDC_AUTHORITY), so the setup wizard stays closed: …`, and no line
    `bootstrap: UME_OIDC_CLIENT_SECRET is left out: …` after it.
11. **From 0.5.x, Open WebUI's client** (step 3): in *Apps and keys*, open *Open WebUI* (*Managed by the deployment*),
    set the class, history and feedback days to the old client's and tick the same assistants; save. Then disable the
    hand-made client and revoke its key.
12. **From 0.5.x,** if you noted groups in step 4, enter them under *Settings → Role groups*.
13. **Check:**
    - `adminapi`'s log has, once, the record
      `The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed` (in the JSON line's
      `Message`), and **no** line with `Setup code`; rag-admin's log has *The deployment declares the sign-in* once,
      and **no** line with *Setup code*; you sign in to both admins as before;
    - each of `rag-api`, `rag-admin` and `rag-worker` logs *the credential key's ceiling: Eu* at start;
    - `migrations`' log has `… 'Ume.Rag' applied, 0 of 3 credential keys created` when your keys came from a manifest
      (`3 of 3` for a stack of step 6 without one; no such line with an empty value);
    - *Models* shows the gateway's version with `act-as-key`, and the embedding profile is still there;
    - `rag-openwebui`'s log has *starting with OIDC sign-in from rag-api's sign-in settings*, Open WebUI's sign-in page
      shows the button, and a test user signs in, sees the assistants and gets an answer; `rag-openwebui-setup`'s log
      says *minted* or *kept*;
    - *Apps and keys* shows `admin-console` and *Open WebUI* as managed, and the assistant test panel answers;
    - the sign-in values in the admin's *Settings* are marked *Set by the deployment* where the stack sets them;
    - an assistant's or knowledge base's *Cost* card shows the gateway's figures (the read-back) within a few minutes.
14. **Only if step 6 let the gateway create three new keys:** in the gateway's admin, *Access → Integrations → Ume.Rag*,
    revoke `rag-api`, `rag-admin` and `rag-worker` (the manifest never creates a revoked key again). Or move to them
    instead: give them the settings of your keys (models, address list), remove the three `UME_RAG_*_GATEWAY_KEY`
    variables and update the stack, so `rag-bootstrap` hands each service its new key from the volume; once *Models*
    shows a recent organisation sync again, revoke your old keys.

15. **Escrow Ume.Rag's keys** with this release's `deploy/escrow-keys.sh`, as *First deploy* step 11, and verify the
    set. From 0.7.x or earlier this is the first set; a set made by an earlier release doesn't pass `--verify`.

**Tested and not:** the release boots 0.6.0's stack (its file and variables, `clamav` and `openwebui`, no gateway
keys, no identity provider) and applies the new file: every service comes back healthy on the new images, the
database, the CA, the pepper and the Data Protection certificate are kept, and `rag-migrate` reports the roles it
stopped. The step from 0.5.1 was tested before this release (#817, 2026-11-17) but is not in 0.8.0's own run: one
of 0.5.1's images is no longer on ghcr.io. Sign-in after the upgrade, a hand-made Open WebUI client and keys issued by
hand are not part of that test, and Portainer's own *Update the stack* is not run by it.

**If rag-admin prints a setup code instead,** the stack did not declare the sign-in: the authority or
`UME_RAG_OIDC_CLIENT_SECRET` is missing or empty. Either add it and update the stack, or use the wizard (*First deploy*
step 5, from the setup code on): both end in a working sign-in. **If `adminapi` prints one,** `UME_OIDC_AUTHORITY` is
missing or empty and the gateway's sign-in is not configured until it is fixed: set it and update the stack (the code is
then void), or use the gateway's wizard (*First deploy* step 4).

**Optional, afterwards: the one-value form.** Set `UME_DOMAIN` to your domain and remove each of these lines whose
value is exactly its default under it: `UME_GATEWAY_HOST` (`llm.<domain>`), `UME_ADMIN_HOST` (`llm-admin.<domain>`),
`UME_GATEWAY_PUBLIC_URL` (`https://llm.<domain>`, no port), `UME_RAG_API_HOST` (`rag.<domain>`), `UME_RAG_ADMIN_HOST`
(`rag-admin.<domain>`), `UME_RAG_OPENWEBUI_HOST` (`chat.<domain>`) and `UME_RAG_API_PUBLIC_URL`
(`https://rag.<domain>`). Keep a line whose value differs (another name, a port). `UME_INTEGRATION_MANIFEST` may go
too: the default names the same integration and keys, which exist, so nothing is created, and a manifest without an
owner leaves the integration's team as it is. Update the stack. `rag-bootstrap`'s log has no `issuing certificate` for
rag-api or rag-admin unless a name changed.

**Optional, afterwards: move the client secrets out of the stack's variables** and into the wizards, where they are
stored encrypted. In one go per product, because sign-in is incomplete in between:

1. Remove `UME_RAG_OIDC_CLIENT_SECRET` (and `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`), set `UME_RAG_SETUP_REOPEN` to a
   new value (for example today's date) and update the stack.
2. Open `https://<admin host>/setup` with the setup code from rag-admin's log, type the secrets, test the sign-in and
   finish. Keep `UME_OIDC_AUTHORITY`: the gateway shares it, and it stays locked in the wizard.
3. The gateway's (a confidential `ume-admin` only): remove `UME_OIDC_CLIENT_SECRET`, set `UME_SETUP_REOPEN` to a new
   value and update the stack. Open `https://<gateway admin host>/setup` with the setup code from `adminapi`'s log,
   type the secret, test the sign-in and save. The provider stays declared and locked.

The gateway's upgrade guide also describes moving its provider into its wizard (*Optional, afterwards: move sign-in
into the wizard*, *Everything*: remove `UME_OIDC_AUTHORITY` and every `UME_OIDC_*` but the role groups, with a new
`UME_SETUP_REOPEN`). In this stack `UME_OIDC_AUTHORITY` is rag-admin's provider too: set `UME_RAG_OIDC_AUTHORITY` to
the same address in the same change, or rag-admin is left without one. With the `data` profile, the Data API then
accepts only Data API keys unless you set `UME_DATA_AUTHORITY` to the old address.

## Upgrade from before 0.5.0

First to 0.5.1, with the steps and the stack file of that time:
[*Upgrade from 0.2.0–0.4.0* in this page at `v0.7.0`](https://github.com/thekim1/ume-rag-platform/blob/v0.7.0/docs/portainer.md#upgrade-from-020040)
and the `v0.5.1` release's `compose.portainer.yaml`. Then *Upgrade*, above.

## Opening the setup wizard again

After setup, the sign-in provider, the client and its secret change only in the wizard, and only after a re-open:
never from an admin session. Re-open it for a client secret that is about to expire, a new provider or client, or a
sign-in that broke.

1. Set `UME_RAG_SETUP_REOPEN` to a new value (for example today's date) and update the stack.
2. rag-admin opens the wizard once, for one hour, and prints a new setup code in its log (*Containers → rag-admin →
   Logs*). The current sign-in keeps working until you finish.
3. Open `https://rag-admin.<domain>/setup`, enter the code, change what you need (a stored secret is never shown: type
   the new one, and its expiry date), test the sign-in and finish.

The value is used once: a restart doesn't open the wizard again, and an abandoned re-open ends after the hour. The next
re-open needs another new value. Values the stack sets stay locked; to change one, change the variable.

**A client secret that expires:** enter its expiry date in the wizard; Home reminds platform admins from 30 days
before it. Create the new secret at the provider before
the old one expires, then re-open and enter it.

**Before restoring a database,** remove `UME_RAG_SETUP_REOPEN` (or keep its value unchanged afterwards): a restored
database may have consumed an older value, and the wizard would open again for an hour. The client secrets the wizard
stored need the same `rag-certs/data-protection.pfx` as the database; with another certificate they can't be read
and sign-in fails until you re-open and type them again.

The code is derived from `rag-secrets/admin/Setup__Secret`, which `rag-bootstrap` generates once; it needs no backup (a
new one only changes future codes).

**The gateway's wizard** re-opens the same way with its own variable: set `UME_SETUP_REOPEN` to a new value and update
the stack; `adminapi` prints a new setup code in its log, valid for an hour, for `https://llm-admin.<domain>/setup`.
The gateway's current sign-in keeps working meanwhile, and values the stack sets stay locked. Empty `UME_SETUP_REOPEN`
before restoring the gateway's database. `UME_RAG_SETUP_REOPEN` opens only Ume.Rag's wizard, `UME_SETUP_REOPEN` only
the gateway's.

## When something fails

| Symptom | Look at |
|---|---|
| The stack won't deploy: `pull access denied` for `ghcr.io/thekim1/ume-rag/...` | the `ghcr.io` registry in Portainer and its token's `read:packages`; pick it on the stack |
| `rag-bootstrap` exits non-zero | its log; `the gateway's CA is missing` means the gateway's `bootstrap` didn't run first (keep the stack name) |
| Ume.Rag's services log `gateway_key_missing` or the gateway answers `401` | the three `UME_RAG_*_GATEWAY_KEY` variables and `rag-bootstrap`'s checklist; update the stack (`0.5.0`'s images: restart the service) |
| The gateway answers `403` to Ume.Rag | the credential key's address list (the `llm` subnet) and its integration role *Credential* |
| *Models* shows `degraded` or `scope_missing` | `organisation.read` (and `usage.read` for the read-back) on the admin's credential key |
| Uploads wait with `clamav.unavailable` | `clamav` in `COMPOSE_PROFILES`, `rag-clamav` healthy (its first signature download takes minutes) |
| `rag-openwebui` restarts | its log: a restart after *sign-in settings changed* is expected; otherwise the wrapper's secret files in `rag-secrets/openwebui/`, the CA bundle, write access to its volume |
| Open WebUI lists no models | *Apps and keys*: the Open WebUI app managed by the deployment offers assistants, and they are published; `rag-migrate`'s log names the client |
| No setup code in `adminapi`'s log | The stack sets `UME_OIDC_AUTHORITY` (the gateway's `bootstrap` log says sign-in *is declared by the stack's variables*), or the gateway's setup is done already (to change it, a new `UME_SETUP_REOPEN` value) |
| Ume.Rag's wizard suggests no provider address and links to the gateway's wizard | The gateway's setup isn't finished (*First deploy* step 4), or rag-admin hasn't read the gateway's context since: it reads it with its credential key, so check `rag-bootstrap`'s checklist for a missing key. Or type the address |
| No setup code in rag-admin's log | Setup is done already (sign in; to change sign-in, re-open). Or the stack declares an authority and `UME_RAG_OIDC_CLIENT_SECRET` (the log says *The deployment declares the sign-in*), or a placeholder such as `CHANGE` is left in that variable (`rag-bootstrap`'s checklist names it). Or the log says *the setup secret (Setup__Secret …) is missing*: update the stack, so `rag-bootstrap` writes it, then restart rag-admin. *The settings row is missing*: `rag-migrate` didn't complete (its log) |
| The wizard says *Too many attempts* | More than 20 wrong codes a minute reached rag-admin. The right code still works at once: paste it again, exactly. If it wasn't you, someone is guessing: security event 6009 has the address ([security-events.md](security-events.md)) |
| *The setup window has ended* | The hour is over. Setup never finished: restart rag-admin for a new code. After a finished setup: a new `UME_RAG_SETUP_REOPEN` value |
| The test sign-in fails: the provider can't be reached, or its certificate isn't trusted | rag-admin itself fetches the provider's discovery document: the address (the issuer, `https`), DNS and firewalls from the VM, and that rag-admin trusts the provider's certificate |
| The provider's own page says the redirect URI is invalid | Register the wizard's redirect URI (step *Addresses*) on the client exactly, with the host name you opened the wizard at |
| The test sign-in fails at the token (*The provider refused rag-admin’s sign-in request*) | A wrong client secret or client ID, or a public client tested with a secret (leave the field empty) |
| The test passed, but *Finish* stays off: *not a platform admin* | Give the person the role `rag-platform-admin` at the provider, or name one of their groups in `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` and update the stack; then test again |
| After setup the sign-in page says sign-in is not set up | No usable authority, or a stored secret that can't be read (a different `data-protection.pfx`): re-open and enter the values again, or declare them in the stack (*Declaring sign-in instead*) and update it |
| Open WebUI shows no sign-in button | `rag-openwebui`'s log: *rag-api did not answer within 60 s* (rag-api down or unreachable from the wrapper: it asks again every minute and restarts Open WebUI once it answers), or *no sign-in settings yet* (no provider: rag-admin has no authority yet) |
