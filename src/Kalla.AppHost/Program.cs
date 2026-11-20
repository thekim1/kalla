using Microsoft.Extensions.Configuration;
using Kalla.ServiceDefaults;

#pragma warning disable ASPIRECERTIFICATES001

// Resource graph of plans/platform/repo-and-deployment.md §4.1. Stage 0 registers every P0 resource; the services are
// skeletons (health and /version only) that the streams fill in.

// Connection-string name the services look up.
const string DatabaseResourceName = "ragdb";

// The stock pgvector image ume-rag/postgres is built from (§5.2; pgBackRest is not needed in dev). Same pin as
// Kalla.TestKit.TestPostgres.Image; update both together.
const string PostgresImage = "pgvector/pgvector";
const string PostgresTag = "0.8.7-pg17-trixie";
const string PostgresDigest = "89947206f52eac3a9df9b663df7e76987cbdc623be6576b3a524953d568700d7";

// The shared Keycloak's version (the gateway's deploy/keycloak/Dockerfile, identity design §11.2), pinned by digest.
// Same pin as Kalla.AdminApi.Tests.KeycloakFixture.Image; update both together.
const string KeycloakTag = "26.8.1-1";
const string KeycloakDigest = "a78d6b79572168cb25886b80a8b5b8d4ed4390743e3eecb810538683e209dd6b";
const string FixtureSiteImage = "nginxinc/nginx-unprivileged";
const string FixtureSiteTag = "1.31.6-alpine";
const string FixtureSiteDigest = "884e0b7f42f303641b0f4e78755aea647521dca0b89ca3be7992ad78b5c07434";

var builder = DistributedApplication.CreateBuilder(args);
// E2E:Enabled=true (scripts/Start-E2E.ps1 only): the browser tests' stack. It adds their fixture website and ignores the
// user secrets that point at real systems (a configured Open WebUI client), so a local run
// starts from the same state as CI.
var e2e = builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue<bool>("E2E:Enabled");
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);

var pepper = Secret("key-pepper", 64);
var devKey = Secret("dev-key", 43);
var oidcSecret = Secret("oidc-client-secret", 48);
var devUserPassword = Secret("dev-user-password", 32);
var apiClientSecret = Secret("api-client-secret", 48);
var openWebUiClientSecret = Secret("openwebui-client-secret", 48);
// The knowledge role's query-embedding cache key and the download-grant signing key (knowledge design §4.6, §5.4).
var cacheKeyPepper = Secret("cache-key-pepper", 48);
var downloadGrantKey = Secret("download-grant-key", 48);

// The image is Postgres 17 (PGDATA /var/lib/postgresql/data); Aspire 13's WithDataVolume mounts at /var/lib/postgresql,
// the Postgres 18 layout, so the data went to the image's anonymous volume and was lost on restart (#168). The volume is
// mounted at the image's data path and the cluster lives in pgdata/ below it: a volume from the old layout holds an
// empty data/ directory, which would make initdb refuse a non-empty PGDATA.
// Fixed host ports for the containers (7410–7417, beside the services' 7401–7403 and the admin UI's 5174), so local
// URLs, psql and scripts stay the same across restarts.
var postgres = builder.AddPostgres("postgres", port: 7412)
    .WithImage(PostgresImage, PostgresTag).WithImageSHA256(PostgresDigest)
    .WithEnvironment("PGDATA", "/var/lib/postgresql/data/pgdata");
postgres.WithVolume(
    (builder.ExecutionContext.IsRunMode ? builder.Configuration["DevelopmentVolumes:Postgres"] : null) ?? VolumeNameGenerator.Generate(postgres, "data"),
    "/var/lib/postgresql/data");
var database = postgres.AddDatabase(DatabaseResourceName);

// Redis:Enabled=true is the P2 path (first multi-host deployment). Off by default: the pilot shape keeps rate limits and
// the query-embedding cache in process (simplification pass §14).
var redis = builder.Configuration.GetValue<bool>("Redis:Enabled")
    ? builder.AddRedis("redis", 7415, Secret("redis-password", 48))
    : null;

