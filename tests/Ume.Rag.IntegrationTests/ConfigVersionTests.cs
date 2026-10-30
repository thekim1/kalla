using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Infrastructure;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.ServiceDefaults;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The config-version library (P0 #7, data-model §5, identity design §4.5): a service on the real database with the
/// monitor, a key cache reloaded from the outbox, the grace window, readiness, the log scope and serialised writes.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed partial class ConfigVersionTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A stand-in for config."Keys" (P0 #10) with the config trigger; its own schema keeps it out of the grant checks.
        await Sql.ExecuteAsync(database.Connection(Migrator), """
            CREATE SCHEMA IF NOT EXISTS keyprobe;
            CREATE TABLE IF NOT EXISTS keyprobe."Keys" ("Id" uuid PRIMARY KEY, "RevokedAt" timestamptz);
            CREATE TABLE IF NOT EXISTS keyprobe."Rules" ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
            CREATE OR REPLACE TRIGGER "TR_Keys_ConfigChanged" AFTER INSERT OR UPDATE OR DELETE ON keyprobe."Keys"
                FOR EACH ROW EXECUTE FUNCTION config.rag_config_changed('true');
            """);
    }

    public async ValueTask DisposeAsync() => await Sql.ExecuteAsync(database.Connection(Migrator), "DROP SCHEMA keyprobe CASCADE");

    [Fact]
    public async Task A_revoked_key_is_refused_as_soon_as_the_notification_arrives()
    {
        var cache = new ProbeKeyCache(database.Connection(Migrator));
        await using var service = await StartServiceAsync(cache);
        var keyId = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Migrator), $"""INSERT INTO keyprobe."Keys" VALUES ('{keyId}', NULL)""");
        await WaitUntilAsync(() => cache.Known(keyId));
        cache.IsRevoked(keyId).ShouldBeFalse();

        var revoked = Stopwatch.StartNew();
        await Sql.ExecuteAsync(database.Connection(Migrator), $"""UPDATE keyprobe."Keys" SET "RevokedAt" = now() WHERE "Id" = '{keyId}'""");
        await WaitUntilAsync(() => cache.IsRevoked(keyId));

        // Well inside the 2 s poll: the notification carried it.
        revoked.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        var change = cache.Changes[^1];
        change.FullReload.ShouldBeFalse();
        change.EntityTypes.ShouldBe(["Keys"]);
        change.SecurityRelevant.ShouldBeTrue();
        service.State.Version.ShouldBe(change.ToVersion);
    }

    [Fact]
    public async Task A_missed_notification_is_caught_by_the_poll()
    {
        var cache = new ProbeKeyCache(database.Connection(Migrator));
        await using var service = await StartServiceAsync(cache);
        var before = service.State.Version;

        // A version bump without NOTIFY, as if the notification were lost.
        await Sql.ExecuteAsync(database.Connection(null), $"""
            BEGIN;
            UPDATE config."ConfigVersion" SET "Version" = "Version" + 1, "LastTransactionId" = pg_current_xact_id() WHERE "Id" = 1;
            INSERT INTO config."ConfigOutbox" ("Version", "EntityType", "EntityId", "Operation", "SecurityRelevant")
                SELECT "Version", 'Keys', 'poll-test', 'U', true FROM config."ConfigVersion";
            COMMIT;
            """);

        var caught = Stopwatch.StartNew();
        await WaitUntilAsync(() => service.State.Version > before, TimeSpan.FromSeconds(5));
        caught.Elapsed.ShouldBeLessThan(ConfigVersionMonitor.PollInterval + TimeSpan.FromSeconds(1));
        cache.Changes[^1].EntityTypes.ShouldContain("Keys");
    }

    [Fact]
    public async Task The_first_load_and_a_gap_in_the_outbox_reload_everything()
    {
        var cache = new ProbeKeyCache(database.Connection(Migrator));
        await using var service = await StartServiceAsync(cache);
        cache.Changes[0].FullReload.ShouldBeTrue();
        var before = service.State.Version;

        // Two versions without outbox rows, as if retention had pruned them.
        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE config."ConfigVersion" SET "Version" = "Version" + 2 WHERE "Id" = 1;
            DELETE FROM config."ConfigOutbox";
            """);

        await WaitUntilAsync(() => service.State.Version == before + 2, TimeSpan.FromSeconds(5));
        cache.Changes[^1].FullReload.ShouldBeTrue();
    }

    [Fact]
    public async Task Within_the_grace_window_decisions_and_readiness_hold_then_refuse_with_503()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturingLoggerProvider();
        await using var service = await StartServiceAsync(new ProbeKeyCache(database.Connection(Migrator)), time, logs, maxUnconfirmedSeconds: 5);
        (await GetAsync(service, "/keyed")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The monitor stops (as if Postgres stopped answering): nothing confirms the snapshot any more.
        await service.Monitor.StopAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(4));
        (await GetAsync(service, "/keyed")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(service, "/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        time.Advance(TimeSpan.FromSeconds(2));
        using (var refused = await GetAsync(service, "/keyed"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
            problem.RootElement.GetProperty("code").GetString().ShouldBe(ConfigVersionState.UnconfirmedCode);
        }

        (await GetAsync(service, "/keyed")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await GetAsync(service, "/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        // One security event for the first refusal; the second falls in the same minute.
        logs.Entries.Count(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5004).ShouldBe(1);

        // It resumes once the monitor confirms again.
        await service.Monitor.StartAsync(Ct);
        await WaitUntilAsync(() => service.State.IsConfirmed);
        (await GetAsync(service, "/keyed")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Readiness_waits_for_the_first_confirmed_version()
    {
        var state = new ConfigVersionState(Microsoft.Extensions.Options.Options.Create(new ConfigOptions()), TimeProvider.System,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ApplicationName = "test" }, new LoggerFactory());
        state.IsConfirmed.ShouldBeFalse();
        state.Version.ShouldBe(-1);
        Should.Throw<ApiFaultException>(state.EnsureConfirmed).Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Every_request_logs_under_the_config_version_it_started_with()
    {
        var logs = new CapturingLoggerProvider();
        await using var service = await StartServiceAsync(new ProbeKeyCache(database.Connection(Migrator)), logs: logs);

        (await GetAsync(service, "/keyed")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var entry = logs.Entries.Single(e => e.Message == "keyed request");
        entry.ScopeValue(ConfigVersionLogScope.Key).ShouldBe(service.State.Version);
    }

    [Fact]
    public async Task Two_concurrent_saves_cannot_both_pass_a_cross_rule()
    {
        // Cross-rule of the probe: at most one rule named "default". Without the version lock both saves would read
        // zero and both insert (write skew); with it the second reads after the first commits.
        var firstHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = await Task.WhenAll(
            SaveAsync(onLocked: () => firstHoldsLock.TrySetResult(), holdFor: TimeSpan.FromMilliseconds(500)),
            firstHoldsLock.Task.ContinueWith(_ => SaveAsync(onLocked: null, holdFor: TimeSpan.Zero), Ct, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap());

        results.Count(r => r == "saved").ShouldBe(1);
        results.Count(r => r == "rule").ShouldBe(1);
        (await Sql.ScalarAsync<long>(database.Connection(Migrator), """SELECT count(*) FROM keyprobe."Rules" WHERE "Name" = 'default'""")).ShouldBe(1);

        async Task<string> SaveAsync(Action? onLocked, TimeSpan holdFor)
        {
            await using var db = TestPostgres.Context(database.Connection(Migrator));
            try
            {
                return await db.ExecuteConfigWriteAsync(async (_, ct) =>
                {
                    onLocked?.Invoke();
                    var existing = await db.Database.SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM keyprobe."Rules" WHERE "Name" = 'default'""").SingleAsync(ct);
                    if (existing > 0)
                    {
                        throw new InvalidOperationException("rule");
                    }

                    await db.Database.ExecuteSqlAsync($"""INSERT INTO keyprobe."Rules" VALUES ({Guid.NewGuid()}, 'default')""", ct);
                    await Task.Delay(holdFor, ct);
                    return "saved";
                }, Ct);
            }
            catch (InvalidOperationException ex) when (ex.Message == "rule")
            {
                return "rule";
            }
        }
    }

    [Fact]
    public async Task Other_channels_reach_their_listener_without_config_reads()
    {
        // Issue #21: the worker's rag_jobs rides on the monitor's one listening connection.
        var jobs = new RecordingListener("rag_jobs");
        var cache = new ProbeKeyCache(database.Connection(Migrator));
        await using var service = await StartServiceAsync(cache, listener: jobs);
        await WaitUntilAsync(() => service.Monitor.Refreshes >= 2); // first load done and one poll in: a quiet moment
        var before = service.Monitor.Refreshes;

        var burst = Stopwatch.StartNew();
        await Sql.ExecuteAsync(database.Connection(Migrator),
            string.Join(";", Enumerable.Range(0, 20).Select(i => $"SELECT pg_notify('rag_jobs', 'parse-{i}')")));
        await WaitUntilAsync(() => jobs.Payloads.Count == 20);

        burst.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        jobs.Payloads.ShouldBe(Enumerable.Range(0, 20).Select(i => $"parse-{i}"));
        // At most the one 2 s poll that may fall into the burst; never one read per job notification.
        (service.Monitor.Refreshes - before).ShouldBeLessThanOrEqualTo(1);

        // A config notification still wakes the monitor at once.
        var keyId = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Migrator), $"""INSERT INTO keyprobe."Keys" VALUES ('{keyId}', NULL)""");
        await WaitUntilAsync(() => cache.Known(keyId), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("rag-jobs")]
    [InlineData("Rag_Jobs")]
    [InlineData("jobs; DROP TABLE x")]
    [InlineData("")]
    public void A_channel_name_that_is_not_a_plain_identifier_is_refused(string channel)
    {
        Should.Throw<InvalidOperationException>(() => new ConfigVersionMonitor(null!, null!, null!, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigVersionMonitor>.Instance, [new RecordingListener(channel)]));
    }

    [Fact]
    public async Task A_retried_config_write_saves_its_changes_once()
    {
        // Review of #13: the retrying strategy reuses the context; a failed attempt must not leave entities tracked.
        var action = "retry-test-" + Guid.NewGuid().ToString("N");
        var interceptor = new FailFirstCommit();
        await using var db = new RagDbContext(new DbContextOptionsBuilder<RagDbContext>()
            .UseNpgsql(database.Connection(Migrator), o =>
            {
                RagDbContext.ConfigureNpgsql(o);
                o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null);
            })
            .AddInterceptors(interceptor).Options);
        var attempts = 0;

        await db.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            attempts++;
            db.AuditLog.Add(new AuditLogEntry { Timestamp = DateTimeOffset.UtcNow, Actor = "system:test", Action = action, EntityType = "Test" });
            await db.SaveChangesAsync(ct);
            return 0;
        }, Ct);

        attempts.ShouldBe(2);
        interceptor.Failed.ShouldBeTrue();
        (await Sql.ScalarAsync<long>(database.Connection(null), $"""SELECT count(*) FROM audit."AuditLog" WHERE "Action" = '{action}'""")).ShouldBe(1);
        db.ChangeTracker.Entries<AuditLogEntry>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_handler_is_logged_keeps_the_version_unconfirmed_and_is_retried()
    {
        var logs = new CapturingLoggerProvider();
        var cache = new ProbeKeyCache(database.Connection(Migrator));
        await using var service = await StartServiceAsync(cache, logs: logs);
        var before = service.State.Version;
        cache.FailNext = true;

        await Sql.ExecuteAsync(database.Connection(Migrator), $"""INSERT INTO keyprobe."Keys" VALUES ('{Guid.NewGuid()}', NULL)""");
        await WaitUntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException));
        service.State.Version.ShouldBe(before);
        logs.Entries.First(e => e.Level == LogLevel.Error).Message.ShouldContain(nameof(ProbeKeyCache));

        // The next poll offers the change again.
        await WaitUntilAsync(() => service.State.Version > before, TimeSpan.FromSeconds(5));
        logs.Text.ShouldNotContain("lost its database connection");
    }

    private async Task<TestService> StartServiceAsync(ProbeKeyCache cache, FakeTimeProvider? time = null, CapturingLoggerProvider? logs = null, int maxUnconfirmedSeconds = 90,
        INotificationListener? listener = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, ApplicationName = "config-test" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(Api),
            ["Config:MaxUnconfirmedSeconds"] = maxUnconfirmedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Logging.ClearProviders().AddProvider(logs ?? new CapturingLoggerProvider());
        if (time is not null)
        {
            builder.Services.AddSingleton<TimeProvider>(time);
        }

        builder.AddServiceDefaults();
        builder.AddUmeWebDefaults(1024);
        builder.AddRagConfigMonitor();
        builder.Services.AddSingleton<IConfigChangeHandler>(cache);
        if (listener is not null)
        {
            builder.Services.AddSingleton(listener);
        }

        var app = builder.Build();
        app.UseUmeWebDefaults();
        app.UseConfigVersionLogScope();
        app.MapDefaultEndpoints();
        // A stand-in for a keyed endpoint: every security-relevant decision starts with EnsureConfirmed.
        app.MapGet("/keyed", (ConfigVersionState state, ILogger<ConfigVersionTests> logger) =>
        {
            state.EnsureConfirmed();
            LogKeyedRequest(logger);
            return Results.Ok(state.Version);
        });
        await app.StartAsync(Ct);
        var service = new TestService(app);
        await WaitUntilAsync(() => service.State.IsConfirmed);
        return service;
    }

    private static async Task<HttpResponseMessage> GetAsync(TestService service, string path)
    {
        using var client = service.App.GetTestClient();
        return await client.GetAsync(path, Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10, Ct);
        }
    }

    [LoggerMessage(LogLevel.Information, "keyed request")]
    private static partial void LogKeyedRequest(ILogger logger);

    private sealed class TestService(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App => app;
        public ConfigVersionState State => app.Services.GetRequiredService<ConfigVersionState>();
        public ConfigVersionMonitor Monitor => app.Services.GetServices<IHostedService>().OfType<ConfigVersionMonitor>().Single();

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingListener(string channel) : INotificationListener
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _payloads = new();

        public string Channel => channel;

        public IReadOnlyList<string> Payloads => [.. _payloads];

        public void OnNotification(string payload) => _payloads.Enqueue(payload);
    }

    /// <summary>Throws a transient Npgsql error on the first commit, before it reaches the server.</summary>
    private sealed class FailFirstCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public bool Failed { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Failed)
            {
                Failed = true;
                throw new NpgsqlException("injected transient failure", new TimeoutException());
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>A key cache as P0 #10 builds it: reloads revocations whenever an outbox row of type Keys arrives.</summary>
    private sealed class ProbeKeyCache(string connectionString) : IConfigChangeHandler
    {
        private volatile Dictionary<Guid, bool> _revoked = [];
        private readonly List<ConfigChange> _changes = [];

        public IReadOnlyList<ConfigChange> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public bool Known(Guid keyId) => _revoked.ContainsKey(keyId);

        public bool IsRevoked(Guid keyId) => _revoked.GetValueOrDefault(keyId);

        /// <summary>Makes the next reload throw once, like a key cache bug.</summary>
        public bool FailNext { get; set; }

        public async Task ApplyAsync(ConfigChange change, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("injected handler failure");
            }

            if (change.Affects("Keys"))
            {
                var revoked = new Dictionary<Guid, bool>();
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("""SELECT "Id", "RevokedAt" IS NOT NULL FROM keyprobe."Keys" """, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    revoked[reader.GetGuid(0)] = reader.GetBoolean(1);
                }

                _revoked = revoked;
            }

            lock (_changes)
            {
                _changes.Add(change);
            }
        }
    }
}
