using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Stores;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>A ragdb with the production roles for the admin API's database tests (one per admin-db-N collection).</summary>
public sealed class AdminDatabaseFixture : RagDatabase, IAsyncLifetime
{
    /// <summary>The admin API on this database as <c>kalla_admin</c>, with a placeholder identity provider.</summary>
    public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("ConnectionStrings:ragdb", Connection(Admin));
        web.UseSetting("Oidc:Authority", "https://idp.invalid/realms/fors");
        web.UseSetting("Oidc:ClientSecret", "test-secret");
        // The shared database's mirror is seeded by the tests: the gateway syncs, which start with any host that has a
        // gateway key, stay off here (OrganisationSyncTests and GatewayClientTests run them on databases of their own).
        web.ConfigureTestServices(services => services.RemoveGatewaySyncs());
        // As the gateway's AdminFixture: a static OIDC configuration, so nothing is fetched from the placeholder authority. It
        // follows the effective authority the options were built with (identity design §19), and none is set without one.
        web.ConfigureTestServices(services => services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
        {
            if (o.Authority is { } authority)
            {
                o.Configuration = StaticConfiguration(authority);
            }
        }));
    });

    /// <summary>The placeholder provider's metadata under <paramref name="authority"/>: its endpoints are the authority's paths.</summary>
    public static OpenIdConnectConfiguration StaticConfiguration(string authority) => new()
    {
        Issuer = authority, AuthorizationEndpoint = authority + "/authorize", TokenEndpoint = authority + "/token", EndSessionEndpoint = authority + "/logout",
    };

    /// <summary>An HTTPS client: the session and antiforgery cookies are Secure.</summary>
    public static HttpClient HttpsClient(WebApplicationFactory<Program> factory, bool allowAutoRedirect = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = allowAutoRedirect });

    /// <summary>The admin API with every request signed in as the user its <see cref="HeaderAuthenticationHandler"/> headers describe.</summary>
    public WebApplicationFactory<Program> HeaderFactory() => Factory().WithWebHostBuilder(web =>
        web.ConfigureTestServices(services => services.AddHeaderAuthentication()));

    /// <summary>
    /// A client of <paramref name="factory"/> signed in with <paramref name="roles"/> (none: a user without a rag-* role) and
    /// <paramref name="departments"/>, holding the XSRF token as the admin UI does.
    /// </summary>
    public static async Task<HttpClient> UserAsync(WebApplicationFactory<Program> factory, string roles, string departments = "")
    {
        var client = factory.CreateHttpsClient();
        // A user without a role is still signed in: the handler treats an empty role header as anonymous.
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, roles.Length == 0 ? "none" : roles);
        client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.DepartmentHeader, departments);
        return await client.WithXsrfTokenAsync();
    }
}

/// <summary>
/// One admin API host (<see cref="AdminDatabaseFixture.HeaderFactory"/>) shared by the tests of a class: a class fixture on the
/// class's admin-db database, started on first use and stopped after the class's last test. Starting a host takes about a second,
/// a request about a millisecond (#785). A test that changes the host's settings or services keeps a factory of its own. The tests
/// of a class sign in as one subject, so the per-user limit is raised for them.
/// </summary>
public class AdminHost(AdminDatabaseFixture database) : IAsyncDisposable
{
    private WebApplicationFactory<Program>? factory;

    /// <summary>The shared host's factory; tests don't dispose it.</summary>
    public WebApplicationFactory<Program> Factory => factory ??= Configure(database.HeaderFactory().WithWebHostBuilder(web =>
        web.UseSetting("Admin:RequestsPerMinute", "10000")));

    /// <summary>A class's own settings for its shared host.</summary>
    protected virtual WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> host) => host;

    /// <summary>Stops or removes what a class's host used, after the host has stopped.</summary>
    protected virtual ValueTask CleanupAsync() => default;

    public async ValueTask DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await CleanupAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Test host helpers.</summary>
