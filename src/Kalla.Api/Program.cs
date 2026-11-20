using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Kalla.Answering;
using Kalla.Api;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Blobs;

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
builder.AddKallaWebDefaults(maxRequestBodySize: 16 * 1024, errors => errors.Title = "The request could not be completed");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
// One pool for EF, the config monitor and retrieval (data-model §1.1), with retrieval's auto-prepare (knowledge §6.6).
builder.AddRagDatabase(data => Kalla.Retrieval.Postgres.RetrievalDataSource.Configure(data), admitAtStart: true);
builder.AddRagConfigMonitor();
builder.AddRagKeys();
// Client addresses come only from the trusted proxies set in the admin UI (ClientAddressMiddleware, as the gateway's).
// ASPNETCORE_FORWARDEDHEADERS_ENABLED would believe X-Forwarded-For from any caller, so it is switched off here.
builder.Services.PostConfigure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.None);

// Rag:Roles (repo-and-deployment §3): a comma-separated list; the knowledge routes exist only with "knowledge".
var roles = (builder.Configuration["Rag:Roles"] ?? "knowledge").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var knowledge = roles.Contains("knowledge", StringComparer.Ordinal);
var publishReference = false;
if (knowledge)
{
    builder.AddRagBlobStore();
    // Citation downloads decrypt internal blobs (knowledge §3.5): the rag-blob-internal key under KeyProvider:Directory.
    builder.AddRagBlobEnvelope();
    builder.AddRagKnowledge();
    builder.Services.AddSingleton<KeyLastUseRecorder>();
    builder.Services.AddSingleton<KnowledgeCallers>();
    builder.Services.AddOptions<ApiSecurityOptions>().BindConfiguration("Api").Configure<IConfiguration>((o, c) =>
        o.SourceAddress = c["Api:Security:SourceAddress"] ?? o.SourceAddress);
    builder.Services.AddSingleton<KnowledgeSecurity>();
    builder.Services.AddHostedService<KnowledgeSecurityFlusher>();
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
            document.Info.Title = "Källa Knowledge API";
            document.Info.Description = "Retrieval over Umeå kommun's published collections. Authenticate with an internal client's API key " +
                "(Authorization: Bearer kalla-sk-…). Citation links of uploaded documents carry a signed download grant (?grant=…), valid " +
                "7 days (1 hour for sensitive) and re-checked on every use. Request bodies are limited to 16 KB; downloads to 120 a minute per address.";
            return Task.CompletedTask;
        }));
    }
}

// The chat role (chat design §2.1; P1a item 15): in P1a it runs with the knowledge role in process (KnowledgeService).
var chat = roles.Contains("chat", StringComparer.Ordinal);
if (chat)
{
    if (!knowledge)
    {
        throw new InvalidOperationException("Rag:Roles: the chat role needs the knowledge role in the same process until HttpRetriever exists (knowledge §2.2).");
    }

    builder.AddGatewayClient(Kalla.Answering.Chat.GatewayChatClient.HttpClientName);
    builder.Services.AddSingleton<Kalla.Answering.Chat.GatewayChatClient>();
    builder.Services.AddSingleton<Kalla.Answering.Chat.ChatService>();
    builder.Services.AddOptions<Kalla.Answering.Chat.ChatOptions>().BindConfiguration(Kalla.Answering.Chat.ChatOptions.SectionName);
    builder.Services.AddSingleton<ChatRateLimiter>();
    builder.Services.AddOptions<OpenWebUiOptions>().BindConfiguration(OpenWebUiOptions.SectionName);
    builder.Services.AddSingleton<ChatCallers>();
    // §11.1, §12: issued conversation ids and the background conversation writer (metadata only in P1).
    var conversationKeys = builder.Configuration.GetSection("Security:ConversationIdKeys").GetChildren().Where(k => !string.IsNullOrEmpty(k.Value))
        .ToDictionary(k => k.Key, k => System.Text.Encoding.UTF8.GetBytes(k.Value!), StringComparer.Ordinal);
    if (conversationKeys.Count == 0)
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException("Security:ConversationIdKeys must have at least one key in production.");
        }

        // Development and tests: one per-process key (issued ids then verify on this instance only).
        conversationKeys["process"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    }

    builder.Services.AddSingleton(new ConversationIds(conversationKeys));
    builder.Services.AddSingleton<ChatMetrics>();
    builder.Services.AddSingleton<ConversationWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ConversationWriter>());
    // §14.2: the Open WebUI feedback import and pruning (runs only with OpenWebUi:BaseUrl, AdminApiKey and ClientId set).
    builder.Services.AddHttpClient(FeedbackImporter.HttpClientName);
    builder.Services.AddSingleton<FeedbackImporter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FeedbackImporter>());
    // Open WebUI's sign-in settings for its start wrapper (identity design §19): the shared key ring, read-only (rag-admin makes the keys),
    // to unprotect the wizard's stored secret.
    builder.AddRagDataProtection(readOnly: true);
    builder.Services.AddSingleton<Kalla.Infrastructure.Security.SettingsSecretProtector>();
    builder.Services.AddScoped<Kalla.Infrastructure.Security.OpenWebUiSignInReader>();
}

var app = builder.Build();
app.UseKallaWebDefaults();
if (knowledge)
{
    // Before anything reads the client address (key allow-lists, the auth-failure throttle, events, the downloads limiter);
    // only the client API, so health probes are untouched. The trusted proxies come with the access snapshot.
    app.UseWhen(http => http.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
                        || http.Request.Path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase),
        branch => branch.UseMiddleware<ClientAddressMiddleware>());
}

app.UseKallaSecurityHeaders(o =>
{
    o.NoStorePrefixes.Add("/v1");
    o.NoStorePrefixes.Add("/internal");
});
app.UseProblemDetailsForEmptyErrors("The request could not be completed");
app.MapDefaultEndpoints();
if (knowledge)
{
    app.UseRateLimiter();
    app.MapKnowledge();
    if (chat)
    {
        app.MapChat();
        app.MapConversations();
        app.MapFeedback();
        app.MapOpenWebUiSignIn();
    }

    if (publishReference)
    {
        // A fresh nonce per request (Scalar keeps it under NonceHttpContextItemKey); the policy middleware sends the matching CSP.
        app.UseApiReferencePolicy("/scalar", ScalarOptions.NonceHttpContextItemKey);
        app.MapOpenApi();
        app.MapScalarApiReference(o => o.WithTitle("Källa Knowledge API").WithNonce().DisableDefaultFonts());
    }
}

app.Run();

/// <summary>Entry point, public for <c>WebApplicationFactory</c> tests.</summary>
public partial class Program;
