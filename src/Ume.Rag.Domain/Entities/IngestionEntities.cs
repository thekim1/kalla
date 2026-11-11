namespace Ume.Rag.Domain.Entities;

// The content tables of P1 ingestion (data-model §2.2, connectors design §2.3, §6.0, §9, §11, §13; issue #34). Written by
// the worker; config rows are referenced by id without a foreign key. Values of the CHECK constraints are the constants
// below; values the design marks P2 are left out until P2 adds them.

/// <summary>A registered stage type and code version (engine design §3.2, §5.2); immutable per pair.</summary>
public sealed class NodeManifestEntry
{
    public required string NodeType { get; set; }
    public int TypeVersion { get; set; }

    /// <summary>The stage's parameter schema (<c>jsonb</c>).</summary>
    public required string ParamsSchema { get; set; }

    /// <summary>Job class, cache class, effect tags, display names, deprecated (<c>jsonb</c>).</summary>
    public required string Metadata { get; set; }

    public DateTimeOffset FirstRegisteredAt { get; set; }
}

/// <summary>Which live worker instance supports which stage version (engine design §5.2); deleted with the instance.</summary>
public sealed class WorkerNodeSupport
{
    public Guid InstanceId { get; set; }
    public required string NodeType { get; set; }
    public int TypeVersion { get; set; }
}

/// <summary>Host kinds of the crawl budget (connectors design §6.1).</summary>
public static class CrawlHostKinds
{
    public const string Municipal = "municipal";
    public const string External = "external";

    public static IReadOnlyList<string> All { get; } = [Municipal, External];
}

/// <summary>The shared politeness budget of one host (<c>content."CrawlHosts"</c>, connectors design §6.1, §6.3).</summary>
public sealed class CrawlHost
{
    /// <summary>Lower-case ASCII (punycode) host, with <c>:port</c> when not the default.</summary>
    public required string Host { get; set; }

    public decimal RequestsPerSecond { get; set; }
    public short MaxConnections { get; set; }
    public DateTimeOffset NextAllowedAt { get; set; }
    public string? RobotsBlobPath { get; set; }
    public DateTimeOffset? RobotsFetchedAt { get; set; }
    public string Kind { get; set; } = CrawlHostKinds.External;
    public string? RobotsStatus { get; set; }
    public int? CrawlDelaySeconds { get; set; }
    public decimal BackoffFactor { get; set; } = 1m;
    public DateTimeOffset? PausedUntil { get; set; }
    public int? LatencyEwmaMs { get; set; }

    /// <summary>Set when a platform admin blocks the host.</summary>
    public DateTimeOffset? BlockedAt { get; set; }
}

/// <summary>
/// The site profile of a (level, host) (<c>content."SiteProfiles"</c>, connectors design §5.6). P1 stores manual and
/// preset versions only; the learning columns (<c>LastLearnedAt</c>, <c>NextLearnAfter</c>) come with P2 (data-model §15).
/// </summary>
public sealed class SiteProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Classification { get; set; }
    public required string Host { get; set; }

    /// <summary>A built-in CMS preset (<c>sitevision</c>).</summary>
    public string? PresetId { get; set; }

    /// <summary>The applied <see cref="SiteProfileVersion"/>; apply and roll back move it.</summary>
    public Guid? AppliedVersionId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Origins of a site profile version in P1 (<c>learned</c> is P2).</summary>
public static class SiteProfileOrigins
{
    public const string Manual = "manual";
    public const string Preset = "preset";

    public static IReadOnlyList<string> All { get; } = [Manual, Preset];
}

/// <summary>States of a site profile version in P1 (<c>proposed</c> and <c>rejected</c> are P2).</summary>
public static class SiteProfileVersionStates
{
    public const string Applied = "applied";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } = [Applied, Superseded];
}

