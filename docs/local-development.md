# Local development

You need:

- the .NET SDK that [`global.json`](../global.json) selects: 10.0.100 or a later 10.0 feature band (`rollForward:
  latestFeature`; CI uses 10.0.401);
- Docker 28+ (Docker Desktop on Windows, or Docker Engine on Linux and WSL);
- PowerShell 7 (`pwsh`) for the start scripts in `scripts/`;
- the Aspire CLI 13.6 on `PATH` as `aspire`: `dotnet tool install --global Aspire.Cli --version 13.6.1`. The AppHost
  sets `AspireUseCliBundle`, so a restore also puts a copy in the NuGet cache
  (`~/.nuget/packages/aspire.cli.<rid>/13.6.1/tools/net10.0/<rid>/aspire`), which you can call by that path;
- [uv](https://docs.astral.sh/uv/) for the parser, and Node 24.15+ once the admin UI (`src/admin-ui`) exists.

Enable the pre-commit hook once per clone. It runs the admin UI lint and the parser's ruff check on staged changes:

```bash
git config core.hooksPath .githooks
```

## Start the stack

```powershell
.\scripts\Start-Dev.ps1          # with demo data (default)
.\scripts\Start-Dev.ps1 -Empty   # empty database, only the Keycloak test users
```

The script sets `Seed__Enabled`, runs `aspire start` on `src/Ume.Rag.AppHost`, then `aspire wait` for the admin UI, or
for `adminapi` while `src/admin-ui` does not exist yet. You can run the same commands yourself, or start the AppHost
directly:

```bash
aspire run      # or: dotnet run --project src/Ume.Rag.AppHost
```

The Aspire dashboard is at `https://localhost:17470` (the AppHost's `https` launch profile). It is for development
only: production never runs it. To stop the stack:

```bash
aspire stop --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive
```

Stop **this** AppHost before you build the backend or edit the AppHost (as in the gateway).

**Demo data.** The migration service seeds only an empty database, and never in Production. The seeder does not exist
yet: it arrives with the first seeded table. Until then `-Empty` and the default start the same stack. To switch an
existing environment between demo and empty, stop the stack, delete the AppHost's Postgres volume
(`docker volume ls`, then `docker volume rm <name>`, or the volume named in `DevelopmentVolumes:Postgres`), and start
again.

**Generated secrets.** The AppHost generates its passwords and keys on the first run and keeps them in its user
secrets (`UserSecretsId` `ume-rag-apphost-dev`) as `Parameters:<name>`: `postgres-password`, `key-pepper`, `dev-key`,
`oidc-client-secret`, `dev-user-password`, `api-client-secret` and the per-level gateway keys `gateway-key-onprem`,
`gateway-key-eu` and `gateway-key-external`. Keep them stable. The Postgres volume stores the password it was created
with, and the key pepper hashes every key in the database, so changing either breaks the existing data. Do not print
them into logs, transcripts or screenshots; the dashboard shows a parameter's value when you need it.

## AppHost switches

The resource graph is in [`src/Ume.Rag.AppHost/Program.cs`](../src/Ume.Rag.AppHost/Program.cs) and
[`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §4.1. Set a switch in the AppHost
user secrets, for example:

```bash
dotnet user-secrets --project src/Ume.Rag.AppHost set Parser:Mode off
```

| Setting | Default | Effect |
|---|---|---|
| `Parser:Mode` | `container` | `container` builds `src/parser/Dockerfile`; `local` runs `uvicorn ume_parser.app:app` from `src/parser` with hot reload (see [the parser's README](../src/parser/README.md) for the models it then needs); `off` starts no parser |
| `ClamAv:Enabled` | `true` | `false` starts no ClamAV (the worker's EICAR-only fake scanner comes with its scan step, P1) |
| `FakeGateway:Enabled` | `true` | `false` runs against a real gateway (below) |
| `FakeGateway:Latency:*` | 0 (off) | a real gateway's timing on the fake (below) |
| `Ollama:Enabled` | `false` | real BGE-M3 embeddings from Ollama behind the fake gateway (development only: the AppHost refuses it outside run mode) |
| `Redis:Enabled` | `false` | adds Redis (the P2 multi-host path; the pilot keeps rate limits and caches in process) |
| `Oidc:Authority` | unset | an existing identity provider instead of the bundled Keycloak, which then does not start; every `Oidc:*` setting is passed to the admin API |
| `Seed:Enabled` | `true` | demo data on an empty database |
| `DevelopmentVolumes:Postgres` | generated | the Postgres volume's name |
| `DevelopmentVolumes:Ollama` | generated | the Ollama volume's name |

Environment variables (`Section__Key`) override the user secrets: `Start-E2E.ps1` sets `FakeGateway__Enabled=true`
that way.

**Blob master key.** `internal` blobs are envelope-encrypted (`URB2`, data-model §7) with the master key file
`rag-blob-internal` in `KeyProvider:Directory` (default `/run/keys`, the Compose mount). Without it, syncs of `internal`
sources fail with "Master key 'rag-blob-internal' is not available"; `public` sources need no key. Until the AppHost
creates a development key, make one and point the worker at it through its user secrets:

```bash
mkdir -p ~/.ume-rag/keys && openssl rand -base64 32 > ~/.ume-rag/keys/rag-blob-internal
dotnet user-secrets --project src/Ume.Rag.Worker set KeyProvider:Directory "$HOME/.ume-rag/keys"
```

On Windows any absolute directory works; write the file with
`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`. Keep the file as long as the
development blobs: without it their `internal` blobs cannot be read.

Service ports (launch profiles): api `https://localhost:7401`, worker `7403`, admin API `7402`, fake gateway `7404`.
The admin UI will be on `https://localhost:5174`, beside the gateway's 5173, so both stacks can run side by side.

## Keycloak test realm

Without `Oidc:Authority` the AppHost starts Keycloak and imports the realm `ume` from `dev/keycloak/` (stream B,
identity design §11). Every test user has the same password, the `dev-user-password` parameter.

| User | Roles | Department codes |
|---|---|---|
| `rag-platform-admin` | `rag-platform-admin` | 1000, 3000, 5000 |
| `rag-department-admin` | `rag-department-admin` | 1000 |
| `rag-pipeline-editor` | `rag-pipeline-editor` | 1000 |
| `rag-reviewer` | `rag-reviewer` | 1000 |
| `rag-skill-editor` | `rag-skill-editor` | 1000 |
| `rag-viewer` | `rag-viewer` | 1000 |
| `rag-group-member` | `rag-viewer` | none: 3000 through its group `Byggnadsnämnden` |
| `rag-no-role` | none (every `/api` call answers 403) | 1000 |
| `rag-chat-user` | none (delegated chat tokens) | none |

Clients: `ume-rag-admin` (the admin BFF, code + PKCE), `ume-rag-api-dev` (client credentials, roles
`Knowledge.Read.All` and `Chat.Invoke.All`), `ume-rag-chat-dev` (public, PKCE, loopback redirect, scopes `Chat.Use`,
`Knowledge.Read`, `Mcp.Use`) and `open-webui-dev`. Their secrets are AppHost parameters as well.

## Fake gateway

Every model call goes through the gateway; locally that is `Ume.Rag.FakeGateway`
([`repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §4.3). It serves 1024-dimension embeddings,
chat completions built from the prompt (streamed or not), `/v1/models`, `/v1/rerank` and the catalogue
(`/v1/catalog/departments`, `/v1/catalog/teams`). Like the real gateway it requires `x-ume-max-residency` and answers
with `x-ume-residency`. Its three dev keys have the ceilings `OnPrem`, `Eu` and `External`; the services get them as
`Gateway__Keys__<level>`, and the worker never gets the `External` one.

Its models are in [`src/Ume.Rag.FakeGateway/appsettings.json`](../src/Ume.Rag.FakeGateway/appsettings.json), including
fault models for tests: `fake-fail-500`, `fake-slow`, `fake-wrong-residency`, `fake-wrong-residency-embed`,
`fake-drained` and the stream faults `fake-stream-broken`, `fake-stream-stall`, `fake-stream-no-done`,
`fake-stream-empty-deltas` and `fake-stream-length`.

By default it answers at once. `FakeGateway:Latency:*` in the AppHost user secrets gives it a real gateway's timing
(all 0 by default): `FirstTokenMs`, `PrefillMsPer1kInputTokens`, `TokenIntervalMs`, `JitterPercent`, `AnswerWords`,
`StallProbability`, `StallMs`.

**Against a real local gateway** (its AppHost running): set `FakeGateway:Enabled=false`,
`Gateway:BaseUrl=https://localhost:<gateway port>` and the gateway's dev keys in the AppHost user secrets.
`Gateway:Keys:<level>` is shared: it goes to the api and the admin API, and to the worker unless the level is
`External`. `Gateway:Services:<service>:Keys:<level>` (service `api`, `admin` or `worker`) gives one service its own key
and wins over the shared one. The levels are `OnPrem`, `Eu` and `External`; the AppHost stops on any other level, an
unknown service or `Gateway:Services:worker:Keys:External`. Every other `Gateway:*` setting (`BaseUrl`, ...) goes to all
three, and `OrgSync:BaseUrl` and `OrgSync:DataApiKey` (the organisation sync's Data API key) to the admin API. The gateway's FakeLlm needs gateway
release 1 for 1024-dimension embeddings (`fake-embed-1024`); with an older one, use its Ollama provider with `bge-m3`.
The `OnPrem` key used for embeddings must have PII policy `Off` or `Allow`, never `Redact` or `Block`.

## Build and test

```bash
dotnet build UmeRag.slnx -c Release --nologo
dotnet test --solution UmeRag.slnx -c Release --no-build --filter-not-trait "Category=Flaky"
```

`--filter-not-trait` skips the tests marked `[Trait(TestCategory.Name, TestCategory.Flaky)]`, as CI does; plain
`dotnet test --solution UmeRag.slnx` runs them too. A flaky mark is a to-do: say why next to the trait and fix the test.

The integration tests start Postgres with Testcontainers, so Docker must be running. By default they use the same
pinned `pgvector/pgvector` image as the AppHost (`TestPostgres.DefaultImage`). To run them against the production
image, build it and name it in `UME_RAG_TEST_POSTGRES_IMAGE`:

```bash
docker build -f images/postgres/Dockerfile -t ume-rag.local/postgres:dev .
UME_RAG_TEST_POSTGRES_IMAGE=ume-rag.local/postgres:dev dotnet test --solution UmeRag.slnx -c Release
```

**Name local images with a registry part that has a dot** (`ume-rag.local/...`). When
`TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX` is set (CI sets `mirror.gcr.io/` to avoid Docker Hub's pull limit),
Testcontainers puts the prefix in front of every image without a registry, so `ume-rag/postgres:dev` would be pulled as
`mirror.gcr.io/ume-rag/postgres:dev` and not found.

The contract tests (`tests/Ume.Rag.ContractTests`) run the worker's parser client against a parser image. Without
`UME_RAG_TEST_PARSER_IMAGE` the tests that need the image skip:

```bash
docker build -t ume-rag.local/parser:ci src/parser
UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci dotnet test --project tests/Ume.Rag.ContractTests
```

**Image tests.** Each image has a test script that runs it hardened, as Compose does:

```bash
docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
bash deploy/postgres/test.sh ume-rag/postgres:latest      # pgvector, HNSW, TLS, archive-push, backup and restore
docker build -t ume-rag/clamav:dev images/clamav
bash images/clamav/test.sh ume-rag/clamav:dev             # offline: a one-line EICAR signature
bash images/clamav/test.sh --online ume-rag/clamav:dev    # downloads the real signatures (about 115 MB)
```

**Browser tests.** `Start-E2E.ps1` starts the stack on its own Postgres volume (`ume-rag-e2e-postgres`), reset on every
start, with demo data and the fake gateway forced on. It uses the dev stack's ports, so stop the dev stack first. The
Playwright tests (`tests/e2e`) arrive with the admin UI.

```powershell
.\scripts\Start-E2E.ps1          # reset the test volume, start
.\scripts\Start-E2E.ps1 -Keep    # start without resetting it
.\scripts\Start-E2E.ps1 -Stop    # stop the stack and remove the test volume
```

## Parser

The parser is a Python service in `src/parser` with its own README: [`src/parser/README.md`](../src/parser/README.md)
(uv, ruff, pytest, the image's test target, the contract and its goldens, updating pins). In short:

```bash
cd src/parser
uv sync --frozen
uv run ruff check . && uv run ruff format --check .
uv run pytest
docker build --target test -t ume-rag/parser:test .
docker run --rm --network none ume-rag/parser:test     # the tests that need Docling, the models and Tesseract
```

The image is about 3 GB. After changing `ume_parser/models.py`, run `uv run python -m scripts.export_schema` and
commit `contracts/parser/v1/`; CI fails when they differ.

## Production Compose on your machine

To try the production files locally, build the images, then run `init-deployment.sh` in a scratch copy of `deploy/`
(not in the clone: it writes `.env`, `certs/`, `secrets/` and, with `--backup-target local`, `upload-backup/` next to
itself) with a local backup target:

```bash
docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
docker build -t ume-rag/clamav:latest images/clamav
docker build -t ume-rag/parser:latest src/parser
for p in Api AdminApi Worker MigrationService; do dotnet publish src/Ume.Rag.$p -c Release -t:PublishContainer; done
cp -r deploy ~/rag-deploy-test
~/rag-deploy-test/init-deployment.sh --backup-target local --allow-file-keys \
  --oidc-authority https://<keycloak host>/realms/ume --gateway-url https://<a gateway> --api-host localhost --yes
echo 'UME_RAG_INGESTION_SLICE=' >> ~/rag-deploy-test/.env    # no rag-ingestion.slice on a developer machine
docker compose --env-file ~/rag-deploy-test/.env up -d --wait
```

Run the `chown` commands the script prints first, unless it ran as root. The .NET images are tagged
`ume-rag/<service>:latest` (`ContainerImageTag` in `Directory.Build.props`), which is what the Compose files use by
default. See [production-setup.md](production-setup.md) for what the script generates, and
[p1a-local-run.md](p1a-local-run.md) for the local P1a run (now on the dev Keycloak and FakeGateway, then the live
block's shared Keycloak and gateway).

## Common problems

| Problem | Cause and fix |
|---|---|
| On WSL, the first `aspire` command asks to trust the development certificate in the Windows certificate store | Trust it once with `dotnet dev-certs https --trust`; the dashboard and the services' HTTPS endpoints use that certificate |
| `docker compose up` of the production files fails with "Pool overlaps with other one on this network" | The Compose networks use fixed subnets in `172.30.0.0/16`, and another Docker network on your machine already uses one. List them with `docker network inspect $(docker network ls -q) --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'`; remove the network that collides, or keep `172.30.0.0/16` out of Docker's `default-address-pools` |
| Integration tests pull `mirror.gcr.io/ume-rag/...` and fail | A local image without a registry part while `TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX` is set: name it `ume-rag.local/...` |
| The parser stays unhealthy for minutes after a start | It warms up its slots (Docling, the models) and runs a self-test first; set `Parser:Mode=off` when you do not need it |
| ClamAV is slow to become healthy on the first start | freshclam downloads the signatures into the `ume-rag-clamav-db` volume first; later starts reuse them. `ClamAv:Enabled=false` skips it |
| Postgres authentication fails after you cleared the user secrets | The volume keeps the old password: restore `Parameters:postgres-password`, or delete the volume (and its data) |
| `ume-rag-e2e-postgres` cannot be removed | A stack still uses it: `.\scripts\Start-E2E.ps1 -Stop`, or stop the AppHost first |
