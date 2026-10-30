using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Who acted, from where and in which session, as written to audit entries and admin security events (adapted from the
/// gateway's <c>AdminActor</c>). The subject is the <c>oid</c>, else <c>sub</c>, else the name (identity design §1.1).
/// Never the cookie: the session is a random id given at sign-in (<see cref="AdminClaims.Session"/>).
/// </summary>
public sealed record AdminActor(
    string Subject, string? Name, string Roles, string Departments, string? SourceAddress, string? SessionId, string RequestId, string? UserAgent)
{
    private const int MaxUserAgent = 200;

    public static AdminActor Of(HttpContext http) => Of(http, http?.User ?? throw new ArgumentNullException(nameof(http)));

    /// <summary>For <paramref name="user"/> while it is being signed in (<see cref="HttpContext.User"/> is not set yet).</summary>
    public static AdminActor Of(HttpContext http, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var agent = http.Request.Headers.UserAgent.ToString();
        return new AdminActor(
            SubjectOf(user),
            Printable(user.Identity?.Name ?? user.FindFirstValue("email")),
            string.Join(',', user.FindAll(AdminClaims.Roles).Select(c => c.Value).Order(StringComparer.Ordinal)),
            string.Join(',', user.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).Order(StringComparer.Ordinal)),
            AddressOf(http),
            user.FindFirstValue(AdminClaims.Session),
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            agent.Length == 0 ? null : Printable(agent.Length > MaxUserAgent ? agent[..MaxUserAgent] : agent));
    }

    /// <summary>The audit actor of <paramref name="user"/>: <c>oid</c>, else <c>sub</c>, else the name.</summary>
    public static string SubjectOf(ClaimsPrincipal user) =>
        user?.FindFirstValue(AdminClaims.ObjectId) ?? user?.FindFirstValue(AdminClaims.Subject) ?? user?.Identity?.Name ?? "unknown";

    /// <summary>Sets the fields of <paramref name="entry"/> that say who made it.</summary>
    public void Stamp(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Actor = Subject;
        entry.ActorName = Name;
        entry.ActorRoles = Roles;
        entry.ActorDepartments = Departments;
        entry.SourceAddress = SourceAddress;
        entry.SessionId = SessionId;
        entry.RequestId = RequestId;
        entry.UserAgent = UserAgent;
    }

    /// <summary>
    /// The connection's address. Behind the reverse proxy this is the proxy until the gateway's trusted-proxy resolution
    /// (<c>AdminClientAddresses</c>) is copied; it reads trusted proxies from <c>Settings</c>, which arrives in P1.
    /// </summary>
    public static string? AddressOf(HttpContext http) => http?.Connection.RemoteIpAddress?.ToString();

    /// <summary>A new session id: 16 random bytes, hex.</summary>
    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Client-supplied text with control characters replaced, so it cannot start a forged log line.</summary>
    public static string? Printable(string? value) =>
        value is null || !value.Any(char.IsControl) ? value : string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
}

/// <summary>
/// Copied from the gateway (without the trusted-proxy address resolution, see <see cref="AdminActor.AddressOf"/>): reports
/// what security teams need to see that the audit log does not hold: refused requests (6006, at most one event per user
/// and minute with the count since the last) and reads of sensitive lists (6007).
/// </summary>
public sealed class AdminActivityMiddleware(RequestDelegate next, AccessDeniedThrottle denied, ILoggerFactory loggers)
{
    /// <summary>The audit log; keys, feedback, the access log and conversation views join as they arrive (identity design §10).</summary>
    private static readonly PathString[] SensitiveReads = ["/api/audit"];

    private const int MaxQuery = 500;
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        await next(http);

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (http.Response.StatusCode == StatusCodes.Status403Forbidden && denied.ShouldLog(AdminActor.SubjectOf(http.User), out var count))
        {
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminAccessDenied(_security, actor.Subject, http.Request.Method, AdminActor.Printable(http.Request.Path.Value) ?? "",
                count, actor.SourceAddress, actor.Roles);
        }
        else if (http.Response.StatusCode == StatusCodes.Status200OK && HttpMethods.IsGet(http.Request.Method) &&
                 SensitiveReads.Any(p => http.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            var query = http.Request.QueryString.Value ?? "";
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminDataRead(_security, actor.Subject, AdminActor.Printable(http.Request.Path.Value) ?? "",
                AdminActor.Printable(query.Length > MaxQuery ? query[..MaxQuery] : query) ?? "", actor.SourceAddress);
        }
    }
}

/// <summary>
/// One 6006 event per user and minute: the first refusal is logged at once, later ones in the same minute are counted into
/// the next event. Bounded, so a flood of users cannot grow it without limit. Copied.
/// </summary>
public sealed class AccessDeniedThrottle(TimeProvider time)
{
    private const int MaxUsers = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Entry> _users = new(StringComparer.Ordinal);

    /// <summary>Whether to log this refusal now; <paramref name="count"/> is the refusals it stands for.</summary>
    public bool ShouldLog(string subject, out int count)
    {
        var now = time.GetUtcNow();
        if (_users.Count >= MaxUsers)
        {
            _users.Clear();
        }

        var entry = _users.GetOrAdd(subject, _ => new Entry());
        lock (entry)
        {
            entry.Pending++;
            if (now - entry.LoggedAt < Window)
            {
                count = 0;
                return false;
            }

            count = entry.Pending;
            entry.Pending = 0;
            entry.LoggedAt = now;
            return true;
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset LoggedAt { get; set; } = DateTimeOffset.MinValue;
        public int Pending { get; set; }
    }
}
