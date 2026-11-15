using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.ServiceDefaults;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The setup API (identity design §19, "Setup API"), anonymous, under <c>/bff/setup</c>: 404 while setup is closed
/// (<see cref="SetupGateMiddleware"/>), the BFF's antiforgery on every POST, the per-address rate limit. <c>POST session</c> takes the
/// setup code; <c>GET state</c>, <c>POST test</c> and <c>POST complete</c> need the setup session it gives. Only <c>complete</c> writes
/// <c>Settings.Identity.Oidc</c>; no route of an admin session does.
/// </summary>
public static class SetupModeEndpoints
{
    public const string SignInUrl = AdminAuthentication.LoginPath;

    private const int WrongCodeEvent = 6009;

    public static void MapSetupMode(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var setup = app.MapGroup(SetupSessions.CookiePath).AllowAnonymous().RequireRateLimiting(AdminRateLimits.PerUser)
            .AddEndpointFilter<AdminValidationFilter>().ExcludeFromDescription();

        setup.MapPost("/session", async Task<IResult> (SetupCodeRequest request, HttpContext http, SetupSessions sessions, SetupCodeThrottle throttle,
            SetupEventSampler sampler, ILoggerFactory loggers, IOptions<ApiExceptionOptions> errors, CancellationToken ct) =>
        {
            var status = SetupGateMiddleware.StatusOf(http);
            var security = SecurityEvents.CreateLogger(loggers);
            var address = AdminActor.AddressOf(http);
            if (!throttle.TryAcquire(out var retryAfter))
            {
                return TooManyAttempts(http, retryAfter, errors.Value);
            }

            var result = await sessions.StartAsync(status, request.Code, ct);
            switch (result.Outcome)
            {
                case SetupCodeOutcome.Started:
                    SetupSessions.SetCookie(http, result.Token!, result.Session!.ExpiresAt);
                    SecurityEvents.SetupSessionStarted(security, SetupSessions.EventId(result.Session), address, result.Session.ExpiresAt);
                    return Results.Ok(new SetupSessionDto(result.Session.ExpiresAt, status.EndsAt!.Value));
                case SetupCodeOutcome.Wrong:
                    if (sampler.ShouldLog(WrongCodeEvent, out var count))
                    {
                        SecurityEvents.SetupCodeRefused(security, address, count);
                    }

                    throw AdminFaults.Of(AdminErrorCodes.SetupCodeInvalid);
                default:
                    return Results.NotFound();
            }
        });

        var session = setup.MapGroup("").AddEndpointFilter<SetupSessionFilter>();

        session.MapGet("/state", async (HttpContext http, SignInSettings signIn, SetupTestSignIn tests, IOptionsMonitor<OpenIdConnectOptions> oidc,
            IOptions<OidcOptions> deployment, IOptions<AdminOptions> admin, IConfiguration configuration, TimeProvider time,
            Ume.Rag.Infrastructure.Persistence.RagDbContext db, CancellationToken ct) =>
        {
            var status = SetupGateMiddleware.StatusOf(http);
            var current = SetupSessionFilter.SessionOf(http);
            var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var latest = await db.SetupTests.AsNoTracking().Where(t => t.SessionId == current.Id && t.Target == SetupTargets.RagAdmin)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).FirstOrDefaultAsync(ct);
            string[] platformAdminGroups = deployment.Value.RoleGroups.TryGetValue(RagRoles.PlatformAdmin, out var groups)
                ? groups.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
            return new SetupStateDto(
                status.Kind, status.EndsAt!.Value, current.ExpiresAt, SignInSettingsDto.Of(await signIn.ReadAsync(ct)),
                new SetupRedirectUris(SetupTestSignIn.RedirectUri(http, options.CallbackPath), SetupTestSignIn.RedirectUri(http, options.SignedOutCallbackPath)),
                new SetupDeploymentFacts(Blank(configuration["Gateway:BaseUrl"]), Blank(admin.Value.ApiPublicUrl), platformAdminGroups),
                latest is null ? null : SetupTestDto.Of(latest, tests.PayloadOf(latest), time.GetUtcNow()));
        });

        session.MapPost("/test", async (SetupValuesRequest request, HttpContext http, SetupTestSignIn tests, CancellationToken ct) =>
            await tests.StartAsync(http, SetupSessionFilter.SessionOf(http), request, ct));

