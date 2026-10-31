using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.ServiceDefaults;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Tests;

/// <summary>
/// A worker as Program.cs builds it, on the test server, as <c>ume_rag_worker</c>, with short timings: heartbeat 1 s,
/// instance timeout 3 s, reaper 1 s, poll 1 s, stop grace 5 s, no maintenance loop; with <c>engine</c> also the pipeline
/// engine as Program.cs adds it (<c>AddRagEngine</c>). <see cref="KillAsync"/> tears it down
/// without a clean stop, as a crash would: no lease is released and the instance row stays.
/// </summary>
internal sealed class WorkerHost : IAsyncDisposable
{
    public const int HeartbeatSeconds = 1;
    public const int InstanceTimeoutSeconds = 3;
    public const int ReaperSeconds = 1;
    public const int PollSeconds = 1;

    private readonly WebApplication _app;
    private bool _gone;

    private WorkerHost(WebApplication app)
    {
        _app = app;
        InstanceId = app.Services.GetRequiredService<WorkerIdentity>().InstanceId;
    }

    public Guid InstanceId { get; }
    public IServiceProvider Services => _app.Services;

    public static async Task<WorkerHost> StartAsync(RagDatabaseFixture database, Action<IServiceCollection>? services = null,
        IReadOnlyDictionary<string, string?>? settings = null, CapturingLoggerProvider? logs = null, bool engine = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, ApplicationName = "worker-test" });
        builder.WebHost.UseTestServer();
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(RagDatabase.Worker),
            ["Worker:HeartbeatSeconds"] = Invariant(HeartbeatSeconds),
            ["Worker:InstanceTimeoutSeconds"] = Invariant(InstanceTimeoutSeconds),
            ["Worker:ReaperSeconds"] = Invariant(ReaperSeconds),
            ["Worker:PollSeconds"] = Invariant(PollSeconds),
            ["Worker:StopGraceSeconds"] = "5",
            ["Worker:Maintenance"] = "false",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders().AddProvider(logs ?? new CapturingLoggerProvider());
        builder.DefaultWorkerPoolSize();
        builder.AddServiceDefaults();
        builder.AddRagDatabase();
        builder.AddRagConfigMonitor();
        builder.AddRagWorker();
        if (engine)
        {
            builder.AddRagEngine();
        }

        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapDefaultEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.Services.GetRequiredService<WorkerIdentity>().Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return new WorkerHost(app);
    }

    /// <summary>A clean stop: drain, release, unregister.</summary>
    public async Task StopAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        _gone = true;
    }

    /// <summary>Gone without a clean stop: the background services are cancelled and nothing is written.</summary>
    public async Task KillAsync()
    {
        await _app.DisposeAsync();
        _gone = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_gone)
        {
            await StopAsync();
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Command jobs as the admin enqueues them (hand-off H4): <c>ume_rag_admin</c> has S I on <c>Jobs</c>.</summary>
internal static class Commands
{
    public static JobRequest Command(string kind, Guid? departmentId = null, object? parameters = null, string? dedupKey = null)
    {
        var payload = new Dictionary<string, object?> { ["kind"] = kind };
        if (parameters is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(parameters, JsonOptions).EnumerateObject())
            {
                payload[property.Name] = property.Value;
            }
        }

        return new JobRequest(JobClasses.Command, departmentId ?? Guid.NewGuid(), Classification.Internal, JsonSerializer.Serialize(payload, JsonOptions))
        {
            DedupKey = dedupKey,
        };
    }

    public static async Task<Guid> EnqueueAsync(RagDatabaseFixture database, JobRequest job)
    {
        var inserted = await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, TestContext.Current.CancellationToken);
        inserted.ShouldContain(job.Id);
        return job.Id;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public static Task WaitForStateAsync(RagDatabaseFixture database, Guid jobId, string state, TimeSpan? timeout = null) =>
        WaitUntilAsync(async () => (await Sql.JobAsync(database.Connection(null), jobId)).State == state, timeout);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// <c>test-block</c>: records when each job starts, then waits for <see cref="Release"/> (or its token). Started released,
/// it completes at once.
/// </summary>
internal sealed class BlockingCommand(bool released = false) : ICommandHandler
{
    public const string KindName = "test-block";

    private readonly TaskCompletionSource _release = NewReleased(released);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TimeSpan>> _started = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string Kind => KindName;

    public int StartedCount => _started.Values.Count(s => s.Task.IsCompleted);

    public void Release() => _release.TrySetResult();

    /// <summary>When (on this handler's clock) <paramref name="jobId"/> started here.</summary>
    public Task<TimeSpan> StartedAsync(Guid jobId, TimeSpan timeout) =>
        Started(jobId).Task.WaitAsync(timeout, TestContext.Current.CancellationToken);

    public TimeSpan Now => _clock.Elapsed;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        Started(context.Job.Id).TrySetResult(_clock.Elapsed);
        await _release.Task.WaitAsync(cancellationToken);
        return new JobResult.Success();
    }

    private TaskCompletionSource<TimeSpan> Started(Guid jobId) =>
        _started.GetOrAdd(jobId, _ => new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously));

    private static TaskCompletionSource NewReleased(bool released)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (released)
        {
            source.SetResult();
        }

        return source;
    }
}

/// <summary><c>test-refuse-once</c>: the first run of each job finds the config unconfirmed (503), the second succeeds.</summary>
internal sealed class RefuseOnceCommand : ICommandHandler
{
    public const string KindName = "test-refuse-once";

    private readonly ConcurrentDictionary<Guid, int> _runs = new();

    public string Kind => KindName;

    public int Runs(Guid jobId) => _runs.GetValueOrDefault(jobId);

    public Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        if (_runs.AddOrUpdate(context.Job.Id, 1, (_, n) => n + 1) == 1)
        {
            // What ConfigVersionState.EnsureConfirmed throws outside the grace window.
            throw new ApiFaultException(StatusCodes.Status503ServiceUnavailable, "unconfirmed",
                new Dictionary<string, object?> { ["code"] = Infrastructure.Config.ConfigVersionState.UnconfirmedCode });
        }

        return Task.FromResult<JobResult>(new JobResult.Success());
    }
}

/// <summary><c>test-hold</c>: holds a pooled connection through <c>RagDbContext</c> for 50 ms, as a job's short statement would.</summary>
internal sealed class HoldConnectionCommand : ICommandHandler
{
    public const string KindName = "test-hold";

    public string Kind => KindName;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<RagDbContext>();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.05)", cancellationToken);
        return new JobResult.Success();
    }
}
