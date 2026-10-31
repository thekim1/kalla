namespace Ume.Rag.Domain.Entities;

/// <summary>
/// The platform's run-time settings, one row (<c>config."Settings"</c>, data-model §2.1; settings rule: run-time values
/// are <c>Settings.&lt;Section&gt;.&lt;Key&gt;</c>, deployment knobs are configuration). No row means the defaults below, as
/// the gateway's <c>GatewaySettings</c>. Sections that are groups of values are <c>jsonb</c> (strings here, as
/// <see cref="Job.Payload"/>); their shapes belong to the code that reads them. Deferred and left out:
/// <c>ChatShreddingEnabled</c> (when A1 allows stored chat content, data-model §15) and <c>Tools</c> (P2 tool hub).
/// </summary>
public sealed class PlatformSettings
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>Reverse proxies whose <c>X-Forwarded-For</c> the admin believes (single addresses or CIDR), as the gateway's.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>The deletion guard trips when more than this share of a source's live documents would go (connectors §13.2).</summary>
    public short DeletionGuardPercent { get; set; } = 20;

    public int DeletionGuardMinCount { get; set; } = 5;
    public int DeletionGuardMaxCount { get; set; } = 500;

    /// <summary>Days audit entries are kept (minimum 90); null keeps them for ever.</summary>
    public int? AuditRetentionDays { get; set; } = 730;

    /// <summary>Days a blob tombstone waits before the purge (= the Postgres backup retention, data-model §9).</summary>
    public int BlobPurgeDelayDays { get; set; } = 35;

    public int QuarantineRetentionDays { get; set; } = 30;
    public DateTimeOffset? RetentionLastRunAt { get; set; }
    public string? RetentionLastRunError { get; set; }

    /// <summary>Fleet-wide worker shares and caps (<c>jsonb</c>); per-instance concurrency is configuration.</summary>
    public string WorkerConcurrency { get; set; } = "{}";

    /// <summary>A run is <c>degraded</c> above this failure ratio (engine design §10.3).</summary>
    public decimal DegradedFailureRatio { get; set; } = 0.05m;

    public int ScheduleJitterMinutes { get; set; } = 90;
    public decimal PreviewMaxUsd { get; set; } = 0.50m;

    /// <summary>Default cron schedules per source type (<c>jsonb</c>, engine design §9.6).</summary>
    public string DefaultSchedules { get; set; } = "{}";

    /// <summary>Crawl politeness, hosts, limits and windows (<c>jsonb</c>, connectors design §3.1, §4–§7).</summary>
    public string Crawl { get; set; } = "{}";

    /// <summary>Personal-data flag rules (<c>jsonb</c>, processing design §9).</summary>
    public string PiiFlagRules { get; set; } = "{}";

    public string[] PiiOwnDomains { get; set; } = [];
    public decimal NerMinScore { get; set; } = 0.80m;
    public string? EvaluationModel { get; set; }
    public string? EvaluationJudgeModel { get; set; }
    public int EvaluationMaxQuestions { get; set; } = 100;

    /// <summary>Platform <c>RetrievalSettings</c> defaults and platform-only retrieval values (<c>jsonb</c>, knowledge §12.1).</summary>
    public string Retrieval { get; set; } = "{}";

    /// <summary>Chat defaults (<c>jsonb</c>, chat design §5, §9.4, §10.1).</summary>
    public string Chat { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Postgres <c>xmin</c>, the concurrency token of an editable aggregate root (data-model §2.1).</summary>
    public uint Version { get; set; }
}

/// <summary>Chat history modes (plan §8a). P1 allows only <see cref="None"/> (weakness review §4, data-model §15).</summary>
public static class HistoryModes
{
    public const string None = "none";

    /// <summary>The modes the schema accepts until A1 allows stored chat content.</summary>
    public static IReadOnlyList<string> All { get; } = [None];
}

/// <summary>The plan §8a matrix, one row per level (<c>config."DataProtectionPolicies"</c>, data-model §2.1).</summary>
public sealed class DataProtectionPolicy
{
    public required string Classification { get; set; }
    public string[] AllowedHistoryModes { get; set; } = [HistoryModes.None];
    public string DefaultHistoryMode { get; set; } = HistoryModes.None;
    public int RetentionDefaultDays { get; set; }
    public int RetentionMaxDays { get; set; }
    public bool EncryptionRequired { get; set; }
    public bool ReviewSecondApprover { get; set; }
    public bool ClientsWithoutHistoryOnly { get; set; }

    /// <summary>The blob master key (<c>rag-blob-internal</c>); null for <c>public</c>, whose blobs are not envelope-encrypted.</summary>
    public string? BlobMasterKeyName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>Embedding profile states (data-model §2.1).</summary>
public static class EmbeddingProfileStates
{
    public const string Active = "active";
    public const string Building = "building";
    public const string Retired = "retired";

