# Local P1a run

P1a is shown on a developer machine with the pilot member present (`plans/p1/README.md` §7, U-a). Until the live block
at the end of P1a, everything runs against the **dev Keycloak** and **FakeGateway**; the live block points the same
stack at the **interim gateway** (J10) and the **Keycloak shared with the gateway** (J11). Their addresses, keys and
secrets go only in the AppHost's user secrets or in `deploy/.env` and the secret files `init-deployment.sh` writes,
never in the repository, a transcript or a screenshot. Every host name below is a placeholder.

There are two ways to run the stack locally:

| | The AppHost ([local-development.md](local-development.md)) | The pilot stack (production Compose) |
|---|---|---|
| What runs | the services from source, the dev Keycloak (realm `ume` from `dev/keycloak/`), FakeGateway, Open WebUI once stream B registers its `open-webui` resource (item 15, J9) | the images, hardened, as on the pilot host: `compose.prod.yaml`, `compose.ingestion.yaml`, and `compose.openwebui.yaml` as a project of its own |
| Sign-in and model calls now | yes: dev Keycloak test users, FakeGateway | no: Compose has no Keycloak and no FakeGateway, so sign-in and answers wait for the live block |
| Use it for | the demo: sign-in, set-up through Scalar, a cited answer in Open WebUI | checking the production shape: every container healthy, secrets as files, the networks |

## 1. Now: the AppHost against the dev Keycloak and FakeGateway

```powershell
.\scripts\Start-Dev.ps1
```

