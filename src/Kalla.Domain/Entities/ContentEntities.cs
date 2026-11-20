namespace Kalla.Domain.Entities;

/// <summary>Job classes of the queue, in dequeue order (data-model §4.2). <c>attachment</c> is P2.</summary>
public static class JobClasses
{
    public const string Preview = "preview";
    public const string Command = "command";
    public const string Sync = "sync";
    public const string Fetch = "fetch";
    public const string Parse = "parse";
    public const string Embed = "embed";
    public const string Eval = "eval";
    public const string Attachment = "attachment";

    public static IReadOnlyList<string> All { get; } = [Preview, Command, Sync, Fetch, Parse, Embed, Eval, Attachment];
}

/// <summary>Job states (data-model §4.1). Only <c>queued</c> and <c>running</c> hold a dedup key.</summary>
public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Dead = "dead";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Queued, Running, Succeeded, Failed, Dead, Cancelled];
}

/// <summary>
/// A row of the unpartitioned queue <c>content."Jobs"</c> (data-model §4.1). The payload holds ids and parameters only,
/// never content.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset EnqueuedAt { get; set; }
    public required string Class { get; set; }

    /// <summary>Within a class; higher first.</summary>
    public short Priority { get; set; }

    public Guid DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public required string Classification { get; set; }
    public string? DedupKey { get; set; }

    /// <summary>JSON (<c>jsonb</c>).</summary>
    public required string Payload { get; set; }

    public string State { get; set; } = JobStates.Queued;
    public DateTimeOffset AvailableAt { get; set; }
    public short Attempts { get; set; }
    public short MaxAttempts { get; set; }

    /// <summary>Reclaims after an instance died; not attempts (data-model §4.2).</summary>
    public short Reclaims { get; set; }

    /// <summary><see cref="WorkerInstance.InstanceId"/> of the lease holder.</summary>
    public Guid? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? RunId { get; set; }

    /// <summary>Small JSON (<c>jsonb</c>): preview summary, command outcome.</summary>
    public string? Result { get; set; }

    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A running worker (heartbeat 15 s; data-model §2.2, engine design §5.2).</summary>
public sealed class WorkerInstance
{
    public Guid InstanceId { get; set; }
    public required string BuildVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>The job classes this instance runs (<c>Worker:Classes</c>).</summary>
    public string[] Classes { get; set; } = [];
}

/// <summary>
/// One document outcome of a run, in the day-partitioned <c>content."RunItems"</c> (data-model §2.2, §4.4). The table
/// is created by raw SQL in the migration (EF cannot create a partitioned table), so the entity is excluded from
/// migrations; the primary key is (<see cref="CreatedAt"/>, <see cref="Id"/>).
/// </summary>
public sealed class RunItem
{
    public long Id { get; set; }

    /// <summary>The partition key (UTC day).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public Guid RunId { get; set; }
    public required string Classification { get; set; }
    public Guid? DocumentId { get; set; }
    public required string Identity { get; set; }
    public required string Outcome { get; set; }
    public string? Reason { get; set; }
    public string? Strategy { get; set; }
    public Guid? PipelineVersionId { get; set; }
    public Guid? ParseProfileId { get; set; }
    public Guid? ExecutionId { get; set; }
    public int? DurationMs { get; set; }
    public decimal? CostUsd { get; set; }
    public byte[]? ParserBuildHash { get; set; }
}
