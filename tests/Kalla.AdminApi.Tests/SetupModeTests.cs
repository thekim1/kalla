using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain.Admin;
using Kalla.TestKit;
using static Kalla.AdminApi.Tests.SetupHosts;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// M-20 S3 (identity design §19, "Setup state", "Setup code", "Setup API"): when setup opens and closes, the setup code and where it
/// is written, the wrong-code limit, the setup session and its cookie. Each test resets setup's state in the shared database and the
/// class leaves it completed and closed.
/// </summary>
[Collection(AdminDatabaseGroup6.Name)]
public sealed partial class SetupModeTests(AdminDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Admin => database.Connection(RagDatabase.Admin);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await ResetAsync(Admin, completed: true);

    private WebApplicationFactory<Program> Host(SetupOutput? output = null, CapturingLoggerProvider? logs = null, TimeProvider? time = null,
        params (string Key, string Value)[] settings) => SetupHosts.Host(database.Factory(), output, logs, time, settings);

    private static async Task<bool> SetupOpenAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        return json.RootElement.GetProperty("setup").GetProperty("open").GetBoolean();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public void The_code_is_26_Crockford_characters_in_groups_and_reads_case_and_separator_tolerant()
    {
        var code = SetupCode.Compute(Secret, "nonce-1");
        CodeShape().IsMatch(code).ShouldBeTrue(code);
        SetupCode.Compute(Secret, "nonce-1").ShouldBe(code);
        SetupCode.Compute(Secret, "nonce-2").ShouldNotBe(code);
        SetupCode.Compute(TestSecrets.RandomHex(), "nonce-1").ShouldNotBe(code);

        SetupCode.Matches(code, code).ShouldBeTrue();
        SetupCode.Matches(code.ToLowerInvariant(), code).ShouldBeTrue();
        SetupCode.Matches(code.Replace("-", "", StringComparison.Ordinal), code).ShouldBeTrue();
        SetupCode.Matches(code.Replace("-", " ", StringComparison.Ordinal), code).ShouldBeTrue();
        // Crockford's readings: O is 0, I and L are 1.
        SetupCode.Matches(code.Replace('0', 'O').Replace('1', 'l'), code).ShouldBeTrue();

        SetupCode.Matches(code[..^1], code).ShouldBeFalse();
        SetupCode.Matches(code + "0", code).ShouldBeFalse();
        SetupCode.Matches(code[..^1] + (code[^1] == 'A' ? 'B' : 'A'), code).ShouldBeFalse();
        SetupCode.Matches(code[..^1] + "U", code).ShouldBeFalse(); // not in the alphabet
        SetupCode.Matches(new string('0', 65), code).ShouldBeFalse();
        SetupCode.Matches(null, code).ShouldBeFalse();
    }

    [Fact]
    public async Task A_first_run_opens_once_on_two_hosts_started_together_and_both_write_the_same_code_to_standard_output_only()
    {
        await ResetAsync(Admin, completed: false);
        var (outA, outB) = (new SetupOutput(), new SetupOutput());
        using var logsA = new CapturingLoggerProvider();
        using var logsB = new CapturingLoggerProvider();
        await using var a = Host(outA, logsA);
        await using var b = Host(outB, logsB);

        await Task.WhenAll(Task.Run(() => a.Services, Ct), Task.Run(() => b.Services, Ct));

        var row = (await RowAsync(Admin)).ShouldNotBeNull();
        row.SetupNonce.ShouldNotBeNull();
        row.SetupCompletedAt.ShouldBeNull();
        var code = SetupCode.Compute(Secret, row.SetupNonce);
        outA.Text.ShouldContain(code);
        outB.Text.ShouldContain(code);
        outA.Text.ShouldContain("/setup");
        // One opening: one 6008 between the two hosts, and never the code in any log record.
        (Events(logsA, 6008).Count + Events(logsB, 6008).Count).ShouldBe(1);
        Events(logsA, 6008).Concat(Events(logsB, 6008)).Single().Message.ShouldContain(SetupMode.FirstRun);
        foreach (var form in Forms(code))
        {
            logsA.Text.ShouldNotContain(form);
            logsB.Text.ShouldNotContain(form);
        }

        using var client = await ClientAsync(a);
        (await SetupOpenAsync(client)).ShouldBeTrue();
    }

    [Fact]
    public async Task Closed_setup_answers_404_on_every_route_even_with_a_cookie_of_an_earlier_opening()
    {
        await ResetAsync(Admin, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using (var opening = Host(time: time))
        {
            using var session = await SessionAsync(opening, Admin);
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // Setup closes (as a completion on another replica does): the same cookie gets 404 everywhere at once, and /bff/user
            // says so once the values last read are two seconds old.
            await ResetAsync(Admin, completed: true);
            time.Advance(AdminSignIn.RefreshInterval);
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/test", new { authority = "https://idp.invalid" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/complete", new { testId = Guid.NewGuid() })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/session", new { code = "anything" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SetupOpenAsync(session)).ShouldBeFalse();
        }

        // A closed start writes no code; even without the antiforgery headers the answer is 404.
        var output = new SetupOutput();
        await using var closed = Host(output);
        using var plain = closed.CreateHttpsClient();
        (await plain.PostAsync("/bff/setup/session", JsonContent(new { code = "x" }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await plain.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_codes_are_throttled_per_minute_write_nothing_never_void_the_code_and_are_logged_sampled()
    {
        await ResetAsync(Admin, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(logs: logs, time: time);
        using var client = await ClientAsync(host);
        var code = await CodeAsync(Admin);
        var wrong = SetupCode.Compute(Secret, "another-nonce");
        var version = await SettingsVersionAsync();

        for (var attempt = 1; attempt <= SetupCodeThrottle.Limit; attempt++)
        {
            using var refused = await PostAsync(client, "/bff/setup/session", new { code = wrong });
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await CodeOf(refused)).ShouldBe(AdminErrorCodes.SetupCodeInvalid.Code);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldNotContain("remaining");
        }

        // A wrong code writes nothing: the settings row (and so the config version) is untouched.
        (await SettingsVersionAsync()).ShouldBe(version);

        // Over 20 wrong codes in the minute: 429 with the seconds to the window's end.
        time.Advance(TimeSpan.FromSeconds(20));
        using (var flooded = await PostAsync(client, "/bff/setup/session", new { code = wrong }))
        {
            flooded.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await CodeOf(flooded)).ShouldBe(AdminErrorCodes.SetupAttemptsExhausted.Code);
            int.Parse(flooded.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(39, 40);
        }

        // The right code is never throttled: a flood of wrong codes cannot keep the deployer out (the code is compared first).
        using (var inTheFlood = await PostAsync(client, "/bff/setup/session", new { code }))
        {
            inTheFlood.StatusCode.ShouldBe(HttpStatusCode.OK, await inTheFlood.Content.ReadAsStringAsync(Ct));
        }

        // Sampled: the first wrong code at once, the other 19 in the next event after 10 seconds.
        Events(logs, 6009).Count.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(41));
        using (var next = await PostAsync(client, "/bff/setup/session", new { code = wrong }))
        {
            next.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var refusedEvents = Events(logs, 6009);
        refusedEvents.Count.ShouldBe(2);
        refusedEvents[1].Message.ShouldContain("20 wrong codes");

        // The window allows again: the right code works, nothing was voided.
        using (var right = await PostAsync(client, "/bff/setup/session", new { code }))
        {
            right.StatusCode.ShouldBe(HttpStatusCode.OK, await right.Content.ReadAsStringAsync(Ct));
        }

        foreach (var form in Forms(code).Concat(Forms(wrong)))
        {
            logs.Text.ShouldNotContain(form);
        }
    }

    private async Task<uint> SettingsVersionAsync() => (await RowAsync(Admin))!.Version;

    [Fact]
    public async Task The_opening_ends_after_an_hour_and_the_next_start_of_a_never_completed_deployment_opens_anew()
    {
        await ResetAsync(Admin, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        string firstNonce;
        await using (var host = Host(time: time))
        {
            using var session = await SessionAsync(host, Admin);
            firstNonce = (await RowAsync(Admin))!.SetupNonce!;
            time.Advance(TimeSpan.FromMinutes(59));
            (await SetupOpenAsync(session)).ShouldBeTrue();
            time.Advance(TimeSpan.FromMinutes(2));
            (await SetupOpenAsync(session)).ShouldBeFalse();
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/session", new { code = SetupCode.Compute(Secret, firstNonce) })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Nothing renews it by itself; the next start does.
        var output = new SetupOutput();
        await using var restarted = Host(output, time: time);
        _ = restarted.Services;
        var row = (await RowAsync(Admin))!;
        row.SetupNonce.ShouldNotBe(firstNonce);
        output.Text.ShouldContain(SetupCode.Compute(Secret, row.SetupNonce!));
        using var client = await ClientAsync(restarted);
        (await SetupOpenAsync(client)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_reopen_value_opens_setup_once_is_consumed_at_the_opening_and_a_restart_with_it_does_not_open_again()
    {
        await ResetAsync(Admin, completed: true);
        using var logs = new CapturingLoggerProvider();
        var output = new SetupOutput();
        await using (var host = Host(output, logs, settings: (SetupMode.ReopenKey, "2026-11-15")))
        {
            _ = host.Services;
            var row = (await RowAsync(Admin))!;
            row.SetupNonce.ShouldNotBeNull();
            row.SetupReopenConsumed.ShouldBe("2026-11-15");
            row.SetupCompletedAt.ShouldNotBeNull();
            Events(logs, 6008).Single().Message.ShouldContain(SetupMode.Reopen);
            output.Text.ShouldContain(SetupCode.Compute(Secret, row.SetupNonce));
        }

        // The opening ran out; a restart with the same value opens nothing and writes no code.
        await using (var db = TestPostgres.Context(Admin))
        {
            await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.SetupOpenedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        }

        var again = new SetupOutput();
        using var logsAgain = new CapturingLoggerProvider();
        await using (var sameValue = Host(again, logsAgain, settings: (SetupMode.ReopenKey, "2026-11-15")))
        {
            using var client = await ClientAsync(sameValue);
            (await SetupOpenAsync(client)).ShouldBeFalse();
            again.Text.ShouldBeEmpty();
            Events(logsAgain, 6008).ShouldBeEmpty();
        }

        // A new value opens it again.
        await using var newValue = Host(settings: (SetupMode.ReopenKey, "2026-11-16"));
        using var reopened = await ClientAsync(newValue);
        (await SetupOpenAsync(reopened)).ShouldBeTrue();
        (await RowAsync(Admin))!.SetupReopenConsumed.ShouldBe("2026-11-16");
    }

    [Fact]
    public async Task Once_completed_a_missing_value_or_an_unreadable_secret_keeps_setup_closed()
    {
        await ResetAsync(Admin, completed: true);
        await using (var db = TestPostgres.Context(Admin))
        {
            var foreign = new Kalla.Infrastructure.Security.SettingsSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Kalla.Infrastructure.Security.SettingsSecretProtector>.Instance)
                .Protect(Kalla.Infrastructure.Security.StoredSecret.IdentityOidcClientSecret, "lost-secret");
            await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.IdentityOidcEncryptedClientSecret, foreign), Ct);
        }

        var output = new SetupOutput();
        await using var host = Host(output);
        using var client = await ClientAsync(host);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        json.RootElement.GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("setup").GetProperty("open").GetBoolean().ShouldBeFalse();
        (await RowAsync(Admin))!.SetupNonce.ShouldBeNull();
        (await PostAsync(client, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_settings_row_keeps_setup_closed_says_so_and_is_not_created()
    {
        // rag-migrate seeds the row, so the first run is a row without SetupCompletedAt; a missing row is a fault and stays closed.
        await using (var db = TestPostgres.Context(Admin))
        {
            await db.Settings.ExecuteDeleteAsync(Ct);
        }

        using var logs = new CapturingLoggerProvider();
        var output = new SetupOutput();
        await using var host = Host(output, logs, settings: [("Oidc:Authority", "https://idp.invalid/realms/ume"), ("Oidc:ClientSecret", "deployed-secret")]);
        using var client = await ClientAsync(host);
        (await SetupOpenAsync(client)).ShouldBeFalse();
        (await RowAsync(Admin)).ShouldBeNull(); // not even the declaring deployment's branch creates it
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("settings row is missing", StringComparison.Ordinal), logs.Text);
        (await PostAsync(client, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deployment_with_an_authority_and_a_client_secret_counts_as_completed_but_an_authority_alone_does_not()
    {
        await ResetAsync(Admin, completed: false);
        // The Portainer stage-1 case: the authority from the deployment, the client id only by default, no secret yet.
        using var logs = new CapturingLoggerProvider();
        await using (var authorityOnly = Host(logs: logs, settings: ("Oidc:Authority", "https://idp.invalid/realms/ume")))
        {
            using var client = await ClientAsync(authorityOnly);
            (await SetupOpenAsync(client)).ShouldBeTrue();
            logs.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message.Contains("sets the authority but no client secret", StringComparison.Ordinal),
                logs.Text);
        }

        // With the secret too, an open first run closes at the start and never opens again.
        var output = new SetupOutput();
        await using var declared = Host(output, settings: [("Oidc:Authority", "https://idp.invalid/realms/ume"), ("Oidc:ClientSecret", "deployed-secret")]);
        using var declaredClient = await ClientAsync(declared);
        (await SetupOpenAsync(declaredClient)).ShouldBeFalse();
        var row = (await RowAsync(Admin))!;
        row.SetupCompletedAt.ShouldNotBeNull();
        row.SetupNonce.ShouldBeNull();
        output.Text.ShouldBeEmpty();
        (await PostAsync(declaredClient, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Outside_development_without_a_setup_secret_setup_stays_closed_logs_the_file_and_consumes_nothing()
    {
        await ResetAsync(Admin, completed: true);
        using var logs = new CapturingLoggerProvider();
        var output = new SetupOutput();
        await using var host = SetupHosts.Host(database.Factory().WithWebHostBuilder(web => web.UseEnvironment("Staging")), output, logs,
            settings: [(SetupSecret.Key, "too-short"), (SetupMode.ReopenKey, "r1")]);
        using var client = await ClientAsync(host);

        (await SetupOpenAsync(client)).ShouldBeFalse();
        var row = (await RowAsync(Admin))!;
        row.SetupNonce.ShouldBeNull();
        row.SetupReopenConsumed.ShouldBeNull(); // the value opens setup at the first start that has the secret
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(SetupSecret.FileName, StringComparison.Ordinal), logs.Text);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_setup_posts_need_the_antiforgery_header_like_every_bff_post()
    {
        await ResetAsync(Admin, completed: false);
        await using var host = Host();
        using var plain = host.CreateHttpsClient();
        var code = await CodeAsync(Admin);
        foreach (var path in new[] { "/bff/setup/session", "/bff/setup/test", "/bff/setup/complete" })
        {
            using var response = await plain.PostAsync(path, JsonContent(new { code }), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, path);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("CSRF");
        }

        // The header present with a token that is not the cookie's: refused as well.
        using var forged = host.CreateHttpsClient();
        forged.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        forged.DefaultRequestHeaders.Add("X-XSRF-TOKEN", "not-the-token");
        using var bad = await forged.PostAsync("/bff/setup/session", JsonContent(new { code }), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("CSRF token is invalid");
    }

    [Fact]
    public async Task Other_spellings_of_the_setup_paths_are_gated_like_the_canonical_one()
    {
        await ResetAsync(Admin, completed: true);
        await using (var closed = Host())
        {
            using var client = await ClientAsync(closed);
            (await client.GetAsync("/BFF/Setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync("/bff/setup/state/", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await ResetAsync(Admin, completed: false);
        await using var open = Host();
        using var session = await SessionAsync(open, Admin);
        // Open: the other spellings route to the same endpoint (the setup cookie's path is matched case-sensitively by clients, so
        // only the canonical spelling carries the session).
        (await session.GetAsync("/bff/setup/state/", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var anonymous = await ClientAsync(open);
        (await anonymous.GetAsync("/BFF/Setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_sessions_of_an_ended_opening_are_deleted_once_setup_is_found_closed()
    {
        await ResetAsync(Admin, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = Host(time: time);
        using var session = await SessionAsync(host, Admin);
        time.Advance(TimeSpan.FromMinutes(61));
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var db = TestPostgres.Context(Admin);
        (await db.SetupSessions.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_Secure_SameSite_Strict_for_the_setup_api_and_30_minutes()
    {
        await ResetAsync(Admin, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(logs: logs, time: time);
        using var client = await ClientAsync(host);
        using var response = await PostAsync(client, "/bff/setup/session", new { code = (await CodeAsync(Admin)).ToLowerInvariant() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SetupSessions.CookieName + "=", StringComparison.Ordinal));
        cookie.ShouldContain("path=/bff/setup", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("expires=", Case.Insensitive);
        var token = cookie.Split(';')[0][(SetupSessions.CookieName.Length + 1)..];
        await using (var db = TestPostgres.Context(Admin))
        {
            var stored = await db.SetupSessions.AsNoTracking().SingleAsync(Ct);
            stored.TokenHash.ShouldBe(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
            (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromMinutes(30));
            Events(logs, 6010).Single().Message.ShouldContain(SetupSessions.EventId(stored));
        }

        logs.Text.ShouldNotContain(token);
        (await client.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        time.Advance(TimeSpan.FromMinutes(31));
        using var expired = await client.GetAsync("/bff/setup/state", Ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(expired)).ShouldBe(AdminErrorCodes.SetupSessionRequired.Code);
    }

    [Fact]
    public async Task A_session_of_an_earlier_opening_is_refused()
    {
        await ResetAsync(Admin, completed: true);
        await using var first = Host(settings: (SetupMode.ReopenKey, "first"));
        using var session = await SessionAsync(first, Admin);
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Another re-open: a new opening, whose start voids the earlier sessions.
        await using var second = Host(settings: (SetupMode.ReopenKey, "second"));
        _ = second.Services;
        using var refused = await session.GetAsync("/bff/setup/state", Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(refused)).ShouldBe(AdminErrorCodes.SetupSessionRequired.Code);
    }

    [Fact]
    public async Task No_route_but_setup_complete_writes_the_sign_in_settings()
    {
        await ResetAsync(Admin, completed: true);
        await using var factory = database.HeaderFactory();
        // The only sign-in route of an admin session reads; nothing else is mapped under it.
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("sign-in", StringComparison.OrdinalIgnoreCase) == true || e.RoutePattern.RawText?.Contains("oidc", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])
            .Distinct().ToList();
        endpoints.ShouldBe(["GET"]);

        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        (await admin.PutAsync("/api/settings/sign-in", JsonContent(new { authority = "https://evil.invalid" }), Ct)).StatusCode
            .ShouldBeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
        // The identity section takes its own values only: sign-in members sent with it are ignored.
        using var identity = JsonDocument.Parse(await admin.GetStringAsync("/api/settings/identity", Ct));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(identity.RootElement.GetRawText())!;
        body["authority"] = "https://evil.invalid";
        body["clientSecret"] = "evil-secret";
        using var put = await admin.PutAsync("/api/settings/identity", JsonContent(body), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var row = (await RowAsync(Admin))!;
        row.IdentityOidc.ShouldBe("{}");
        row.IdentityOidcEncryptedClientSecret.ShouldBeNull();
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}-[0-9A-HJKMNP-TV-Z]{6}$")]
    private static partial Regex CodeShape();
}
