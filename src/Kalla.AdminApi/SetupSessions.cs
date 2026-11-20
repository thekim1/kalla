using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Persistence;

namespace Kalla.AdminApi;

/// <summary>What a setup code gave (<see cref="SetupSessions.StartAsync"/>).</summary>
public enum SetupCodeOutcome
{
    /// <summary>The code was right: a session started.</summary>
    Started = 0,

    /// <summary>A wrong code. It voids nothing.</summary>
    Wrong = 1,

    /// <summary>The opening ended or changed meanwhile.</summary>
    Closed = 2,
}

/// <summary>The outcome of a code and, when a session started, the session and its cookie token.</summary>
public sealed record SetupCodeResult(SetupCodeOutcome Outcome, SetupSession? Session = null, string? Token = null);

/// <summary>
/// Setup sessions (identity design §19, "Setup API"): a correct setup code gives a random token in an <c>HttpOnly</c>,
/// <c>Secure</c>, <c>SameSite=Strict</c> cookie scoped to <c>/bff/setup</c>, valid 30 minutes and only within the opening it was
/// given in; the database keeps its SHA-256. Wrong codes write nothing (<see cref="SetupCodeThrottle"/> limits the checks); a wrong
/// code never voids the code. The session row is written under the config version lock, which every opening is written under too,
/// so it is created only while its opening is still the current one.
/// </summary>
public sealed class SetupSessions(RagDbContext db, TimeProvider time, SetupSecret secret, ConfigLockMetrics lockMetrics)
{
    /// <summary><c>__Secure-</c>, not <c>__Host-</c>: the cookie's path is <c>/bff/setup</c>, and <c>__Host-</c> requires <c>/</c>.</summary>
    public const string CookieName = "__Secure-ume-rag-setup";

    public const string CookiePath = "/bff/setup";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Checks <paramref name="code"/> against the opening of <paramref name="status"/> and starts a session when it is right.</summary>
    public async Task<SetupCodeResult> StartAsync(SetupStatus status, string? code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Nonce is not { } nonce || secret.Current is not { } key)
        {
            return new SetupCodeResult(SetupCodeOutcome.Closed);
        }

        if (!SetupCode.Matches(code, SetupCode.Compute(key, nonce)))
        {
            return new SetupCodeResult(SetupCodeOutcome.Wrong);
        }

        return await db.ExecuteConfigWriteAsync(async (_, attempt) =>
        {
            var now = time.GetUtcNow();
            var runningSince = now - SetupMode.OpeningLifetime;
            if (!await db.Settings.AnyAsync(s => s.SetupNonce == nonce && s.SetupOpenedAt > runningSince, attempt))
            {
                return new SetupCodeResult(SetupCodeOutcome.Closed);
            }

            var token = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
            var session = new SetupSession
            {
                Id = Guid.NewGuid(), TokenHash = Hash(token), OpeningNonce = nonce, CreatedAt = now, ExpiresAt = now + Lifetime,
            };
            db.SetupSessions.Add(session);
            await db.SaveChangesAsync(attempt);
            // Sessions that ran out are of no use to anyone; they go with the next one.
            await db.SetupSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(attempt);
            return new SetupCodeResult(SetupCodeOutcome.Started, session, token);
        }, ct, lockMetrics);
    }

    /// <summary>The caller's session: its cookie's token names a session of the current opening that has not expired.</summary>
    public async Task<SetupSession?> FindAsync(HttpContext http, SetupStatus status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(status);
        if (status.Nonce is not { } nonce || !http.Request.Cookies.TryGetValue(CookieName, out var token) || token.Length is 0 or > 100)
        {
            return null;
        }

        var hash = Hash(token);
        var now = time.GetUtcNow();
        return await db.SetupSessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash && s.OpeningNonce == nonce && s.ExpiresAt > now, ct);
    }

    /// <summary>Sets the session cookie: <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c>, path <c>/bff/setup</c>, until the session expires.</summary>
    public static void SetCookie(HttpContext http, string token, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(CookieName, token, Options(expiresAt));
    }

    public static void DeleteCookie(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(CookieName, Options(null));
    }

    /// <summary>The id events and the audit entry name a session by (<c>setup-</c> and its row id), never its token.</summary>
    public static string EventId(SetupSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return "setup-" + session.Id.ToString("N");
    }

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = CookiePath, Expires = expiresAt, IsEssential = true,
    };
}

/// <summary>
/// Before the antiforgery check: every request under <c>/bff/setup</c> answers 404 while setup is closed (identity design §19),
/// whatever its method, body or cookie. While it is open the state read here is kept for the endpoint (<see cref="StatusOf"/>).
/// </summary>
public sealed class SetupGateMiddleware(RequestDelegate next)
{
    private const string StatusKey = "kalla.setupStatus";

    public async Task InvokeAsync(HttpContext http, SetupMode setup, SetupCleanup cleanup)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(cleanup);
        if (http.Request.Path.StartsWithSegments(SetupSessions.CookiePath, StringComparison.OrdinalIgnoreCase))
        {
            var status = await setup.ReadAsync(http.RequestAborted);
            if (!status.Open)
            {
                // An ended opening's sessions and typed secrets go, once per process and opening.
                await cleanup.AfterClosedAsync(status, setup, http.RequestAborted);
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Items[StatusKey] = status;
        }

        await next(http);
    }

    /// <summary>The state the gate read for this request.</summary>
    public static SetupStatus StatusOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[StatusKey] as SetupStatus ?? throw new InvalidOperationException("The setup gate did not run for this request.");
    }
}