        session.MapPost("/complete", async (SetupCompleteRequest request, HttpContext http, SetupTestSignIn tests, SignInSettings signIn, AdminContext admin,
            IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var current = SetupSessionFilter.SessionOf(http);
            var values = request.ToWizardValues().Normalized();
            var test = await admin.Db.SetupTests.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == request.TestId && t.SessionId == current.Id && t.Target == SetupTargets.RagAdmin, ct);
            var latestId = await admin.Db.SetupTests.AsNoTracking().Where(t => t.SessionId == current.Id && t.Target == SetupTargets.RagAdmin)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Select(t => t.Id).FirstOrDefaultAsync(ct);
            var payload = test is null ? null : tests.PayloadOf(test);
            var result = test?.Result is { } json ? JsonSerializer.Deserialize<SetupTestResult>(json, SetupTestSignIn.Json) : null;
            var refusal = test is null || payload is null ? "unknown_test"
                : latestId != test.Id ? "not_latest"
                : test.Passed != true || result is null ? "not_passed"
                : !result.IsPlatformAdmin ? "not_platform_admin"
                : !CryptographicOperations.FixedTimeEquals(test.ValuesFingerprint, SetupTestSignIn.Fingerprint(test.Target, values)) ? "values_changed"
                : null;
            if (refusal is not null)
            {
                // A complete that lost the race with another one finds its session and tests deleted: setup has closed, which is
                // a 404 like every other request after it, not a missing test.
                if (!(await http.RequestServices.GetRequiredService<SetupMode>().ReadAsync(ct)).Open)
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                throw AdminFaults.Of(AdminErrorCodes.SetupTestRequired, new Dictionary<string, string> { ["reason"] = refusal });
            }

            // The audit actor is the tested person, explicitly (identity design §19); the secret comes from the test, typed once.
            var actor = AdminActor.ForSetup(http, result!.Subject, result.Name, result.Roles, result.DepartmentCodes, SetupSessions.EventId(current));
            var clientSecret = payload!.SecretSource switch
            {
                SetupSecretSources.Typed => payload.Secret,
                SetupSecretSources.Removed => "",
                _ => null,
            };
            var now = admin.Now;
            await signIn.SaveWizardValuesAsync(admin, actor, SignInSettings.WithoutDeployed(configuration, values), clientSecret,
                "Setup mode: saved after this person's passed test sign-in (identity design §19)", async (row, attempt) =>
                {
                    // Under the config lock: a second complete of the same opening finds it closed.
                    if (row.SetupNonce is null || row.SetupNonce != current.OpeningNonce)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    }

                    row.SetupCompletedAt = now;
                    row.SetupNonce = null;
                    await admin.Db.SetupSessions.ExecuteDeleteAsync(attempt);
                }, ct);
            SetupSessions.DeleteCookie(http);
            SecurityEvents.SetupCompleted(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SessionId, actor.SourceAddress);
            return new SetupCompletedDto(SignInUrl);
        });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IResult TooManyAttempts(HttpContext http, TimeSpan retryAfter, ApiExceptionOptions errors)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var code = AdminErrorCodes.SetupAttemptsExhausted;
        return Results.Problem(statusCode: code.Status, title: errors.Title, detail: code.Message, extensions: new Dictionary<string, object?> { ["code"] = code.Code });
    }
}

/// <summary>Requires the setup session (<see cref="SetupSessions.FindAsync"/>) and keeps it for the endpoint; else <c>setup_session_required</c>.</summary>
public sealed class SetupSessionFilter : IEndpointFilter
{
    private const string SessionKey = "ume.setupSession";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var session = await http.RequestServices.GetRequiredService<SetupSessions>().FindAsync(http, SetupGateMiddleware.StatusOf(http), http.RequestAborted);
        if (session is null)
        {
            // Setup may have closed since the gate read it (a completion just now): then it is 404, as for any closed setup.
            return (await http.RequestServices.GetRequiredService<SetupMode>().ReadAsync(http.RequestAborted)).Open
                ? throw AdminFaults.Of(AdminErrorCodes.SetupSessionRequired)
                : Results.NotFound();
        }

        http.Items[SessionKey] = session;
        return await next(context);
    }

    public static SetupSession SessionOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[SessionKey] as SetupSession ?? throw new InvalidOperationException("The setup session filter did not run for this request.");
    }
}

/// <summary><c>POST /bff/setup/session</c>.</summary>
public sealed record SetupCodeRequest : AdminRequest
{
    [Required, MaxLength(SetupCode.MaxInputLength)]
    public string? Code { get; init; }
}

/// <summary>
/// Candidate sign-in values (<see cref="OidcWizardValues"/>'s members) for <c>POST /bff/setup/test</c>. <see cref="ClientSecret"/>:
/// null tests with the wizard's stored secret, empty tests without one, a value tests with it; a deployment secret wins over all.
/// </summary>
public record SetupValuesRequest : AdminRequest, IValidatableObject
{
    [MaxLength(500)]
    public string? Authority { get; init; }

