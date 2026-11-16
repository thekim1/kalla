# Local P1a run

P1a is shown on a developer machine with the pilot member present (`plans/archive/p1/README.md` §7, U-a). Until the live block
at the end of P1a, everything runs against the **dev Keycloak** and **FakeGateway**; the live block points the same
stack at the **interim gateway** (J10) and the **Keycloak shared with the gateway** (J11). Their addresses, keys and
secrets go only in the AppHost's user secrets or in `deploy/.env`, which `init-deployment.sh` writes (mode 600),
never in the repository, a transcript or a screenshot. Every host name below is a placeholder.

There are two ways to run the stack locally:

| | The AppHost ([local-development.md](local-development.md)) | The pilot stack (production Compose) |
|---|---|---|
| What runs | the services from source, the dev Keycloak (realm `ume` from `dev/keycloak/`), FakeGateway, Open WebUI (`http://localhost:7410`) | the images, as on the pilot host: `compose.rag.yaml` with `compose.backup.yaml` (the pilot host adds `compose.hardened.yaml`), Open WebUI as the profile `openwebui` |
| Sign-in and model calls now | yes: dev Keycloak test users, FakeGateway | no: Compose has no Keycloak and no FakeGateway, so sign-in and answers wait for the live block |
| Use it for | the demo: sign-in, set-up through Scalar, a cited answer in Open WebUI | checking the production shape: every container healthy, secrets as files, the networks |

## 1. Now: the AppHost against the dev Keycloak and FakeGateway

```powershell
.\scripts\Start-Dev.ps1
```

