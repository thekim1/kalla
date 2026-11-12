using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// The worker's reasons for a gateway answer about the knowledge base's key (engine design §7.3, §7.7; gateway-changes §5.7):
/// a spent budget or an unusable key waits until the next full hour with an attempt counted, and after the job's last attempt
/// the document fails with the reason; a call made as the key whose answer lacks the echo is final (event 5011, logged by
/// <see cref="ResidencyCeilingHandler"/>).
/// </summary>
public static class GatewayRefusals
{
    /// <summary><c>402 budget_exceeded</c>: the key's, its team's or its department's budget is used up.</summary>
    public const string BudgetExhausted = "gateway.budget_exhausted";

    /// <summary><c>403 delegation_not_allowed</c>, <c>key_disabled</c>, <c>key_revoked</c> or <c>key_expired</c> on a call made as the key.</summary>
    public const string KeyUnavailable = "gateway.key_unavailable";

    /// <summary>A <c>2xx</c> answer to a call made as the key without its echo, or with another id.</summary>
    public const string DelegationNotApplied = "gateway.delegation_not_applied";

    /// <summary>
    /// The reason of a refusal of the key, by its code alone: a <c>402</c> is the budget whatever its code, and the status of a
    /// revoked or expired key differs between gateway versions (gateway-changes §5.2).
    /// </summary>
    public static string Reason(GatewayKeyRefusedException refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        return refused.Code switch
        {
            "delegation_not_allowed" or "key_disabled" or "key_revoked" or "key_expired" => KeyUnavailable,
            _ => BudgetExhausted,
        };
    }

    /// <summary>Whether <paramref name="reason"/> is a wait that ends the document after the job's last attempt.</summary>
    public static bool Waits(string reason) => reason is BudgetExhausted or KeyUnavailable;

    /// <summary>The time until the next full hour (UTC), when a spent budget or an unusable key is tried again.</summary>
    public static TimeSpan UntilNextHour(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1) - now;
    }
}
