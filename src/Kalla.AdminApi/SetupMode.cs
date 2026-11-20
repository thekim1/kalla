using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi;

/// <summary>
/// The setup code (identity design §19, "Setup code"): the first 128 bits of HMAC-SHA-256 under the setup secret over the
/// opening's nonce, as 26 Crockford base32 characters in five groups (<c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX</c>). Input is read
/// case-insensitively, with or without separators, and with Crockford's readings of O as 0 and I, L as 1.
/// </summary>
public static class SetupCode
{
    /// <summary>Crockford's base32 alphabet: no I, L, O or U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters without separators: 128 bits in 5-bit characters.</summary>
    public const int Length = 26;

    /// <summary>The longest input looked at; anything longer is a wrong code.</summary>
    public const int MaxInputLength = 64;

    private static readonly int[] Groups = [5, 5, 5, 5, 6];

    /// <summary>The code of the opening with <paramref name="nonce"/>, grouped. Never logged or answered.</summary>
    public static string Compute(string secret, string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("rag-setup-code:" + nonce));
        var plain = Encode(mac.AsSpan(0, 16));
        var grouped = new StringBuilder(Length + Groups.Length);
        var at = 0;
        foreach (var size in Groups)
        {
            grouped.Append(grouped.Length == 0 ? "" : "-").Append(plain, at, size);
            at += size;
        }

        return grouped.ToString();
    }

    /// <summary>Whether <paramref name="input"/> is <paramref name="expected"/> (as <see cref="Compute"/> gives it), compared in constant time.</summary>
    public static bool Matches(string? input, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var wanted = Encoding.ASCII.GetBytes(expected.Replace("-", "", StringComparison.Ordinal));
        var normalized = Normalize(input);
        // A malformed input is compared too (as zero bytes, never equal), so both paths take the same time.
        var given = normalized is null ? new byte[wanted.Length] : Encoding.ASCII.GetBytes(normalized);
        return CryptographicOperations.FixedTimeEquals(given, wanted) && normalized is not null;
    }

    /// <summary>The 26 characters of <paramref name="input"/> in the alphabet, or null when it is not a well-formed code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInputLength)
        {
            return null;
        }

        var chars = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(upper, StringComparison.Ordinal) || chars.Length == Length)
            {
                return null;
            }

            chars.Append(upper);
        }

        return chars.Length == Length ? chars.ToString() : null;
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new StringBuilder(Length);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            chars.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return chars.ToString();
    }
}

/// <summary>
/// The setup secret (<c>Setup:Secret</c>, the key-per-file secret <c>Setup__Secret</c> the deployment generates), read at each use so a
/// written file applies. Development without one uses a random secret per process, so codes differ between instances there.
/// </summary>
public sealed partial class SetupSecret(IConfiguration configuration, IHostEnvironment environment, ILogger<SetupSecret> logger)
{
    public const string Key = "Setup:Secret";
    public const string FileName = "Setup__Secret";

    /// <summary>A generated secret is 64 hex characters; anything under this is treated as missing.</summary>
    public const int MinLength = 32;

    private readonly Lazy<string> _development = new(() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    private int _warned;

    /// <summary>The secret, or null outside Development while the deployment provides none (or one too short).</summary>
    public string? Current
    {
        get
        {
            if (configuration[Key]?.Trim() is { Length: >= MinLength } configured)
            {
                return configured;
            }

            if (!environment.IsDevelopment())
            {
                return null;
            }

            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                LogDevelopmentSecret(logger, FileName);
            }

            return _development.Value;
        }
    }

    [LoggerMessage(LogLevel.Warning, "No {FileName}: setup codes come from a random secret of this process, so they differ between instances")]
    private static partial void LogDevelopmentSecret(ILogger logger, string fileName);
}

/// <summary>Where the setup code is written: standard output (identity design §19), never the logger. Tests give their own writer.</summary>
public sealed class SetupConsole(TextWriter? output = null)
{
    public TextWriter Out => output ?? Console.Out;
}

