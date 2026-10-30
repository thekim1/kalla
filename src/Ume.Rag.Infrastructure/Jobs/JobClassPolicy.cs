using System.Collections.Frozen;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// Lease, attempts and default per-instance concurrency of each job class (data-model §4.2, engine design §9.1). The
/// concurrency is the default of the deployment setting <c>Worker:Concurrency:&lt;class&gt;</c>.
/// </summary>
public sealed record JobClassPolicy(string Class, TimeSpan Lease, short MaxAttempts, int DefaultConcurrency)
{
    /// <summary>Every class in dequeue order (data-model §4.2; <c>attachment</c> is P2 and comes right after <c>preview</c>).</summary>
    public static IReadOnlyList<JobClassPolicy> All { get; } =
    [
        new(JobClasses.Preview, TimeSpan.FromMinutes(2), 1, 2),
        new(JobClasses.Attachment, TimeSpan.FromMinutes(2), 3, 2),
        new(JobClasses.Command, TimeSpan.FromMinutes(1), 3, 2),
        new(JobClasses.Sync, TimeSpan.FromMinutes(5), 3, 2),
        new(JobClasses.Fetch, TimeSpan.FromMinutes(2), 5, 8),
        // min(setting, parser convert_slots) from P1; the pilot parser has one slot.
        new(JobClasses.Parse, TimeSpan.FromMinutes(15), 3, 1),
        new(JobClasses.Embed, TimeSpan.FromMinutes(2), 6, 4),
        new(JobClasses.Eval, TimeSpan.FromMinutes(15), 2, 1),
    ];

    private static readonly FrozenDictionary<string, JobClassPolicy> ByClass = All.ToFrozenDictionary(p => p.Class, StringComparer.Ordinal);

    /// <summary>The shortest lease of any class; the lease heartbeat must run at least three times within it.</summary>
    public static TimeSpan ShortestLease { get; } = All.Min(p => p.Lease);

    public static bool IsKnown(string jobClass) => ByClass.ContainsKey(jobClass);

    public static JobClassPolicy For(string jobClass) =>
        ByClass.TryGetValue(jobClass, out var policy) ? policy : throw new ArgumentOutOfRangeException(nameof(jobClass), jobClass, "Unknown job class.");
}
