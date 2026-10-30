using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// Readiness: unhealthy (<c>config_unconfirmed</c>) only when the snapshot is outside the grace window or was never
/// loaded. Within the window it stays healthy, so one stalled database does not drain every instance at the same moment
/// (data-model §5, identity design §4.5).
/// </summary>
public sealed class ConfigConfirmedHealthCheck(ConfigVersionState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsConfirmed
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(ConfigVersionState.UnconfirmedCode));
}