public static class GatewaySyncTestExtensions
{
    /// <summary>
    /// Removes the hosted runs of <see cref="OrganisationSync"/> and <see cref="GatewayModelSync"/> (registered as
    /// <c>AddHostedService(sp => sp.GetRequiredService&lt;T&gt;())</c>); the services themselves stay for the endpoints.
    /// </summary>
    public static IServiceCollection RemoveGatewaySyncs(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                     d.ImplementationFactory?.Method.ReturnType is { } type && (type == typeof(OrganisationSync) || type == typeof(GatewayModelSync))).ToList())
        {
            services.Remove(descriptor);
        }

        return services;
    }
}

// The admin-db collections, each with its own database, as the worker tests' ragdb-N. The classes of one collection run one
// after the other, the collections in parallel. Classes are spread by measured duration (80-90 s each locally,
// 2026-11-16, #785; the longest collection sets the project's time), so the collections end at about the same time; a new class
// goes to the one that is shortest then. In CI collection N runs in process (N-1) mod 4 of scripts/ci/backend-tests.sh: the
// collections of one process wait for one another (what for is not found), those of different processes don't.

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup1 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-1";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup2 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-2";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup3 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-3";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup4 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-4";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup5 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-5";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup6 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-6";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup7 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-7";
}

[CollectionDefinition(Name)]
public sealed class AdminDatabaseGroup8 : ICollectionFixture<AdminDatabaseFixture>
{
    public const string Name = "admin-db-8";
}

