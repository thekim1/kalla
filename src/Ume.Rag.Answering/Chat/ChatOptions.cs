namespace Ume.Rag.Answering.Chat;

/// <summary>
/// Chat deployment knobs (chat design §8.3, §9.4, §4.1 step 4): <c>Chat:GatewayHeadersTimeoutSeconds</c> (15),
/// <c>Chat:StreamIdleTimeoutSeconds</c> (60), <c>Chat:MaxAnswerDurationSeconds</c> (240), <c>Chat:MessagesPerMinutePerUser</c>
/// (20) and <c>Chat:AllowedLinkHosts</c> (<c>umea.se</c>, <c>*.umea.se</c>). The last two move to <c>Settings.Chat</c> with
/// part c of item 15.
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
}
