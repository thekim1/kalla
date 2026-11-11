using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Scalar.AspNetCore;
using Ume.Rag.AdminApi;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Infrastructure.Stores;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddUmeWebDefaults(maxRequestBodySize: 1024 * 1024, errors =>
{
    errors.Title = "The action could not be completed";
    // Database refusals are the caller's to resolve, each with its admin error code (admin design §4.1).
    errors.Translators.Add(AdminDatabaseFaults.Translate);
});
builder.AddRagDatabase();
// Admin actions enqueue worker jobs in their own transaction (H4, #28); the admin registers no rag_jobs listener.
builder.AddRagJobQueue();
// Upload batches (admin design §4.5): staged under tmp/, promoted to uploads/ at commit (BlobStore:Root).
builder.AddRagBlobStore();
// Internal uploads are envelope-encrypted when staged (data-model §7, #181): KeyProvider:Directory holds rag-blob-internal.
builder.AddRagBlobEnvelope();
// API keys (identity §6.4): hashed with Security:KeyPepper as rag-api does; resolved when a key is created or rotated.
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);
builder.Services.AddSingleton<AdminKeyHasher>();
builder.AddRagDataProtection();
// Explain access (identity §4.9): the policy snapshot loaded at the current version for each question (no monitor: the
// admin applies no outbox), and the api's policy with the same Knowledge:OpenToClientKinds.
builder.Services.TryAddSingleton<ConfigVersionState>();
builder.Services.AddSingleton(sp => new Ume.Rag.Infrastructure.Access.PolicySnapshotStore(sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<ConfigVersionState>(), sp.GetRequiredService<ILogger<Ume.Rag.Infrastructure.Access.PolicySnapshotStore>>()));
builder.Services.AddSingleton<Ume.Rag.Domain.Access.IAccessPolicy>(new Ume.Rag.Domain.Access.AccessPolicy(new HashSet<string>(
    builder.Configuration.GetSection("Knowledge:OpenToClientKinds").Get<string[]>() ?? ["internal"], StringComparer.Ordinal)));
builder.Services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
// The ceilings V37 checks host politeness profiles against (connectors design §6.6): the worker's Crawl keys and Render:MaxConcurrent.
builder.Services.AddOptions<CrawlCeilingOptions>().BindConfiguration(CrawlCeilingOptions.SectionName)
    .Configure<IConfiguration>((o, configuration) => o.MaxRenderPerHost = configuration.GetValue("Render:MaxConcurrent", o.MaxRenderPerHost))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OidcOptions>, OidcOptionsValidator>(); // role groups: rag- names or Entra ids (#413)
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAdminSessionStore, PostgresAdminSessionStore>();
builder.Services.AddSingleton<AccessDeniedThrottle>();
builder.Services.AddSingleton<AdminClientAddresses>();
// The OpenAPI document and Scalar (admin design §4.1, copied): in P1 also the action UI for platform objects without a page.
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ConfigLockMetrics>();
builder.Services.AddScoped<AdminContext>();
builder.Services.AddOptions<OrgSyncOptions>().BindConfiguration(OrgSyncOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
// The sync calls the gateway's Data API (OrgSync:BaseUrl) on its own cadence: no standard resilience handler, whose Polly
// telemetry would log the gateway's address at Warning and Error on every retry (#265).
builder.Services.AddHttpClient(OrganisationSync.HttpClientName).RemoveStandardResilienceHandler();
builder.Services.AddSingleton<OrganisationSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrganisationSync>());
// The gateway client of item 13: the model mirror, canary embeddings and checks, V8's /v1/models check and the key status.
builder.AddGatewayClient(GatewayApi.HttpClientName);
builder.Services.AddSingleton<GatewayApi>();
builder.Services.AddSingleton<GatewayModelSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayModelSync>());
builder.Services.AddScoped<GatewayModelCheck>();
builder.Services.AddSingleton<GatewayModelAvailability>(); // the pickers' availability flag, 5 minutes per ceiling (#447)
builder.Services.AddSingleton<EmbeddingCanaryCheck>();
builder.Services.AddSingleton<EmbeddingProfileTester>();
// The retrieval test (admin design §4.9): the search core on the process's data source, query embeddings on their own gateway client.
builder.AddGatewayClient(Ume.Rag.Answering.GatewayQueryEmbedder.HttpClientName, retryFailed: false); // its own retry (knowledge §5.3)
// The assistant test panel reranks as chat does when the assistant reranks (knowledge design §6.7), on its own gateway client.
builder.AddGatewayClient(Ume.Rag.Retrieval.ChatModelReranker.HttpClientName);
builder.Services.AddSingleton<AdminRetrieval>();
// The test panel (admin design §4.9): chat's gateway client and options, the panel over Ume.Rag.Answering's chat pipeline.
builder.AddGatewayClient(Ume.Rag.Answering.Chat.GatewayChatClient.HttpClientName);
builder.Services.AddOptions<Ume.Rag.Answering.Chat.ChatOptions>().BindConfiguration(Ume.Rag.Answering.Chat.ChatOptions.SectionName);
builder.Services.AddSingleton<Ume.Rag.Answering.Chat.GatewayChatClient>();
builder.Services.AddSingleton<AdminTestPanel>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<PublishReportLinker>();
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy(AdminRateLimits.PerUser, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.IsAuthenticated == true ? AdminActor.SubjectOf(http.User) : http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.AddPolicy(AssistantTestEndpoints.RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.IsAuthenticated == true ? AdminActor.SubjectOf(http.User) : http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = AssistantTestEndpoints.QuestionsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.RejectWithProblem("Too many requests. Wait a minute.");
});
builder.AddAdminAuthentication();

