namespace Ume.Rag.Pipelines.Jobs;

/// <summary>Patterns and enums shared by the job contracts.</summary>
internal static class ContractPatterns
{
    /// <summary>A SHA-256 as lower-case hex (cache keys, URL hashes).</summary>
    public const string Sha256Hex = "^[0-9a-f]{64}$";

    /// <summary>The data levels (<c>Ume.Rag.Domain.Classification</c>).</summary>
    public const string LevelEnum = "[\"public\", \"internal\", \"sensitive\"]";

    public const string StageEnum = "[\"fetch\", \"scan\", \"detect\", \"parse\", \"clean\", \"chunk\", \"personal-data\", \"embed\", \"index\"]";

    /// <summary>A W3C <c>traceparent</c> header value.</summary>
    public const string TraceParent = "^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$";

    /// <summary>An <c>&lt;area&gt;.&lt;code&gt;</c> reason (engine design §10.2).</summary>
    public const string Reason = "^[a-z0-9_-]+(\\.[a-z0-9_-]+)+$";
}
