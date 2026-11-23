namespace Kalla.Domain.Entities;

// The chat schema (data-model §2.3; P1b item 1). P1 stores metadata and ratings only (weakness review §4): history mode
// `none`, so no ciphertext column, no DataKeyId and no DataKeys row is written; they exist for when A1 allows content.

/// <summary>Who a conversation belongs to (identity design §1.1, §5.1).</summary>
public static class ChatSubjectKinds
{
    public const string EntraUser = "entra-user";
    public const string OwuiUser = "owui-user";
    public const string Anonymous = "anonymous";
    public const string Application = "application";

    public static IReadOnlyList<string> All { get; } = [EntraUser, OwuiUser, Anonymous, Application];
}

/// <summary>A message's role.</summary>
public static class MessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";

    public static IReadOnlyList<string> All { get; } = [User, Assistant];
}

/// <summary>How a turn ended (chat design §6, §8, §10).</summary>
public static class MessageOutcomes
{
    public const string Answered = "answered";

    /// <summary>Coverage <c>Low</c>, answered from general knowledge behind the caveat line (chat design §10.4).</summary>
    public const string AnsweredGeneral = "answered-general";
    public const string NotFound = "not-found";
    public const string Unavailable = "unavailable";
    public const string Refused = "refused";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Answered, AnsweredGeneral, NotFound, Unavailable, Refused, Error, Cancelled];
}

/// <summary>A message's coverage; <c>skipped</c> = a conversational message (chat design §6.2).</summary>
public static class MessageCoverage
{
    public const string Ok = "ok";
    public const string Low = "low";
    public const string Unknown = "unknown";
    public const string Skipped = "skipped";

    public static IReadOnlyList<string> All { get; } = [Ok, Low, Unknown, Skipped];
}

/// <summary>Where a rating came from (chat design §14).</summary>
public static class FeedbackSources
{
    public const string Api = "api";
    public const string OwuiImport = "owui-import";

    public static IReadOnlyList<string> All { get; } = [Api, OwuiImport];
}

/// <summary>A rating's review state (admin design §8.1).</summary>
public static class FeedbackReviewStates
{
    public const string Open = "open";
    public const string Reviewed = "reviewed";
    public const string Dismissed = "dismissed";

    public static IReadOnlyList<string> All { get; } = [Open, Reviewed, Dismissed];
}

/// <summary>One conversation (<c>chat."Conversations"</c>).</summary>
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The effective level from the first message.</summary>
    public required string Classification { get; set; }

    public Guid AssistantId { get; set; }
    public Guid ClientId { get; set; }
    public required string SubjectKind { get; set; }

    /// <summary>Keycloak <c>sub</c> in P1, Entra <c>oid</c> from P2, the Open WebUI user id, or an HMAC of a widget session.</summary>
    public required string SubjectId { get; set; }

    /// <summary>For example <c>X-OpenWebUI-Chat-Id</c>.</summary>
    public string? ExternalChatId { get; set; }

    public string HistoryMode { get; set; } = HistoryModes.None;
    public int RetentionDays { get; set; }
    public byte[]? TitleCiphertext { get; set; }
    public Guid? TitleDataKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>One wrapped data key per (level, expiry month) bucket (<c>chat."DataKeys"</c>, data-model §7). Unused in P1.</summary>
public sealed class DataKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Classification { get; set; }

    /// <summary>The first day of the expiry month.</summary>
    public DateOnly ExpiryMonth { get; set; }

    public required byte[] WrappedKey { get; set; }
    public required string KeyProvider { get; set; }
    public required string MasterKeyName { get; set; }
    public string? MasterKeyVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A user or assistant turn (<c>chat."Messages"</c>).</summary>