// Oidc:Authority (AppHost user secrets) points local runs at an existing identity provider instead of the bundled test
// Keycloak, which is then not started. Its realm is dev/keycloak/ume-realm.json (identity design §11).
var externalOidc = !string.IsNullOrWhiteSpace(builder.Configuration["Oidc:Authority"]);
var keycloak = builder.ExecutionContext.IsRunMode && !externalOidc ? builder.AddKeycloak("keycloak", 7411)
    .WithImageTag(KeycloakTag).WithImageSHA256(KeycloakDigest)
    .WithRealmImport(Path.Combine("..", "..", "dev", "keycloak"))
    .WithEnvironment("UME_RAG_OIDC_CLIENT_SECRET", oidcSecret)
    .WithEnvironment("UME_RAG_DEV_USER_PASSWORD", devUserPassword)
    .WithEnvironment("UME_RAG_API_CLIENT_SECRET", apiClientSecret)
    .WithEnvironment("UME_RAG_OWUI_CLIENT_SECRET", openWebUiClientSecret)
    .WithHttpsDeveloperCertificate() : null;

// FakeGateway:Enabled=false (AppHost user secrets) runs against a real local gateway instead (§4.4).
var fakeGateway = builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("FakeGateway:Enabled", true)
    ? builder.AddProject<Projects.Kalla_FakeGateway>("fake-gateway", o => o.ExcludeLaunchProfile = true)
        .WithHttpsEndpoint()
        .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https")
    : null;

// One dev credential key per service, like the gateway integration Källa's (identity design §4.7): the fake accepts
// each up to its highest residency (the worker's Eu), the service holds it as Gateway:Key.
string[] gatewayServices = ["api", "admin", "worker"];
var gatewayKeys = gatewayServices.ToDictionary(service => service, service => Secret($"gateway-key-{service}", 43), StringComparer.Ordinal);

// FakeGateway:Latency:* (AppHost user secrets) gives the fake a real gateway's timing, as the gateway's FakeLlm.
if (fakeGateway is not null)
{
    foreach (var (service, key) in gatewayKeys)
    {
        fakeGateway.WithEnvironment($"FakeGateway__CredentialKeys__{service}__Key", key)
            .WithEnvironment($"FakeGateway__CredentialKeys__{service}__MaxResidency", service == "worker" ? "Eu" : "External");
        if (service == "admin")
        {
            // rag-admin's key reads the whole organisation in the context (identity design §2.1, gateway-changes §5.5).
            fakeGateway.WithEnvironment("FakeGateway__CredentialKeys__admin__Scopes__0", "organisation.read");
            // ... and only it reads the Integration API (gateway phase 3, gateway-changes §5.6; B-45).
            fakeGateway.WithEnvironment("FakeGateway__CredentialKeys__admin__Scopes__1", "usage.read");
        }
    }

    foreach (var setting in builder.Configuration.GetSection("FakeGateway:Latency").GetChildren())
    {
        fakeGateway.WithEnvironment($"FakeGateway__Latency__{setting.Key}", setting.Value);
    }

    // The browser tests' pilot journey publishes a public assistant of Kommunledningskontoret over a knowledge base on
    // bge-m3 (OnPrem): V39 needs an assigned key of that department reaching both its chat model (Eu) and the embedding
    // model (admin §4.6). The development catalogue's keys don't (Kontaktcenter's is Eu only, #720), so the E2E stack adds one.
    if (e2e)
    {
        fakeGateway.WithEnvironment("FakeGateway__AssignedKeys__5__Id", "019a0000-0000-7000-8000-00000000b0e2")
            .WithEnvironment("FakeGateway__AssignedKeys__5__Name", "Digitalisering – pilot")
            .WithEnvironment("FakeGateway__AssignedKeys__5__TeamId", "019a0000-0000-7000-8000-000000001001")
            .WithEnvironment("FakeGateway__AssignedKeys__5__AllowedResidencies__0", "OnPrem")
            .WithEnvironment("FakeGateway__AssignedKeys__5__AllowedResidencies__1", "Eu");
    }
}

