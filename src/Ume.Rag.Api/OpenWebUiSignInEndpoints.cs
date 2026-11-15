using System.Text;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.Api;

/// <summary>
/// <c>GET /internal/openwebui/sign-in</c>: Open WebUI's effective sign-in values. <see cref="Configured"/> is
/// <see cref="EffectiveOpenWebUiSignIn.Configured"/>; while false every value is null. <see cref="ClientSecret"/> is null for a public
/// client. <see cref="Version"/> is opaque and changes when any value does (<see cref="OpenWebUiSignIn.Version"/>).
/// </summary>
public sealed record OpenWebUiSignInAnswer(bool Configured, string? ProviderUrl, string? ClientId, string? ClientSecret, string? ProviderName, string Version);

/// <summary>
/// Open WebUI's sign-in settings for its start wrapper (identity design §19, "Open WebUI"; <c>deploy/openwebui/start-with-sign-in.py</c>):
/// the values from the deployment (<c>OpenWebUi:Oidc:*</c>), else the setup wizard's (the stored secret unprotected with the shared key
/// ring, read-only here), else the defaults. Only for the deployment's own Open WebUI client (<c>Kind = open-webui</c> and
/// <see cref="ClientEntry.IsManagedByDeployment"/>, identity design §5.2), authenticated with its key: no key or a refused one is 401,
/// any other client 403, each with its 5001 event. The message limit of the key (the chat limiter) applies; the answer is never
/// stored (<c>Cache-Control: no-store</c>) and the route is not in the API reference. Nothing of the answer is logged.
/// </summary>
public static partial class OpenWebUiSignInEndpoints
{
    public const string Path = "/internal/openwebui/sign-in";

    public static IEndpointRouteBuilder MapOpenWebUiSignIn(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(Path, async (HttpContext http, KnowledgeCallers callers, KnowledgeSecurity security, ChatRateLimiter limiter, OpenWebUiSignInReader reader,
                IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct) =>
            {
                var who = await callers.AuthenticateAsync(http.Request, ct, knowledgeRoute: false);
                if (who.Client.Kind != ClientKinds.OpenWebUi || !who.Client.IsManagedByDeployment)
                {
                    security.AuthFailed(http, DenyReasons.ClientNotAllowed, who.Key?.Kind ?? "sk", who.Key?.Id, null, who.Client.Id);
                    throw new KnowledgeFaultException(403, DenyReasons.ClientNotAllowed, "Only the deployment's own Open WebUI client may read its sign-in settings.");
                }

                try
                {
                    limiter.Check(who);
                }
                catch (ChatException limited) when (limited.Status == 429)
                {
                    throw new KnowledgeFaultException(429, limited.Code, limited.Message);
                }

                var effective = await reader.ReadAsync(ct);
                if (effective.ClientSecret.Unreadable)
                {
                    LogSecretUnreadable(loggers.CreateLogger(typeof(OpenWebUiSignInEndpoints).FullName!));
                }

                var answered = effective.Configured ? effective : null;
                var key = Encoding.UTF8.GetBytes(configuration["Security:CacheKeyPepper"]
                    ?? throw new InvalidOperationException("Security:CacheKeyPepper must be set (at least 32 characters)."));
                return new OpenWebUiSignInAnswer(answered is not null, answered?.ProviderUrl.Value, answered?.ClientId.Value, answered?.ClientSecret.Value,
                    answered?.ProviderName.Value, OpenWebUiSignIn.Version(answered, key));
            })
            .AddEndpointFilter<KnowledgeProblems>()
            .ExcludeFromDescription();
        return app;
    }

    [LoggerMessage(LogLevel.Warning, "Open WebUI's stored client secret cannot be read: Open WebUI is answered that sign-in is not configured until it is saved again in setup mode")]
    private static partial void LogSecretUnreadable(ILogger logger);
}
