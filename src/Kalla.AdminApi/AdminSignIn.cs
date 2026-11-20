using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi;

/// <summary>Why kalla-admin cannot start a sign-in (<see cref="SignInSnapshot.NotConfiguredReason"/>); logged, never answered.</summary>
public static class SignInNotConfiguredReasons
{
    /// <summary>Neither the deployment nor the setup wizard sets an authority.</summary>
    public const string NoAuthority = "no_authority";

    /// <summary>The authority is not an absolute https URL (http is allowed in Development only, as <c>RequireHttpsMetadata</c>).</summary>
    public const string InvalidAuthority = "invalid_authority";

    /// <summary>The wizard's client secret is stored but the key ring cannot unprotect it (<see cref="SignInClientSecret.Unreadable"/>).</summary>
    public const string UnreadableClientSecret = "unreadable_client_secret";
}

/// <summary>
/// kalla-admin's effective sign-in values at one moment (identity design §19): what the OpenID Connect options are built from and
/// what the sign-in checks read. Two snapshots are compared by a SHA-256 fingerprint of every value, the client secret included,
/// so a changed settings row and a reloaded deployment value (a secret file) are both seen; the fingerprint never leaves the class.
/// </summary>
public sealed class SignInSnapshot
{
    private readonly byte[] _fingerprint;

    private SignInSnapshot(EffectiveSignIn values, string? notConfiguredReason, byte[] fingerprint)
    {
        Values = values;
        NotConfiguredReason = notConfiguredReason;
        _fingerprint = fingerprint;
    }

    /// <summary>The values with their sources; <see cref="SignInClientSecret.Value"/> is for the options only.</summary>
    public EffectiveSignIn Values { get; }

    /// <summary>A <see cref="SignInNotConfiguredReasons"/> value, or null when a sign-in can start.</summary>
    public string? NotConfiguredReason { get; }

    public bool IsConfigured => NotConfiguredReason is null;

    /// <summary>The settings row's version the values were read at (<c>0</c> before the first read or without a row).</summary>
    public uint Version => Values.Version;

    /// <param name="values">The effective values (<see cref="SignInSettings.ReadAsync"/>).</param>
    /// <param name="requireHttps">As <c>RequireHttpsMetadata</c>: true outside Development.</param>
    public static SignInSnapshot Of(EffectiveSignIn values, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? reason = values.Authority.Value is not { } authority ? SignInNotConfiguredReasons.NoAuthority
            : !Uri.TryCreate(authority, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp))
                ? SignInNotConfiguredReasons.InvalidAuthority
            : values.ClientSecret.Unreadable ? SignInNotConfiguredReasons.UnreadableClientSecret
            : null;
        return new SignInSnapshot(values, reason, Fingerprint(values));
    }

    /// <summary>Whether both hold the same values (the settings version alone is not compared: any settings save changes it).</summary>
    public bool HasSameValues(SignInSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CryptographicOperations.FixedTimeEquals(_fingerprint, other._fingerprint);
    }

    /// <summary>
    /// The values as the sign-in checks take them (<see cref="AdminAuthentication.ValidateSignInAsync"/>), with the deployment's
    /// <c>Oidc:RoleGroups</c>, which stay a deployment value (identity design §2.5).
    /// </summary>
    public OidcOptions ToOidcOptions(IReadOnlyDictionary<string, string> roleGroups)
    {
        ArgumentNullException.ThrowIfNull(roleGroups);
        return new OidcOptions
        {
            Authority = Values.Authority.Value, ClientId = Values.ClientId.Value, RoleClaim = Values.RoleClaim.Value, DepartmentClaim = Values.DepartmentClaim.Value,
            TeamClaim = Values.TeamClaim.Value, GroupClaim = Values.GroupClaim.Value, ExtraScopes = [.. Values.ExtraScopes.Value], TenantId = Values.TenantId.Value,
            RefuseGuests = Values.RefuseGuests.Value, RoleGroups = new Dictionary<string, string>(roleGroups),
        };
    }

    public override string ToString() => $"version {Version}, configured: {IsConfigured}";

    private static byte[] Fingerprint(EffectiveSignIn v)
    {
        // Length-prefixed, so no two different value sets give the same text.
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value is null ? "-1" : value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
        Add(v.Authority.Value);
        Add(v.ClientId.Value);
        Add(v.ClientSecret.Value);
        Add(v.ClientSecret.Unreadable ? "unreadable" : "readable");
        Add(v.RoleClaim.Value);
        Add(v.DepartmentClaim.Value);
        Add(v.TeamClaim.Value);
        Add(v.GroupClaim.Value);
        Add(string.Join(' ', v.ExtraScopes.Value));
        Add(v.ExtraScopes.Value.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(v.TenantId.Value);
        Add(v.RefuseGuests.Value ? "true" : "false");
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }
}