/// <summary>
/// Setup mode's state as one request sees it (identity design §19, "Setup state"): <see cref="Nonce"/> is the open opening's (null
/// while closed); <see cref="CompletedAt"/> is null on a first run and set on a re-open.
/// </summary>
public sealed record SetupStatus(bool Open, string? Nonce, DateTimeOffset? OpenedAt, DateTimeOffset? EndsAt, DateTimeOffset? CompletedAt)
{
    /// <summary><c>first_run</c> or <c>reopen</c>.</summary>
    public string Kind => CompletedAt is null ? SetupMode.FirstRun : SetupMode.Reopen;
}

/// <summary>What the evaluation at start found or did.</summary>
public enum SetupEvaluationOutcome
{
    /// <summary>Setup is closed and stays closed.</summary>
    Closed = 0,

    /// <summary>An opening started earlier is still running.</summary>
    StillOpen = 1,

    /// <summary>A new opening started (new nonce).</summary>
    Opened = 2,

    /// <summary>The deployment declares the sign-in: <c>SetupCompletedAt</c> was set.</summary>
    CompletedByDeployment = 3,

    /// <summary>Setup would open, but there is no setup secret (outside Development): it stays closed.</summary>
    SecretMissing = 4,

    /// <summary>There is no settings row (kalla-migrate creates it): setup stays closed.</summary>
    RowMissing = 5,
}

/// <summary>
/// The evaluation's outcome and, while open, the opening. <see cref="AuthorityWithoutSecret"/>: a never-completed deployment sets the
/// authority but no client secret, which is why setup is open.
/// </summary>
public sealed record SetupEvaluation(SetupEvaluationOutcome Outcome, string? Nonce = null, DateTimeOffset? EndsAt = null, string? Kind = null,
    bool AuthorityWithoutSecret = false);

/// <summary>
/// Open or closed (identity design §19, "Setup state"): open while an opening (a nonce and its start) is younger than one hour.
/// An opening starts only at kalla-admin's start (<see cref="EvaluateAsync"/>): when setup has never been completed (the settings row,
/// which kalla-migrate seeds, has no <c>SetupCompletedAt</c>) and no opening runs, or when <c>Setup:Reopen</c> holds a value other
/// than the last consumed one, which the same transaction consumes. A deployment that declares the sign-in
/// (<see cref="SignInSettings.DeclaresSignIn"/>) counts as completed. Nothing else opens setup: a missing row, a missing value or an
/// unreadable secret leaves it closed, and setup never creates the row. Writes run under the config version lock, so replicas
/// starting together end with one nonce.
/// </summary>
public sealed class SetupMode(RagDbContext db, IConfiguration configuration, TimeProvider time, SetupSecret secret, ConfigLockMetrics lockMetrics)
{
    public const string ReopenKey = "Setup:Reopen";
    public const string FirstRun = "first_run";
    public const string Reopen = "reopen";

    public static readonly TimeSpan OpeningLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// The rule on its own: an opening (<paramref name="openedAt"/>, null while no opening has a nonce) younger than an hour, unless the
    /// deployment now declares a sign-in that was never completed.
    /// </summary>
    public static bool IsOpen(DateTimeOffset? completedAt, DateTimeOffset? openedAt, bool declaredByDeployment, DateTimeOffset now) =>
        openedAt is { } opened && now < opened + OpeningLifetime && !(completedAt is null && declaredByDeployment);

