namespace Ume.Rag.Answering.Chat;

/// <summary>
/// Chat deployment knobs (chat design §8.3, §9.4, §4.1 step 4): <c>Chat:GatewayHeadersTimeoutSeconds</c> (15; a stream's
/// headers only), <c>Chat:StreamIdleTimeoutSeconds</c> (60), <c>Chat:MaxAnswerDurationSeconds</c> (240; a stream's whole read,
/// and a JSON answer's whole call, since its headers come only after the answer, #695), <c>Chat:MessagesPerMinutePerUser</c>
/// (20) and <c>Chat:AllowedLinkHosts</c> (<c>umea.se</c>, <c>*.umea.se</c>). The last two move to <c>Settings.Chat</c> with
/// part c of item 15. <c>Chat:CondenseTimeout</c> (1,200 ms) and <c>Chat:LongQuestionCondenseTimeout</c> (3 s) bound the condense calls,
/// <c>Chat:FollowUpTimeout</c> (2 s; 0 = off) the follow-up call (§9.8), <c>Chat:EscalationTimeout</c> (2 s; 0 = off) the
/// escalation summary's call (§9.9).
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public int GatewayHeadersTimeoutSeconds { get; set; } = 15;
    public int StreamIdleTimeoutSeconds { get; set; } = 60;
    public int MaxAnswerDurationSeconds { get; set; } = 240;
    public int MessagesPerMinutePerUser { get; set; } = 20;
    public string[] AllowedLinkHosts { get; set; } = ["umea.se", "*.umea.se"];

    /// <summary>How long the condense call may take before the raw question is used (chat design §5; #287).</summary>
    public TimeSpan CondenseTimeout { get; set; } = Condenser.DefaultTimeout;

    /// <summary>How long the long-question condense may take before the question's first 2,000 characters are the query (chat design §5.1).</summary>
    public TimeSpan LongQuestionCondenseTimeout { get; set; } = Condenser.DefaultLongTimeout;

    /// <summary>
    /// How long the follow-up call may take before the suggestions are <c>[]</c> (chat design §9.8): <c>[DONE]</c> waits at
    /// most this long; zero switches follow-up suggestions off.
    /// </summary>
    public TimeSpan FollowUpTimeout { get; set; } = FollowUps.DefaultTimeout;

    /// <summary>
    /// How long the escalation summary's call may take before its <c>summary</c> is null (chat design §9.9): <c>[DONE]</c> waits
    /// at most this long (the longer of it and the follow-up budget when both run); zero switches the summary off, and the
    /// route (reason and contacts) still comes.
    /// </summary>
    public TimeSpan EscalationTimeout { get; set; } = Escalation.DefaultTimeout;
}
