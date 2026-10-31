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

var postgres = builder.AddPostgres("postgres")
    .WithImage(PostgresImage, PostgresTag).WithImageSHA256(PostgresDigest)
    .WithDataVolume(builder.ExecutionContext.IsRunMode ? builder.Configuration["DevelopmentVolumes:Postgres"] : null);
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