var app = builder.Build();
app.UseUmeWebDefaults();
// Pipeline order copied from the gateway admin (admin design §2.3).
app.UseUmeSecurityHeaders(o =>
{
    o.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    o.NoStorePrefixes.Add("/api");
    o.NoStorePrefixes.Add("/bff");
});
app.UseApiReferencePolicy("/scalar", ScalarOptions.NonceHttpContextItemKey);
app.UseProblemDetailsForEmptyErrors("The action could not be completed");
// Outside authentication and authorization, so it sees their 403s and the signed-in user afterwards.
app.UseMiddleware<AdminActivityMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AdminAntiforgeryMiddleware>();
app.MapDefaultEndpoints();
app.MapAdminAuthentication();

// /api needs a rag-* role (Read); each endpoint adds its own policy from the permission matrix (admin design §3.4).
var api = app.MapGroup("/api").RequireAuthorization(AdminPolicies.Read).RequireRateLimiting(AdminRateLimits.PerUser)
    .AddEndpointFilter<AdminValidationFilter>();
api.MapGet("/ping", () => Results.Ok(new { ok = true })).RequireAuthorization(AdminPolicies.Read);
api.MapClients();
api.MapCollections();
api.MapCollectionSharing();
api.MapSetup();
api.MapFeedback();
api.MapEvaluation();
api.MapSiteProfiles();
api.MapAssistants();
api.MapSources();
api.MapRuns();
api.MapPiiReport();
api.MapDocuments();
api.MapAudit();
api.MapExplainAccess();
api.MapPreviews();
api.MapTestRetrieval();
api.MapAssistantTest();
api.MapPipelines();
api.MapUploads();
api.MapKeys();
api.MapOrganisation();
api.MapSettings();
api.MapPromptTexts();
api.MapDataProtection();
api.MapTemplates();
app.MapOpenApi().RequireAuthorization(AdminPolicies.Admin);
app.MapScalarApiReference(o => o.WithTitle("Ume.Rag Admin API").WithNonce().DisableDefaultFonts()).RequireAuthorization(AdminPolicies.Admin);
api.MapGateway();
api.MapEmbeddingProfiles();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Entry point, public for <c>WebApplicationFactory</c> tests.</summary>
public partial class Program;

namespace Ume.Rag.AdminApi
{
    /// <summary>Rate-limit policy names.</summary>
    public static class AdminRateLimits
    {
        /// <summary>Per signed-in user (<c>Admin:RequestsPerMinute</c>, 120).</summary>
        public const string PerUser = "per-user";
    }

    /// <summary>Database refusals as admin faults (admin design §4.1): 23505, 23503, 23514, concurrency, the config lock.</summary>
    public static class AdminDatabaseFaults
    {
        public static ServiceDefaults.ApiFaultException? Translate(Exception exception) => exception switch
        {
            ConfigBusyException => AdminFaults.Of(AdminErrorCodes.ConfigBusy),
            DbUpdateConcurrencyException => AdminFaults.Of(AdminErrorCodes.ConcurrentChange),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => AdminFaults.Of(AdminErrorCodes.AlreadyExists),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => AdminFaults.Of(AdminErrorCodes.InUse),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation } check } =>
                AdminFaults.Of(AdminErrorCodes.PlatformRule, new Dictionary<string, string> { ["constraint"] = check.ConstraintName ?? "" }),
            _ => null,
        };
    }
}
