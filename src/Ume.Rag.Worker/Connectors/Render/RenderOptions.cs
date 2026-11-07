using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>
/// Deployment settings of the renderer (<c>Render:*</c>, repo-and-deployment §3; connectors design §8.3). Without
/// <see cref="Endpoint"/> there is no renderer and <c>browser</c> sources wait with <c>render.not_configured</c>.
/// </summary>
public sealed partial class RenderOptions
{
    public const string SectionName = "Render";

    /// <summary>The <c>rag-renderer</c> WebSocket URL with its secret path (<c>ws://rag-renderer:3000/&lt;path&gt;</c>); a secret.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Renders at once in this worker; also the most a host's <c>renderPerHost</c> may be (§6.6).</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>A page must be ready within this.</summary>
    public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Requests per page; past them the browser gets 204.</summary>
    public int MaxRequests { get; set; } = 50;

    /// <summary>Bytes fetched per page; past them the browser gets 204.</summary>
    public long MaxBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Ready when <c>load</c> fired and no routed request has been open for this long.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The worker's script and stylesheet cache, in MB.</summary>
    public int AssetCacheMb { get; set; } = 128;

    /// <summary><c>{width}x{height}</c> of the page's viewport; a tall one loads lazy content that fits without scrolling.</summary>
    public string Viewport { get; set; } = "1280x2000";

    /// <summary>Hosts answered with 204 besides the built-in tracker list (<see cref="RenderBlocklist"/>).</summary>
    public IList<string> BlockedHosts { get; set; } = [];

    public bool Configured => !string.IsNullOrWhiteSpace(Endpoint);

    public RenderLimits Limits() => new(MaxRequests, MaxBytes, PageTimeout, QuietPeriod);

    /// <summary>The viewport's width and height.</summary>
    public (int Width, int Height) ViewportSize()
    {
        var match = ViewportPattern().Match(Viewport);
        return match.Success
            ? (int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture))
            : throw new InvalidOperationException("Render:Viewport is not {width}x{height}.");
    }

    [GeneratedRegex("^([1-9][0-9]{2,4})x([1-9][0-9]{2,4})$")]
    internal static partial Regex ViewportPattern();
}

/// <summary>A typo in <c>Render:*</c> fails at start: an endpoint that is not a WebSocket URL with a path, values out of range.</summary>
public sealed class RenderOptionsValidator : IValidateOptions<RenderOptions>
{
    public ValidateOptionsResult Validate(string? name, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Configured && !(Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "ws" or "wss" && endpoint.AbsolutePath.Length > 1))
        {
            failures.Add("Render:Endpoint must be a ws:// or wss:// URL with the renderer's secret path.");
        }

        if (options.MaxConcurrent is < 1 or > 32)
        {
            failures.Add("Render:MaxConcurrent must be between 1 and 32.");
        }

        if (options.PageTimeout < TimeSpan.FromSeconds(1) || options.PageTimeout > TimeSpan.FromMinutes(2))
        {
            failures.Add("Render:PageTimeout must be between 00:00:01 and 00:02:00.");
        }

        if (options.MaxRequests is < 1 or > 1000)
        {
            failures.Add("Render:MaxRequests must be between 1 and 1000.");
        }

        if (options.MaxBytes is < 1024 * 1024 or > 200L * 1024 * 1024)
        {
            failures.Add("Render:MaxBytes must be between 1 MB and 200 MB.");
        }

        if (options.QuietPeriod < TimeSpan.FromMilliseconds(10) || options.QuietPeriod > TimeSpan.FromSeconds(5))
        {
            failures.Add("Render:QuietPeriod must be between 00:00:00.010 and 00:00:05.");
        }

        if (options.AssetCacheMb is < 0 or > 4096)
        {
            failures.Add("Render:AssetCacheMb must be between 0 and 4096.");
        }

        if (!RenderOptions.ViewportPattern().IsMatch(options.Viewport))
        {
            failures.Add("Render:Viewport must be {width}x{height}, such as 1280x2000.");
        }

        failures.AddRange(options.BlockedHosts.Where(h => Uri.CheckHostName(h) != UriHostNameType.Dns)
            .Select(h => $"Render:BlockedHosts contains '{h}', which is not a host name."));
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
