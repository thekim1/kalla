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
| Its `Eu` keys, one per deployable | `Gateway:Services:api:Keys:Eu=<key>`, `Gateway:Services:admin:Keys:Eu=<key>`, `Gateway:Services:worker:Keys:Eu=<key>`; a shared `Gateway:Keys:Eu=<key>` goes to all three, and a service's own key wins over it. The AppHost stops on `Gateway:Services:worker:Keys:External`, an unknown service or a level other than `OnPrem`, `Eu`, `External` | `--gateway-key-file api:Eu=api-eu.key --gateway-key-file admin:Eu=admin-eu.key --gateway-key-file worker:Eu=worker-eu.key` |
| The org sync's Data API key (`catalog.read`) | `OrgSync:BaseUrl=https://<gateway host>`, `OrgSync:DataApiKey=<key>` (passed to the admin API only) | `--org-sync-key-file org-sync.key` |
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

**Not in this run book:** the umea.se crawl (items 19 and 20) and the gold set (item 21). The pilot embedding model
(U-b) is in section 4.

## 4. Milestone 1: ingestion and retrieval against the interim gateway

The first run of the AppHost against the user's interim gateway (Decisions table: "Interim gateway for P1a", "Pilot
embedding model, U-b"): a handful of public umea.se pages ingested with `text-embedding-3-large` at 1024 dimensions,
then a retrieval query. Every step is marked **now** (works on `main` today) or **after #NNN** (waits for that PR or
issue). Until the admin's set-up endpoints (J7) exist, the source comes from a dev seed script.

### 4.1 Prerequisites

