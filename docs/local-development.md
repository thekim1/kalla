# Local development

You need:

- the .NET SDK that [`global.json`](../global.json) selects: 10.0.100 or a later 10.0 feature band (`rollForward:
  latestFeature`; CI uses 10.0.401);
- Docker 28+ (Docker Desktop on Windows, or Docker Engine on Linux and WSL);
- PowerShell 7 (`pwsh`) for the start scripts in `scripts/`;
- the Aspire CLI 13.6 on `PATH` as `aspire`: `dotnet tool install --global Aspire.Cli --version 13.6.5`. The AppHost
  sets `AspireUseCliBundle`, so a restore also puts a copy in the NuGet cache
  (`~/.nuget/packages/aspire.cli.<rid>/13.6.1/tools/net10.0/<rid>/aspire`), which you can call by that path;
- [uv](https://docs.astral.sh/uv/) for the parser, and Node 22 or later for the admin UI in `src/admin-ui` (CI uses 24).

Enable the pre-commit hook once per clone. It runs the admin UI lint and the parser's ruff check on staged changes:

```bash
git config core.hooksPath .githooks
```

## Start the stack

```powershell
.\scripts\Start-Dev.ps1          # with demo data (default)
.\scripts\Start-Dev.ps1 -Empty   # empty database, only the Keycloak test users
```

The script sets `Seed__Enabled`, runs `aspire start` on `src/Kalla.AppHost`, then `aspire wait` for the admin UI, or
for `adminapi` while `src/admin-ui` does not exist yet. You can run the same commands yourself, or start the AppHost
directly:

```bash
aspire run      # or: dotnet run --project src/Kalla.AppHost
```

The Aspire dashboard is at `https://localhost:17470` (the AppHost's `https` launch profile). It is for development
only: production never runs it. To stop the stack:

```bash
aspire stop --apphost src/Kalla.AppHost/Kalla.AppHost.csproj --non-interactive
```

Stop **this** AppHost before you build the backend or edit the AppHost (as in the gateway).

**Demo data.** The migration service seeds only an empty database, and never in Production. `-Empty`
(`Seed__Enabled=false`) starts without it. To switch an
existing environment between demo and empty, stop the stack, delete the AppHost's Postgres volume
(`docker volume ls`, then `docker volume rm <name>`, or the volume named in `DevelopmentVolumes:Postgres`), and start
again.

**Generated secrets.** The AppHost generates its passwords and keys on the first run and keeps them in its user
secrets (`UserSecretsId` `kalla-apphost-dev`) as `Parameters:<name>`: `postgres-password`, `key-pepper`, `dev-key`,
`oidc-client-secret`, `dev-user-password`, `api-client-secret`, `openwebui-client-secret`, `cache-key-pepper`,
`download-grant-key`, `render-ws-path`, `openwebui-jwt-secret`, `openwebui-secret-key`, the services' dev credential
keys for the fake gateway (`gateway-key-api`, `gateway-key-admin`, `gateway-key-worker`) and, when their switch is on,
`setup-secret` and `redis-password`. Keep them stable. The Postgres volume
stores the password it was created with, and the key pepper hashes every key in the database, so changing either
breaks the existing data. Do not print them into logs, transcripts or screenshots; the dashboard shows a parameter's
value when you need it.

## AppHost switches

The resource graph is in [`src/Kalla.AppHost/Program.cs`](../src/Kalla.AppHost/Program.cs) and
[`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §4.1. Set a switch in the AppHost
user secrets, for example:

```bash
dotnet user-secrets --project src/Kalla.AppHost set Parser:Mode off
```

| Setting | Default | Effect |
|---|---|---|
| `Parser:Mode` | `container` | `container` builds `src/parser/Dockerfile`; `local` runs `uvicorn ume_parser.app:app` from `src/parser` with hot reload (see [the parser's README](../src/parser/README.md) for the models it then needs); `off` starts no parser |
| `ClamAv:Mode` | `on` | `off` starts no ClamAV and gives the worker `ClamAv__Mode=off`: files are stored without a malware scan and marked as not scanned (Decisions "Malware scan optional"). With `on` and no clamd configured, a Development worker run outside the AppHost uses its EICAR-only scanner |
| `Render:Mode` | `container` | `container` builds `images/renderer` (Chromium behind Playwright's `launch-server`, hardened as in Compose with `images/renderer/seccomp.json`) and gives the worker `Render__Endpoint` with a generated secret path (parameter `render-ws-path`); `off` starts no renderer, and `browser` sources wait with `render.not_configured`. Unlike production the renderer is on Aspire's network, not an internal one |
| `OpenWebUi:Enabled` | `true` | `false` starts no Open WebUI |
| `FakeGateway:Enabled` | `true` | `false` runs against a real gateway (below) |
| `FakeGateway:Latency:*` | 0 (off) | a real gateway's timing on the fake (below) |
| `Ollama:Enabled` | `false` | real BGE-M3 embeddings from Ollama behind the fake gateway (development only: the AppHost refuses it outside run mode) |
| `Redis:Enabled` | `false` | adds Redis (the P2 multi-host path; the pilot keeps rate limits and caches in process) |
| `Oidc:Authority` | unset | an existing identity provider instead of the bundled Keycloak, which then does not start; every `Oidc:*` setting is passed to the admin API |
| `Setup:Try` | `false` | `true` starts the admin API without `Oidc__Authority` and `Oidc__ClientSecret` and with a generated `Setup__Secret` (parameter `setup-secret`), so the setup wizard opens (identity design §19); below |
| `Seed:Enabled` | `true` | demo data on an empty database |
| `DevelopmentVolumes:Postgres` | generated | the Postgres volume's name |
| `DevelopmentVolumes:Ollama` | generated | the Ollama volume's name |

Environment variables (`Section__Key`) override the user secrets: `Start-E2E.ps1` sets `FakeGateway__Enabled=true`
that way.

**Blob master key.** `internal` blobs are envelope-encrypted (`URB2`, data-model §7) with the master key file
`rag-blob-internal` in `KeyProvider:Directory` (default `/run/keys`, the Compose mount). Without it, syncs of `internal`
sources fail with "Master key 'rag-blob-internal' is not available"; `public` sources need no key. In run mode the
AppHost creates a development key once under `~/.kalla/keys/rag-blob-internal` (32 random bytes, base64, mode 600)
and passes `KeyProvider__Directory` to the worker, the api and the admin (#165); `KeyProvider:Directory` in the AppHost's
configuration wins. Keep the file as long as the development blobs: without it their `internal` blobs cannot be read.
An earlier manual key in the same place is used as it is.

**Trying the setup wizard.** With `Setup:Try=true` the admin API prints the setup code in its console log: the
dashboard's `adminapi` resource, or `aspire logs adminapi` when the stack runs under `aspire start`; the wizard is the admin UI's `/setup`. A database whose setup is already done (any earlier run signed in with the AppHost's
values) stays closed: also set `Setup:Reopen` to a new value, which the AppHost passes on. In the wizard the values to
test are the bundled Keycloak's realm, `https://localhost:7411/realms/fors`, and the AppHost parameter
`oidc-client-secret` as the client secret; the client ID `kalla-admin` is set by the AppHost and shows as locked.
Remove both settings to go back.

Service ports (launch profiles): api `https://localhost:7401`, worker `7403`, admin API `7402`, fake gateway `7404`.
The admin UI is on `https://localhost:5174`, beside the gateway's 5173, so both stacks can run side by side.
Container ports are fixed too: Open WebUI `http://localhost:7410`, Keycloak `https://localhost:7411`, Postgres `7412`,
parser `7413`, ClamAV `7414`, Redis `7415` (when `Redis:Enabled`), renderer `7417` (WebSocket, the path is secret).

## Keycloak test realm

Without `Oidc:Authority` the AppHost starts Keycloak and imports the realm `fors` from `dev/keycloak/` (stream B,
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

Clients: `kalla-admin` (the admin BFF, code + PKCE), `kalla-api-dev` (client credentials, roles
`Knowledge.Read.All` and `Chat.Invoke.All`), `kalla-chat-dev` (public, PKCE, loopback redirect, scopes `Chat.Use`,
`Knowledge.Read`, `Mcp.Use`) and `open-webui-dev`. Their secrets are AppHost parameters as well.

## Fake gateway

Every model call goes through the gateway; locally that is `Kalla.FakeGateway`
([`repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §4.3). It serves 1024-dimension embeddings,
chat completions built from the prompt (streamed or not), `/v1/models`, `/v1/rerank` and `/v1/fors/context` (the
organisation and the assigned keys, which the admin's syncs read). Like the real gateway it requires `x-fors-max-residency` and answers
with `x-fors-residency`. Each service has its own dev credential key, as the gateway integration *Kalla* gives
(production-setup.md, *Gateway credential keys*): the service gets it as `Gateway__Key`, and the fake accepts api's and
admin's at every ceiling and the worker's up to `Eu`, narrowing a higher ceiling to the key's residencies as the gateway
does. Only the admin's holds the scope `organisation.read`.

Its models are in [`src/Kalla.FakeGateway/appsettings.json`](../src/Kalla.FakeGateway/appsettings.json), including
fault models for tests: `fake-fail-500` (502 `all_providers_failed`, as the gateway answers when every target within
the ceiling fails; there is no circuit-open 503), `fake-fail-timeout` (504 `all_providers_failed`),
`fake-fail-fallback` (its `OnPrem` target fails: 502 at `OnPrem`, an answer from `Eu` at `Eu`), `fake-slow`,
`fake-wrong-residency`, `fake-wrong-residency-embed`, `fake-drained` (503 `no_eligible_provider` + `Retry-After`) and
the stream faults `fake-stream-broken`, `fake-stream-stall`, `fake-stream-no-done`, `fake-stream-empty-deltas` and
`fake-stream-length`. `fake-no-cost` answers without `x-fors-cost-usd` and without the stream cost comment, as a
gateway that can't price the call (the usage is then recorded at Källa's estimate).

Switches:
- `FakeGateway:RerankEnabled` (default `false`): rerank is optional, so by default `/v1/rerank` answers 404
  `model_not_found` like a gateway without a rerank model. `true` serves `bge-reranker-v2-m3` as an `Eu` target (like
  Berget AI).
- `FakeGateway:RequireMaxResidency` (default `true`): `false` lets a request without `x-fors-max-residency` run with
  no ceiling.
- `FakeGateway:StrictKeyLevel` (default `true`): with one of the fake's per-level keys (`FakeGateway:Keys:<level>`,
  which the AppHost does not set), a ceiling other than the key's level answers 403 `key_level_mismatch` (fake-only).
  It does not apply to the credential keys.
- `FakeGateway:CachedInputTokens` (default `0`): the prompt tokens every chat answer reports as cached
  (`usage.prompt_tokens_details.cached_tokens`, at most the prompt), priced at the cached rate. Tests set it to check the
  cached-input price (chat design §8.3).

Every gateway client registered with `AddGatewayClient` sends a 502 or 504 once more with the same request (same
ceiling, key and body; `GatewayRetryHandler`, `Gateway:RetryDelay` 250 ms) before any job retry. 403, 503 and 404
are not retried.

By default it answers at once. `FakeGateway:Latency:*` in the AppHost user secrets gives it a real gateway's timing
(all 0 by default): `FirstTokenMs`, `PrefillMsPer1kInputTokens`, `TokenIntervalMs`, `JitterPercent`, `AnswerWords`,
`StallProbability`, `StallMs`.

**Against a real local gateway** (its AppHost running): set `FakeGateway:Enabled=false`,
`Gateway:BaseUrl=https://localhost:<gateway port>` and each service's credential key of a local integration *Kalla*
in the AppHost user secrets, made as production-setup.md, *Gateway credential keys*, says:
`Gateway:Services:<service>:Key` (service `api`, `admin` or `worker`), which the service gets as `Gateway:Key`. The
AppHost stops on an unknown service, and on the per-level keys of before B-37 (`Gateway:Keys:<level>`): remove them.
Every other `Gateway:*` setting (`BaseUrl`, ...) goes to all three, and `OrgSync:IntervalMinutes` to the admin API,
whose organisation and model syncs read the gateway with its credential key (scope `organisation.read`). The AppHost
stops on `OrgSync:BaseUrl` and `OrgSync:DataApiKey`, the Data API settings of before: remove them. The gateway's
FakeLlm needs gateway release 1 for 1024-dimension embeddings (`fake-embed-1024`); with an older one, use its Ollama
provider with `bge-m3`. Every credential key must have PII policy `Off` or `Allow` for embeddings, never `Redact` or
`Block`.

## Build and test

```bash
dotnet build Kalla.slnx -c Release --nologo
dotnet test --solution Kalla.slnx -c Release --no-build --filter-not-trait "Category=Flaky"
```

`--filter-not-trait` skips the tests marked `[Trait(TestCategory.Name, TestCategory.Flaky)]`, as CI does; plain
`dotnet test --solution Kalla.slnx` runs them too. A flaky mark is a to-do: say why next to the trait and fix the test.

CI runs the same tests with `scripts/ci/backend-tests.sh` (after `dotnet build Kalla.slnx -c Release`): every test
project as a process of its own and `Kalla.AdminApi.Tests` as five, which takes about half the time of
`dotnet test --solution`. It works locally too and prints each process's totals; a failed process's output follows.

The integration tests start Postgres with Testcontainers, so Docker must be running. By default they use the same
pinned `pgvector/pgvector` image as the AppHost (`TestPostgres.DefaultImage`). To run them against the production
image, build it and name it in `KALLA_TEST_POSTGRES_IMAGE`:

```bash
docker build -f images/postgres/Dockerfile -t kalla.local/postgres:dev .
KALLA_TEST_POSTGRES_IMAGE=kalla.local/postgres:dev dotnet test --solution Kalla.slnx -c Release
```

**Name local images with a registry part that has a dot** (`kalla.local/...`). When
`TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX` is set (CI sets `mirror.gcr.io/` to avoid Docker Hub's pull limit),
Testcontainers puts the prefix in front of every image without a registry, so `kalla/postgres:dev` would be pulled as
`mirror.gcr.io/kalla/postgres:dev` and not found.

The contract tests (`tests/Kalla.ContractTests`) run the worker's parser client against a parser image. Without
`KALLA_TEST_PARSER_IMAGE` the tests that need the image skip:

```bash
docker build -t kalla.local/parser:ci src/parser
KALLA_TEST_PARSER_IMAGE=kalla.local/parser:ci dotnet test --project tests/Kalla.ContractTests
```

**Image tests.** Each image has a test script that runs it hardened, as Compose does:

```bash
docker build -f images/postgres/Dockerfile -t kalla/postgres:latest .
bash deploy/postgres/test.sh kalla/postgres:latest      # pgvector, HNSW, TLS, archive-push, backup and restore
docker build -t kalla/clamav:dev images/clamav
bash images/clamav/test.sh kalla/clamav:dev             # offline: a one-line EICAR signature
bash images/clamav/test.sh --online kalla/clamav:dev    # downloads the real signatures (about 115 MB)
docker build -t kalla/renderer:dev images/renderer
bash images/renderer/test.sh kalla/renderer:dev         # sandbox, a rendered page, nothing outside its network
```

The renderer test uses the subnets `10.214.0.0/24` and `10.214.1.0/24`; set `RENDER_TEST_SUBNET` and
`RENDER_TEST_OUTSIDE_SUBNET` where they are taken.

**Browser tests.** `Start-E2E.ps1` starts the stack on its own Postgres volume (`kalla-e2e-postgres`), reset on every
start, with demo data and the fake gateway forced on. It uses the dev stack's ports, so stop the dev stack first. The
Playwright tests are in `tests/e2e` ([its README](../tests/e2e/README.md)).

```powershell
.\scripts\Start-E2E.ps1          # reset the test volume, start
.\scripts\Start-E2E.ps1 -Keep    # start without resetting it
.\scripts\Start-E2E.ps1 -Setup   # the setup wizard's stack instead (below)
.\scripts\Start-E2E.ps1 -Stop    # stop the stack and remove both test volumes
```

The setup wizard's spec (`tests/e2e/specs/setup.spec.ts`) needs `Setup:Try` on a fresh database, so `-Setup` starts
that stack on a volume of its own (`kalla-e2e-setup-postgres`, reset on every start, without parser, renderer, ClamAV
and Open WebUI). Run it with `npx playwright test --project=setup` in `tests/e2e`, and the other specs with
`--project=pilot --project=accessibility` on the normal stack. Finishing closes setup, so start `-Setup` again before
another run. Without pwsh, set the script's variables by hand (`DevelopmentVolumes__Postgres=kalla-e2e-setup-postgres
Seed__Enabled=true FakeGateway__Enabled=true Setup__Try=true Parser__Mode=off Render__Mode=off ClamAv__Mode=off
OpenWebUi__Enabled=false`), and set `ASPIRE_CLI` when `aspire` is not on the path: the spec reads the setup code with it.

## Parser

The parser is a Python service in `src/parser` with its own README: [`src/parser/README.md`](../src/parser/README.md)
(uv, ruff, pytest, the image's test target, the contract and its goldens, updating pins). In short:

```bash
cd src/parser
uv sync --frozen
uv run ruff check . && uv run ruff format --check .
uv run pytest
docker build --target test -t kalla/parser:test .
docker run --rm --network none kalla/parser:test     # the tests that need Docling, the models and Tesseract
```

The image is about 3 GB. After changing `ume_parser/models.py`, run `uv run python -m scripts.export_schema` and
commit `contracts/parser/v1/`; CI fails when they differ.

## Production Compose on your machine

To try the production files locally, build the images, then run `init-deployment.sh` in a scratch copy of `deploy/`
(not in the clone), with `KALLA_STATE_DIR` naming that copy: on a host the env file goes to `/etc/kalla/.env`, here
next to the scripts. The certificates and secrets go into the project's Docker
volumes (`kalla-bootstrap` creates them at the first `up`). The image defaults in `compose.rag.yaml` are the released
pins, so name the local builds with `--set`:

```bash
docker build -f images/postgres/Dockerfile -t kalla/postgres:latest .
docker build -t kalla/clamav:latest images/clamav
docker build -t kalla/parser:latest src/parser
docker build -t kalla/renderer:latest images/renderer
for p in Api AdminApi Worker MigrationService; do dotnet publish src/Kalla.$p -c Release -t:PublishContainer; done
mkdir -p ~/rag-deploy-test && cp -r deploy ~/rag-deploy-test/
images=()
for s in postgres migrations api admin worker parser clamav renderer; do images+=(--set "KALLA_${s^^}_IMAGE=kalla/$s:latest"); done
KALLA_STATE_DIR=~/rag-deploy-test/deploy ~/rag-deploy-test/deploy/init-deployment.sh --api-host localhost --gateway-url https://<a gateway> \
  --backup-target local "${images[@]}" --yes
docker compose --env-file ~/rag-deploy-test/deploy/.env up -d --wait
docker compose --env-file ~/rag-deploy-test/deploy/.env logs kalla-admin | grep 'Setup code'
```

Sign-in is then set in the setup wizard at `https://localhost:9543/setup`, with the setup code from kalla-admin's log. A
gateway whose certificate comes from its own CA needs `--gateway-ca-file` (its CA certificate, `ca.pem`). With
`--hardened` (the host firewall's subnets, the ingestion slice, the renderer's seccomp profile) the copy also needs
`images/renderer/seccomp.json` at `~/rag-deploy-test/images/renderer/` (the layout of a release bundle) and, without
the slice installed, the line `KALLA_INGESTION_SLICE=` in `deploy/.env` (`--set KALLA_INGESTION_SLICE=`; the script keeps this one
empty value on a re-run). The .NET images are tagged `kalla/<service>:latest` (`ContainerImageTag` in
`Directory.Build.props`). See [production-setup.md](production-setup.md) for what `kalla-bootstrap` generates, and
[p1a-local-run.md](p1a-local-run.md) for the local P1a run (now on the dev Keycloak and FakeGateway, then the live
block's shared Keycloak and gateway).

## Working notes

Things the code will not tell you, collected from earlier sessions.

**Admin API from a script.** Sign in through the BFF as a browser does: `GET /bff/login` → the Keycloak form
(`#username`, `#password`, `#kc-login`; the dev user's password is the AppHost user secret
`Parameters:dev-user-password`, in a `secrets.json` that starts with a BOM) → the `form_post` page (its `FORM` and
`INPUT` attributes are upper case: match case-insensitively) → `GET /bff/user` to confirm. Writes need
`X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN` with the value of the `RAG-XSRF-TOKEN` cookie. The OpenAPI
document is `/openapi/v1.json` through the BFF. An assistant draft created by API needs `collectionLinks`, or it
searches nothing. The admin's test endpoint is rate limited (20 calls a minute, then 429): pace latency samples at
about one per 10 s.

**Browser checks outside `tests/e2e`.** Use `playwright-core` (the version in `tests/e2e/package.json`) in a scratch
folder, with `executablePath` set to the Chromium that `npx playwright install chromium` put under
`~/.cache/ms-playwright/`, and `ignoreHTTPSErrors`. The admin UI's drawers are `dialog[open]`. Wait for the data to
show, not only for `networkidle`.

**Worktrees.** Running the AppHost from a worktree makes it `npm install` into `src/admin-ui/node_modules`, replacing a
symlink with a real directory (git-ignored, but `git worktree remove` then needs `--force`). Never `git add -A` in a
worktree. Admin UI checks in a worktree: symlink `node_modules` from the main clone, run `npx vitest run
--pool=threads`, `npx eslint --max-warnings 0 …` and `npx vue-tsc -p tsconfig.app.json --noEmit --composite false
--incremental false`, then remove the symlink.

**Tests.** Read the first real failure, and don't run two `Worker.Tests` runs at once. A test that listens to a
process-wide `ActivitySource` or meter must filter to its own scope (#431), or another test's activity leaks in.

**Settings that surprise.**

- `Chat:*Timeout` knobs are TimeSpans (`00:00:03`); a bare `3` means three days.
- Some knobs live in two services: `Render:MaxConcurrent` is the worker's render cap *and* the admin API's ceiling for
  `renderPerHost`, so a local test value needs both user secrets (`kalla-worker`, `kalla-adminapi`).
- kalla-api writes snake_case JSON on every route (its OpenAI routes set it globally), including `/version` (#497).
- AppHost containers cannot have a read-only root: DCP copies files into a container before it starts. The production
  Compose files keep it read-only.

**A real gateway instead of the fake** (the interim gateway, whose address is never in the repository): Claude models
refuse `temperature`; Python's default `User-Agent` gets 403; embeddings answer 429 under load (the worker requeues);
its front proxy answers an intermittent `error code: 502`, so chat-model rerank runs carry fallbacks.

**Changes that need a second step.**

- **A PR that changes `deploy/postgres/*.sql`, `deploy/portainer/bootstrap.sh` or the Portainer template** must also
  regenerate `deploy/portainer/compose.portainer.yaml` (`sh deploy/portainer/build-stack.sh current`: the gateway
  release the file already names), or the Portainer stack lacks the change (#566 caught B-30's grants missing).
  `verify.yml`'s deploy-checks fail on a stale file (`build-stack.sh --check`).
- **xunit v3 test output** (`TestOutputHelper.WriteLine`) shows only in a report: `--report-xunit-trx --results-directory
  DIR`, then the TRX's `<Message>` elements.
- **Two open PRs with an EF migration each:** the second to merge regenerates its own. Keep a copy of the migration's
  `.cs`, remove it and its designer, restore `RagDbContextModelSnapshot.cs` from `main`, `dotnet ef migrations add
  <Name> --project src/Kalla.Infrastructure --startup-project src/Kalla.Infrastructure --output-dir
  Persistence/Migrations`, compare the new `.cs` with the copy (it should differ only by the header and hand-written
  SQL) and put the copy back under the new timestamp. The file needs `// <auto-generated />` as its first line or the
  build fails on style rules. A new worktree needs `dotnet restore` first. Tell the other stream on its PR.
- **SQL in `src/Kalla.Infrastructure/` is in the API's SQL contract** (`tests/Kalla.Api.Tests/sql-shapes.json`),
  not only the worker's: an agent that adds a statement there must also run `tests/Kalla.Api.Tests` (regenerate with
  `KALLA_UPDATE_CONTRACTS=1`). A-39's agent ran only the worker tests and CI failed.

**Shell.** `pkill -f <pattern>` kills its own shell when the pattern appears in its command line: use
`pgrep -f '<pat>[.]sh' | xargs -r kill`, or kill by PID.

## Common problems

| Problem | Cause and fix |
|---|---|
| On WSL, the first `aspire` command asks to trust the development certificate in the Windows certificate store | Trust it once with `dotnet dev-certs https --trust`; the dashboard and the services' HTTPS endpoints use that certificate |
| `docker compose up` of the production files fails with "Pool overlaps with other one on this network" | With `compose.hardened.yaml` the Compose networks use fixed subnets in `172.30.0.0/16`, and another Docker network on your machine already uses one. List them with `docker network inspect $(docker network ls -q) --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'`; remove the network that collides, or keep `172.30.0.0/16` out of Docker's `default-address-pools` |
| Integration tests pull `mirror.gcr.io/kalla/...` and fail | A local image without a registry part while `TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX` is set: name it `kalla.local/...` |
| The parser stays unhealthy for minutes after a start | It warms up its slots (Docling, the models) and runs a self-test first; set `Parser:Mode=off` when you do not need it |
| ClamAV is slow to become healthy on the first start | freshclam downloads the signatures into the `kalla-clamav-db` volume first; later starts reuse them. `ClamAv:Mode=off` skips it |
| Postgres authentication fails after you cleared the user secrets | The volume keeps the old password: restore `Parameters:postgres-password`, or delete the volume (and its data) |
| `kalla-e2e-postgres` cannot be removed | A stack still uses it: `.\scripts\Start-E2E.ps1 -Stop`, or stop the AppHost first |
