using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// What a job ended with (engine design §5.1, §7.3). <see cref="TransientError"/> with <c>CountsAttempt = false</c> is a
/// capacity refusal: the job is requeued without using an attempt.
/// </summary>
public abstract record JobResult
{
    private JobResult()
    {
    }

    /// <summary>Done; <paramref name="ResultJson"/> is the small <c>Jobs.Result</c> (a command outcome), or null.</summary>
    public sealed record Success(string? ResultJson = null) : JobResult;

    /// <summary>Retry later: after <paramref name="RetryAfter"/>, or the queue's back-off when null.</summary>
    public sealed record TransientError(string Code, TimeSpan? RetryAfter = null, bool CountsAttempt = true) : JobResult;

    /// <summary>Terminal (<c>failed</c>) at once.</summary>
    public sealed record FatalError(string Code) : JobResult;
}

/// <summary>A leased job and the services of its own scope.</summary>
public sealed record JobContext(LeasedJob Job, IServiceProvider Services);

/// <summary>Runs the jobs of one class. One handler per class; registered as a singleton.</summary>
public interface IJobHandler
{
    string JobClass { get; }

    /// <summary>
    /// Runs the job. <paramref name="cancellationToken"/> is cancelled when the lease is lost or the worker stops; the
    /// handler then stops without committing anything.
    /// </summary>
    Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