/// <summary>
/// The sign-in values kalla-admin's OpenID Connect options are built from (identity design §19, "Reload"). <see cref="Configure"/>
/// is the options' configure step for the first build and every rebuild; it takes <see cref="Current"/>, which
/// <see cref="RefreshAsync"/> reads from the database before a sign-in challenge, callback, sign-out or <c>/bff/user</c>, before
/// the authentication middleware creates the handler, at most once per <see cref="RefreshInterval"/>: anyone may call those paths,
/// so a flood of them costs one read per interval and never queues real sign-ins behind reads. When the values differ from what the live options were built from,
/// the named options are evicted from the options cache, so the next use builds them again, with the new authority's metadata.
/// Nothing is shared between replicas: each refreshes on its own next sign-in.
/// </summary>
public sealed partial class AdminSignIn(IConfiguration configuration, IHostEnvironment environment, TimeProvider time, ILogger<AdminSignIn> logger) : IDisposable
{
    /// <summary>
    /// How long the paths of <see cref="Refreshes"/>, which anyone may call, go on with the values last read. Completing setup reads
    /// them at once on its own instance; another replica follows a change within this time.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private const string Scheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>When the values were last read, in UTC ticks; 0 before the first read.</summary>
    private long _readAt;

    /// <summary>Which snapshot each live options instance was built from; weak, so evicted options are collected.</summary>
    private readonly ConditionalWeakTable<OpenIdConnectOptions, SignInSnapshot> _builtFrom = new();

    /// <summary>One refresh at a time, so a slower read never replaces a newer one.</summary>
    private readonly SemaphoreSlim _refresh = new(1, 1);

    /// <summary>Until the first read: the deployment's values alone, as at start (no database read at start).</summary>
    private volatile SignInSnapshot _current = SignInSnapshot.Of(
        SignInSettings.Resolve(configuration, new OidcWizardValues(), () => StoredSecretValue.NotSet, null, 0, null), !environment.IsDevelopment());

    /// <summary>The latest values read; the sign-in checks take <see cref="For"/> instead.</summary>
    public SignInSnapshot Current => _current;

