using Microsoft.Extensions.Configuration;
using Ume.Rag.ServiceDefaults;

#pragma warning disable ASPIRECERTIFICATES001

// Resource graph of plans/platform/repo-and-deployment.md §4.1. Stage 0 registers every P0 resource; the services are
// skeletons (health and /version only) that the streams fill in.

// Connection-string name the services look up.
const string DatabaseResourceName = "ragdb";

// The stock pgvector image ume-rag/postgres is built from (§5.2; pgBackRest is not needed in dev). Same pin as
// Ume.Rag.TestKit.TestPostgres.Image; update both together.
const string PostgresImage = "pgvector/pgvector";
const string PostgresTag = "0.8.7-pg17-trixie";
const string PostgresDigest = "7a7e9f22015b67edb4bef5c59daeebcd7e74bfa570df6ce60ae01237c8648a84";

// The Keycloak version Aspire.Hosting.Keycloak runs (tag 26.6), pinned by digest. Same pin as
// Ume.Rag.AdminApi.Tests.KeycloakFixture.Image; update both together.
const string KeycloakTag = "26.6.4";
const string KeycloakDigest = "0aae0de7fca85525f727d3354df17896092de8bb26ae4c12d89c77e5df8cbce4";

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);

var pepper = Secret("key-pepper", 64);
var devKey = Secret("dev-key", 43);
var oidcSecret = Secret("oidc-client-secret", 48);
var devUserPassword = Secret("dev-user-password", 32);
var apiClientSecret = Secret("api-client-secret", 48);
var openWebUiClientSecret = Secret("openwebui-client-secret", 48);
var dataApiKey = Secret("data-api-key", 43);
// The knowledge role's query-embedding cache key and the download-grant signing key (knowledge design §4.6, §5.4).
var cacheKeyPepper = Secret("cache-key-pepper", 48);
var downloadGrantKey = Secret("download-grant-key", 48);

// The image is Postgres 17 (PGDATA /var/lib/postgresql/data); Aspire 13's WithDataVolume mounts at /var/lib/postgresql,
// the Postgres 18 layout, so the data went to the image's anonymous volume and was lost on restart (#168). The volume is
// mounted at the image's data path and the cluster lives in pgdata/ below it: a volume from the old layout holds an
// empty data/ directory, which would make initdb refuse a non-empty PGDATA.
var postgres = builder.AddPostgres("postgres")
    .WithImage(PostgresImage, PostgresTag).WithImageSHA256(PostgresDigest)
    .WithEnvironment("PGDATA", "/var/lib/postgresql/data/pgdata");
postgres.WithVolume(
    (builder.ExecutionContext.IsRunMode ? builder.Configuration["DevelopmentVolumes:Postgres"] : null) ?? VolumeNameGenerator.Generate(postgres, "data"),
    "/var/lib/postgresql/data");
var database = postgres.AddDatabase(DatabaseResourceName);

// Redis:Enabled=true is the P2 path (first multi-host deployment). Off by default: the pilot shape keeps rate limits and
// the query-embedding cache in process (simplification pass §14).
var redis = builder.Configuration.GetValue<bool>("Redis:Enabled")
    ? builder.AddRedis("redis", password: Secret("redis-password", 48))
    : null;

// Oidc:Authority (AppHost user secrets) points local runs at an existing identity provider instead of the bundled test
// Keycloak, which is then not started. Its realm is dev/keycloak/ume-realm.json (identity design §11).
var externalOidc = !string.IsNullOrWhiteSpace(builder.Configuration["Oidc:Authority"]);
var keycloak = builder.ExecutionContext.IsRunMode && !externalOidc ? builder.AddKeycloak("keycloak")
    .WithImageTag(KeycloakTag).WithImageSHA256(KeycloakDigest)
    .WithRealmImport(Path.Combine("..", "..", "dev", "keycloak"))
    .WithEnvironment("UME_RAG_OIDC_CLIENT_SECRET", oidcSecret)
    .WithEnvironment("UME_RAG_DEV_USER_PASSWORD", devUserPassword)
    .WithEnvironment("UME_RAG_API_CLIENT_SECRET", apiClientSecret)
    .WithEnvironment("UME_RAG_OWUI_CLIENT_SECRET", openWebUiClientSecret)
    .WithHttpsDeveloperCertificate() : null;

