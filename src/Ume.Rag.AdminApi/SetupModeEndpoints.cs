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
/// setup code; <c>GET state</c>, <c>POST test</c> and <c>POST complete</c> need the setup session it gives. <c>test</c> and
/// <c>complete</c> take a <c>target</c>: <c>rag-admin</c> (the default) or <c>open-webui</c>. Only <c>complete</c> writes
/// <c>Settings.Identity.Oidc</c> and <c>Settings.OpenWebUi.Oidc</c>; no route of an admin session does (the provider's display name
/// excepted, <c>PUT /api/settings/openwebui</c>).
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
            // The code is compared first: the right one always gives a session, so a flood of wrong codes cannot keep the
            // deployer out. Only wrong codes count towards the throttle.
            var result = await sessions.StartAsync(status, request.Code, ct);
            switch (result.Outcome)
            {
                case SetupCodeOutcome.Started:
                    SetupSessions.SetCookie(http, result.Token!, result.Session!.ExpiresAt);
                    SecurityEvents.SetupSessionStarted(security, SetupSessions.EventId(result.Session), address, result.Session.ExpiresAt);
                    return Results.Ok(new SetupSessionDto(result.Session.ExpiresAt, status.EndsAt!.Value));
                case SetupCodeOutcome.Wrong:
                    if (!throttle.TryAcquire(out var retryAfter))
                    {
                        return TooManyAttempts(http, retryAfter, errors.Value);
                    }

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

        session.MapGet("/state", async (HttpContext http, SignInSettings signIn, OpenWebUiSignInSettings openWebUi, SetupTestSignIn tests,
            SetupOpenWebUiTest openWebUiTests, IOptionsMonitor<OpenIdConnectOptions> oidc, IOptions<OidcOptions> deployment, IOptions<AdminOptions> admin,
            IConfiguration configuration, TimeProvider time, Ume.Rag.Infrastructure.Persistence.RagDbContext db, CancellationToken ct) =>
        {
            var status = SetupGateMiddleware.StatusOf(http);
            var current = SetupSessionFilter.SessionOf(http);
            var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var latest = await LatestTestAsync(db, current, SetupTargets.RagAdmin, ct);
            var latestOpenWebUi = await LatestTestAsync(db, current, SetupTargets.OpenWebUi, ct);
            string[] platformAdminGroups = deployment.Value.RoleGroups.TryGetValue(RagRoles.PlatformAdmin, out var groups)
                ? groups.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
            var shown = await openWebUi.ShowAsync(await openWebUiTests.SessionAuthorityAsync(current, ct), ct);
            return new SetupStateDto(
                status.Kind, status.EndsAt!.Value, current.ExpiresAt, SignInSettingsDto.Of(await signIn.ReadAsync(ct)),
                new SetupRedirectUris(SetupTestSignIn.RedirectUri(http, options.CallbackPath), SetupTestSignIn.RedirectUri(http, options.SignedOutCallbackPath)),
                new SetupDeploymentFacts(Blank(configuration["Gateway:BaseUrl"]), Blank(admin.Value.ApiPublicUrl), platformAdminGroups),
                latest is null ? null : SetupTestDto.Of(latest, tests.PayloadOf(latest), time.GetUtcNow()),
                new SetupOpenWebUiState(shown.Enabled, shown.SignIn, shown.RedirectUri,
                    latestOpenWebUi is null ? null : OpenWebUiTestDto.Of(latestOpenWebUi, openWebUiTests.PayloadOf(latestOpenWebUi))));
        });

        session.MapPost("/test", async Task<object> (SetupValuesRequest request, HttpContext http, IServiceProvider services, CancellationToken ct) =>
            request.TargetOrDefault == SetupTargets.OpenWebUi
                ? await services.GetRequiredService<SetupOpenWebUiTest>().RunAsync(http, SetupSessionFilter.SessionOf(http), request, ct)
                : await services.GetRequiredService<SetupTestSignIn>().StartAsync(http, SetupSessionFilter.SessionOf(http), request, ct));

        session.MapPost("/complete", async Task<object> (SetupCompleteRequest request, HttpContext http, SetupTestSignIn tests, SignInSettings signIn, AdminContext admin,
            IConfiguration configuration, ILoggerFactory loggers, IServiceProvider services, CancellationToken ct) =>
        {
            var current = SetupSessionFilter.SessionOf(http);
            // Open WebUI's values are saved by a session whose latest rag-admin test passed as a platform admin; that person is the actor.
            var (adminTest, result) = await AdminTestAsync(admin.Db, current, ct);
            if (request.TargetOrDefault == SetupTargets.OpenWebUi)
            {
                return await CompleteOpenWebUiAsync(request, http, adminTest is not null && result?.IsPlatformAdmin == true && adminTest.Passed == true ? result : null,
                    services.GetRequiredService<SetupOpenWebUiTest>(), services.GetRequiredService<OpenWebUiSignInSettings>(), admin, configuration, loggers, ct);
            }

            var values = request.ToWizardValues().Normalized();
            var test = await admin.Db.SetupTests.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == request.TestId && t.SessionId == current.Id && t.Target == SetupTargets.RagAdmin, ct);
            var payload = test is null ? null : tests.PayloadOf(test);
            var refusal = test is null || payload is null ? "unknown_test"
                : adminTest?.Id != test.Id ? "not_latest"
                : test.Passed != true || result is null ? "not_passed"
                : !result.IsPlatformAdmin ? "not_platform_admin"
                : !CryptographicOperations.FixedTimeEquals(test.ValuesFingerprint, SetupTestSignIn.Fingerprint(test.Target, values)) ? "values_changed"
                // What is about to be in force must be what the test ran against: the deployment's values are read again here,
                // and they may differ from the instance and moment the test started on (a rolling change of Oidc:Authority).
                : !TestedProviderIsInForce(configuration, payload, values) ? "values_changed"
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
            var clientSecret = StoredSecretOf(payload!.SecretSource, payload.Secret);
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

    /// <summary>
    /// <c>complete</c> for Open WebUI: needs <paramref name="admin"/>, the person of the session's latest rag-admin test that passed as
    /// a platform admin (else <c>admin_test_required</c>), and the session's latest Open WebUI test, passed, with exactly the submitted
    /// values. Saves <c>Settings.OpenWebUi.Oidc.*</c> and the typed secret (what the deployment sets is not stored) and leaves setup
    /// open: only rag-admin's <c>complete</c> closes it.
    /// </summary>
    private static async Task<SetupOpenWebUiSavedDto> CompleteOpenWebUiAsync(SetupCompleteRequest request, HttpContext http, SetupTestResult? adminPerson,
        SetupOpenWebUiTest tests, OpenWebUiSignInSettings settings, AdminContext admin, IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct)
    {
        var current = SetupSessionFilter.SessionOf(http);
        var values = request.ToOpenWebUiValues().Normalized();
        var test = await admin.Db.SetupTests.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == request.TestId && t.SessionId == current.Id && t.Target == SetupTargets.OpenWebUi, ct);
        var latest = await LatestTestAsync(admin.Db, current, SetupTargets.OpenWebUi, ct);
        var payload = test is null ? null : tests.PayloadOf(test);
        var refusal = adminPerson is null ? "admin_test_required"
            : test is null || payload is null ? "unknown_test"
            : latest?.Id != test.Id ? "not_latest"
            : test.Passed != true ? "not_passed"
            : !CryptographicOperations.FixedTimeEquals(test.ValuesFingerprint, SetupOpenWebUiTest.Fingerprint(values)) ? "values_changed"
            : null;
        if (refusal is not null)
        {
            throw AdminFaults.Of(AdminErrorCodes.SetupTestRequired, new Dictionary<string, string> { ["reason"] = refusal });
        }

        var actor = AdminActor.ForSetup(http, adminPerson!.Subject, adminPerson.Name, adminPerson.Roles, adminPerson.DepartmentCodes, SetupSessions.EventId(current));
        await settings.SaveWizardValuesAsync(admin, actor, OpenWebUiSignIn.WithoutDeployed(configuration, values), StoredSecretOf(payload!.SecretSource, payload.Secret),
            "Setup mode: Open WebUI's sign-in values, saved after their test by the person of this session's passed test sign-in (identity design §19)",
            (row, _) => row.SetupNonce is not null && row.SetupNonce == current.OpeningNonce
                // Under the config lock: an opening that ended meanwhile saves nothing.
                ? Task.CompletedTask
                : throw AdminFaults.Of(AdminErrorCodes.NotFound), ct);
        var result = JsonSerializer.Deserialize<OpenWebUiTestResult>(test!.Result!, SetupTestSignIn.Json)!;
        var verified = result.ClientAccepted == true;
        SecurityEvents.SetupOpenWebUiSaved(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SessionId, actor.SourceAddress, verified);
        return new SetupOpenWebUiSavedDto(await settings.ShowAsync(await tests.SessionAuthorityAsync(current, ct), ct), verified);
    }

    /// <summary>The session's latest rag-admin test and its person (null while it has none or it did not finish).</summary>
    private static async Task<(SetupTest? Test, SetupTestResult? Result)> AdminTestAsync(Ume.Rag.Infrastructure.Persistence.RagDbContext db, SetupSession session,
        CancellationToken ct)
    {
        var test = await LatestTestAsync(db, session, SetupTargets.RagAdmin, ct);
        return (test, test?.Result is { } json ? JsonSerializer.Deserialize<SetupTestResult>(json, SetupTestSignIn.Json) : null);
    }

    private static Task<SetupTest?> LatestTestAsync(Ume.Rag.Infrastructure.Persistence.RagDbContext db, SetupSession session, string target, CancellationToken ct) =>
        db.SetupTests.AsNoTracking().Where(t => t.SessionId == session.Id && t.Target == target)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).FirstOrDefaultAsync(ct);

    /// <summary>What <c>complete</c> stores of a test's secret: the typed one, an empty value (remove) for a test without one, else nothing (keep).</summary>
    private static string? StoredSecretOf(string secretSource, string? secret) => secretSource switch
    {
        SetupSecretSources.Typed => secret,
        SetupSecretSources.Removed => "",
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Whether the authority and client the test signed in with are the ones <paramref name="values"/> give with this instance's deployment values.</summary>
    private static bool TestedProviderIsInForce(IConfiguration configuration, SetupTestPayload payload, OidcWizardValues values)
    {
        var effective = SignInSettings.Resolve(configuration, values, () => StoredSecretValue.NotSet, null, 0, null);
        return string.Equals(effective.Authority.Value?.Trim(), payload.Authority.Trim(), StringComparison.Ordinal) &&
               string.Equals(effective.ClientId.Value.Trim(), payload.ClientId.Trim(), StringComparison.Ordinal);
    }

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
/// Candidate sign-in values for <c>POST /bff/setup/test</c> of a <see cref="Target"/>: <c>rag-admin</c> (the default) takes
/// <see cref="OidcWizardValues"/>'s members, <c>open-webui</c> <see cref="ProviderUrl"/>, <see cref="ClientId"/>,
/// <see cref="ClientSecret"/>, <see cref="ProviderName"/> and <see cref="ClientSecretExpiresOn"/>; a member of the other target is
/// refused. <see cref="ClientSecret"/>: null tests with the wizard's stored secret, empty tests without one, a value tests with it; a
/// deployment secret wins over all.
/// </summary>
public record SetupValuesRequest : AdminRequest, IValidatableObject
{
    /// <summary>A <see cref="SetupTargets"/> value; null is <c>rag-admin</c>.</summary>
    [MaxLength(20)]
    public string? Target { get; init; }

    /// <summary>Open WebUI's provider address: the discovery document (<c>OPENID_PROVIDER_URL</c>).</summary>
    [MaxLength(500)]
    public string? ProviderUrl { get; init; }

    /// <summary>Open WebUI's sign-in button label.</summary>
    [MaxLength(100)]
    public string? ProviderName { get; init; }

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

    /// <summary>The target, <c>rag-admin</c> when none is given.</summary>
    public string TargetOrDefault => Target ?? SetupTargets.RagAdmin;

    public OidcWizardValues ToWizardValues() => new()
    {
        Authority = Authority, ClientId = ClientId, RoleClaim = RoleClaim, DepartmentClaim = DepartmentClaim, TeamClaim = TeamClaim, GroupClaim = GroupClaim,
        ExtraScopes = ExtraScopes, TenantId = TenantId, RefuseGuests = RefuseGuests, ClientSecretExpiresOn = ClientSecretExpiresOn,
    };

    public OpenWebUiOidcValues ToOpenWebUiValues() => new()
    {
        ProviderUrl = ProviderUrl, ClientId = ClientId, ProviderName = ProviderName, ClientSecretExpiresOn = ClientSecretExpiresOn,
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        (string Name, string? Value)[] texts =
        [
            (nameof(Authority), Authority), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret), (nameof(RoleClaim), RoleClaim),
            (nameof(DepartmentClaim), DepartmentClaim), (nameof(TeamClaim), TeamClaim), (nameof(GroupClaim), GroupClaim), (nameof(TenantId), TenantId),
            (nameof(ProviderUrl), ProviderUrl), (nameof(ProviderName), ProviderName),
        ];
        foreach (var (name, value) in texts.Where(t => t.Value?.Any(char.IsControl) == true))
        {
            yield return new ValidationResult("The value may not hold control characters.", [name]);
        }

        if (!SetupTargets.All.Contains(TargetOrDefault, StringComparer.Ordinal))
        {
            yield return new ValidationResult($"The target is one of {string.Join(", ", SetupTargets.All)}.", [nameof(Target)]);
        }

        // Each target takes its own members only, so a value never lands in the other target's settings unnoticed.
        (string Name, bool Set)[] otherTarget = TargetOrDefault == SetupTargets.OpenWebUi
            ? [(nameof(Authority), Authority is not null), (nameof(RoleClaim), RoleClaim is not null), (nameof(DepartmentClaim), DepartmentClaim is not null),
                (nameof(TeamClaim), TeamClaim is not null), (nameof(GroupClaim), GroupClaim is not null), (nameof(ExtraScopes), ExtraScopes is not null),
                (nameof(TenantId), TenantId is not null), (nameof(RefuseGuests), RefuseGuests is not null)]
            : [(nameof(ProviderUrl), ProviderUrl is not null), (nameof(ProviderName), ProviderName is not null)];
        foreach (var (name, _) in otherTarget.Where(m => m.Set))
        {
            yield return new ValidationResult("The value belongs to the other target.", [name]);
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

/// <summary><c>GET /bff/setup/state</c>: rag-admin's sign-in, its latest test, and Open WebUI's block.</summary>
public sealed record SetupStateDto(
    string Kind, DateTimeOffset EndsAt, DateTimeOffset SessionExpiresAt, SignInSettingsDto SignIn, SetupRedirectUris RedirectUris, SetupDeploymentFacts Deployment,
    SetupTestDto? LatestTest, SetupOpenWebUiState OpenWebUi);

/// <summary>
/// Open WebUI in <c>GET /bff/setup/state</c>: whether the deployment runs it, its sign-in values with their sources, the redirect URI to
/// register on its client, and the session's latest Open WebUI test.
/// </summary>
public sealed record SetupOpenWebUiState(bool Enabled, OpenWebUiSignInDto SignIn, string? RedirectUri, OpenWebUiTestDto? LatestTest);

/// <summary>
/// <c>POST /bff/setup/complete</c> for <c>open-webui</c>: the saved values as shown, and whether the test confirmed the client's credentials
/// (false: a wrong secret shows only when a user signs in). Setup stays open.
/// </summary>
public sealed record SetupOpenWebUiSavedDto(OpenWebUiSettingsDto OpenWebUi, bool ClientVerified);

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