    /// <summary>The paths before whose handling <see cref="RefreshAsync"/> runs: the sign-in start, the callback, the sign-out, the user document.</summary>
    public static bool Refreshes(PathString path, OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return path.Equals(AdminAuthentication.LoginPath, StringComparison.OrdinalIgnoreCase) || path.Equals(AdminAuthentication.LogoutPath, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(AdminAuthentication.UserPath, StringComparison.OrdinalIgnoreCase) || path == options.CallbackPath;
    }

    /// <summary>Whether the values were read less than <see cref="RefreshInterval"/> ago.</summary>
    public bool ReadRecently => time.GetUtcNow().UtcTicks - Interlocked.Read(ref _readAt) < RefreshInterval.Ticks;

    /// <summary>Whether <paramref name="ex"/> says the database is away, so the values last read stay in use.</summary>
    public static bool IsDatabaseAway(Exception ex) =>
        ex is Npgsql.NpgsqlException or TimeoutException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException { InnerException: Npgsql.NpgsqlException };

    /// <summary>The snapshot <paramref name="options"/> were built from (the handler's own), else <see cref="Current"/>.</summary>
    public SignInSnapshot For(OpenIdConnectOptions options) => _builtFrom.TryGetValue(options, out var snapshot) ? snapshot : _current;

    /// <summary>
    /// The configure step of the scheme's options: authority, client and secret, extra scopes. Without a usable authority it
    /// gives an empty static configuration, so the options stay valid, nothing is fetched, and the challenge is refused
    /// (<see cref="AdminAuthentication"/>'s <c>OnRedirectToIdentityProvider</c>).
    /// </summary>
    public void Configure(OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = _current;
        options.ClientId = snapshot.Values.ClientId.Value;
        if (snapshot.IsConfigured)
        {
            options.Authority = snapshot.Values.Authority.Value;
            options.ClientSecret = snapshot.Values.ClientSecret.Value;
        }
        else
        {
            options.Configuration = new OpenIdConnectConfiguration();
        }

        foreach (var scope in snapshot.Values.ExtraScopes.Value)
        {
            options.Scope.Add(scope);
        }

        _builtFrom.AddOrUpdate(options, snapshot);
    }

    /// <summary>
    /// Reads the effective values (one primary-key read) and, when they differ from what the live options were built from, evicts
    /// the options so they are built again from them. Returns the values read. With <paramref name="unlessReadRecently"/>, a caller
    /// that waited for another one's read takes that read instead of making its own, so requests arriving together read once.
    /// </summary>
    public async Task<SignInSnapshot> RefreshAsync(SignInSettings settings, IOptionsMonitor<OpenIdConnectOptions> monitor, IOptionsMonitorCache<OpenIdConnectOptions> cache,
        CancellationToken ct, bool unlessReadRecently = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cache);
        await _refresh.WaitAsync(ct);
        try
        {
            if (unlessReadRecently && ReadRecently)
            {
                return _current;
            }

            var fresh = SignInSnapshot.Of(await settings.ReadAsync(ct), !environment.IsDevelopment());
            _current = fresh;
            Interlocked.Exchange(ref _readAt, time.GetUtcNow().UtcTicks);
            // A build that started before _current changed may land after an eviction; the check after each build catches it.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_builtFrom.TryGetValue(monitor.Get(Scheme), out var built) && built.HasSameValues(fresh))
                {
                    if (attempt > 0)
                    {
                        LogRebuilt(logger, fresh.Version, fresh.IsConfigured);
                        if (fresh.NotConfiguredReason is { } reason)
                        {
                            LogNotConfigured(logger, reason);
                        }
                    }

                    return fresh;
                }

                cache.TryRemove(Scheme);
            }

            throw new InvalidOperationException("The sign-in options could not be rebuilt from the current sign-in values.");
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose() => _refresh.Dispose();

    [LoggerMessage(LogLevel.Information, "Sign-in options rebuilt from the sign-in values at settings version {Version}; configured: {Configured}")]
    private static partial void LogRebuilt(ILogger logger, uint version, bool configured);

    [LoggerMessage(LogLevel.Warning, "Sign-in is not configured ({Reason}): a sign-in is refused until the values are set")]
    private static partial void LogNotConfigured(ILogger logger, string reason);
}

/// <summary>
/// Runs <see cref="AdminSignIn.RefreshAsync"/> before the authentication middleware on the paths of
/// <see cref="AdminSignIn.Refreshes"/>, so the handler created for the request has the current values (at most
/// <see cref="AdminSignIn.RefreshInterval"/> old). Every other request reads nothing.
/// </summary>
public sealed partial class AdminSignInRefreshMiddleware(RequestDelegate next, ILogger<AdminSignInRefreshMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, AdminSignIn signIn, IOptionsMonitor<OpenIdConnectOptions> monitor, IOptionsMonitorCache<OpenIdConnectOptions> cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(monitor);
        if (AdminSignIn.Refreshes(http.Request.Path, monitor.Get(OpenIdConnectDefaults.AuthenticationScheme)) && !signIn.ReadRecently)
        {
            try
            {
                await signIn.RefreshAsync(http.RequestServices.GetRequiredService<SignInSettings>(), monitor, cache, http.RequestAborted, unlessReadRecently: true);
            }
            catch (Exception ex) when (AdminSignIn.IsDatabaseAway(ex))
            {
                // The database is away: go on with the values last read, so the sign-in page still loads and says what it can.
                // A sign-in itself needs the database (the session store) and fails there.
                LogRefreshFailed(logger, ex.GetType().Name);
            }
        }

        await next(http);
    }

    [LoggerMessage(LogLevel.Warning, "The sign-in values could not be read ({ExceptionType}); the last values read stay in use")]
    private static partial void LogRefreshFailed(ILogger logger, string exceptionType);
}
