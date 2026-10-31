using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// How long a file waits for a ClamAV verdict (connectors design §11, §15: fail closed). A scan without a verdict
/// (<c>clamav.unavailable</c>) requeues the <c>fetch</c> job without an attempt every 30 s; after 24 h of waiting the item
/// ends <c>failed</c> <c>clamav.unavailable</c>. The job row records no time of its first refusal, so the wait is measured
/// from the job's <c>EnqueuedAt</c>, and only once its previous try was refused the same way (<c>LastError</c>): a job that
/// waited for another reason (politeness, a full queue) gets at least one more scan after ClamAV first refuses it.
/// </summary>
public sealed class ScanWait(TimeProvider time)
{
    public const string Unavailable = "clamav.unavailable";

    public static readonly TimeSpan Limit = TimeSpan.FromHours(24);

    /// <summary>The job's file has waited for a verdict longer than <see cref="Limit"/>: it fails instead of requeuing.</summary>
    public bool Expired(string reason, LeasedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return string.Equals(reason, Unavailable, StringComparison.Ordinal)
            && string.Equals(job.LastError, Unavailable, StringComparison.Ordinal)
            && time.GetUtcNow() - job.EnqueuedAt > Limit;
    }
}