    public static IReadOnlyList<string> All { get; } = [Active, Building, Retired];
}

/// <summary>
/// An embedding profile (R3 registry, <c>config."EmbeddingProfiles"</c>). Its chunks get a partial HNSW index of their
/// own on <c>("Embedding"::halfvec(Dimensions))</c>, created by <c>content.rag_ensure_profile_index</c> (data-model §3.2).
/// <see cref="MaxResidency"/> is the ceiling every embed and query-embed call of the profile sends (P1 README §8 O1).
/// The <c>CanaryEmbedding vector</c> column is created by the migration and not mapped (the Pgvector EF plugin is not
/// registered yet); it is read and written with SQL.
/// </summary>
public sealed class EmbeddingProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }

    /// <summary>The gateway model or route alias.</summary>
    public required string GatewayModel { get; set; }

    public int Dimensions { get; set; }

    /// <summary><c>cosine</c>, the only distance the per-profile index supports.</summary>
    public string Distance { get; set; } = "cosine";

    public int MaxInputTokens { get; set; }
    public string? CanaryText { get; set; }
    public bool IsDefault { get; set; }
    public string State { get; set; } = EmbeddingProfileStates.Active;
    public Residency MaxResidency { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>The canary of a profile (processing design §11): a fixed Swedish text whose embedding is recorded once.</summary>
public static class EmbeddingCanaryText
{
    /// <summary>Seeded on every profile; changing it needs a new canary embedding for every profile (P3 profile migration).</summary>
    public const string Default = "Bygglov för altan och plank i Umeå kommun: vad gäller när du vill bygga nära tomtgränsen?";
}

/// <summary>
/// A collection (admin: kunskapsbank): search scope, access and level (<c>config."Collections"</c>, data-model §2.1). Content rows copy its id
/// without a foreign key.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The domain's name for a search scope (data-model §2.1), not a .NET collection type.")]
public sealed class Collection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? OwnerTeamId { get; set; }
    public required string Classification { get; set; }
    public Guid EmbeddingProfileId { get; set; }

    /// <summary><c>RetrievalSettings</c> (<c>jsonb</c>, knowledge design §12.1); unset values fall back to <c>Settings.Retrieval</c>.</summary>
    public string RetrievalSettings { get; set; } = "{}";

    /// <summary>Null until its owner publishes it; unpublished collections are invisible to retrieval except the test panel.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>Source types of P1 (data-model §2.1). <c>sharepoint</c> is P2.</summary>
public static class SourceTypes
{
    public const string UrlList = "url-list";
    public const string Crawl = "crawl";
    public const string Upload = "upload";

    public static IReadOnlyList<string> All { get; } = [UrlList, Crawl, Upload];
}

/// <summary>Source states (data-model §2.1).</summary>
public static class SourceStates
{
    public const string Active = "active";
    public const string Paused = "paused";

    public static IReadOnlyList<string> All { get; } = [Active, Paused];
}

/// <summary>
/// One connector instance, in exactly one collection for life (<c>config."Sources"</c>, data-model §2.1): chunks copy
/// <see cref="CollectionId"/>, so moving a source is a new source.
/// </summary>
public sealed class Source
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CollectionId { get; set; }
    public required string Type { get; set; }
    public required string Name { get; set; }
    public required string Classification { get; set; }

    /// <summary>Connector settings (<c>jsonb</c>, <c>contracts/sources/v1</c>), fields tagged with their effect.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Cron expression; null = the platform default for the source type.</summary>
    public string? Schedule { get; set; }

    public Guid PipelineId { get; set; }
    public bool ContainsThirdPartyText { get; set; } = true;
    public string State { get; set; } = SourceStates.Active;
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// A parameter set for a source type's fixed pipeline template (<c>config."Pipelines"</c>, data-model §2.1); rows with
/// no department are the platform defaults.
/// </summary>
public sealed class Pipeline
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Null = a platform default.</summary>
    public Guid? DepartmentId { get; set; }

    public required string Name { get; set; }
    public required string TemplateId { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>Pipeline version states (data-model §2.1).</summary>
public static class PipelineVersionStates
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } = [Draft, Published, Superseded];
}

/// <summary>
/// A pipeline version: template id, template version and parameters as JSON (<c>contracts/pipeline/v2</c>), immutable
/// once published (<c>config."PipelineVersions"</c>, data-model §2.1). Saved through its <see cref="Pipeline"/>.
/// </summary>
public sealed class PipelineVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PipelineId { get; set; }
    public int Number { get; set; }
    public string State { get; set; } = PipelineVersionStates.Draft;

    /// <summary>The definition (<c>jsonb</c>, engine design §3.1).</summary>
    public required string Definition { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The publish report run (<c>content."EvaluationRuns"</c>, no FK).</summary>
    public Guid? EvaluationRunId { get; set; }
}
