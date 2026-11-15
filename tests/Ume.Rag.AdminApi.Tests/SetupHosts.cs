using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>What a host wrote as its setup code output (<see cref="SetupConsole"/>), instead of the process's standard output.</summary>
public sealed class SetupOutput
{
    private readonly StringWriter _inner = new();

    public SetupOutput() => Writer = TextWriter.Synchronized(_inner);

    public TextWriter Writer { get; }

    public string Text
    {
        get
        {
            lock (Writer)
            {
                return _inner.ToString();
            }
        }
    }
}

/// <summary>Hosts and helpers for the setup mode tests (M-20 S3, identity design §19).</summary>
internal static class SetupHosts
{
    /// <summary>The setup secret the test hosts share, as a deployment's generated one (64 hex characters).</summary>
    public static readonly string Secret = TestSecrets.RandomHex();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <paramref name="factory"/> without a deployment sign-in (so setup is not completed by the deployment), with <see cref="Secret"/>,
    /// <paramref name="settings"/> after them, and the given output, log capture and clock.
    /// </summary>
    public static WebApplicationFactory<Program> Host(WebApplicationFactory<Program> factory, SetupOutput? output = null, CapturingLoggerProvider? logs = null,
        TimeProvider? time = null, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(web =>
        {
            web.UseSetting("Oidc:Authority", "");
            web.UseSetting("Oidc:ClientSecret", "");
            web.UseSetting(SetupSecret.Key, Secret);
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureTestServices(services =>
            {
                services.AddSingleton(new SetupConsole((output ?? new SetupOutput()).Writer));
                if (logs is not null)
                {
                    services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
                }

                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });
        });

    /// <summary>
    /// Puts setup back to a known state: never completed (a first run) or completed and closed, no opening, no sessions or tests, no
    /// re-open consumed, and no wizard values (rag-admin's or Open WebUI's). A missing settings row is created as rag-migrate's seeder creates it (its actor, the
    /// defaults, no <c>SetupCompletedAt</c>), then marked completed when asked.
    /// </summary>
    public static async Task ResetAsync(string connection, bool completed)
    {
        await using var db = TestPostgres.Context(connection);
        var now = DateTimeOffset.UtcNow;
        await db.SetupSessions.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SetupCompletedAt, completed ? now : null)
            .SetProperty(x => x.SetupNonce, (string?)null)
            .SetProperty(x => x.SetupOpenedAt, (DateTimeOffset?)null)
            .SetProperty(x => x.SetupReopenConsumed, (string?)null)
            .SetProperty(x => x.IdentityOidc, "{}")
            .SetProperty(x => x.IdentityOidcEncryptedClientSecret, (string?)null)
            .SetProperty(x => x.IdentityOidcClientSecretSetAt, (DateTimeOffset?)null)
            .SetProperty(x => x.OpenWebUiOidc, "{}")
            .SetProperty(x => x.OpenWebUiOidcEncryptedClientSecret, (string?)null)
            .SetProperty(x => x.OpenWebUiOidcClientSecretSetAt, (DateTimeOffset?)null), Ct);
        if (!await db.Settings.AnyAsync(Ct))
        {
            db.Settings.Add(new PlatformSettings { PiiOwnDomains = ["umea.se"], UpdatedAt = now, UpdatedBy = "system:seed", SetupCompletedAt = completed ? now : null });
            await db.SaveChangesAsync(Ct);
        }
    }

    public static async Task<PlatformSettings?> RowAsync(string connection)
    {
        await using var db = TestPostgres.Context(connection);
        return await db.Settings.AsNoTracking().SingleOrDefaultAsync(Ct);
    }

    /// <summary>The current opening's code, from the row's nonce.</summary>
    public static async Task<string> CodeAsync(string connection) =>
        SetupCode.Compute(Secret, (await RowAsync(connection))?.SetupNonce ?? throw new InvalidOperationException("Setup is not open."));

    /// <summary>A browser-like client: https, cookies kept, no redirects followed, holding the XSRF token from <c>/bff/user</c>.</summary>
    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory, string baseAddress = "https://localhost") =>
        await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false }).WithXsrfTokenAsync();

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) => client.PostAsJsonAsync(path, body, Ct);

    /// <summary>A client with a setup session for the current opening's code.</summary>
    public static async Task<HttpClient> SessionAsync(WebApplicationFactory<Program> factory, string connection, string baseAddress = "https://localhost")
    {
        var client = await ClientAsync(factory, baseAddress);
        using var response = await PostAsync(client, "/bff/setup/session", new { code = await CodeAsync(connection) });
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return client;
    }

    /// <summary>The security events of <paramref name="logs"/> with <paramref name="id"/>.</summary>
    public static IReadOnlyList<LogEntry> Events(CapturingLoggerProvider logs, int id) =>
        [.. logs.Entries.Where(e => e.Category == Ume.Rag.Infrastructure.Security.SecurityEvents.Category && e.EventId.Id == id)];

    /// <summary>Every form of <paramref name="code"/> that could leak: grouped, plain, lower case.</summary>
    public static IEnumerable<string> Forms(string code) => [code, code.Replace("-", "", StringComparison.Ordinal), code.ToLowerInvariant()];
}
