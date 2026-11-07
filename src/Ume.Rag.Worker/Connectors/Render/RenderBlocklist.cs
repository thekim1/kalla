using Microsoft.Extensions.Options;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>
/// Hosts a rendered page never reaches (connectors design §8.3, "Not fetched"): analytics, tag managers and chat
/// widgets, which add requests and no content, plus <c>Render:BlockedHosts</c>. A host matches itself and its
/// subdomains. Their requests are answered with an empty 204.
/// </summary>
public sealed class RenderBlocklist(IOptions<RenderOptions> options)
{
    /// <summary>The built-in list.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        // Analytics and tag managers.
        "google-analytics.com", "analytics.google.com", "googletagmanager.com", "googleadservices.com", "doubleclick.net",
        "connect.facebook.net", "hotjar.com", "hotjar.io", "clarity.ms", "siteimproveanalytics.com", "siteimproveanalytics.io",
        "matomo.cloud", "piwik.pro", "plausible.io",

        // Chat widgets.
        "intercom.io", "intercomcdn.com", "tawk.to", "crisp.chat", "driftt.com", "zopim.com", "zdassets.com",
    ];

    private readonly HashSet<string> _hosts = new(BuiltIn.Concat(options.Value.BlockedHosts).Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()), StringComparer.Ordinal);

    public bool Blocks(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        while (true)
        {
            if (_hosts.Contains(host))
            {
                return true;
            }

            var dot = host.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return false;
            }

            host = host[(dot + 1)..];
        }
    }
}
