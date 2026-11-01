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

**Passed 2026-11-01** (session 13, on Linux/WSL): 5 pages fetched, 4 added (one hub page `parse.empty`), 17 chunks at
1024 dimensions, the canary recorded at `Eu`, and `/v1/retrieve` answered in under 1 s with the right chunk first. Until
#168 was fixed (#172), the AppHost needed three manual workarounds; they are no longer needed. What the run showed is in §4.7.

### 4.1 Prerequisites

| | Status |
|---|---|
| Stream B's gateway client, `EmbeddingProfile:*` seeder and canary recording (`GET /api/gateway/status`) | now (#123) |
| The AppHost's real-gateway mode: `Gateway:Services:<service>:Keys:<level>` per service, `OrgSync:*` passed to the admin API | now (#119) |
| `EmbeddingProfiles.RequestDimensions` and `dimensions` sent on every embedding call (without it the model answers 3072 dimensions and the profile's 1024 refuses them) | now (#159: column, seeder, admin calls, query embedding; #162: the worker's embed, canary and evaluation calls) |
| The AppHost passes `EmbeddingProfile:*` to the `migrations` resource (without it the pilot profile is not seeded) | now (#159) |
| `KnowledgeService`, query embedding and `/v1/retrieve` | now (#127) |
| An API key for `/v1/retrieve`: `POST /api/keys` in the admin API (#185), or the dev seed's `--api-key` offline (§4.4) | now |
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
dotnet user-secrets --project $p set Gateway:MaxResidency Eu                            # now
dotnet user-secrets --project $p set Gateway:Services:api:Keys:Eu <the api's Eu key>      # now
dotnet user-secrets --project $p set Gateway:Services:admin:Keys:Eu <the admin's Eu key>  # now
dotnet user-secrets --project $p set Gateway:Services:worker:Keys:Eu <the worker's Eu key>  # now
dotnet user-secrets --project $p set OrgSync:BaseUrl https://<gateway host>              # now
dotnet user-secrets --project $p set OrgSync:DataApiKey <the catalog.read key>           # now
dotnet user-secrets --project $p set EmbeddingProfile:GatewayModel text-embedding-3-large  # now
dotnet user-secrets --project $p set EmbeddingProfile:Dimensions 1024                    # now
dotnet user-secrets --project $p set EmbeddingProfile:RequestDimensions true             # now
dotnet user-secrets --project $p set EmbeddingProfile:MaxInputTokens 8191                # now
dotnet user-secrets --project $p set EmbeddingProfile:MaxResidency Eu                    # now
```

Set no `External` and no `OnPrem` key: the worker never holds `External` (the AppHost refuses
`Gateway:Services:worker:Keys:External` and keeps a shared `Gateway:Keys:External` from the worker), and the pilot runs
at `Eu`. With one key for all three, `Gateway:Keys:Eu` alone works too. `Gateway:MaxResidency Eu` caps every model
call's ceiling at `Eu` (register M2, identity design §4.7): without it a `public` assistant is checked and answered at
`External`, for which no key exists (admin code `gateway_key_missing`). The AppHost forwards every `Gateway:*` value. The
migrator seeds the profile as `pilot-embedding` (#123's default `EmbeddingProfile:Name`) and never changes it once it
exists: a different model, dimension count or `RequestDimensions` needs a new volume (the migrator only logs that the
profile differs from `EmbeddingProfile:*`).

### 4.3 Start

**Now** (#123, #159, #162). Start without the demo data, so the FakeGateway's `dev-embedding`
profile is not seeded next to the pilot profile:

```powershell
.\scripts\Start-Dev.ps1 -Empty
```

In the Aspire dashboard (`https://localhost:17470`), check that `migrations` completed and that no `fake-gateway`
resource runs. Copy the `ragdb` connection string from the `postgres` resource (Host `localhost`, the mapped port,
user `postgres`, the generated password); keep it out of transcripts. For the API key (§4.4) also copy the
`key-pepper` parameter (the dashboard's Parameters, or `dotnet user-secrets --project src/Ume.Rag.AppHost list`, key
`Parameters:key-pepper`): rag-api hashes keys with it (`Security:KeyPepper`).

**On Linux or WSL** (no PowerShell needed), use the Aspire CLI with the same switches; `aspire` is the CLI from the
NuGet cache (`~/.nuget/packages/aspire.cli.linux-x64/<version>/tools/net10.0/linux-x64/aspire`) or `dotnet tool`:

```bash
export Seed__Enabled=false
aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive   # prints the dashboard login URL
aspire describe --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj                    # resource states
```

With WSL's mirrored networking the Windows browser reaches every `localhost` port. If `https://localhost:5174` shows
another app, the browser serves a cached one: open it in a private window. The dev certificate is not trusted on the
Windows side (accept the warning once per port: 5174, 7402, Keycloak's port).

**Since #172** (2026-11-01) the AppHost creates the `vector` extension (the migrator, dev superuser path), keeps the
database across restarts (the volume at `/var/lib/postgresql/data`, the cluster in `pgdata/` below it) and passes
`BlobStore__Root` = `~/.ume-rag/blobs/rag` (mode 0700) to the services; `BlobStore:Root` in the AppHost configuration
wins. A dev volume created before #172 never held the database (it lived in an anonymous volume), so the first start
after #172 begins with an empty database: run the seed (§4.4) again.

**Open WebUI on the AppHost** (#251): with `Seed:Enabled=false` the `open-webui` container starts only for a configured
client. Create the Open WebUI client (`POST /api/clients`, kind `open-webui`) and its key (`POST /api/keys`), then put
the client's id in `OpenWebUi:ClientId` and the whole `ume-rag-sk-…` key in `Parameters:openwebui-api-key` (user
secrets only) and restart the AppHost. Publish an assistant with that client in its audience; it then appears as a
model in Open WebUI. `dotnet run --project src/Ume.Rag.AppHost` runs the Debug build even with `-c Release`, so build
Debug first.

### 4.4 Seed a URL-list source (now on FakeGateway; for the pilot profile after 4.3)

`scripts/dev/seed_url_list.py` creates, in the local database only, a department (`Pilot (dev seed)`), a public and
published collection (`pilot-public`) on the given embedding profile, and a URL-list source (`Pilot URL list`) on the
platform `url-list` pipeline, then enqueues a manual sync as the admin's "sync now" does. The source's settings carry
`piiMode: report`, as a source on the municipality's own published site does (Decisions table, 2026-11-01): a
personal-data flag is reported, not held, except a personnummer or samordningsnummer; `--pii-mode hold` keeps the
hold. A re-run finds the same rows and replaces the source's URL list and `piiMode`. It refuses any database host other than `localhost`, `127.0.0.1` or `::1`.
The SQL is `scripts/dev/seed-url-list.sql`; `DevSeedScriptTests` runs the same file against the test database.

Pick a handful of public pages (five is plenty for the milestone); the worker fetches them as `UmeaKommunBot/1.0`,
reads robots.txt first and keeps its per-host budget, so a small list is a few polite requests:

```bash
export UME_RAG_DEV_DB='Host=localhost;Port=<port>;Username=postgres;Password=<password>;Database=ragdb'
python3 -I scripts/dev/seed_url_list.py --profile pilot-embedding \
  https://www.umea.se/<page 1> https://www.umea.se/<page 2> https://www.umea.se/<page 3>
```

To keep the password out of the shell history and transcripts, build the connection string from the container:

```bash
C=$(docker ps --format '{{.Names}}' | grep -E '^postgres-' | head -1)
export UME_RAG_DEV_DB="Host=localhost;Port=$(docker port "$C" 5432/tcp | head -1 | sed 's/.*://');Username=postgres;Password=$(docker exec "$C" printenv POSTGRES_PASSWORD);Database=ragdb"
```

Without `psql` on the machine, add `--docker <the postgres container>` (`docker ps --filter name=postgres`); the
script then runs psql inside the container. `--no-sync` leaves the first run to the scheduler (the platform url-list
schedule), `--urls-file` reads one URL per line. The output lists every row it found or created, the sync job id (the
run id) and the config version. On FakeGateway the same command works now with `--profile dev-embedding` (the default
start seeds that profile).

The department is not in the gateway catalogue, so the org sync marks it inactive on its next run; the worker does not
read that flag, so ingestion is not affected. Pass `--department-id <id>` to use a synced department instead.

**An API key for `/v1/retrieve` (now).** rag-api's knowledge routes take an `internal` client's key only. The admin
API issues keys since #185 (below); offline, the seed script does it. `--api-key` also creates, insert-if-absent like the rest: an `internal`
client `Pilot retrieval (dev seed)` (`--client-name`) with ceiling `public` and no history store; a secret key of that
client scoped to the collection, expiring after 30 days; and the `public` level's data-protection row (history mode
`none`, 90/90 days), without which rag-api refuses every public collection. The pepper comes only from the environment:

```bash
read -rs UME_RAG_DEV_KEY_PEPPER && export UME_RAG_DEV_KEY_PEPPER   # paste the key-pepper parameter (§4.3)
python3 -I scripts/dev/seed_url_list.py --profile pilot-embedding --api-key \
  https://www.umea.se/<page 1> https://www.umea.se/<page 2> https://www.umea.se/<page 3>
```

The script generates the key (`ume-rag-sk-` and 43 base62 characters), sends only its HMAC to the database and prints
the key once, at the end of the output, when it creates it. Copy it from the terminal; it is not stored or logged
anywhere. A re-run with `--api-key` finds the usable key and prints nothing secret. A key whose plaintext is lost, or
one hashed under an earlier pepper (a new AppHost secrets store), is replaced by revoking it and running again:

```sql
UPDATE config."Keys" SET "RevokedAt" = now() WHERE "Name" = 'dev seed: pilot-public' AND "RevokedAt" IS NULL;
```

**Or through the admin API (now, #185).** Signed in to Scalar (`https://localhost:7402/scalar`) as `rag-platform-admin`:

1. `POST /api/clients` `{"name": "Pilot retrieval", "kind": "internal", "classificationCeiling": "public", "storesHistory": false}`
   (clients are global and only a platform admin creates them).
2. `POST /api/keys` `{"clientId": "<its id>", "name": "pilot retrieval", "scopes": {"collectionIds": ["<pilot-public's id>"]}, "isEnabled": true}`.
   The response holds the secret (`ume-rag-sk-…`) once; copy it into `read -rs UME_RAG_KEY` (§4.5). A platform admin
   may leave out `teamId` and `scopes`; a department admin must give a team of theirs and non-empty scopes in its
   department (`key_scope_required`).

The admin hashes the key with the same `Security:KeyPepper` as rag-api, so no pepper is copied by hand. The seed
script's `--api-key` stays for runs without signing in (it reads the pepper from `UME_RAG_DEV_KEY_PEPPER`).

### 4.5 What to check

| Check | How | When |
|---|---|---|
| The run finished | `SELECT "State", "Added", "Updated", "Unchanged", "Failed" FROM content."Runs" WHERE "Id" = '<sync job id>';` → `succeeded`, `Added` = the number of URLs | now (#162) |
| Live chunks with 1024-dimension vectors | `SELECT count(*), min(vector_dims(c."Embedding")) FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" WHERE d."SourceId" = '<source id>' AND c."DeletedAt" IS NULL;` → at least one chunk per page, 1024 | now (#162) |
| Failures, if any | `SELECT "Identity", "Outcome", "Reason" FROM content."RunItems" WHERE "RunId" = '<sync job id>' ORDER BY "Id";` and the worker's log in the dashboard | now (#162) |
| The canary is recorded | `GET /api/gateway/status` in the admin API's Scalar (signed in as a dev Keycloak admin): every `levels[]` entry you hold a key for is `ok` with residency `Eu`, `profiles[]` lists `pilot-embedding` with `canaryRecorded: true`; `POST /api/gateway/sync` records it at once instead of waiting for the next sync | now (#123) |
| Retrieval | a `/v1/retrieve` query on the api about one of the seeded pages with the dev seed's key (below): the hits are chunks of the seeded pages, every `collection` is `pilot-public` | now (#127, the key from §4.4) |

Run the queries with `psql -h localhost -p <port> -U postgres -d ragdb` (the password from the dashboard) or in the
container (`docker exec -it <postgres container> psql -U postgres -d ragdb`).

The retrieval query, on the api's https endpoint from the dashboard (`https://localhost:7401` by its launch profile).
The key goes in `Authorization: Bearer`, `x-api-key` or `api-key`; read it into a variable so it stays out of the
shell history:

```bash
read -rs UME_RAG_KEY   # paste the ume-rag-sk-… key the seed printed
curl -sS https://localhost:7401/v1/retrieve -H "Authorization: Bearer $UME_RAG_KEY" -H 'Content-Type: application/json' \
  -d '{"query": "Hur ansöker jag om bygglov?", "collections": ["pilot-public"], "top_k": 5}'
```

`401 invalid_key` means the key is mistyped or hashed under another pepper; `403 client_not_allowed` a client of another
kind; `404 collection_not_found` a slug the client may not see (or no `public` level row); `503 config_unconfirmed`
right after a start: try again. On Linux, where curl does not trust the dev certificate, add `-k` (local only).

Retrieval serves only `live` sets. A page whose personal-data check flagged it in a `public` collection is **held**
(processing design §9.5): `Added` in the run, with reason `pii.flagged` in its run item, but not served until an admin
clears it. Check with:

```sql
SELECT d."Url", s."State", s."PiiCounts" FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId";
```

The §4.5 chunk query counts only served chunks (`DeletedAt IS NULL`); a held set's chunks keep `DeletedAt`.

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

### 4.7 What milestone 1 showed (2026-11-01)

| Finding | Status |
|---|---|
| The interim gateway accepts `dimensions` for `text-embedding-3-large`; vectors are 1024 | confirmed |
| The dashboard parameter is `key-pepper` (user secret `Parameters:key-pepper`); rag-api runs on `https://localhost:7401` | confirmed |
| The AppHost from an empty volume: `vector` extension, volume path, `BlobStore:Root` | fixed by #172 (closes #168) |
| Two pages held for e-mail addresses only: `Settings.Engine.PiiOwnDomains` is empty, so `@umea.se` counts as external | the user decided `piiMode: report` for the municipality's own site (Decisions table, 2026-11-01); set `PiiOwnDomains` to `umea.se` and the municipal companies' domains |
| The gateway's PII filter answered a policy block with 503: the worker retried it as an outage until the key policy changed (attempt 5 of 6) | for the gateway session: a policy refusal should be a 4xx with a reason code |
| A hub page of link cards (`bygganyttandraellerriva`) is `parse.empty` and counted as `Unchanged` in the run | expected for the page; the counter is checked by stream A |
| The worker's job log names the exception type only (`threw System.UnauthorizedAccessException`); `LastError` is `job.exception` | #179: the log names the type chain and the throw site (never the message) |
