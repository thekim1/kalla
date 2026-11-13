using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// An unusable gateway key and a spent budget as chat errors (chat design §3.6, B-44): <c>503 assistant_unavailable</c>, its
/// message naming the reason and never the key, and <c>402 budget_exceeded</c> with the gateway's message (it names the scope,
/// gateway-changes §5.4). The turn's outcome is <c>unavailable</c> (<see cref="ChatException.Unavailable"/>).
/// </summary>
public static class ChatRefusals
{
    public const string AssistantUnavailableCode = "assistant_unavailable";
    public const string BudgetExceededCode = "budget_exceeded";

    /// <summary><c>503 assistant_unavailable</c> for <paramref name="reason"/> (the mirror's, or the gateway's refusal code).</summary>
    public static ChatException AssistantUnavailable(string reason) => new(503, AssistantUnavailableCode, reason switch
    {
        "disabled" or "key_disabled" => "The assistant is unavailable: its gateway key is disabled.",
        "expired" or "key_expired" => "The assistant is unavailable: its gateway key has expired.",
        "revoked" or "key_revoked" => "The assistant is unavailable: its gateway key has been revoked.",
        "owner_inactive" => "The assistant is unavailable: the team or department of its gateway key is inactive.",
        "missing" => "The assistant is unavailable: its gateway key is not known to the platform.",
        "delegation_not_allowed" => "The assistant is unavailable: the gateway does not let this platform use its key.",
        _ => "The assistant is unavailable: its gateway key cannot be used now.",
    })
    {
        Unavailable = true,
    };

    /// <summary><c>402 budget_exceeded</c> with the gateway's message (the key's, its team's or its department's budget).</summary>
    public static ChatException BudgetExceeded(string? gatewayMessage) =>
        new(402, BudgetExceededCode, string.IsNullOrWhiteSpace(gatewayMessage) ? "The budget for the model service is used up." : gatewayMessage)
        {
            Unavailable = true,
        };

    /// <summary>The chat error of a refusal of the object's key (B-42's typed refusal).</summary>
    public static ChatException Of(GatewayKeyRefusedException refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        return refused.Status == 402 ? BudgetExceeded(refused.GatewayMessage) : AssistantUnavailable(refused.Code);
    }
}