    [MaxLength(200)]
    public string? ClientId { get; init; }

    [MaxLength(500)]
    public string? ClientSecret { get; init; }

    [MaxLength(100)]
    public string? RoleClaim { get; init; }

    [MaxLength(100)]
    public string? DepartmentClaim { get; init; }

    [MaxLength(100)]
    public string? TeamClaim { get; init; }

    [MaxLength(100)]
    public string? GroupClaim { get; init; }

    [MaxLength(20)]
    public IReadOnlyList<string>? ExtraScopes { get; init; }

    [MaxLength(100)]
    public string? TenantId { get; init; }

    public bool? RefuseGuests { get; init; }

    public DateOnly? ClientSecretExpiresOn { get; init; }

    public OidcWizardValues ToWizardValues() => new()
    {
        Authority = Authority, ClientId = ClientId, RoleClaim = RoleClaim, DepartmentClaim = DepartmentClaim, TeamClaim = TeamClaim, GroupClaim = GroupClaim,
        ExtraScopes = ExtraScopes, TenantId = TenantId, RefuseGuests = RefuseGuests, ClientSecretExpiresOn = ClientSecretExpiresOn,
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        (string Name, string? Value)[] texts =
        [
            (nameof(Authority), Authority), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret), (nameof(RoleClaim), RoleClaim),
            (nameof(DepartmentClaim), DepartmentClaim), (nameof(TeamClaim), TeamClaim), (nameof(GroupClaim), GroupClaim), (nameof(TenantId), TenantId),
        ];
        foreach (var (name, value) in texts.Where(t => t.Value?.Any(char.IsControl) == true))
        {
            yield return new ValidationResult("The value may not hold control characters.", [name]);
        }

        if (ExtraScopes?.Any(s => s is null || s.Length > 100 || s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) == true)
        {
            yield return new ValidationResult("Each scope is one word of at most 100 characters.", [nameof(ExtraScopes)]);
        }
    }
}

/// <summary><c>POST /bff/setup/complete</c>: the tested values again (without the secret) and the test's id.</summary>
public sealed record SetupCompleteRequest : SetupValuesRequest
{
    public Guid TestId { get; init; }
}

/// <summary>A started setup session.</summary>
public sealed record SetupSessionDto(DateTimeOffset ExpiresAt, DateTimeOffset SetupEndsAt);

/// <summary>Where setup sends the person after completion: the normal sign-in.</summary>
public sealed record SetupCompletedDto(string SignInUrl);

/// <summary>The redirect URIs to register at the provider, as the OpenID Connect handler builds them for this request's address.</summary>
public sealed record SetupRedirectUris(string SignIn, string SignOutCallback);

/// <summary>What rag-admin's own configuration says of the deployment (admin design §5.11 step 5, as far as rag-admin knows it).</summary>
public sealed record SetupDeploymentFacts(string? GatewayBaseUrl, string? ApiPublicUrl, IReadOnlyList<string> PlatformAdminGroups);

/// <summary><c>GET /bff/setup/state</c>.</summary>
public sealed record SetupStateDto(
    string Kind, DateTimeOffset EndsAt, DateTimeOffset SessionExpiresAt, SignInSettingsDto SignIn, SetupRedirectUris RedirectUris, SetupDeploymentFacts Deployment,
    SetupTestDto? LatestTest);

/// <summary>
/// A test as the wizard shows it: <c>pending</c> (waiting for the callback), <c>passed</c>, <c>failed</c> (with its reason) or
/// <c>incomplete</c> (expired without a callback, or interrupted); the tested values without the secret, and where the secret came from.
/// </summary>
public sealed record SetupTestDto(
    Guid TestId, string Target, string Status, string? Reason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, OidcWizardValues? Values, string? ClientSecret,
    SetupTestResult? Result)
{
    public static SetupTestDto Of(SetupTest test, SetupTestPayload? payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(test);
        var status = test.Passed switch
        {
            true => "passed",
            false => "failed",
            null => test.CallbackAt is null && test.ExpiresAt > now ? "pending" : "incomplete",
        };
        return new SetupTestDto(test.Id, test.Target, status, test.Reason, test.CreatedAt, test.ExpiresAt, payload?.Values, payload?.SecretSource,
            test.Result is { } json ? JsonSerializer.Deserialize<SetupTestResult>(json, SetupTestSignIn.Json) : null);
    }
}