// Ollama:Enabled=true: real BGE-M3 embeddings behind the fake gateway (dev only).
IResourceBuilder<OllamaModelResource>? bgeM3 = null;
if (builder.Configuration.GetValue<bool>("Ollama:Enabled"))
{
    if (!builder.ExecutionContext.IsRunMode)
    {
        throw new InvalidOperationException("Ollama:Enabled is development-only. Production embeds through the gateway's on-prem BGE-M3.");
    }

    var ollama = builder.AddOllama("ollama")
        .WithDataVolume(builder.Configuration["DevelopmentVolumes:Ollama"]);
    bgeM3 = ollama.AddModel("bge-m3", "bge-m3");
    fakeGateway?.WithEnvironment("FakeGateway__Upstream__Embeddings", ReferenceExpression.Create($"{ollama.GetEndpoint("http")}/v1"))
        .WaitFor(bgeM3);
}

// Parser:Mode = container (default) | local (uvicorn with hot reload) | off. Internal endpoint only.
const int ParserPort = 8000;
var parserMode = builder.Configuration["Parser:Mode"] ?? "container";
EndpointReference? parserEndpoint = null;
switch (parserMode)
{
    case "container":
        parserEndpoint = builder.AddDockerfile("parser", Path.Combine("..", "parser"))
            .WithHttpEndpoint(port: 7413, targetPort: ParserPort, name: "http")
            .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "http")
            .GetEndpoint("http");
        break;
    case "local":
        parserEndpoint = builder.AddUvicornApp("parser", Path.Combine("..", "parser"), "ume_parser.app:app")
            .WithHttpHealthCheck(HealthEndpoints.ReadyPath)
            .GetEndpoint("http");
        break;
    case "off":
        break;
    default:
        throw new InvalidOperationException($"Parser:Mode must be container, local or off, not '{parserMode}'.");
}

// ClamAv:Mode = on (default) | off (Decisions "Malware scan optional"; connectors design §11). Off: no clamav resource,
// and the worker gets ClamAv__Mode=off and stores files without a scan. Dev freshclam uses the public mirror (§5.5).
if (builder.Configuration["ClamAv:Enabled"] is not null)
{
    throw new InvalidOperationException("ClamAv:Enabled is gone: set ClamAv:Mode to on or off.");
}

var clamAvMode = builder.Configuration["ClamAv:Mode"] ?? "on";
if (clamAvMode is not ("on" or "off"))
{
    throw new InvalidOperationException($"ClamAv:Mode must be on or off, not '{clamAvMode}'.");
}

var clamAv = clamAvMode == "on"
    ? builder.AddDockerfile("clamav", Path.Combine("..", "..", "images", "clamav"))
        .WithVolume("ume-rag-clamav-db", "/var/lib/clamav")
        .WithEndpoint(port: 7414, targetPort: 3310, name: "clamd", scheme: "tcp")
    : null;

// Render:Mode = container (default) | off: the renderer for `browser` sources (images/renderer, repo-and-deployment §5.4;
// connectors design §8.3), hardened as in Compose, with Chromium's sandbox and the seccomp profile it needs. Off: no
// Render:Endpoint, so `browser` sources wait with render.not_configured. Unlike production it sits on Aspire's network
// with a published port, because the worker runs on the host: what the worker's route doesn't see (a popup's first
// navigation, a download) could leave the container here, never in production.
const int RendererPort = 3000;
var renderMode = builder.Configuration["Render:Mode"] ?? "container";
ReferenceExpression? renderEndpoint = null;
switch (renderMode)
{
    case "container":
        // The secret WebSocket path (UME_RAG_RENDER_WS_PATH: "/" and at least 16 of [A-Za-z0-9_-]).
        var renderPath = Secret("render-ws-path", 32);
        var seccompProfile = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "images", "renderer", "seccomp.json"));
        var wsEndpoint = builder.AddDockerfile("renderer", Path.Combine("..", "..", "images", "renderer"))
            .WithEndpoint(port: 7417, targetPort: RendererPort, name: "ws", scheme: "tcp")
            .WithEnvironment("UME_RAG_RENDER_WS_PATH", ReferenceExpression.Create($"/{renderPath}"))
            // No --read-only here: DCP copies files into a container before it starts, which a read-only root refuses
            // ("container rootfs is marked read-only"). Compose keeps the read-only root (repo-and-deployment §5.4).
            .WithContainerRuntimeArgs(
                "--tmpfs", "/tmp:size=512m", "--cap-drop", "ALL", "--init",
                "--security-opt", "no-new-privileges", "--security-opt", $"seccomp={seccompProfile}",
                "--memory", "2g", "--cpus", "2", "--pids-limit", "512")
            .GetEndpoint("ws");
        renderEndpoint = ReferenceExpression.Create(
            $"ws://{wsEndpoint.Property(EndpointProperty.Host)}:{wsEndpoint.Property(EndpointProperty.Port)}/{renderPath}");
        break;
    case "off":
        break;
    default:
        throw new InvalidOperationException($"Render:Mode must be container or off, not '{renderMode}'.");
}

