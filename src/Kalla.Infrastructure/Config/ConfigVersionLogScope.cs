using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kalla.Infrastructure.Config;

/// <summary>
/// The <c>ConfigVersion</c> log scope of every request and job (repo-and-deployment §9, data-model §5): the version the
/// work started under. Scopes are shared by every logger of the process's logger factory.
/// </summary>
public static class ConfigVersionLogScope
{
    public const string Key = "ConfigVersion";

    /// <summary>Opens the scope for a job or any other unit of work outside a request.</summary>
    public static IDisposable? Begin(ILogger logger, ConfigVersionState state)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(state);
        return logger.BeginScope(new Dictionary<string, object> { [Key] = state.Version });
    }

    /// <summary>Opens the scope around every request (early in the pipeline, so the request's logs carry it).</summary>
    public static IApplicationBuilder UseConfigVersionLogScope(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var state = app.ApplicationServices.GetRequiredService<ConfigVersionState>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConfigVersionLogScope));
        return app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            using (Begin(logger, state))
            {
                await next(context);
            }
        });
    }
}
