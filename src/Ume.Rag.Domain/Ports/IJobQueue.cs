using System.Data.Common;

namespace Ume.Rag.Domain.Ports;

/// <summary>
/// The job queue seam (performance pass §3.4, §8; data-model §4). The Postgres implementation in
/// <c>Ume.Rag.Infrastructure/Jobs</c> is plain SQL over the unpartitioned <c>content."Jobs"</c>; a broker would sit behind
/// the same members. Every time decision (availability, leases, back-off) uses the database clock. Members that take a
/// <see cref="DbTransaction"/> join the caller's transaction, so a job is created or completed atomically with the domain
/// writes it belongs to; with <c>null</c> they run on their own.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a job queue; the name is fixed by the design (data-model §4).")]
public interface IJobQueue
{
    /// <summary>
    /// Inserts the jobs; a job whose <see cref="JobRequest.DedupKey"/> is already held by a queued or running job is skipped
    /// (<c>ON CONFLICT … DO NOTHING</c>). Wakes the workers of each class that got a job (<c>pg_notify('rag_jobs', class)</c>,
    /// delivered at commit). Returns the ids that were inserted.
    /// </summary>
    Task<IReadOnlySet<Guid>> EnqueueAsync(IReadOnlyList<JobRequest> jobs, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>
    /// The departments with a job of <paramref name="jobClass"/> ready now, for per-department fairness (data-model §4.2),
    /// and, from the same statement, how long until the class's next queued job becomes ready, so an idle dispatcher
    /// knows how long it may sleep.
    /// </summary>
    Task<ReadyDepartments> ReadyDepartmentsAsync(string jobClass, CancellationToken cancellationToken);

    /// <summary>
    /// Leases the next ready job of one class and department (highest priority, then oldest), or null. Sets the lease and
    /// counts an attempt; one job per statement (batched dequeue is P2).
    /// </summary>
    Task<LeasedJob?> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Extends the leases of the owner's running jobs in one statement. Returns the ids still held: a job missing from the
    /// result has lost its lease (reclaimed or cancelled) and must stop without writing.
    /// </summary>
    Task<IReadOnlySet<Guid>> HeartbeatAsync(Guid owner, IReadOnlyList<JobLease> running, CancellationToken cancellationToken);

    /// <summary>Marks a running job of <paramref name="owner"/> succeeded. False when the lease was lost.</summary>
    Task<bool> CompleteAsync(Guid jobId, Guid owner, string? resultJson, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure that counts the attempt. Fatal: <c>failed</c> at once. Transient: back to <c>queued</c> after
    /// <paramref name="retryAfter"/>, or the back-off <c>least(10 s · 2^Attempts, 1 h) ± 20 %</c>; <c>dead</c> once the attempts
    /// are used up. A job back in the queue wakes the workers of its class, as an enqueue does, so they wait for its
    /// new <c>AvailableAt</c>. Returns the new state, or null when the lease was lost.
    /// </summary>
    Task<string?> FailAsync(Guid jobId, Guid owner, string error, bool fatal, TimeSpan? retryAfter, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a running job back without counting the attempt (<c>Attempts − 1</c>): capacity refusals, shutdown
    /// (data-model §4.2). <paramref name="reason"/> is kept in <c>LastError</c>. Wakes the workers of the job's class, as an
    /// enqueue does. False when the lease was lost.
    /// </summary>
    Task<bool> ReleaseAsync(Guid jobId, Guid owner, string reason, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the queued and running jobs of a run except <paramref name="exceptJobId"/>. Running ones lose their lease
    /// and stop at their owner's next heartbeat. Returns the number cancelled.
    /// </summary>
    Task<int> CancelRunAsync(Guid runId, string reason, Guid? exceptJobId, CancellationToken cancellationToken);
}

/// <summary>A job to enqueue (data-model §4.1). The payload is ids and parameters only (≤ 4 KB JSON), never content.</summary>
public sealed record JobRequest(string Class, Guid DepartmentId, string Classification, string Payload)
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Within a class; higher first.</summary>
    public short Priority { get; init; }

    public Guid? TeamId { get; init; }
    public string? DedupKey { get; init; }
    public Guid? RunId { get; init; }

    /// <summary>Delay before the job becomes ready.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Null: the class default (data-model §4.2).</summary>
    public short? MaxAttempts { get; init; }
}

/// <summary>A department with ready jobs of a class: its best priority and its oldest ready job.</summary>
public sealed record ReadyDepartment(Guid DepartmentId, short MaxPriority, DateTimeOffset OldestAvailableAt);

/// <summary>
/// The ready departments of a class, and how long until its next queued job that is not ready yet becomes ready, on the
/// database clock (<see cref="NextReadyIn"/>; null when every queued job of the class is ready or there are none).
/// </summary>
public sealed record ReadyDepartments(IReadOnlyList<ReadyDepartment> Departments, TimeSpan? NextReadyIn);

/// <summary>What to lease: one job of <see cref="Class"/> and <see cref="DepartmentId"/> for <see cref="Owner"/>.</summary>
public sealed record DequeueRequest(string Class, Guid DepartmentId, Guid Owner, TimeSpan Lease);

/// <summary>A running job and the lease length its heartbeat renews.</summary>
public readonly record struct JobLease(Guid JobId, TimeSpan Lease);

/// <summary>A job as leased by <see cref="IJobQueue.DequeueAsync"/>; <see cref="Attempts"/> includes this one.</summary>
public sealed record LeasedJob(
    Guid Id,
    string Class,
    short Priority,
    Guid DepartmentId,
    Guid? TeamId,
    string Classification,
    string? DedupKey,
    string Payload,
    short Attempts,
    short MaxAttempts,
    short Reclaims,
    Guid? RunId,
    string? LastError,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset LeaseUntil);