The AppHost starts Postgres, the dev Keycloak, FakeGateway (1024-dimension embeddings, `Eu` and the other dev keys),
the parser, ClamAV, the api, the worker and the admin API. Sign in to the admin with a test user from
[local-development.md](local-development.md#keycloak-test-realm) (password: the `dev-user-password` parameter). Open
WebUI joins once stream B adds it to the AppHost (`OpenWebUi:Enabled`, the dev realm's client `open-webui-dev`, the
seeded dev key; repo-and-deployment §4.1).

## 2. Now: the pilot stack on your machine

Build the images from the commit you are testing (as in
[local-development.md](local-development.md#production-compose-on-your-machine)): the Compose files mount
`deploy/postgres/app-roles.sql` from the tree, so older `rag-migrate` images fail on tables they do not have yet. Then
run `init-deployment.sh` in a scratch copy of `deploy/` with placeholders for the systems that are not there yet:

```bash
cp -r deploy ~/rag-p1a && cd ~/rag-p1a
./init-deployment.sh --oidc-authority https://keycloak.invalid/realms/ume --gateway-url https://gateway.invalid \
  --api-host localhost --openwebui-host chat.localhost --backup-target local --allow-file-keys --yes
echo 'UME_RAG_INGESTION_SLICE=' >> .env    # no rag-ingestion.slice on a developer machine
docker compose --env-file .env up -d --wait
docker compose --env-file .env ps
curl --cacert certs/ca.pem https://localhost:8543/health/ready
```

Run the `chown` commands the script prints first (the containers read their files as their own users), unless it ran
as root. `up --wait` returns when every service with a health check is healthy and `rag-migrate` has completed; the
parser and ClamAV take a few minutes on the first start. The health checks do not call the OIDC authority or the
gateway, so the placeholders do not stop the stack from becoming healthy. If `172.30.0.0/16` is taken on your machine,
add a Compose file with other subnets to `COMPOSE_FILE` in `.env` (`deploy/tests/release-smoke.sh --compose-file`
does the same); the host firewall is not installed on a developer machine.

`deploy/tests/release-smoke.sh` runs the same steps on a release bundle and checks the listeners
([production-setup.md](production-setup.md)).

What the script generated for P1a: the Open WebUI JWT secret, once, as `secrets/api/OpenWebUi__JwtSecret` and as
`FORWARD_USER_INFO_HEADER_JWT_SECRET` in `secrets/openwebui.env`; the worker's crawl key pepper
`secrets/worker/Security__CrawlKeyPepper` (the HMAC key of `HostHash` in security event 5101). A re-run never changes
them.

**Open WebUI in Compose on one machine.** `compose.openwebui.yaml` calls `rag-api` at `UME_RAG_API_PUBLIC_URL` and
sends the browser back to `https://UME_RAG_OPENWEBUI_HOST/oauth/oidc/callback`: on the pilot host the reverse proxy
serves both with a trusted certificate. On a developer machine neither exists (`https://localhost:8543` is the Open
WebUI container itself, and it does not trust the internal CA), so the demo's Open WebUI is the AppHost's. Check the
Compose project with `docker compose -f compose.openwebui.yaml --env-file .env config --quiet`.

## 3. The live block: what changes

Nothing in the repository changes. You set values in one of two places:

| Value | AppHost (user secrets, `dotnet user-secrets --project src/Ume.Rag.AppHost set ...`) | Pilot stack (`init-deployment.sh` options: `deploy/.env` and secret files) |
|---|---|---|
| The interim gateway (J10) | `FakeGateway:Enabled=false`, `Gateway:BaseUrl=https://<gateway host>` | `--gateway-url https://<gateway host>` (a publicly trusted certificate: no `--ca-file`) |
| Its `Eu` keys, one per deployable | `Gateway:Keys:Eu=<key>` (the AppHost gives every `Gateway:*` value to api, admin API and worker alike, so set no `External` key there) | `--gateway-key-file api:Eu=api-eu.key --gateway-key-file admin:Eu=admin-eu.key --gateway-key-file worker:Eu=worker-eu.key` |
| The org sync's Data API key (`catalog.read`) | not passed yet: the AppHost sets `OrgSync:*` only for FakeGateway (stream B) | `--org-sync-key-file org-sync.key` |
| The shared Keycloak (J11) for the admin | `Oidc:Authority=https://<keycloak host>/realms/ume`, `Oidc:ClientSecret=<secret of ume-rag-admin>`; the bundled Keycloak then does not start | `--oidc-authority https://<keycloak host>/realms/ume --oidc-client-secret-file ume-rag-admin.secret` |
| The shared Keycloak for Open WebUI | stream B's `open-webui` resource (J9) | `--openwebui-oidc-client-secret-file open-webui.secret`; client id `open-webui` and the provider URL (`<authority>/.well-known/openid-configuration`) are the defaults; `--openwebui-oidc-provider-name` sets the sign-in button's label (default *Umeå kommun*) |
| Open WebUI's client in rag-api | created by the AppHost seed (with J9) | create it in the admin API (Scalar): kind `open-webui`, its ceiling, `AllowedIpRanges` = the Open WebUI host (identity design §5.3); then `--openwebui-api-key-file open-webui.key --openwebui-rag-client-id <its id>` |

Keep the key and secret files outside the repository (for example in `~/rag-p1a-secrets/`, mode 0600) and delete them
once `init-deployment.sh` has copied them. Re-run `init-deployment.sh` with all the options of the first run plus the
new ones: it keeps every generated secret and rewrites `.env` (the previous one is kept as `.env.bak`; carry over lines
you added by hand, such as `UME_RAG_INGESTION_SLICE=`). Then recreate the services:
`docker compose --env-file .env up -d --wait` (and the Open WebUI project).

The redirect URIs the script prints must be registered on the shared realm's clients: `ume-rag-admin`
(`https://<admin host>[:<port>]/signin-oidc`, `/signout-callback-oidc`) and `open-webui`
(`https://<owui host>/oauth/oidc/callback`, the legacy path). The realm additions (`deploy/keycloak/`, stream B, J8)
carry the pilot hosts; a demo on a developer machine needs that machine's URIs on the clients too (the AppHost's admin
port, `https://localhost:5174`). Ask the gateway session for them with J11.

Pilot accounts are created by hand in the shared realm (U-c). Every enabled account of realm `ume` can sign in to Open
WebUI (identity design §5.3, open question 5 of §16).

**Not in this run book:** the embedding model (U-b: 1024 dimensions keep the schema and FakeGateway unchanged), the
umea.se crawl (items 19 and 20) and the gold set (item 21).
