using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Ume.Rag.Answering;
using Ume.Rag.Api;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Blobs;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
// Download grants are bearer tokens in the query string (knowledge design §4.6): the hosting "Request starting …" line
// (Information) would log them, so that category stays at Warning whatever the log level; traces drop url.query anyway.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// Bodies above 16 KB are refused (knowledge design §3.6, as the gateway's Data API).
builder.AddUmeWebDefaults(maxRequestBodySize: 16 * 1024, errors => errors.Title = "The request could not be completed");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.AddRagDatabase();
builder.AddRagConfigMonitor();
builder.AddRagKeys();

// Rag:Roles (repo-and-deployment §3): a comma-separated list; the knowledge routes exist only with "knowledge".
var roles = (builder.Configuration["Rag:Roles"] ?? "knowledge").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var knowledge = roles.Contains("knowledge", StringComparer.Ordinal);
var publishReference = false;
if (knowledge)
{
    builder.AddRagBlobStore();
    builder.AddRagKnowledge();
    builder.Services.AddSingleton<KnowledgeCallers>();
    builder.Services.AddRateLimiter(o =>
    {
        o.AddPolicy(KnowledgeEndpoints.DownloadsPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = http.RequestServices.GetRequiredService<IOptions<KnowledgeOptions>>().Value.DownloadsPerMinutePerAddress,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        o.RejectWithProblem("Too many downloads. Wait a minute.");
    });
    publishReference = builder.Configuration.GetValue("Knowledge:PublishApiReference", true);
    if (publishReference)
    {
        builder.Services.AddOpenApi("knowledge", o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Ume.Rag Knowledge API";
            document.Info.Description = "Retrieval over Umeå kommun's published collections. Authenticate with an internal client's API key " +
                "(Authorization: Bearer ume-rag-sk-…). Citation links of uploaded documents carry a signed download grant (?grant=…), valid " +
                "7 days (1 hour for sensitive) and re-checked on every use. Request bodies are limited to 16 KB; downloads to 120 a minute per address.";
            return Task.CompletedTask;
        }));
    }
}

var app = builder.Build();
app.UseUmeWebDefaults();
app.UseUmeSecurityHeaders(o => o.NoStorePrefixes.Add("/v1"));
app.UseProblemDetailsForEmptyErrors("The request could not be completed");
app.MapDefaultEndpoints();
if (knowledge)
{
    app.UseRateLimiter();
    app.MapKnowledge();
    if (publishReference)
    {
        // A fresh nonce per request (Scalar keeps it under NonceHttpContextItemKey); the policy middleware sends the matching CSP.
        app.UseApiReferencePolicy("/scalar", ScalarOptions.NonceHttpContextItemKey);
        app.MapOpenApi();
        app.MapScalarApiReference(o => o.WithTitle("Ume.Rag Knowledge API").WithNonce().DisableDefaultFonts());
    }
}

app.Run();

/// <summary>Entry point, public for <c>WebApplicationFactory</c> tests.</summary>
public partial class Program;
