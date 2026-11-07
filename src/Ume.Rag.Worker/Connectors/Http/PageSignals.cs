using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Connectors.Http;

/// <summary>
/// What the fetcher reads from the start of an HTML body (connectors design §3.2, §5.3): a <c>meta</c> refresh that acts as
/// a redirect, and the markers of a bot challenge or WAF block. Only the first <see cref="HeadBytes"/> are read, as text
/// (the markers and the tag are ASCII, so the page's charset does not matter); the DOM is built later, once, by the store step.
/// </summary>
internal static partial class PageSignals
{
    /// <summary>How much of a body is read for the signals: the <c>head</c> of a page, or the whole of a challenge page.</summary>
    public const int HeadBytes = 32 * 1024;

    /// <summary>A refresh with a longer delay is the page reloading itself or a notice, not a redirect (§3.2).</summary>
    public const double MaxRefreshSeconds = 5;

    /// <summary>The response header Cloudflare sets on a challenge (<c>cf-mitigated: challenge</c>).</summary>
    public const string MitigatedHeader = "cf-mitigated";

    /// <summary>Markers only a challenge or block page carries: Cloudflare's challenge options, Incapsula's block page.</summary>
    private static readonly string[] ChallengeMarkers = ["_cf_chl_opt", "Incapsula incident ID"];

    /// <summary>
    /// Markers ordinary pages can carry too (a Turnstile widget in a form, Incapsula's injected script, Cloudflare's error
    /// layout): a challenge only on a 403.
    /// </summary>
    private static readonly string[] ForbiddenMarkers =
        ["challenges.cloudflare.com/turnstile", "_Incapsula_Resource", "Attention Required! | Cloudflare", "cf-error-details"];

    /// <summary>The start of a body as text, at most <see cref="HeadBytes"/>.</summary>
    public static async Task<string> ReadHeadAsync(Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var buffer = new byte[HeadBytes];
        var read = await body.ReadAtLeastAsync(buffer, HeadBytes, throwOnEndOfStream: false, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>A bot challenge or WAF block (§5.3): never solved or evaded, the fetch is transient.</summary>
    /// <param name="head">The start of the body (<see cref="ReadHeadAsync"/>).</param>
    /// <param name="forbidden">The page came with a 403, where the weaker markers count too.</param>
    public static bool IsChallenge(string head, bool forbidden)
    {
        ArgumentNullException.ThrowIfNull(head);
        return ChallengeMarkers.Any(m => head.Contains(m, StringComparison.Ordinal))
            || (forbidden && ForbiddenMarkers.Any(m => head.Contains(m, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The target of a <c>&lt;meta http-equiv="refresh" content="N;url=…"&gt;</c> with N ≤ <see cref="MaxRefreshSeconds"/>,
    /// as written (resolve it against the page's URL); null when there is none, it is slower, or it names no URL (a reload).
    /// The content is read as browsers do (HTML Living Standard, "shared declarative refresh steps"): the delay, an optional
    /// <c>;</c> or <c>,</c>, an optional <c>url=</c> and the URL, optionally quoted.
    /// </summary>
    public static string? MetaRefresh(string head)
    {
        ArgumentNullException.ThrowIfNull(head);
        foreach (Match tag in MetaTag().Matches(head))
        {
            string? equiv = null;
            string? content = null;
            foreach (Match attribute in Attribute().Matches(tag.Groups["attributes"].Value))
            {
                var name = attribute.Groups["name"].Value;
                if (name.Equals("http-equiv", StringComparison.OrdinalIgnoreCase))
                {
                    equiv = attribute.Groups["value"].Value;
                }
                else if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                {
                    content = WebUtility.HtmlDecode(attribute.Groups["value"].Value);
                }
            }

            if (string.Equals(equiv?.Trim(), "refresh", StringComparison.OrdinalIgnoreCase) && content is not null)
            {
                return RefreshTarget(content);
            }
        }

        return null;
    }

    private static string? RefreshTarget(string content)
    {
        var match = RefreshContent().Match(content);
        if (!match.Success
            || !double.TryParse(match.Groups["delay"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var delay)
            || delay > MaxRefreshSeconds)
        {
            return null;
        }

        var url = match.Groups["url"].Value.Trim();
        if (url.Length > 0 && url[0] is '"' or '\'')
        {
            var end = url.IndexOf(url[0], 1);
            url = end < 0 ? url[1..] : url[1..end];
        }

        return url.Trim() is { Length: > 0 } target ? target : null;
    }

    [GeneratedRegex(@"<meta\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"(?<name>[^\s=/>]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+)))?", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^\s*(?<delay>\d+(?:\.\d*)?|\.\d+)[\d.]*\s*(?:[;,]\s*)?(?:url\s*=\s*)?(?<url>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RefreshContent();
}