| | Status |
|---|---|
| Stream B's gateway client, `EmbeddingProfile:*` seeder and canary recording (`GET /api/gateway/status`) | after #123 |
| The AppHost's real-gateway mode: `Gateway:Services:<service>:Keys:<level>` per service, `OrgSync:*` passed to the admin API | now (#119) |
| `EmbeddingProfiles.RequestDimensions` and `dimensions` sent on every embedding call (without it the model answers 3072 dimensions and the profile's 1024 refuses them) | after #142 |
| The AppHost passes `EmbeddingProfile:*` to the `migrations` resource (neither #119 nor #123 does yet; without it the pilot profile is not seeded) | after stream B adds it |
| `KnowledgeService`, query embedding and `/v1/retrieve` | after #127 |
| On the gateway: a key restricted to residency `Eu`; the `text-embedding-3-large` deployment's `ContextWindow` empty; a Data API key with `catalog.read` for the org and model syncs | now (the gateway session) |
| `psql` on your machine, or the AppHost's Postgres container (`--docker` below); Python 3.12 | now |

### 4.2 User secrets

All values go in the AppHost's user secrets, never in the repository. Use a Postgres volume of its own, so the
FakeGateway data stays as it is and the pilot profile is the only (and default) profile:

```bash
p=src/Ume.Rag.AppHost
dotnet user-secrets --project $p set DevelopmentVolumes:Postgres ume-rag-m1-postgres     # now
dotnet user-secrets --project $p set FakeGateway:Enabled false                           # now
dotnet user-secrets --project $p set Gateway:BaseUrl https://<gateway host>              # now
dotnet user-secrets --project $p set Gateway:Services:api:Keys:Eu <the api's Eu key>      # now
dotnet user-secrets --project $p set Gateway:Services:admin:Keys:Eu <the admin's Eu key>  # now
dotnet user-secrets --project $p set Gateway:Services:worker:Keys:Eu <the worker's Eu key>  # now
dotnet user-secrets --project $p set OrgSync:BaseUrl https://<gateway host>              # now
dotnet user-secrets --project $p set OrgSync:DataApiKey <the catalog.read key>           # now
dotnet user-secrets --project $p set EmbeddingProfile:GatewayModel text-embedding-3-large  # after #123 + forwarding
dotnet user-secrets --project $p set EmbeddingProfile:Dimensions 1024                    # after #123 + forwarding
dotnet user-secrets --project $p set EmbeddingProfile:RequestDimensions true             # after #142
dotnet user-secrets --project $p set EmbeddingProfile:MaxInputTokens 8191                # after #123 + forwarding
dotnet user-secrets --project $p set EmbeddingProfile:MaxResidency Eu                    # after #123 + forwarding
```

Set no `External` and no `OnPrem` key: the worker never holds `External` (the AppHost refuses
`Gateway:Services:worker:Keys:External` and keeps a shared `Gateway:Keys:External` from the worker), and the pilot runs
at `Eu`. With one key for all three, `Gateway:Keys:Eu` alone works too. The
migrator seeds the profile as `pilot-embedding` (#123's default `EmbeddingProfile:Name`) and never changes it once it
exists: a different model or dimension count needs a new volume.

### 4.3 Start

**After #123, #142 and the forwarding.** Start without the demo data, so the FakeGateway's `dev-embedding`
profile is not seeded next to the pilot profile:

```powershell
.\scripts\Start-Dev.ps1 -Empty
```

In the Aspire dashboard (`https://localhost:17470`), check that `migrations` completed and that no `fake-gateway`
resource runs. Copy the `ragdb` connection string from the `postgres` resource (Host `localhost`, the mapped port,
user `postgres`, the generated password); keep it out of transcripts.

### 4.4 Seed a URL-list source (now on FakeGateway; for the pilot profile after 4.3)

`scripts/dev/seed_url_list.py` creates, in the local database only, a department (`Pilot (dev seed)`), a public and
published collection (`pilot-public`) on the given embedding profile, and a URL-list source (`Pilot URL list`) on the
platform `url-list` pipeline, then enqueues a manual sync as the admin's "sync now" does. A re-run finds the same rows
and replaces the source's URL list. It refuses any database host other than `localhost`, `127.0.0.1` or `::1`.
The SQL is `scripts/dev/seed-url-list.sql`; `DevSeedScriptTests` runs the same file against the test database.

Pick a handful of public pages (five is plenty for the milestone); the worker fetches them as `UmeaKommunBot/1.0`,
reads robots.txt first and keeps its per-host budget, so a small list is a few polite requests:

```bash
export UME_RAG_DEV_DB='Host=localhost;Port=<port>;Username=postgres;Password=<password>;Database=ragdb'
python3 -I scripts/dev/seed_url_list.py --profile pilot-embedding \
  https://www.umea.se/<page 1> https://www.umea.se/<page 2> https://www.umea.se/<page 3>
```

Without `psql` on the machine, add `--docker <the postgres container>` (`docker ps --filter name=postgres`); the
script then runs psql inside the container. `--no-sync` leaves the first run to the scheduler (the platform url-list
schedule), `--urls-file` reads one URL per line. The output lists every row it found or created, the sync job id (the
run id) and the config version. On FakeGateway the same command works now with `--profile dev-embedding` (the default
start seeds that profile).

The department is not in the gateway catalogue, so the org sync marks it inactive on its next run; the worker does not
read that flag, so ingestion is not affected. Pass `--department-id <id>` to use a synced department instead.

### 4.5 What to check

| Check | How | When |
|---|---|---|
| The run finished | `SELECT "State", "Added", "Updated", "Unchanged", "Failed" FROM content."Runs" WHERE "Id" = '<sync job id>';` → `succeeded`, `Added` = the number of URLs | after #142 |
| Live chunks with 1024-dimension vectors | `SELECT count(*), min(vector_dims(c."Embedding")) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" WHERE d."SourceId" = '<source id>' AND c."DeletedAt" IS NULL;` → at least one chunk per page, 1024 | after #142 |
| Failures, if any | `SELECT "Identity", "Outcome", "Reason" FROM content."RunItems" WHERE "RunId" = '<sync job id>' ORDER BY "Id";` and the worker's log in the dashboard | after #142 |
| The canary is recorded | `GET /api/gateway/status` in the admin API's Scalar (signed in as a dev Keycloak admin): every `levels[]` entry you hold a key for is `ok` with residency `Eu`, `profiles[]` lists `pilot-embedding` with `canaryRecorded: true`; `POST /api/gateway/sync` records it at once instead of waiting for the next sync | after #123 |
| Retrieval | a `/v1/retrieve` query on the api about one of the seeded pages, with a key of the api: the hits are chunks of the seeded pages | after #127 |

Run the queries with `psql -h localhost -p <port> -U postgres -d ragdb` (the password from the dashboard) or in the
container (`docker exec -it <postgres container> psql -U postgres -d ragdb`).

### 4.6 Back to FakeGateway (now)

Stop the AppHost, then remove the milestone's switches; the FakeGateway database on the default volume is as you
left it:

```bash
p=src/Ume.Rag.AppHost
for k in DevelopmentVolumes:Postgres FakeGateway:Enabled Gateway:BaseUrl Gateway:Keys:Eu \
         Gateway:Services:api:Keys:Eu Gateway:Services:admin:Keys:Eu Gateway:Services:worker:Keys:Eu OrgSync:BaseUrl OrgSync:DataApiKey \
         EmbeddingProfile:GatewayModel EmbeddingProfile:Dimensions EmbeddingProfile:RequestDimensions \
         EmbeddingProfile:MaxInputTokens EmbeddingProfile:MaxResidency; do
  dotnet user-secrets --project $p remove $k
done
```

Keep the `ume-rag-m1-postgres` volume for the next live run, or delete it with `docker volume rm ume-rag-m1-postgres`.