    /// <summary>The rule on the values <c>/bff/user</c> read (one settings row read, <see cref="SignInSettings.ReadAsync"/>).</summary>
    public static bool IsOpen(EffectiveSignIn values, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);
        return IsOpen(values.SetupCompletedAt, values.SetupOpenedAt, values.DeclaredByDeployment, now);
    }

    /// <summary>The state now: one read of the settings row; no row is closed.</summary>
    public async Task<SetupStatus> ReadAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking()
            .Select(s => new { s.SetupCompletedAt, s.SetupNonce, s.SetupOpenedAt })
            .SingleOrDefaultAsync(ct);
        var openedAt = row?.SetupNonce is null ? null : row.SetupOpenedAt;
        var open = IsOpen(row?.SetupCompletedAt, openedAt, SignInSettings.DeclaresSignIn(configuration), time.GetUtcNow());
        return new SetupStatus(open, open ? row!.SetupNonce : null, openedAt, openedAt + OpeningLifetime, row?.SetupCompletedAt);
    }

    /// <summary>
    /// Deletes the setup sessions (their tests cascade) of every opening that is not running now, in one conditional statement, so a
    /// typed candidate secret does not stay in the table once its opening ended. A session of the running opening is kept, whichever
    /// replica started it.
    /// </summary>
    public Task<int> DeleteEndedSessionsAsync(CancellationToken ct)
    {
        var runningSince = time.GetUtcNow() - OpeningLifetime;
        return db.SetupSessions
            .Where(s => !db.Settings.Any(x => x.SetupNonce == s.OpeningNonce && x.SetupOpenedAt > runningSince))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>The evaluation at start: completes for a declaring deployment, starts an opening when one is due, else reports the state.</summary>
    public async Task<SetupEvaluation> EvaluateAsync(CancellationToken ct)
    {
        var declared = SignInSettings.DeclaresSignIn(configuration);
        var authorityWithoutSecret = !declared && SignInSettings.DeploysAuthority(configuration);
        var reopen = configuration[ReopenKey] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var hasSecret = secret.Current is not null;
        return await db.ExecuteConfigWriteAsync(async (_, attempt) =>
        {
            var now = time.GetUtcNow();
            var row = await db.Settings.SingleOrDefaultAsync(attempt);
            if (row is null)
            {
                return new SetupEvaluation(SetupEvaluationOutcome.RowMissing);
            }

            if (declared && row.SetupCompletedAt is null)
            {
                row.SetupCompletedAt = now;
                row.SetupNonce = null;
                await db.SaveChangesAsync(attempt);
                await db.SetupSessions.ExecuteDeleteAsync(attempt);
                return new SetupEvaluation(SetupEvaluationOutcome.CompletedByDeployment);
            }

            var neverCompleted = row.SetupCompletedAt is null;
            var reopenPending = reopen is not null && !string.Equals(reopen, row.SetupReopenConsumed, StringComparison.Ordinal);
            var running = row.SetupNonce is not null && row.SetupOpenedAt is { } openedAt && now < openedAt + OpeningLifetime;
            if (running && !reopenPending)
            {
                return new SetupEvaluation(SetupEvaluationOutcome.StillOpen, row.SetupNonce, row.SetupOpenedAt + OpeningLifetime, neverCompleted ? FirstRun : Reopen,
                    neverCompleted && authorityWithoutSecret);
            }

            var closed = !reopenPending && !neverCompleted;
            if (closed || !hasSecret)
            {
                // The sessions of ended openings go. Without the secret nothing is consumed, so the re-open value opens setup at
                // the first start that has it.
                await DeleteEndedSessionsAsync(attempt);
                return new SetupEvaluation(closed ? SetupEvaluationOutcome.Closed : SetupEvaluationOutcome.SecretMissing);
            }

            row.SetupNonce = Base64Url(RandomNumberGenerator.GetBytes(32));
            row.SetupOpenedAt = now;
            if (reopenPending)
            {
                row.SetupReopenConsumed = reopen;
            }

            await db.SaveChangesAsync(attempt);
            // A new opening voids the sessions (and their tests) of every earlier one.
            await db.SetupSessions.ExecuteDeleteAsync(attempt);
            return new SetupEvaluation(SetupEvaluationOutcome.Opened, row.SetupNonce, now + OpeningLifetime, neverCompleted ? FirstRun : Reopen,
                neverCompleted && authorityWithoutSecret);
        }, ct, lockMetrics);
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The throttle on wrong setup codes (identity design §19): at most <see cref="Limit"/> per minute in this process, a fixed window;
/// further wrong codes in the window answer 429. The code is compared before the throttle is asked, so the right code always gives a
/// session and a guesser cannot keep the deployer out. Nothing is persistent and nothing voids the code. The per-address limiter
/// stays on the route as well.
/// </summary>
public sealed class SetupCodeThrottle(TimeProvider time)
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _count;

    /// <summary>Whether one more check may run now; else <paramref name="retryAfter"/> is the time to the window's end.</summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _count = 0;
            }

            retryAfter = _windowStart + Window - now;
            return ++_count <= Limit;
        }
    }
}