var migrations = builder.AddProject<Projects.Kalla_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Seed__Enabled", builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("Seed:Enabled", true) ? "true" : "false")
    .WaitFor(database);

if (builder.ExecutionContext.IsRunMode)
{
    migrations.WithEnvironment("Seed__DevKey", devKey);
}

if (fakeGateway is not null)
{
    migrations.WithEnvironment("Seed__FakeGatewayUrl", ReferenceExpression.Create($"{fakeGateway.GetEndpoint("https")}/v1"))
        .WaitFor(fakeGateway);
}

var api = builder.AddProject<Projects.Kalla_Api>("api", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Rag__Roles", "knowledge,chat,mcp")
    .WithEnvironment("Security__CacheKeyPepper", cacheKeyPepper)
    .WithEnvironment("Security__DownloadGrantKeys__dev", downloadGrantKey)
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https")
    .WithExternalHttpEndpoints();
// Citation download links are absolute (knowledge design §4.6): Open WebUI shows them on its own origin, where a relative
// /v1/documents/... link would point at Open WebUI instead of rag-api.
api.WithEnvironment("Knowledge__PublicBaseUrl", api.GetEndpoint("https"));

// ConnectionStrings:ragdb-maintenance: the HNSW rebuild's own unpooled session (Decisions "M-2 open design calls"). Same
// server and database here; it stays a string of its own so a later pooler in front of ragdb never carries the rebuild.
var worker = builder.AddProject<Projects.Kalla_Worker>("worker", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WithEnvironment("ConnectionStrings__ragdb-maintenance", database)
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https");

if (parserEndpoint is not null)
{
    worker.WithEnvironment("Parser__BaseUrl", parserEndpoint);
}

worker.WithEnvironment("ClamAv__Mode", clamAvMode);
if (clamAv is not null)
{
    worker.WithEnvironment("ClamAv__Host", clamAv.GetEndpoint("clamd").Property(EndpointProperty.Host))
        .WithEnvironment("ClamAv__Port", clamAv.GetEndpoint("clamd").Property(EndpointProperty.Port));
}

if (renderEndpoint is not null)
{
    worker.WithEnvironment("Render__Endpoint", renderEndpoint);
}

// The browser tests' static Swedish website (tests/e2e/fixture-site, admin design §11.4) on 127.0.0.1:7416. The worker may
// then reach loopback, and the first Settings row allows the port (the worker reads Settings.Crawl at start).
if (e2e)
{
    builder.AddContainer("fixture-site", FixtureSiteImage, FixtureSiteTag).WithImageSHA256(FixtureSiteDigest)
        .WithBindMount(Path.Combine("..", "..", "tests", "e2e", "fixture-site"), "/usr/share/nginx/html", isReadOnly: true)
        .WithHttpEndpoint(port: 7416, targetPort: 8080, name: "http");
    worker.WithEnvironment("Crawl__AllowedPrivateRanges", "127.0.0.1/32,::1/128");
    migrations.WithEnvironment("SettingsSeed__CrawlAllowedPorts__0", "7416");
}

var adminApi = builder.AddProject<Projects.Kalla_AdminApi>("adminapi", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Oidc__ClientId", "ume-rag-admin")
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https");

// The blob master key (data-model §7): KeyProvider:Directory from the AppHost's configuration, else a development key the
// AppHost creates once under ~/.ume-rag/keys (32 random bytes, base64, as init-deployment.sh writes it). The worker writes
// internal blobs with it, the admin writes uploads, and rag-api reads them for citation downloads (#158).
var keyDirectory = builder.Configuration["KeyProvider:Directory"];
if (string.IsNullOrWhiteSpace(keyDirectory) && builder.ExecutionContext.IsRunMode)
{
    keyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ume-rag", "keys");
    var keyFile = Path.Combine(keyDirectory, "rag-blob-internal");
    if (!File.Exists(keyFile))
    {
        Directory.CreateDirectory(keyDirectory);
        File.WriteAllText(keyFile, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

if (!string.IsNullOrWhiteSpace(keyDirectory))
{
    foreach (var service in new[] { api, worker, adminApi })
    {
        service.WithEnvironment("KeyProvider__Directory", keyDirectory);
    }
}

// The blob root (#168): BlobStore:Root from the AppHost's configuration, else ~/.ume-rag/blobs/rag in run mode, next to
// the dev keys. The services' default /data/blobs/rag is the container path and is not writable on a developer machine.
var blobRoot = builder.Configuration["BlobStore:Root"];
if (string.IsNullOrWhiteSpace(blobRoot) && builder.ExecutionContext.IsRunMode)
{
    blobRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ume-rag", "blobs", "rag");
    // Owner-only, like the keys directory: public blobs are stored as plaintext.
    if (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(blobRoot);
    }
    else
    {
        Directory.CreateDirectory(blobRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

if (!string.IsNullOrWhiteSpace(blobRoot))
{
    // The migration service writes the demo content's upload blobs (public: plaintext, no key).
    foreach (var service in new[] { api, worker, adminApi, migrations })
    {
        service.WithEnvironment("BlobStore__Root", blobRoot);
    }
}

if (redis is not null)
{
    foreach (var service in new[] { api, worker, adminApi })
    {
        service.WithReference(redis).WaitFor(redis);
    }
}

// Every model call goes through the gateway: locally the fake one (AGENTS.md).
if (fakeGateway is not null)
{
    // The admin's organisation and model syncs read /v1/ume/context and /v1/models with its credential key (admin §4.10).
    foreach (var (name, service) in new[] { ("api", api), ("worker", worker), ("admin", adminApi) })
    {
        service.WithEnvironment("Gateway__BaseUrl", fakeGateway.GetEndpoint("https")).WaitFor(fakeGateway)
            .WithEnvironment("Gateway__Key", gatewayKeys[name]);
    }

    // B-45 (gateway-changes §5.6): the fake serves the Integration API on its own address and names it in its context
    // (integration_api_url, M-17 C11), read by the admin only.
}
else
{
    // A real gateway: each service's credential key of the integration Källa (identity design §4.7) as
    //   Gateway:Services:<service>:Key   (service api, admin or worker);
    //   every other Gateway:* value (BaseUrl, ...) goes to all three.
    var services = new Dictionary<string, IResourceBuilder<ProjectResource>>(StringComparer.Ordinal)
    {
        ["api"] = api, ["admin"] = adminApi, ["worker"] = worker,
    };
    var gateway = builder.Configuration.GetSection("Gateway");
    if (gateway.GetSection("Keys").Exists())
    {
        throw new InvalidOperationException("Gateway:Keys:<level> is gone: give each service its credential key as Gateway:Services:<service>:Key.");
    }

    foreach (var own in gateway.GetSection("Services").GetChildren())
    {
        if (!services.TryGetValue(own.Key, out var service))
        {
            throw new InvalidOperationException($"Gateway:Services:{own.Key}: the service is api, admin or worker.");
        }

        if (own["Key"] is { Length: > 0 } key)
        {
            service.WithEnvironment("Gateway__Key", key);
        }
    }

    foreach (var service in services.Values)
    {
        foreach (var (key, value) in gateway.AsEnumerable(makePathsRelative: true))
        {
            if (value is not null && !key.StartsWith("Services:", StringComparison.Ordinal))
            {
                service.WithEnvironment("Gateway__" + key.Replace(":", "__", StringComparison.Ordinal), value);
            }
        }
    }

    // The admin's organisation and model syncs (admin design §4.10) call the gateway with Gateway:Services:admin:Key, which
    // needs the scope organisation.read; OrgSync:IntervalMinutes is passed on. There is no Data API key any more.
    if (builder.Configuration["OrgSync:BaseUrl"] is not null || builder.Configuration["OrgSync:DataApiKey"] is not null)
    {
        throw new InvalidOperationException(
            "OrgSync:BaseUrl and OrgSync:DataApiKey are gone: the admin syncs from the gateway with Gateway:Services:admin:Key (scope organisation.read).");
    }

    foreach (var (key, value) in builder.Configuration.GetSection("OrgSync").AsEnumerable(makePathsRelative: true))
    {
        if (value is not null)
        {
            adminApi.WithEnvironment("OrgSync__" + key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
}

// Setup:Try=true (AppHost user secrets) starts rag-admin without a sign-in authority and client secret and with a setup
// secret, so the setup wizard opens (identity design §19); Setup:Reopen is passed on for a database whose setup is done.
var setupTry = builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue<bool>("Setup:Try");

// The demo content (repo-and-deployment §4.4): its department is the fake catalog's, so only with the FakeGateway, and not
// on the setup wizard's stack, which stands for a first install (#877 review).
if (fakeGateway is not null && !setupTry)
{
    migrations.WithEnvironment("Seed__DemoDirectory", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "tests", "evaluation", "smoke")));
}
if (keycloak is not null)
{
    if (!setupTry)
    {
        adminApi.WithEnvironment("Oidc__Authority", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/ume"))
            .WithEnvironment("Oidc__ClientSecret", oidcSecret);
    }

    adminApi.WaitFor(keycloak);
}
else
{
    adminApi.WithExternalHttpEndpoints();
}

if (setupTry)
{
    adminApi.WithEnvironment("Setup__Secret", Secret("setup-secret", 64));
    // The wizard's Open WebUI step too (identity design §19), whether or not the Open WebUI container runs: its values are
    // then entered in the wizard (the local Keycloak's client open-webui-dev).
    adminApi.WithEnvironment("OpenWebUi__Enabled", "true").WithEnvironment("OpenWebUi__PublicUrl", "http://localhost:7410");
    if (builder.Configuration["Setup:Reopen"] is { Length: > 0 } reopen)
    {
        adminApi.WithEnvironment("Setup__Reopen", reopen);
    }
}

if (externalOidc)
{
    // Every Oidc:* setting (Authority, ClientId, ClientSecret, RoleClaim, ...) is passed through; with Setup:Try, all but
    // the authority and the client secret.
    foreach (var (key, value) in builder.Configuration.GetSection("Oidc").AsEnumerable(makePathsRelative: true))
    {
        if (setupTry && key is "Authority" or "ClientSecret")
        {
            continue;
        }

        if (value is not null)
        {
            adminApi.WithEnvironment("Oidc__" + key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
}

// The admin UI is registered once stream B adds src/admin-ui (P0 checklist #11). Port 5174, so the gateway's admin UI
// (5173) can run at the same time.
if (File.Exists(Path.Combine(builder.AppHostDirectory, "..", "admin-ui", "package.json")))
{
    var adminUi = builder.AddViteApp("admin-ui", Path.Combine("..", "admin-ui"))
        .WithReference(adminApi)
        .WithEndpoint("http", endpoint => { endpoint.UriScheme = "https"; endpoint.Port = 5174; })
        .WithHttpsDeveloperCertificate()
        .WaitFor(adminApi)
        .WithExternalHttpEndpoints();

    adminApi.PublishWithContainerFiles(adminUi, "wwwroot");
}

// Open WebUI against rag-api (P1a item 15; chat design §4.3), OpenWebUi:Enabled (default true) in run mode. It connects with
// the development deployment's own Open WebUI client (identity §5.2, M-17 C4): with Seed:Enabled the seeded "Open WebUI
// (dev)" (DevSeeder.DevClientId, key ume-rag-sk- + dev-key); without it (e.g. the pilot stack run with Seed:Enabled=false,
// #236) the key from the secret parameter openwebui-api-key (user secrets Parameters:openwebui-api-key, a whole
// ume-rag-sk- key), which rag-migrate registers as that client (OpenWebUi__ApiKey). rag-api trusts the managed client's users.
// It forwards each user as an HS256 JWT that rag-api checks (identity §5.2). Local accounts sign in (the first is Open WebUI's admin); the pilot signs in
// over OIDC through rag-openwebui in deploy/compose.rag.yaml (item 18). Same pin as that file; update both together.
const string OpenWebUiImage = "ghcr.io/open-webui/open-webui";
const string OpenWebUiTag = "v0.12.5";
const string OpenWebUiDigest = "03443b8de6b617da9d900edd09b4bf1cb0ddbfdcba6afb42c548336a0b821ded";
var seeded = builder.Configuration.GetValue("Seed:Enabled", true);
// Without the seed, Open WebUI runs only when the user secrets hold its key (never in the browser tests).
var configuredOpenWebUiKey = !seeded && !e2e && builder.Configuration["Parameters:openwebui-api-key"] is { Length: > 0 };
if (builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("OpenWebUi:Enabled", true) && (seeded || configuredOpenWebUiKey))
{
    var openWebUiJwtSecret = Secret("openwebui-jwt-secret", 48);
    var openWebUiSecretKey = Secret("openwebui-secret-key", 48);
    // Seeded: the dev key (rag-migrate's Seed:DevKey). Otherwise the key is never generated, only read from the user secrets,
    // and rag-migrate registers it as the deployment's client.
    ReferenceExpression openWebUiApiKey;
    if (seeded)
    {
        openWebUiApiKey = ReferenceExpression.Create($"ume-rag-sk-{devKey}");
    }
    else
    {
        var configuredKey = builder.AddParameter("openwebui-api-key", secret: true);
        migrations.WithEnvironment("OpenWebUi__ApiKey", configuredKey);
        openWebUiApiKey = ReferenceExpression.Create($"{configuredKey}");
    }

    api.WithEnvironment("OpenWebUi__JwtSecret", openWebUiJwtSecret);
    // As the openwebui profile in deploy/: rag-admin knows the chat runs, so the admin UI names it (/bff/user openWebUi.enabled).
    adminApi.WithEnvironment("OpenWebUi__Enabled", "true");
    var openWebUi = builder.AddContainer("open-webui", OpenWebUiImage, OpenWebUiTag).WithImageSHA256(OpenWebUiDigest)
        .WithHttpEndpoint(port: 7410, targetPort: 8080, name: "http")
        .WithVolume("ume-rag-openwebui-data", "/app/backend/data")
        .WithEnvironment("WEBUI_SECRET_KEY", openWebUiSecretKey)
        .WithEnvironment("ENABLE_PERSISTENT_CONFIG", "false")
        // No model downloads and no retrieval of Open WebUI's own: rag-api does the RAG (as rag-openwebui in deploy/compose.rag.yaml).
        .WithEnvironment("OFFLINE_MODE", "true")
        .WithEnvironment("BYPASS_EMBEDDING_AND_RETRIEVAL", "true")
        .WithEnvironment("ENABLE_OLLAMA_API", "false")
        .WithEnvironment("ENABLE_OPENAI_API", "true")
        // rag-api's HTTP endpoint: the container does not trust the AppHost's development certificate.
        .WithEnvironment("OPENAI_API_BASE_URLS", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1"))
        .WithEnvironment("OPENAI_API_KEYS", openWebUiApiKey)
        .WithEnvironment("OPENAI_API_CONFIGS", """{"0":{"headers":{"X-Rag-Owui-Message-Id":"{{MESSAGE_ID}}","X-Rag-Owui-User-Message-Id":"{{USER_MESSAGE_ID}}"}}}""")
        // Open WebUI 0.12.3 adds its builtin tools to every browser chat unless a model's capabilities.builtin_tools is false
        // (utils/middleware.py:2076-2082), and rag-api refuses tools (chat design §3.2): the default metadata is merged into every
        // model's capabilities, per-model overrides win (utils/models.py:367-381), and it holds with ENABLE_PERSISTENT_CONFIG=false (#276).
        .WithEnvironment("DEFAULT_MODEL_METADATA", """{"capabilities":{"builtin_tools":false}}""")
        .WithEnvironment("ENABLE_FORWARD_USER_INFO_HEADERS", "true")
        .WithEnvironment("FORWARD_USER_INFO_HEADER_JWT_SECRET", openWebUiJwtSecret)
        .WithEnvironment("ENABLE_DIRECT_CONNECTIONS", "false")
        .WithEnvironment("ENABLE_TITLE_GENERATION", "false")
        .WithEnvironment("ENABLE_TAGS_GENERATION", "false")
        .WithEnvironment("ENABLE_FOLLOW_UP_GENERATION", "false")
        .WithEnvironment("ENABLE_RETRIEVAL_QUERY_GENERATION", "false")
        .WithEnvironment("ENABLE_SEARCH_QUERY_GENERATION", "false")
        .WithEnvironment("ENABLE_AUTOCOMPLETE_GENERATION", "false")
        .WithEnvironment("ENABLE_CONTEXT_COMPACTION", "false")
        .WithEnvironment("ENABLE_WEB_SEARCH", "false")
        .WithEnvironment("ENABLE_CODE_EXECUTION", "false")
        .WithEnvironment("ENABLE_CODE_INTERPRETER", "false")
        .WithEnvironment("ENABLE_IMAGE_GENERATION", "false")
        .WithEnvironment("USER_PERMISSIONS_CHAT_FILE_UPLOAD", "false")
        .WithEnvironment("USER_PERMISSIONS_CHAT_WEB_UPLOAD", "false")
        .WithEnvironment("USER_PERMISSIONS_CHAT_SYSTEM_PROMPT", "false")
        .WithEnvironment("USER_PERMISSIONS_CHAT_PARAMS", "false")
        .WithEnvironment("ENABLE_ADMIN_CHAT_ACCESS", "false")
        .WithEnvironment("ENABLE_ADMIN_EXPORT", "false")
        // As rag-openwebui in deploy/compose.rag.yaml: API keys only for the feedback routes the importer uses (chat design §14.2).
        .WithEnvironment("ENABLE_API_KEYS", "true")
        .WithEnvironment("ENABLE_API_KEYS_ENDPOINT_RESTRICTIONS", "true")
        .WithEnvironment("API_KEYS_ALLOWED_ENDPOINTS", "/api/v1/evaluations/feedbacks/list,/api/v1/evaluations/feedback")
        .WithEnvironment("USER_PERMISSIONS_FEATURES_API_KEYS", "false")
        .WithEnvironment("BYPASS_MODEL_ACCESS_CONTROL", builder.Configuration.GetValue("OpenWebUi:BypassModelAccessControl", true) ? "true" : "false")
        .WithEnvironment("DEFAULT_USER_ROLE", "user")
        .WithEnvironment("ENABLE_SIGNUP", "true")
        .WithEnvironment("ENABLE_LOGIN_FORM", "true")
        .WaitFor(api);

    // The client links the assistant page to this Open WebUI (its PortalUrl, admin design §5.4 step 13), set when made.
    migrations.WithEnvironment(seeded ? "Seed__DevClientPortalUrl" : "OpenWebUi__PortalUrl", openWebUi.GetEndpoint("http"));

    // The feedback importer and pruning (chat design §13.3, §14.2) run when the user secret Parameters:openwebui-admin-api-key
    // holds the API key of Open WebUI's admin (the first local account, minted in Settings › Account).
    if (builder.Configuration["Parameters:openwebui-admin-api-key"] is { Length: > 0 })
    {
        api.WithEnvironment("OpenWebUi__BaseUrl", openWebUi.GetEndpoint("http"))
            .WithEnvironment("OpenWebUi__AdminApiKey", builder.AddParameter("openwebui-admin-api-key", secret: true))
            .WithEnvironment("OpenWebUi__FeedbackImportInterval", builder.Configuration["OpenWebUi:FeedbackImportInterval"] ?? "00:01:00");
    }
}

builder.Build().Run();

IResourceBuilder<ParameterResource> Secret(string name, int length) =>
    builder.AddParameter(name, new GenerateParameterDefault
    {
        MinLength = length, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