/// <summary>An immutable site profile version (<c>content."SiteProfileVersions"</c>). <c>LearnedFromRunId</c> and <c>Sample</c> are P2.</summary>
public sealed class SiteProfileVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SiteProfileId { get; set; }
    public int Number { get; set; }

    /// <summary>The profile (<c>jsonb</c>, <c>contracts/site-profiles/v1</c>).</summary>
    public required string Profile { get; set; }

    public string Origin { get; set; } = SiteProfileOrigins.Manual;
    public string State { get; set; } = SiteProfileVersionStates.Applied;

    /// <summary><c>system:worker</c> or the admin's audit actor.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public string? AppliedBy { get; set; }
}

/// <summary>Frontier states (connectors design §6.1).</summary>
public static class CrawlFrontierStates
{
    public const string Idle = "idle";
    public const string Pending = "pending";
    public const string Released = "released";

    public static IReadOnlyList<string> All { get; } = [Idle, Pending, Released];
}

/// <summary>
/// One URL of a source's crawl frontier (<c>content."CrawlFrontier"</c>). P1 keeps one frontier per source, keyed
/// (<see cref="Classification"/>, <see cref="SourceId"/>, <see cref="UrlHash"/>) with the source's run, depth and leaf
/// (connectors design §6.0); the shared (level, URL) key and <c>CrawlClaims</c> are P2.
/// </summary>
public sealed class CrawlFrontierEntry
{
    public required string Classification { get; set; }
    public Guid SourceId { get; set; }

    /// <summary>SHA-256 of the canonical URL.</summary>
    public required byte[] UrlHash { get; set; }

    public required string Url { get; set; }

    /// <summary>
    /// The URL as found (canonical except the site profile's identity rule, connectors design §3.1 rule 10), null when it
    /// equals <see cref="Url"/> (#228): the worker requests it, so <c>Documents.Url</c> keeps the readable path form.
    /// </summary>
    public string? FetchUrl { get; set; }

    public required string Host { get; set; }
    public string State { get; set; } = CrawlFrontierStates.Pending;
    public Guid? RunId { get; set; }
    public short Depth { get; set; }
    public bool Leaf { get; set; }
    public DateTimeOffset? NextFetchAt { get; set; }
    public DateTimeOffset? LastFetchedAt { get; set; }
    public short? LastStatus { get; set; }
    public string? RedirectTo { get; set; }

    /// <summary>The last response's validators, replayed as they came.</summary>
    public string? ETag { get; set; }

    public string? LastModified { get; set; }
    public byte[]? ContentHash { get; set; }

    /// <summary>The sitemap's <c>lastmod</c>; cleared when the URL leaves the sitemap.</summary>
    public DateTimeOffset? SitemapLastMod { get; set; }

    /// <summary>A removal-check row (#102); replaces the <c>Depth = -1</c> sentinel.</summary>
    public bool RemovalCheck { get; set; }
}