The AppHost starts Postgres, the dev Keycloak, FakeGateway (1024-dimension embeddings, a dev credential key per
service), the parser, ClamAV, the renderer, the api, the worker, the admin API, the admin UI and Open WebUI
(`OpenWebUi:Enabled`, the dev realm's client `open-webui-dev`, the seeded dev key; repo-and-deployment §4.1). Sign in
to the admin with a test user from [local-development.md](local-development.md#keycloak-test-realm) (password: the
`dev-user-password` parameter).

## 2. Now: the pilot stack on your machine

Build the images from the commit you are testing (as in
[local-development.md](local-development.md#production-compose-on-your-machine)): `rag-bootstrap` copies
`deploy/postgres/app-roles.sql` from the tree into the stack, so older `rag-migrate` images fail on tables they do not
have yet. Then run `init-deployment.sh` in a scratch copy of `deploy/` with a placeholder for the gateway, which is not
there yet, and the local images:

```bash
cp -r deploy ~/rag-p1a && cd ~/rag-p1a
images=()
for s in postgres migrations api admin worker parser clamav renderer; do images+=(--set "RAG_${s^^}_IMAGE=ume-rag/$s:latest"); done
./init-deployment.sh --gateway-url https://gateway.invalid --api-host localhost --backup-target local "${images[@]}" --yes
docker compose --env-file .env up -d --wait
docker compose --env-file .env ps
docker run --rm --network none -v ume-rag_rag-certs:/c:ro --entrypoint cat \
  "$(docker compose --env-file .env config --images rag-bootstrap)" /c/ca.pem > ca.pem
curl --cacert ca.pem https://localhost:8543/health/ready
```

`rag-bootstrap` creates the certificates and secrets in the project's volumes (`ume-rag_rag-certs`,
`ume-rag_rag-secrets`) and gives each service's files to its user. `up --wait` returns when every service with a
health check is healthy and `rag-migrate` has completed; the parser and ClamAV take a few minutes on the first start.
The health checks do not call the OIDC authority or the gateway, so the placeholder does not stop the stack from
becoming healthy. Without `--hardened` the networks take subnets that Docker chooses, and neither the host firewall
nor the ingestion slice is used on a developer machine.

`deploy/tests/release-smoke.sh` runs the same steps on a release bundle and checks the listeners
([production-setup.md](production-setup.md)).

What `rag-bootstrap` generated for P1a, in `rag-secrets`: the Open WebUI JWT secret, once, as
`api/OpenWebUi__JwtSecret` and as `openwebui/jwt-secret`; the worker's crawl key pepper
`worker/Security__CrawlKeyPepper` (the HMAC key of `HostHash` in security event 5101). A re-run never changes them.

**Open WebUI in Compose on one machine.** With the profile `openwebui`, `rag-openwebui` calls `rag-api` inside the
stack and trusts RAG's CA, but it sends the browser back to `https://UME_RAG_OPENWEBUI_HOST/oauth/oidc/login/callback`
and sets secure cookies: on the pilot host a TLS proxy serves it with a trusted certificate. On a developer machine
there is none (Open WebUI's own port, `127.0.0.1:3000`, is plain HTTP), so the demo's Open WebUI is the AppHost's.
Check that the profile renders with `docker compose --env-file .env --profile openwebui config --quiet`.

## 3. The live block: what changes

Nothing in the repository changes. You set values in one of two places:

| Value | AppHost (user secrets, `dotnet user-secrets --project src/Ume.Rag.AppHost set ...`) | Pilot stack (`init-deployment.sh` options, written to `deploy/.env`, or rag-admin's setup wizard) |
|---|---|---|
| The interim gateway (J10) | `FakeGateway:Enabled=false`, `Gateway:BaseUrl=https://<gateway host>` | `--gateway-url https://<gateway host>` (a publicly trusted certificate: no `--gateway-ca-file`) |
| Its credential keys of the integration *Ume.Rag*, one per service (production-setup.md, *Gateway credential keys*) | `Gateway:Services:api:Key=<key>`, `Gateway:Services:admin:Key=<key>`, `Gateway:Services:worker:Key=<key>`. The AppHost stops on an unknown service | `--gateway-credential-key-file api=rag-api.key --gateway-credential-key-file admin=rag-admin.key --gateway-credential-key-file worker=rag-worker.key` |
| The admin's organisation and model syncs | nothing more: they read the gateway's `/v1/ume/context` and `/v1/models` with `Gateway:Services:admin:Key`, which needs the scope `organisation.read`. The AppHost stops on `OrgSync:BaseUrl` and `OrgSync:DataApiKey`, the Data API settings of before | nothing more: the admin's key (`--gateway-credential-key-file admin=...`) with the scope `organisation.read` |
| The shared Keycloak (J11) for the admin | `Oidc:Authority=https://<keycloak host>/realms/ume`, `Oidc:ClientSecret=<secret of ume-rag-admin>`; the bundled Keycloak then does not start | the setup wizard (`https://<admin host>/setup`, the setup code in rag-admin's log) takes the realm and the secret of `ume-rag-admin`; declared instead: `--oidc-authority https://<keycloak host>/realms/ume --set-file UME_RAG_OIDC_CLIENT_SECRET=ume-rag-admin.secret` |
| The shared Keycloak for Open WebUI | the AppHost's `open-webui` resource | `openwebui` in `--profiles`, and the setup wizard's Open WebUI step takes the secret (declared instead: `--set-file UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=open-webui.secret`); client id `open-webui` and the provider URL (`<authority>/.well-known/openid-configuration`) are the defaults; `--set UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME=...` sets the sign-in button's label (default *Umeå kommun*) |
| Open WebUI's client in rag-api | created by the AppHost seed | nothing: `rag-bootstrap` generates its key and `rag-migrate` registers it as the deployment's own Open WebUI client (identity design §5.2) |

Keep the key and secret files outside the repository (for example in `~/rag-p1a-secrets/`, mode 0600) and delete them
once `init-deployment.sh` has run: it keeps each value in `.env` (mode 600). Re-run `init-deployment.sh` with only the
new options: it changes what they name and keeps every other line of `.env`, and `rag-bootstrap` keeps every generated
secret in the volumes. Then recreate the services: `docker compose --env-file .env up -d --wait`.

The redirect URIs the setup wizard shows must be registered on the shared realm's clients: `ume-rag-admin`
(`https://<admin host>[:<port>]/signin-oidc`, `/signout-callback-oidc`) and `open-webui`
(`https://<owui host>/oauth/oidc/login/callback`). The realm additions (`deploy/keycloak/`, stream B, J8)
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
issue). The run used a dev seed script for the source; the admin UI creates it today.

**Passed 2026-11-01** (session 13, on Linux/WSL): 5 pages fetched, 4 added (one hub page `parse.empty`), 17 chunks at
1024 dimensions, the canary recorded at `Eu`, and `/v1/retrieve` answered in under 1 s with the right chunk first. Until
#168 was fixed (#172), the AppHost needed three manual workarounds; they are no longer needed. What the run showed is in §4.7.

### 4.1 Prerequisites

| | Status |
|---|---|
| Stream B's gateway client and canary recording (`GET /api/gateway/status`); the embedding profile created in the admin UI (M-17 C5) | now (#123) |
| The AppHost's real-gateway mode: `Gateway:Services:<service>:Key` per service, `OrgSync:IntervalMinutes` passed to the admin API | now (#119, B-37) |
| `EmbeddingProfiles.RequestDimensions` and `dimensions` sent on every embedding call (without it the model answers 3072 dimensions and the profile's 1024 refuses them) | now (#159: column, seeder, admin calls, query embedding; #162: the worker's embed, canary and evaluation calls) |
| `KnowledgeService`, query embedding and `/v1/retrieve` | now (#127) |
| An API key for `/v1/retrieve`: `POST /api/keys` in the admin API (#185), or the dev seed's `--api-key` offline (§4.4) | now |
| On the gateway: the integration *Ume.Rag* with one credential key per service, each allowing `OnPrem` and `Eu` only; a gateway at v0.9.0 or later (it checks each embedding input against the deployment's `ContextWindow`), or the `text-embedding-3-large` deployment's `ContextWindow` empty; the scope `organisation.read` on the admin's key for the organisation and model syncs | now (the gateway session) |
| `psql` on your machine, or the AppHost's Postgres container (`--docker` below); Python 3.12 | now |

### 4.2 User secrets

All values go in the AppHost's user secrets, never in the repository. Use a Postgres volume of its own, so the
FakeGateway data stays as it is and the pilot profile is the only (and default) profile:

```bash
p=src/Ume.Rag.AppHost
dotnet user-secrets --project $p set DevelopmentVolumes:Postgres ume-rag-m1-postgres     # now
dotnet user-secrets --project $p set FakeGateway:Enabled false                           # now
dotnet user-secrets --project $p set Gateway:BaseUrl https://<gateway host>              # now
dotnet user-secrets --project $p set Gateway:Services:api:Key <rag-api's credential key>      # now
dotnet user-secrets --project $p set Gateway:Services:admin:Key <rag-admin's credential key>  # now
dotnet user-secrets --project $p set Gateway:Services:worker:Key <rag-worker's credential key>  # now
```

Each service holds its own key, never another's; the worker's never allows `External`, and the pilot runs at `Eu`.
Each key's residencies are its service's ceiling (register M2: `OnPrem` and `Eu`; identity design §4.7): a `public`
assistant's calls carry the ceiling `Eu`, read from the gateway's context.
The AppHost forwards every `Gateway:*` value. The
embedding profile is no secret: you create it in the admin UI after the start (§4.3).

The admin's organisation and model syncs read the gateway itself (`GET /v1/ume/context`, `GET /v1/models`) with
`Gateway:Services:admin:Key`; there is no Data API key. Without the scope `organisation.read` on that key the
organisation sync keeps departments and teams as they are and the admin's gateway status says why (`degraded`). Until
the model sync has run, the model mirror is empty and every model setting is refused (V32).

### 4.3 Start

**Now** (#123, #159, #162). Start without the demo data, **even on the first start**, so the FakeGateway's
`dev-embedding` profile is not seeded next to the pilot profile (it would land in the live database, and its canary
fails with `model_not_found`; #440):

```powershell
.\scripts\Start-Dev.ps1 -Empty
```

Then create the pilot profile in the admin UI, signed in as a dev Keycloak platform admin: *Models → Embedding
profiles → Create profile*, name `pilot-embedding`, model `text-embedding-3-large` (the mirror lists it once the model
sync has run), *Request dimensions* on, dimensions `1024`. The ceiling (`Eu`) and the input limit come from the model,
and the save's test embedding is stored as the canary. It is the only profile, so it is the default.

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

**Open WebUI on the AppHost** (#251): with `Seed:Enabled=false` the `open-webui` container starts only when the user
secrets hold its key: put a whole `ume-rag-sk-` key of your choice (the prefix and 43 letters or digits) in
`Parameters:openwebui-api-key` and restart the AppHost. `rag-migrate` registers it as the deployment's own Open WebUI
client (identity design §5.2, M-17 C4); no client is made by hand. Publish an assistant with that client in its
audience; it then appears as a model in Open WebUI. `dotnet run --project src/Ume.Rag.AppHost` runs the Debug build even with `-c Release`, so build
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
for k in DevelopmentVolumes:Postgres FakeGateway:Enabled Gateway:BaseUrl \
         Gateway:Services:api:Key Gateway:Services:admin:Key Gateway:Services:worker:Key; do
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
