using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAdminSessionStore, PostgresAdminSessionStore>();
builder.Services.AddSingleton<AccessDeniedThrottle>();
builder.Services.AddSingleton<AdminClientAddresses>();
// The OpenAPI document and Scalar (admin design §4.1, copied): in P1 also the action UI for platform objects without a page.
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AdminContext>();
builder.Services.AddOptions<OrgSyncOptions>().BindConfiguration(OrgSyncOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddHttpClient(OrganisationSync.HttpClientName);
builder.Services.AddSingleton<OrganisationSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrganisationSync>());
// The gateway client of item 13: the model mirror, canary embeddings, V8's /v1/models check and the key status.
builder.AddGatewayClient(GatewayApi.HttpClientName);
builder.Services.AddSingleton<GatewayApi>();
builder.Services.AddSingleton<GatewayModelSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayModelSync>());
builder.Services.AddScoped<GatewayModelCheck>();
builder.Services.AddHostedService<RetentionService>();
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
api.MapEvaluation();
api.MapSiteProfiles();
api.MapAssistants();
api.MapSources();
api.MapRuns();
api.MapPiiReport();
api.MapDocuments();
api.MapPreviews();
api.MapUploads();
api.MapKeys();
api.MapOrganisation();
api.MapSettings();
api.MapDataProtection();
api.MapTemplates();
app.MapOpenApi().RequireAuthorization(AdminPolicies.Admin);
app.MapScalarApiReference(o => o.WithTitle("Ume.Rag Admin API").WithNonce().DisableDefaultFonts()).RequireAuthorization(AdminPolicies.Admin);
api.MapGateway();

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