/// <summary>Per crawl run limits and report data (<c>content."CrawlRuns"</c>, connectors design §6.1, §5.5).</summary>
public sealed class CrawlRun
{
    public Guid RunId { get; set; }
    public required string Classification { get; set; }
    public Guid SourceId { get; set; }
    public int Admitted { get; set; }
    public int MaxPages { get; set; }
    public int MaxDepth { get; set; }
    public bool LimitHit { get; set; }
    public int Seeds { get; set; }
    public int SeedsFailed { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? PausedHosts { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? TrapStats { get; set; }

    /// <summary>A complete traversal (weakness review §3).</summary>
    public bool Complete { get; set; }

    /// <summary>An incremental run (#102): never a complete traversal, so the incomplete-traversal alert skips it.</summary>
    public bool Incremental { get; set; }
}

/// <summary>Checkpoint kinds (connectors design §2.3).</summary>
public static class ConnectorCheckpointKinds
{
    /// <summary>The position of the current listing.</summary>
    public const string Run = "run";

    /// <summary>A durable cursor such as a delta link.</summary>
    public const string State = "state";

    public static IReadOnlyList<string> All { get; } = [Run, State];
}

/// <summary>A connector checkpoint, one per (source, kind) (<c>content."ConnectorCheckpoints"</c>, connectors design §2.3).</summary>
public sealed class ConnectorCheckpoint
{
    public Guid SourceId { get; set; }
    public required string Kind { get; set; }
    public required string Classification { get; set; }
    public int SchemaVersion { get; set; }

    /// <summary><c>jsonb</c>; may hold paths.</summary>
    public required string Checkpoint { get; set; }

    public Guid? RunId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Upload slot states (connectors design §9.1).</summary>
public static class UploadStates
{
    public const string Received = "received";
    public const string Clean = "clean";
    public const string Malware = "malware";
    public const string Refused = "refused";
    public const string Indexed = "indexed";
    public const string Deleted = "deleted";

    public static IReadOnlyList<string> All { get; } = [Received, Clean, Malware, Refused, Indexed, Deleted];
}

/// <summary>
/// One upload slot (<c>content."Uploads"</c>, connectors design §9.1): <see cref="Id"/> is the slot in the document
/// identity <c>upload:&lt;id&gt;</c>, so replacing the file keeps the document.
/// </summary>
public sealed class Upload
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SourceId { get; set; }
    public required string Classification { get; set; }

    /// <summary>The batch of the last change.</summary>
    public Guid BatchId { get; set; }

    public required string FileName { get; set; }
    public string? DeclaredMediaType { get; set; }
    public string? SniffedMediaType { get; set; }
    public long Bytes { get; set; }

    /// <summary>SHA-256 of the current version.</summary>
    public required byte[] Sha256 { get; set; }

    public string State { get; set; } = UploadStates.Received;
    public string? Reason { get; set; }
    public Guid? DocumentId { get; set; }
    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? ReplacedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A deletion candidate of a run (<c>content."DeletionProposals"</c>, connectors design §13.1).</summary>
public sealed class DeletionProposal
{
    public Guid RunId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid SourceId { get; set; }
    public required string Classification { get; set; }

    /// <summary><c>not-seen</c>, <c>http.404</c>, <c>robots.disallow</c>, … (connectors design §13.1).</summary>
    public required string Reason { get; set; }

    public bool Exempt { get; set; }
    public string? Decision { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Where a quarantined file came from (connectors design §11).</summary>
public static class QuarantineOrigins
{
    public const string Upload = "upload";
    public const string Crawl = "crawl";

    public static IReadOnlyList<string> All { get; } = [Upload, Crawl];
}

/// <summary>A file ClamAV found malware in (<c>content."QuarantinedFiles"</c>, connectors design §11).</summary>
public sealed class QuarantinedFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SourceId { get; set; }
    public Guid? RunId { get; set; }
    public required string Classification { get; set; }
    public required byte[] Sha256 { get; set; }
    public required string Signature { get; set; }
    public required string Origin { get; set; }

    /// <summary>The upload's file name (uploads).</summary>
    public string? Name { get; set; }

    /// <summary>The fetched URL (crawls).</summary>
    public string? Url { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset PurgeAfter { get; set; }
}

/// <summary>Document identity kinds of P1 (connectors design §3.4; <c>sharepoint</c> is P2).</summary>
public static class IdentityKinds
{
    public const string Url = "url";
    public const string Upload = "upload";

    public static IReadOnlyList<string> All { get; } = [Url, Upload];
}

/// <summary><c>Documents.FetchMode</c> (data-model §2.2, connectors design §8.1): how the current content was fetched.</summary>
public static class FetchModes
{
    public const string Http = "http";
    public const string Browser = "browser";

    public static IReadOnlyList<string> All { get; } = [Http, Browser];
}

/// <summary>One document per (source, identity) (<c>content."Documents"</c>, data-model §2.2).</summary>
public sealed class Document
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SourceId { get; set; }

    /// <summary>The source's level.</summary>
    public required string Classification { get; set; }

    public required string IdentityKind { get; set; }

    /// <summary>The final canonical URL (or the site profile's verified identity rule), or <c>upload:&lt;slot&gt;</c>.</summary>
    public required string CanonicalIdentity { get; set; }

    /// <summary>SHA-256 of <see cref="CanonicalIdentity"/>.</summary>
    public required byte[] IdentityHash { get; set; }

    /// <summary>The content version.</summary>
    public byte[]? ContentHash { get; set; }

    /// <summary>The noise rules version of <see cref="ContentHash"/>.</summary>
    public short ContentHashVersion { get; set; }

    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? MediaType { get; set; }
    public string? Language { get; set; }

    /// <summary>The upload's file name; never in <see cref="Title"/>.</summary>
    public string? DisplayName { get; set; }

    public string? Description { get; set; }
    public string[]? Keywords { get; set; }
    public DateTimeOffset? SourceModifiedAt { get; set; }

    /// <summary>
    /// HTTP <c>Last-Modified</c> of the last 200 response, as a date: the last chunk date candidate (processing
    /// design §7).
    /// </summary>
    public DateTimeOffset? HttpLastModified { get; set; }

    /// <summary>
    /// The URL's sitemap <c>lastmod</c> at the last 200 response (its frontier row's); a chunk date only while the
    /// host's site profile marks <c>lastmod</c> reliable (processing design §7).
    /// </summary>
    public DateTimeOffset? SitemapLastMod { get; set; }

    public DateTimeOffset? FetchedAt { get; set; }
    public string? CanonicalHint { get; set; }
    public Guid? SiteProfileVersionId { get; set; }

    /// <summary><see cref="FetchModes"/>: rendered by the browser or taken from the HTTP response.</summary>
    public string FetchMode { get; set; } = FetchModes.Http;

    public string? ParseStrategy { get; set; }
    public Guid? ParseProfileId { get; set; }
    public string? ParseOutcome { get; set; }

    /// <summary>The parse output's <see cref="NodeOutput.CacheKey"/>.</summary>
    public byte[]? ParseCacheKey { get; set; }

    public int? PageCount { get; set; }
    public Guid? PipelineVersionId { get; set; }
    public Guid? LiveChunkSetId { get; set; }

    /// <summary>The execution fence (engine design §7.5).</summary>
    public Guid? CurrentExecutionId { get; set; }

    public Guid? LastRunId { get; set; }
    public DateTimeOffset? PiiClearedAt { get; set; }
    public string? PiiClearedBy { get; set; }

    /// <summary>The counts the clearance covers (<c>jsonb</c>, processing design §9.5).</summary>
    public string? PiiClearedCounts { get; set; }

    /// <summary>Set by a rejected deletion (connectors design §13.3).</summary>
    public DateTimeOffset? HeldAt { get; set; }

    public string? HeldBy { get; set; }
    public short ConsecutiveFailures { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

/// <summary>A content-addressed stage output in the blob store (<c>content."NodeOutputs"</c>, data-model §9, engine design §8).</summary>
public sealed class NodeOutput
{
    public required byte[] CacheKey { get; set; }
    public required string Classification { get; set; }
    public required string NodeType { get; set; }
    public int NodeVersion { get; set; }
    public required byte[] ParamsHash { get; set; }
    public required byte[] InputHash { get; set; }
    public required byte[] OutputHash { get; set; }

    /// <summary>A blob key, or a prefix for multi-blob outputs such as the parse output.</summary>
    public required string BlobPath { get; set; }

    public bool IsParseOutput { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>Chunk set states (data-model §2.2): only a <c>live</c> set's chunks are searchable.</summary>
public static class ChunkSetStates
{
    public const string Writing = "writing";
    public const string Live = "live";
    public const string Held = "held";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } = [Writing, Live, Held, Superseded];
}

/// <summary>
/// One chunk-stage output per (document, pipeline version, site-profile version, embedding profile, content version)
/// (<c>content."ChunkSets"</c>, data-model §2.2, engine design §14).
/// </summary>
public sealed class ChunkSet
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DocumentId { get; set; }
    public required string Classification { get; set; }
    public Guid CollectionId { get; set; }
    public Guid PipelineVersionId { get; set; }
    public Guid? SiteProfileVersionId { get; set; }
    public Guid EmbeddingProfileId { get; set; }
    public required byte[] ContentHash { get; set; }

    /// <summary>The chunk stage's input hash; a noise-only change re-keys the set (processing design §12).</summary>
    public required byte[] InputHash { get; set; }

    /// <summary>The stage keys and output hashes the set was built from (<c>jsonb</c>, engine design §3.3).</summary>
    public string? StageKeys { get; set; }

    public string State { get; set; } = ChunkSetStates.Writing;
    public bool PiiFlagged { get; set; }

    /// <summary>When the set was first flagged (#175; set by the worker with the 5103 event's time, kept on re-flag).</summary>
    public DateTimeOffset? PiiFlaggedAt { get; set; }

    /// <summary>The admin parse rules that shaped the set (#134); part of the set's key, null without rules.</summary>
    public string? ParseRuleKey { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? PiiCounts { get; set; }

    public int ChunkCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LiveAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}

/// <summary>Chunk kinds (data-model §2.2): every P1 chunk is a <c>child</c>.</summary>
public static class ChunkKinds
{
    public const string Parent = "parent";
    public const string Child = "child";

    public static IReadOnlyList<string> All { get; } = [Parent, Child];
}

/// <summary>
/// What a chunk's body is (#129 point 2; processing §13.1 stratification), next to its structural <see cref="ChunkKinds"/>:
/// a table row group, a list (list items at least half of the body tokens), else text. Parents are text.
/// </summary>
public static class ChunkContentKinds
{
    public const string Text = "text";
    public const string Table = "table";
    public const string List = "list";

    public static IReadOnlyList<string> All { get; } = [Text, Table, List];
}

/// <summary>
/// One chunk of the collection index (<c>content."Chunks"</c>, data-model §2.2, §3.2). Searchable iff
/// <see cref="DeletedAt"/> is null. Two columns are created by the migration and not mapped: <c>Embedding vector</c>
/// (untyped, so profiles may differ in size; written by binary <c>COPY</c>) and the generated <c>SearchVector tsvector</c>.
/// </summary>
public sealed class Chunk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChunkSetId { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>The source's collection.</summary>
    public Guid CollectionId { get; set; }

    public required string Classification { get; set; }
    public string Kind { get; set; } = ChunkKinds.Child;

    /// <summary><see cref="ChunkContentKinds"/> (#129).</summary>
    public string ContentKind { get; set; } = ChunkContentKinds.Text;

    /// <summary>The second or a later row group of a table over the chunker's large-table limit (#129); the first is false.</summary>
    public bool LargeTable { get; set; }
    public Guid? ParentId { get; set; }
    public int Ordinal { get; set; }
    public string? HeadingPath { get; set; }
    public int? Page { get; set; }
    public int? PageEnd { get; set; }
    public required string Text { get; set; }

    /// <summary>Text plus the compound parts from <c>SwedishDecompounder</c> (data-model §3.1).</summary>
    public required string SearchText { get; set; }

    /// <summary>Leading characters of <see cref="Text"/> repeated from the previous chunk.</summary>
    public short OverlapChars { get; set; }

    public required byte[] TextHash { get; set; }
    public int TokenCount { get; set; }
    public string[]? Tags { get; set; }
    public string? DocumentType { get; set; }
    public DateTimeOffset? SourceModifiedAt { get; set; }

    /// <summary>Children only.</summary>
    public Guid? EmbeddingProfileId { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// (profile, text hash) → embedding (<c>content."EmbeddingCache"</c>, data-model §2.2). The <c>Embedding vector</c>
/// column is created by the migration and not mapped; the cache is read and written with SQL.
/// </summary>
public sealed class EmbeddingCacheEntry
{
    public required string Classification { get; set; }
    public Guid EmbeddingProfileId { get; set; }
    public required byte[] TextHash { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>
/// One frequent lexeme of a collection's live child chunks for one embedding profile (<c>content."KeywordStatistics"</c>,
/// data-model §2.2; knowledge design §6.2): <see cref="Ndoc"/> chunks contain it, out of <see cref="ChunkCount"/> when
/// <see cref="ComputedAt"/>. Only lexemes in at least 10 % of the chunks are kept. Written only by the worker; rag-api
/// builds its keyword stop list from it and runs no <c>ts_stat</c> (#499).
/// </summary>
public sealed class KeywordStatistic
{
    public Guid EmbeddingProfileId { get; set; }
    public Guid CollectionId { get; set; }
    public required string Lexeme { get; set; }
    public int Ndoc { get; set; }
    public int ChunkCount { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>Run kinds (engine design §10.1).</summary>
public static class RunKinds
{
    public const string Sync = "sync";
    public const string RetryFailed = "retry-failed";
    public const string Reprocess = "reprocess";

    public static IReadOnlyList<string> All { get; } = [Sync, RetryFailed, Reprocess];
}

/// <summary>Run triggers of P1 (engine design §10.1; <c>notification</c> is P2 SharePoint).</summary>
public static class RunTriggers
{
    public const string Schedule = "schedule";
    public const string Manual = "manual";
    public const string Upload = "upload";
    public const string Publish = "publish";
    public const string Rollback = "rollback";
    public const string SiteProfile = "site-profile";
    public const string Retry = "retry";
    public const string ConfigChange = "config-change";

    public static IReadOnlyList<string> All { get; } = [Schedule, Manual, Upload, Publish, Rollback, SiteProfile, Retry, ConfigChange];
}

/// <summary>Phases of a sync run (engine design §10.1).</summary>
public static class RunPhases
{
    public const string List = "list";
    public const string Fetch = "fetch";
    public const string Reconcile = "reconcile";
    public const string Done = "done";

    public static IReadOnlyList<string> All { get; } = [List, Fetch, Reconcile, Done];
}

/// <summary>Run states (engine design §10.1).</summary>
public static class RunStates
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
    public const string AwaitingDeletionConfirmation = "awaiting-deletion-confirmation";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Running, Succeeded, Degraded, Failed, AwaitingDeletionConfirmation, Cancelled];
}

/// <summary>One sync or re-process of a source (<c>content."Runs"</c>, data-model §2.2, engine design §10).</summary>
public sealed class Run
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SourceId { get; set; }
    public required string Classification { get; set; }
    public required string Kind { get; set; }
    public required string Trigger { get; set; }
    public Guid? PipelineVersionId { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public string? Phase { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? Report { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? SettingsHashes { get; set; }

    public string State { get; set; } = RunStates.Running;
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int ProposedDeletions { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>A parse quality observation (<c>content."ParseObservations"</c>), input for P2 profile proposals.</summary>
public sealed class ParseObservation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Classification { get; set; }
    public Guid DocumentId { get; set; }
    public required string Domain { get; set; }
    public required string PathPattern { get; set; }
    public string? DocumentType { get; set; }
    public string? Strategy { get; set; }
    public decimal Score { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? Metrics { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>Parse profile origins of P1 (<c>learned</c> is P2).</summary>
public static class ParseProfileOrigins
{
    public const string Builtin = "builtin";
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Builtin, Manual];
}

/// <summary>Parse profile states of P1 (<c>proposed</c>, for learned proposals, is P2).</summary>
public static class ParseProfileStates
{
    public const string Active = "active";
    public const string Disabled = "disabled";

    public static IReadOnlyList<string> All { get; } = [Active, Disabled];
}

/// <summary>A parse rule, built-in or admin-made (<c>content."ParseProfiles"</c>, processing design §3.5).</summary>
public sealed class ParseProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Classification { get; set; }

    /// <summary>The department scope; null = platform.</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public required string Match { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public required string Strategy { get; set; }

    /// <summary><c>jsonb</c>.</summary>
    public string? CleanupRules { get; set; }

    public string Origin { get; set; } = ParseProfileOrigins.Manual;
    public string State { get; set; } = ParseProfileStates.Active;
}

/// <summary>
/// A blob deleted from the database's point of view (<c>content."BlobTombstones"</c>, data-model §9, issue #34): inserted
/// in the transaction that drops the reference, so it rolls back with it on a point-in-time restore; the worker's sweep
/// purges the blob after <c>Settings.BlobPurgeDelayDays</c> and then deletes the row.
/// </summary>
public sealed class BlobTombstone
{
    /// <summary>The blob key (<c>BlobKey</c>, ≤ 1,024 characters).</summary>
    public required string Key { get; set; }

    public DateTimeOffset DeletedAt { get; set; }

    /// <summary>Short code: <c>upload.replaced</c>, <c>upload.deleted</c>, <c>retention.quarantine</c>, ….</summary>
    public string? Reason { get; set; }
}