/// <summary>
/// Server-side admin sessions (signing out ends every copy of the cookie) and the absolute session limit. Copied from the
/// gateway's <c>AdminSessionTests</c>; the store is <see cref="PostgresAdminSessionStore"/> on <c>config."AdminSessions"</c>
/// as <c>kalla_admin</c> instead of the gateway's in-memory one (to-verify #163).
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class AdminSessionTests(AdminDatabaseFixture database)
{
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(12);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingStore(IAdminSessionStore inner) : IAdminSessionStore
    {
        public Dictionary<string, byte[]> Written { get; } = [];

        public Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Written[sessionId] = value;
            return inner.SetAsync(sessionId, value, timeToLive, cancellationToken);
        }

        public Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken) => inner.GetAsync(sessionId, cancellationToken);

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) => inner.RemoveAsync(sessionId, cancellationToken);
    }

    private PostgresAdminSessionStore Store(TimeProvider time)
    {
        var services = new ServiceCollection()
            .AddDbContext<RagDbContext>(o => o.UseNpgsql(database.Connection(RagDatabase.Admin), RagDbContext.ConfigureNpgsql))
            .BuildServiceProvider();
        return new PostgresAdminSessionStore(services.GetRequiredService<IServiceScopeFactory>(), time);
    }

    private static AuthenticationTicket Ticket(DateTimeOffset expires) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("roles", "rag-platform-admin"), new Claim("email", "anna@umea.se")], "test")),
        new AuthenticationProperties { ExpiresUtc = expires }, CookieAuthenticationDefaults.AuthenticationScheme);

    [Fact]
    public async Task Ticket_store_keeps_encrypted_sessions_and_forgets_them_on_sign_out()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new RecordingStore(Store(time));
        var tickets = new AdminTicketStore(store, new EphemeralDataProtectionProvider(), time);

        var id = await tickets.StoreAsync(Ticket(time.GetUtcNow().AddHours(8)));

        id.Length.ShouldBeGreaterThanOrEqualTo(40); // 32 random bytes
        (await tickets.RetrieveAsync(id)).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("user-1");
        var stored = Encoding.UTF8.GetString(store.Written[id]);
        stored.ShouldNotContain("user-1");
        stored.ShouldNotContain("anna@umea.se");

        await tickets.RemoveAsync(id);
        (await tickets.RetrieveAsync(id)).ShouldBeNull();
        (await RowCountAsync(id)).ShouldBe(0); // the row is gone, so every copy of the cookie stops working
    }

    [Fact]
    public async Task Ticket_store_refuses_entries_it_did_not_write_and_expired_sessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = Store(time);
        var tickets = new AdminTicketStore(store, new EphemeralDataProtectionProvider(), time);

        // Someone who can write to the store cannot create a session.
        var forged = "forged-" + Guid.NewGuid().ToString("N");
        await store.SetAsync(forged, TicketSerializer.Default.Serialize(Ticket(time.GetUtcNow().AddHours(8))), TimeSpan.FromHours(8), CancellationToken.None);
        (await tickets.RetrieveAsync(forged)).ShouldBeNull();

        var id = await tickets.StoreAsync(Ticket(time.GetUtcNow().AddHours(1)));
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await tickets.RetrieveAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task Renewing_a_session_replaces_its_row()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tickets = new AdminTicketStore(Store(time), new EphemeralDataProtectionProvider(), time);
        var id = await tickets.StoreAsync(Ticket(time.GetUtcNow().AddHours(1)));

        await tickets.RenewAsync(id, Ticket(time.GetUtcNow().AddHours(8)));
        time.Advance(TimeSpan.FromHours(2));

        (await tickets.RetrieveAsync(id)).ShouldNotBeNull();
        (await RowCountAsync(id)).ShouldBe(1);
    }

    [Fact]
    public async Task The_retention_step_deletes_expired_sessions_only()
    {
        // Sessions written "two days ago" with lifetimes of 1 h (expired) and 3 days (still valid).
        var past = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-2));
        var store = Store(past);
        var expired = "expired-" + Guid.NewGuid().ToString("N");
        var valid = "valid-" + Guid.NewGuid().ToString("N");
        await store.SetAsync(expired, [1], TimeSpan.FromHours(1), Ct);
        await store.SetAsync(valid, [2], TimeSpan.FromDays(3), Ct);

        await using (var connection = new NpgsqlConnection(database.Connection(RagDatabase.Admin)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT config.rag_retention_config_step(1000)", connection);
            ((long)(await command.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThanOrEqualTo(1);
        }

        (await RowCountAsync(expired)).ShouldBe(0);
        (await RowCountAsync(valid)).ShouldBe(1);
    }

    [Theory]
    [InlineData(RagDatabase.Api)]
    [InlineData(RagDatabase.Worker)]
    public async Task Only_the_admin_reads_sessions_or_runs_the_retention_step(string role)
    {
        await using var connection = new NpgsqlConnection(database.Connection(role));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[] { """SELECT count(*) FROM config."AdminSessions" """, "SELECT config.rag_retention_config_step(10)" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public void Sign_in_stamps_the_session_and_extension_stops_at_the_limit()
    {
        var signedIn = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var properties = new AuthenticationProperties { ExpiresUtc = signedIn + AdminSessionLimit.Lifetime };
        var context = new CookieSigningInContext(new DefaultHttpContext(),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new ClaimsPrincipal(), properties, new CookieOptions());

        AdminSessionLimit.OnSigningIn(context, MaxSession, signedIn);
        AdminSessionLimit.LimitOf(properties, MaxSession).ShouldBe(signedIn.AddHours(12));
        properties.ExpiresUtc.ShouldBe(signedIn.AddHours(8));

        // Two hours in: a full lifetime. Seven hours in: only up to the limit. At the limit: refused, nothing changes.
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(2)).ShouldBe(signedIn.AddHours(10));
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(7)).ShouldBe(signedIn.AddHours(12));
        AdminSessionLimit.Extend(properties, MaxSession, signedIn.AddHours(12)).ShouldBeNull();
        properties.ExpiresUtc.ShouldBe(signedIn.AddHours(12));

        // Signing in again on extension keeps the original sign-in time, so the limit does not move either.
        AdminSessionLimit.OnSigningIn(context, MaxSession, signedIn.AddHours(11));
        AdminSessionLimit.LimitOf(properties, MaxSession).ShouldBe(signedIn.AddHours(12));
    }

    [Fact]
    public void First_expiry_is_capped_when_the_limit_is_shorter_than_a_lifetime()
    {
        // Whole seconds: AuthenticationProperties keeps ExpiresUtc with one-second precision.
        var now = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var properties = new AuthenticationProperties { ExpiresUtc = now + AdminSessionLimit.Lifetime };
        var context = new CookieSigningInContext(new DefaultHttpContext(),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new ClaimsPrincipal(), properties, new CookieOptions());

        AdminSessionLimit.OnSigningIn(context, TimeSpan.FromHours(2), now);

        properties.ExpiresUtc.ShouldBe(now.AddHours(2));
    }

    private async Task<long> RowCountAsync(string sessionId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT count(*) FROM config."AdminSessions" WHERE "SessionId" = $1""", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = sessionId });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