public sealed class Message
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public required string Classification { get; set; }
    public int Seq { get; set; }
    public required string Role { get; set; }

    /// <summary>Null in history mode <c>none</c>.</summary>
    public byte[]? ContentCiphertext { get; set; }

    public Guid? DataKeyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The Open WebUI message id (chat design §11.4); regenerations dedup on it.</summary>
    public string? ExternalMessageId { get; set; }

    /// <summary>The published version that answered; no foreign key.</summary>
    public Guid? AssistantVersionId { get; set; }

    public string? GatewayModel { get; set; }
    public string? GatewayRequestId { get; set; }
    public string? ObservedResidency { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public int? TimeToFirstTokenMs { get; set; }
    public string? Coverage { get; set; }
    public string? Degraded { get; set; }
    public string? Language { get; set; }
    public bool CondenseUsed { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A citation with a snapshot of the cited text (<c>chat."MessageSources"</c>); config and content ids without foreign keys.</summary>
public sealed class MessageSource
{
    public Guid MessageId { get; set; }
    public int Rank { get; set; }
    public required string Classification { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ChunkId { get; set; }
    public Guid CollectionId { get; set; }
    public byte[]? SnippetCiphertext { get; set; }
    public Guid? DataKeyId { get; set; }

    /// <summary>The display number; null when not cited (chat design §9.2).</summary>
    public short? Number { get; set; }

    public int? Page { get; set; }
    public int? PageEnd { get; set; }

    /// <summary>Cited.</summary>
    public bool Used { get; set; }
}

/// <summary>A rating and optional comment (<c>chat."Feedback"</c>, chat design §14).</summary>
public sealed class Feedback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public required string Classification { get; set; }
    public Guid ClientId { get; set; }
    public required string Source { get; set; }

    /// <summary>The Open WebUI feedback id.</summary>
    public string? ExternalId { get; set; }

    /// <summary>−1 or 1.</summary>
    public short Rating { get; set; }

    /// <summary>1–10.</summary>
    public short? Score { get; set; }

    /// <summary>One of the fixed Open WebUI reason keys or <c>other</c>; never free text (admin design §8.1).</summary>
    public string? ReasonCategory { get; set; }

    public byte[]? CommentCiphertext { get; set; }
    public Guid? DataKeyId { get; set; }
    public string ReviewState { get; set; } = FeedbackReviewStates.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Full tool arguments and results (<c>chat."ToolCalls"</c>, tools design §3, §6.11). Unused in P1.</summary>
public sealed class ToolCall
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public required string Classification { get; set; }
    public short CallIndex { get; set; }
    public Guid? ToolServerId { get; set; }
    public required string ToolName { get; set; }
    public Guid? ToolId { get; set; }
    public Guid? DefinitionId { get; set; }
    public Guid? SkillVersionId { get; set; }
    public byte[]? ArgumentsCiphertext { get; set; }
    public byte[]? ResultCiphertext { get; set; }
    public Guid? DataKeyId { get; set; }
    public byte[]? ArgumentsHmac { get; set; }
    public int? ArgumentsBytes { get; set; }
    public int? ResultBytes { get; set; }
    public int? DurationMs { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Statistics that outlive conversations (<c>chat."AssistantDailyTotals"</c>, data-model §2.3, §8); the day is Europe/Stockholm.
/// Tokens and cost are in <see cref="AssistantDailyUsage"/>.
/// </summary>
public sealed class AssistantDailyTotal
{
    public DateOnly Day { get; set; }
    public Guid AssistantId { get; set; }
    public Guid ClientId { get; set; }
    public Guid DepartmentId { get; set; }
    public required string Classification { get; set; }
    public int Conversations { get; set; }
    public int Messages { get; set; }
    public int NotFound { get; set; }

    /// <summary>General-knowledge answers (<c>answered-general</c>, chat design §10.4), apart from <see cref="NotFound"/>.</summary>
    public int GeneralAnswers { get; set; }
    public int Unavailable { get; set; }
    public int Degraded { get; set; }
    public int Errors { get; set; }
    public int Cancelled { get; set; }
    public int UncitedAnswers { get; set; }
    public int ThumbsUp { get; set; }
    public int ThumbsDown { get; set; }
    public int ToolCalls { get; set; }
    public int ToolErrors { get; set; }
    public int AgentLimitHits { get; set; }
}

/// <summary>
/// Tokens and cost of the model calls made for an assistant, per Europe/Stockholm day, client, level and step
/// (<c>chat."AssistantDailyUsage"</c>, data-model §2.3; chat design §8.3, §12.2): the gateway's cost apart from the
/// estimate of calls without one. Written by the api's conversation writer and the admin's test panel.
/// </summary>
public sealed class AssistantDailyUsage
{
    public DateOnly Day { get; set; }
    public Guid AssistantId { get; set; }

    /// <summary>The client the calls were made for; the test panel's and P2 assistant tests' are the <c>admin-console</c> row.</summary>
    public Guid ClientId { get; set; }

    public required string Classification { get; set; }

    /// <summary>The <c>x-fors-step</c> the calls sent: <c>condense</c>, <c>condense-long</c>, <c>query-embed</c>, <c>rerank</c>, <c>chat-answer</c>, <c>follow-ups</c>.</summary>
    public required string Step { get; set; }

    /// <summary>The assistant's department at the time of the calls.</summary>
    public Guid DepartmentId { get; set; }

    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>The gateway's cost (<c>x-fors-cost-usd</c> or the stream comment).</summary>
    public decimal CostUsd { get; set; }

    /// <summary>The calls without a gateway cost, priced from <c>GatewayModels</c> into <see cref="EstimatedCostUsd"/>.</summary>
    public int EstimatedCalls { get; set; }

    public decimal EstimatedCostUsd { get; set; }
}

/// <summary>
/// Turns and their full cost per Europe/Stockholm day, client, level and outcome (<c>chat."AssistantDailyOutcomes"</c>,
/// data-model §2.3; Decisions "Cost per outcome"; chat design §12.2): the sums of the turns' usage records, the gateway's cost
/// apart from the estimate, as <see cref="AssistantDailyUsage"/>. Written by the api's conversation writer and the admin's test panel.
/// </summary>
public sealed class AssistantDailyOutcome
{
    public DateOnly Day { get; set; }
    public Guid AssistantId { get; set; }

    /// <summary>The client the turns were made for; the test panel's are the <c>admin-console</c> row.</summary>
    public Guid ClientId { get; set; }

    public required string Classification { get; set; }

    /// <summary>One of <see cref="MessageOutcomes.All"/>; a usage-only turn is <c>unavailable</c> or <c>error</c>.</summary>
    public required string Outcome { get; set; }

    /// <summary>The assistant's department at the time of the turns.</summary>
    public Guid DepartmentId { get; set; }

    public int Turns { get; set; }

    /// <summary>The gateway's cost of the turns' calls.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>The estimate of the turns' calls without a gateway cost.</summary>
    public decimal EstimatedCostUsd { get; set; }
}

/// <summary>The watermark of the Open WebUI feedback import per client (<c>chat."FeedbackImportState"</c>, chat design §14.2).</summary>
public sealed class FeedbackImportState
{
    /// <summary>The config client id; no foreign key.</summary>
    public Guid ClientId { get; set; }

    public long WatermarkUpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastOutcome { get; set; }
    public int Unmapped { get; set; }

    /// <summary>Failed import runs in a row (Open WebUI unreachable, 401/403); 0 after a run that imported (chat §14.3).</summary>
    public int FailedRuns { get; set; }

    /// <summary>The import alerts from this many failed runs in a row (chat §14.3).</summary>
    public const int AlertAfterFailedRuns = 4;
}
