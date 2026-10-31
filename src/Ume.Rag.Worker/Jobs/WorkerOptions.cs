using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Jobs;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// Deployment settings of the worker (<c>Worker:*</c>, repo-and-deployment §3; engine design §9). Validated at start:
/// unknown classes fail.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Comma list of the job classes this instance runs (env <c>Worker__Classes=parse,embed</c>); empty runs every class. A
    /// class without a handler in this build is not dequeued.
    /// </summary>
    public string? Classes { get; set; }

    /// <summary>Per-instance concurrency per class (<c>Worker:Concurrency:&lt;class&gt;</c>); defaults in <see cref="JobClassPolicy"/>.</summary>
    public Dictionary<string, int> Concurrency { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Runs the nightly maintenance (partitions, terminal-job cleanup); on exactly one instance until P2.</summary>
    public bool Maintenance { get; set; } = true;

    /// <summary>
    /// Instance heartbeat and job-lease renewal (10 s, so the 30 s instance timeout tolerates two missed heartbeats; at most a third of the shortest lease).
    /// </summary>
    [Range(1, 20)]
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>
    /// The reaper reclaims the jobs of an instance silent for this long: 30 s, so two heartbeats (10 s) may be missed. With the 10 s reaper a
    /// killed worker's jobs are back in the queue at most 40 s after the kill (P0 #20 done-when: within 45 s; settled by
    /// the main session, data-model §4.2).
    /// </summary>
    [Range(2, 600)]
    public int InstanceTimeoutSeconds { get; set; } = 30;

    /// <summary>Reaper interval (10 s; one indexed statement under an advisory lock per pass).</summary>
    [Range(1, 300)]
    public int ReaperSeconds { get; set; } = 10;

    /// <summary>Dispatcher poll per class when no wake-up arrives (2 s, data-model §4.3).</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;

    /// <summary>On stop, running jobs get this long to finish before they are cancelled and released (90 s).</summary>
    [Range(0, 600)]
    public int StopGraceSeconds { get; set; } = 90;

    /// <summary>The run finalizer's interval (30 s, engine design §10.4); one advisory-locked pass per tick across instances.</summary>
    [Range(1, 600)]
    public int FinalizerSeconds { get; set; } = 30;

    /// <summary>The scheduler's tick (60 s, engine design §9.6); one advisory-locked pass per tick across instances.</summary>
    [Range(1, 600)]
    public int SchedulerSeconds { get; set; } = 60;

    /// <summary>The configured classes in dequeue order.</summary>
    public IReadOnlyList<string> ClassList()
    {
        var configured = (Classes ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return configured.Length == 0
            ? [.. JobClassPolicy.All.Select(p => p.Class)]
            : [.. JobClassPolicy.All.Select(p => p.Class).Where(c => configured.Contains(c, StringComparer.Ordinal))];
    }

    public int ConcurrencyOf(string jobClass) =>
        Concurrency.TryGetValue(jobClass, out var value) ? value : JobClassPolicy.For(jobClass).DefaultConcurrency;
}

/// <summary>The checks data annotations cannot express.</summary>
public sealed class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    public const int MaxConcurrency = 64;

    public ValidateOptionsResult Validate(string? name, WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var value in (options.Classes ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!JobClassPolicy.IsKnown(value))
            {
                failures.Add($"Worker:Classes contains the unknown job class '{value}'.");
            }
        }

        foreach (var (jobClass, concurrency) in options.Concurrency)
        {
            if (!JobClassPolicy.IsKnown(jobClass))
            {
                failures.Add($"Worker:Concurrency names the unknown job class '{jobClass}'.");
            }
            else if (concurrency is < 1 or > MaxConcurrency)
            {
                failures.Add($"Worker:Concurrency:{jobClass} must be between 1 and {MaxConcurrency}.");
            }
        }

        if (options.HeartbeatSeconds * 3 > JobClassPolicy.ShortestLease.TotalSeconds)
        {
            failures.Add($"Worker:HeartbeatSeconds must renew the shortest lease ({JobClassPolicy.ShortestLease.TotalSeconds} s) at least three times.");
        }

        if (options.InstanceTimeoutSeconds < options.HeartbeatSeconds * 2)
        {
            failures.Add("Worker:InstanceTimeoutSeconds must be at least two heartbeats, so one late heartbeat does not reclaim live work.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
