using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>
/// Soft-404 pages (connectors design §5.3, Crawlee review 2026-11-07): a host that answers "page not found" with a 200
/// would otherwise replace a document's good content with its error page. Once per host and run the crawl requests a random
/// path that cannot exist (<see cref="ProbeUrl"/>); when the host answers it with an HTML 200 without redirects, the
/// <see cref="Fingerprint"/> of that page is the host's fingerprint for the run, and a page with the same fingerprint
/// ends <c>failed</c> <c>http.soft_404</c> (counted like a 404 toward <c>persistent-failure</c>, never an instant deletion).
/// </summary>
/// <remarks>
/// The fingerprint is a hash of the page's main text (the <c>main</c> element, else the <c>body</c>) after the noise
/// normalisation (§12.2), with whitespace collapsed, not the page's content hash: an error page's markup carries the
/// requested URL (canonical link, <c>og:url</c>, form actions, a language switcher), so the content hash differs per URL
/// and would never match, while its main text is the same for every missing page. A page with no main text has no
/// fingerprint, so a host that serves an empty shell for every path (a JavaScript application) never marks its pages.
/// </remarks>
public static partial class SoftNotFound
{
    public const string Reason = "http.soft_404";

    /// <summary>The probe path's suffix after the random letters: names us in the site's logs.</summary>
    public const string ProbeSuffix = "-ume-rag-404";

    private const int ProbeLetters = 12;

    /// <summary><c>/{12 random letters}-ume-rag-404</c> on the origin of <paramref name="url"/>.</summary>
    public static Uri ProbeUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var letters = RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", ProbeLetters);
        return new Uri(url.GetLeftPart(UriPartial.Authority) + "/" + new string(letters) + ProbeSuffix);
    }

    /// <summary>
    /// The main text's SHA-256 (lower-case hex) of a document the noise normalisation has run on, or null when it has no
    /// main text.
    /// </summary>
    public static string? Fingerprint(IHtmlDocument normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var root = normalized.QuerySelector("main") ?? normalized.Body;
        var text = Whitespace().Replace(root?.TextContent ?? string.Empty, " ").Trim();
        return text.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>
/// The soft-404 fingerprints of a run's hosts (<see cref="SoftNotFound"/>), in memory per worker instance like the trap
/// counters: the first fetch of a host in a run claims the probe and sends it in its own host slot; the host's other
/// fetches wait for its answer. A probe that throws is released, so the next fetch of the host probes again.
/// </summary>
public sealed class SoftNotFoundFingerprints
{
    private readonly ConcurrentDictionary<(Guid Run, string Host), TaskCompletionSource<string?>> _hosts = new();

    /// <summary>True when the caller must probe the host now and then <see cref="Set"/> or <see cref="Release"/> it.</summary>
    public bool Claim(Guid runId, string host) =>
        _hosts.TryAdd((runId, host), new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Set(Guid runId, string host, string? fingerprint)
    {
        if (_hosts.TryGetValue((runId, host), out var probe))
        {
            probe.TrySetResult(fingerprint);
        }
    }

    /// <summary>The probe failed: the fetches waiting for it go on without a fingerprint, the next one probes again.</summary>
    public void Release(Guid runId, string host)
    {
        if (_hosts.TryRemove((runId, host), out var probe))
        {
            probe.TrySetResult(null);
        }
    }

    /// <summary>The host's fingerprint for the run, once its probe answered; null when it has none or was never probed.</summary>
    public Task<string?> GetAsync(Guid runId, string host, CancellationToken cancellationToken) =>
        _hosts.TryGetValue((runId, host), out var probe) ? probe.Task.WaitAsync(cancellationToken) : Task.FromResult<string?>(null);

    /// <summary>Forgets the run's hosts (the traversal was evaluated).</summary>
    public void Drop(Guid runId)
    {
        foreach (var key in _hosts.Keys.Where(k => k.Run == runId))
        {
            Release(key.Run, key.Host);
        }
    }
}