// FakeGateway:Enabled=false (AppHost user secrets) runs against a real local gateway instead (§4.4).
var fakeGateway = builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("FakeGateway:Enabled", true)
    ? builder.AddProject<Projects.Ume_Rag_FakeGateway>("fake-gateway", o => o.ExcludeLaunchProfile = true)
        .WithHttpsEndpoint()
        .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https")
    : null;

// Per-level dev keys (OnPrem, Eu, External), like the pilot team's gateway keys: the fake accepts them, the services
// hold them as Gateway:Keys:<level> (the worker never the External one, simplification pass §6).
string[] residencyLevels = ["OnPrem", "Eu", "External"];
var gatewayKeys = residencyLevels.ToDictionary(level => level, level => Secret($"gateway-key-{level.ToLowerInvariant()}", 43));

// FakeGateway:Latency:* (AppHost user secrets) gives the fake a real gateway's timing, as the gateway's FakeLlm.
if (fakeGateway is not null)
{
    foreach (var (level, key) in gatewayKeys)
    {
        fakeGateway.WithEnvironment($"FakeGateway__Keys__{level}", key);
    }

    foreach (var setting in builder.Configuration.GetSection("FakeGateway:Latency").GetChildren())
    {
        fakeGateway.WithEnvironment($"FakeGateway__Latency__{setting.Key}", setting.Value);
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
            .WithHttpEndpoint(targetPort: ParserPort, name: "http")
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

// ClamAv:Enabled=false: the worker uses an EICAR-only fake scanner. Dev freshclam uses the public mirror (§5.5).
var clamAv = builder.Configuration.GetValue("ClamAv:Enabled", true)
    ? builder.AddDockerfile("clamav", Path.Combine("..", "..", "images", "clamav"))
        .WithVolume("ume-rag-clamav-db", "/var/lib/clamav")
        .WithEndpoint(targetPort: 3310, name: "clamd", scheme: "tcp")
    : null;

var migrations = builder.AddProject<Projects.Ume_Rag_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Seed__Enabled", builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("Seed:Enabled", true) ? "true" : "false")
    .WaitFor(database);

if (builder.ExecutionContext.IsRunMode)
{
    migrations.WithEnvironment("Seed__DevKey", devKey);
}

// EmbeddingProfile:* (AppHost configuration or user secrets) seeds the platform profile in the migrator (P1a item 13,
// #142): e.g. GatewayModel=text-embedding-3-large, Dimensions=1024, RequestDimensions=true, MaxResidency=Eu.
foreach (var (key, value) in builder.Configuration.GetSection("EmbeddingProfile").AsEnumerable(makePathsRelative: true))
{
    if (value is not null)
    {
        migrations.WithEnvironment("EmbeddingProfile__" + key.Replace(":", "__", StringComparison.Ordinal), value);
    }
}

if (fakeGateway is not null)
{
    migrations.WithEnvironment("Seed__FakeGatewayUrl", ReferenceExpression.Create($"{fakeGateway.GetEndpoint("https")}/v1"))
        .WaitFor(fakeGateway);
}

var api = builder.AddProject<Projects.Ume_Rag_Api>("api", o => o.LaunchProfileName = "https")
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

var worker = builder.AddProject<Projects.Ume_Rag_Worker>("worker", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https");

if (parserEndpoint is not null)
{
    worker.WithEnvironment("Parser__BaseUrl", parserEndpoint);
}

if (clamAv is not null)
{
    worker.WithEnvironment("ClamAv__Host", clamAv.GetEndpoint("clamd").Property(EndpointProperty.Host))
        .WithEnvironment("ClamAv__Port", clamAv.GetEndpoint("clamd").Property(EndpointProperty.Port));
}

var adminApi = builder.AddProject<Projects.Ume_Rag_AdminApi>("adminapi", o => o.LaunchProfileName = "https")
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
    foreach (var service in new[] { api, worker, adminApi })
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
    // The admin's organisation sync reads the fake's Data API catalogue (admin design §4.10).
    fakeGateway.WithEnvironment("FakeGateway__DataApiKey", dataApiKey);
    adminApi.WithEnvironment("OrgSync__BaseUrl", fakeGateway.GetEndpoint("https")).WithEnvironment("OrgSync__DataApiKey", dataApiKey);
    foreach (var service in new[] { api, worker, adminApi })
    {
        service.WithEnvironment("Gateway__BaseUrl", fakeGateway.GetEndpoint("https")).WaitFor(fakeGateway);
        foreach (var (level, key) in gatewayKeys)
        {
            if (service != worker || level != "External")
            {
                service.WithEnvironment($"Gateway__Keys__{level}", key);
            }
        }
    }
}
else
{
    // A real gateway, keys as init-deployment.sh --gateway-key-file [SERVICE:]LEVEL=FILE (#95):
    //   Gateway:Keys:<level>                     shared: api and admin, the worker too unless it is External;
    //   Gateway:Services:<service>:Keys:<level>  one deployable's own key (api, admin, worker), wins over the shared one;
    //   every other Gateway:* value (BaseUrl, ...) goes to all three. The worker never holds External.
    var services = new Dictionary<string, IResourceBuilder<ProjectResource>>(StringComparer.Ordinal)
    {
        ["api"] = api, ["admin"] = adminApi, ["worker"] = worker,
    };
    var gateway = builder.Configuration.GetSection("Gateway");
    var keys = services.Keys.ToDictionary(s => s, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
    foreach (var shared in gateway.GetSection("Keys").GetChildren())
    {
        foreach (var service in services.Keys.Where(s => s != "worker" || shared.Key != "External"))
        {
            keys[service][GatewayLevel(shared.Key)] = shared.Value ?? "";
        }
    }

    foreach (var own in gateway.GetSection("Services").GetChildren())
    {
        if (!services.ContainsKey(own.Key))
        {
            throw new InvalidOperationException($"Gateway:Services:{own.Key}: the service is api, admin or worker.");
        }

        foreach (var key in own.GetSection("Keys").GetChildren())
        {
            if (own.Key == "worker" && key.Key == "External")
            {
                throw new InvalidOperationException("Gateway:Services:worker:Keys:External: the worker never holds the External key.");
            }

            keys[own.Key][GatewayLevel(key.Key)] = key.Value ?? "";
        }
    }

    foreach (var (name, service) in services)
    {
        foreach (var (level, key) in keys[name].Where(k => k.Value.Length > 0))
        {
            service.WithEnvironment($"Gateway__Keys__{level}", key);
        }

        foreach (var (key, value) in gateway.AsEnumerable(makePathsRelative: true))
        {
            if (value is not null && !key.StartsWith("Keys:", StringComparison.Ordinal) && !key.StartsWith("Services:", StringComparison.Ordinal))
            {
                service.WithEnvironment("Gateway__" + key.Replace(":", "__", StringComparison.Ordinal), value);
            }
        }
    }

    // The admin's organisation sync against the gateway's Data API (admin design §4.10): OrgSync:BaseUrl, OrgSync:DataApiKey.
    foreach (var (key, value) in builder.Configuration.GetSection("OrgSync").AsEnumerable(makePathsRelative: true))
    {
        if (value is not null)
        {
            adminApi.WithEnvironment("OrgSync__" + key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
}

if (keycloak is not null)
{
    adminApi.WithEnvironment("Oidc__Authority", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/ume"))
        .WithEnvironment("Oidc__ClientSecret", oidcSecret)
        .WaitFor(keycloak);
}
else
{
    adminApi.WithExternalHttpEndpoints();
}

if (externalOidc)
{
    // Every Oidc:* setting (Authority, ClientId, ClientSecret, RoleClaim, ...) is passed through.
    foreach (var (key, value) in builder.Configuration.GetSection("Oidc").AsEnumerable(makePathsRelative: true))
    {
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
// the seeded dev client "Open WebUI (dev)" (DevSeeder.DevClientId, key ume-rag-sk- + dev-key), or, when OpenWebUi:ClientId is
// set, with that client (one made through the admin API, e.g. the pilot stack run with Seed:Enabled=false, #236) and its
// key from the secret parameter openwebui-api-key (user secrets Parameters:openwebui-api-key, the whole ume-rag-sk- key).
// It forwards each user as an HS256 JWT that rag-api checks (identity §5.2). Local accounts sign in (the first is Open WebUI's admin); the pilot signs in
// over OIDC through deploy/compose.openwebui.yaml (item 18). Same pin as the Compose file; update both together.
const string OpenWebUiImage = "ghcr.io/open-webui/open-webui";
const string OpenWebUiTag = "v0.12.3";
const string OpenWebUiDigest = "a537bbe1e6b428820f52ed4a029d7fff97062e24d5cf32c365fba36fd0542def";
const string DevOpenWebUiClientId = "01929b6e-0000-7000-8000-0000000c11e7"; // DevSeeder.DevClientId
var openWebUiClientId = builder.Configuration["OpenWebUi:ClientId"];
if (openWebUiClientId is { Length: > 0 } && !Guid.TryParse(openWebUiClientId, out _))
{
    throw new InvalidOperationException("OpenWebUi:ClientId: the id of an open-webui client (POST /api/clients), a uuid.");
}

if (builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("OpenWebUi:Enabled", true)
    && (openWebUiClientId is { Length: > 0 } || builder.Configuration.GetValue("Seed:Enabled", true)))
{
    var openWebUiJwtSecret = Secret("openwebui-jwt-secret", 48);
    var openWebUiSecretKey = Secret("openwebui-secret-key", 48);
    // A configured client wins over the seeded one; its key is never generated, only read from the user secrets.
    var openWebUiApiKey = openWebUiClientId is { Length: > 0 }
        ? ReferenceExpression.Create($"{builder.AddParameter("openwebui-api-key", secret: true)}")
        : ReferenceExpression.Create($"ume-rag-sk-{devKey}");
    api.WithEnvironment("OpenWebUi__ClientId", openWebUiClientId is { Length: > 0 } ? openWebUiClientId : DevOpenWebUiClientId)
        .WithEnvironment("OpenWebUi__JwtSecret", openWebUiJwtSecret);
    builder.AddContainer("open-webui", OpenWebUiImage, OpenWebUiTag).WithImageSHA256(OpenWebUiDigest)
        .WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithVolume("ume-rag-openwebui-data", "/app/backend/data")
        .WithEnvironment("WEBUI_SECRET_KEY", openWebUiSecretKey)
        .WithEnvironment("ENABLE_PERSISTENT_CONFIG", "false")
        // No model downloads and no retrieval of Open WebUI's own: rag-api does the RAG (as deploy/compose.openwebui.yaml).
        .WithEnvironment("OFFLINE_MODE", "true")
        .WithEnvironment("BYPASS_EMBEDDING_AND_RETRIEVAL", "true")
        .WithEnvironment("ENABLE_OLLAMA_API", "false")
        .WithEnvironment("ENABLE_OPENAI_API", "true")
        // rag-api's HTTP endpoint: the container does not trust the AppHost's development certificate.
        .WithEnvironment("OPENAI_API_BASE_URLS", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1"))
        .WithEnvironment("OPENAI_API_KEYS", openWebUiApiKey)
        .WithEnvironment("OPENAI_API_CONFIGS", """{"0":{"headers":{"X-Rag-Owui-Message-Id":"{{MESSAGE_ID}}","X-Rag-Owui-User-Message-Id":"{{USER_MESSAGE_ID}}"}}}""")
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
        .WithEnvironment("BYPASS_MODEL_ACCESS_CONTROL", "true")
        .WithEnvironment("DEFAULT_USER_ROLE", "user")
        .WithEnvironment("ENABLE_SIGNUP", "true")
        .WithEnvironment("ENABLE_LOGIN_FORM", "true")
        .WaitFor(api);
}

builder.Build().Run();

IResourceBuilder<ParameterResource> Secret(string name, int length) =>
    builder.AddParameter(name, new GenerateParameterDefault
    {
        MinLength = length, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);

// A gateway key's level as the services bind it (Gateway:Keys:<level>); anything else is a typo that would be ignored.
string GatewayLevel(string level) => residencyLevels.Contains(level, StringComparer.Ordinal)
    ? level
    : throw new InvalidOperationException($"Gateway key level {level}: the level is OnPrem, Eu or External.");