/// <summary>
/// The limit on setup test callbacks (<see cref="SetupCallbackMiddleware"/>; identity design §19), which anyone can send while setup
/// is open and which each cost a database statement: at most <see cref="Limit"/> per minute and client address in this process, a
/// fixed window; further ones answer 429 before anything is read. Per address, so a flood from elsewhere does not keep the
/// deployer's own callback out; past <see cref="MaxAddresses"/> addresses in a window the rest share one count.
/// </summary>
public sealed class SetupCallbackThrottle(TimeProvider time)
{
    public const int Limit = 20;
    public const int MaxAddresses = 10_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;

    /// <summary>Counts one callback from <paramref name="address"/>: false when its window is full, with <paramref name="retryAfter"/> the time to its end.</summary>
    public bool TryAcquire(string? address, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _counts.Clear();
            }

            retryAfter = _windowStart + Window - now;
            var key = address ?? "";
            if (!_counts.ContainsKey(key) && _counts.Count >= MaxAddresses)
            {
                key = "\0others";
            }

            var count = _counts.GetValueOrDefault(key) + 1;
            _counts[key] = count;
            return count <= Limit;
        }
    }
}

/// <summary>
/// Samples setup's flood-prone security events (6009 wrong codes, 6011 <c>unknown_test</c>): at most one per <see cref="Every"/> per
/// event in this process, carrying the number since the last one, so a flood cannot fill the log.
/// </summary>
public sealed class SetupEventSampler(TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<int, (DateTimeOffset LoggedAt, int Pending)> _events = [];

    /// <summary>Whether to log event <paramref name="eventId"/> now; <paramref name="count"/> is the occurrences it stands for.</summary>
    public bool ShouldLog(int eventId, out int count)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var (loggedAt, pending) = _events.GetValueOrDefault(eventId, (DateTimeOffset.MinValue, 0));
            pending++;
            if (now - loggedAt < Every)
            {
                _events[eventId] = (loggedAt, pending);
                count = 0;
                return false;
            }

            _events[eventId] = (now, 0);
            count = pending;
            return true;
        }
    }
}

/// <summary>
/// Deletes the sessions of an ended opening once per process and opening when a request finds setup closed
/// (<see cref="SetupMode.DeleteEndedSessionsAsync"/>), so a closed setup's 404s do not each run a delete.
/// </summary>
public sealed class SetupCleanup
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _cleanedFor = DateTimeOffset.MaxValue;

    /// <summary>Runs the delete when the ended opening <paramref name="status"/> names has not been cleaned in this process yet.</summary>
    public async Task AfterClosedAsync(SetupStatus status, SetupMode setup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(setup);
        lock (_lock)
        {
            if (_cleanedFor == status.OpenedAt)
            {
                return;
            }

            _cleanedFor = status.OpenedAt;
        }

        await setup.DeleteEndedSessionsAsync(ct);
    }
}

/// <summary>
/// Evaluates setup mode at kalla-admin's start (<see cref="SetupMode.EvaluateAsync"/>) and, while it is open, writes the setup code to
/// standard output with the wizard's address and when the code expires (identity design §19): never through the logger or a security
/// event. The first try runs within the start, briefly; while the database is away it tries again in the background. A
/// never-completed deployment gets a new opening at its next start after the hour, not by itself: setup is open only while a person
/// with host access has just started or re-opened it, and the code is not written to the output again and again.
/// </summary>
public sealed partial class SetupStartup(IServiceScopeFactory scopes, SetupSecret secret, SetupConsole console, ILoggerFactory loggers, ILogger<SetupStartup> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstTry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);
    private bool _evaluated;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            first.CancelAfter(FirstTry);
            _evaluated = await TryEvaluateAsync(first.Token);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_evaluated && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _evaluated = await TryEvaluateAsync(stoppingToken);
        }
    }

    private async Task<bool> TryEvaluateAsync(CancellationToken ct)
    {
        SetupEvaluation evaluation;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            evaluation = await scope.ServiceProvider.GetRequiredService<SetupMode>().EvaluateAsync(ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or OperationCanceledException or DbUpdateException or ConfigBusyException
                                       or InvalidOperationException { InnerException: Npgsql.NpgsqlException })
        {
            LogEvaluationFailed(logger, ex.GetType().Name);
            return false;
        }

        switch (evaluation.Outcome)
        {
            case SetupEvaluationOutcome.Opened:
                SecurityEvents.SetupOpened(SecurityEvents.CreateLogger(loggers), evaluation.Kind!, evaluation.EndsAt!.Value);
                Print(evaluation);
                break;
            case SetupEvaluationOutcome.StillOpen:
                Print(evaluation);
                break;
            case SetupEvaluationOutcome.CompletedByDeployment:
                LogCompletedByDeployment(logger);
                break;
            case SetupEvaluationOutcome.SecretMissing:
                LogSecretMissing(logger, SetupSecret.FileName, SetupSecret.MinLength);
                break;
            case SetupEvaluationOutcome.RowMissing:
                LogRowMissing(logger);
                break;
            default:
                break;
        }

        return true;
    }

    private void Print(SetupEvaluation evaluation)
    {
        if (secret.Current is not { } key)
        {
            LogSecretMissing(logger, SetupSecret.FileName, SetupSecret.MinLength);
            return;
        }

        LogOpen(logger, evaluation.Kind!, evaluation.EndsAt!.Value);
        if (evaluation.AuthorityWithoutSecret)
        {
            LogAuthorityWithoutSecret(logger);
        }

        var endsAt = evaluation.EndsAt.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        // Standard output only (identity design §19): the logger's records leave the host through the OTLP export.
        var output = console.Out;
        lock (output)
        {
            output.WriteLine($"kalla-admin: setup mode is open until {endsAt}. Setup code: {SetupCode.Compute(key, evaluation.Nonce!)}");
            output.WriteLine("kalla-admin: enter it at /setup on kalla-admin's address (the admin UI's host name, https://<admin host>/setup).");
            output.Flush();
        }
    }

    [LoggerMessage(LogLevel.Warning, "Setup mode could not be evaluated at start ({ExceptionType}); trying again")]
    private static partial void LogEvaluationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Information, "The deployment declares the sign-in (an authority and a client secret): setup counts as completed")]
    private static partial void LogCompletedByDeployment(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Setup mode would open, but the setup secret ({FileName}, at least {MinLength} characters) is missing: setup stays closed until the deployment provides it and kalla-admin starts again")]
    private static partial void LogSecretMissing(ILogger logger, string fileName, int minLength);

    [LoggerMessage(LogLevel.Error, "The settings row is missing (kalla-migrate creates it): setup mode stays closed")]
    private static partial void LogRowMissing(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Setup mode is open because the deployment sets the authority but no client secret (Oidc:ClientSecret)")]
    private static partial void LogAuthorityWithoutSecret(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Setup mode is open ({Kind}) until {EndsAt}; the setup code is on kalla-admin's standard output")]
    private static partial void LogOpen(ILogger logger, string kind, DateTimeOffset endsAt);
}
