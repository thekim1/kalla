using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Connectors.Crawl;

/// <summary>Whether a URL may become a frontier row of the source (connectors design §5.2).</summary>
/// <param name="Admitted">In scope.</param>
/// <param name="Leaf">An off-site URL admitted by an explicit <c>include</c>: fetched, its links never followed.</param>
/// <param name="Reason">Why not: <c>scope.excluded</c>, <c>scope.not_included</c>, <c>scope.off_site</c>, <c>scope.type</c>.</param>
public sealed record ScopeDecision(bool Admitted, bool Leaf, string? Reason)
{
    public static ScopeDecision In { get; } = new(true, false, null);

    public static ScopeDecision OffSiteLeaf { get; } = new(true, true, null);

    public static ScopeDecision Out(string reason) => new(false, false, reason);
}

/// <summary>
/// A crawl source's scope (connectors design §5.2): the scope hosts are the start URLs' hosts and <c>extraHosts</c>;
/// <c>exclude</c> wins over <c>include</c>, an empty <c>include</c> means everything on the scope hosts; a link to another
/// host is admitted only when an <c>include</c> glob names that host literally, and then as a leaf. The patterns match the
/// URL's path form, without the site profile's identity rule (an editor's glob over a section of a Sitevision host
/// matches its pages, which the identity rule maps to bare node ids; pilot set-up, 2026-11-01). URLs whose extension
/// names a type outside the pipeline's formats, or one of the URL's host's site profile <c>excludedTypes</c> (images and
/// video by default, also for hosts without a profile), are not admitted: <c>.zip</c> and <c>.exe</c> are never fetched;
/// a response of such a type is not read past its headers (<see cref="AcceptedMediaTypes"/>). <c>demote</c> patterns change no crawling
/// (weakness review §3): they are compiled with the scope, so an invalid one fails like an invalid <c>include</c>, and
/// <see cref="DemoteFactor"/> gives the rank multiplier retrieval applies from <c>Sources.DemoteRules</c>.
/// </summary>
public sealed class CrawlScope
{
    public const string Excluded = "scope.excluded";
    public const string NotIncluded = "scope.not_included";
    public const string OffSite = "scope.off_site";
    public const string ExcludedType = "scope.type";

    /// <summary>The site profile's default <c>excludedTypes</c> (<c>contracts/site-profiles/v1</c>).</summary>
    public static IReadOnlyList<string> DefaultExcludedTypes { get; } = ["image/*", "video/*"];

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["html"] = "text/html", ["htm"] = "text/html", ["xhtml"] = "application/xhtml+xml", ["php"] = "text/html",
        ["asp"] = "text/html", ["aspx"] = "text/html", ["jsp"] = "text/html",
        ["pdf"] = "application/pdf", ["txt"] = "text/plain", ["md"] = "text/markdown", ["csv"] = "text/csv",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["odt"] = "application/vnd.oasis.opendocument.text", ["ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        ["odp"] = "application/vnd.oasis.opendocument.presentation",
        ["doc"] = "application/msword", ["xls"] = "application/vnd.ms-excel", ["ppt"] = "application/vnd.ms-powerpoint",
        ["rtf"] = "application/rtf", ["eml"] = "message/rfc822", ["msg"] = "application/vnd.ms-outlook",
        ["docm"] = "application/vnd.ms-word.document.macroenabled.12", ["xlsm"] = "application/vnd.ms-excel.sheet.macroenabled.12",
        ["pptm"] = "application/vnd.ms-powerpoint.presentation.macroenabled.12",
        ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["jfif"] = "image/jpeg", ["png"] = "image/png", ["gif"] = "image/gif", ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml", ["bmp"] = "image/bmp", ["tif"] = "image/tiff", ["tiff"] = "image/tiff", ["ico"] = "image/x-icon",
        ["avif"] = "image/avif", ["heic"] = "image/heic",
        ["mp4"] = "video/mp4", ["webm"] = "video/webm", ["mov"] = "video/quicktime", ["avi"] = "video/x-msvideo",
        ["mkv"] = "video/x-matroska", ["m4v"] = "video/x-m4v", ["mpg"] = "video/mpeg", ["mpeg"] = "video/mpeg", ["wmv"] = "video/x-ms-wmv",
        ["mp3"] = "audio/mpeg", ["wav"] = "audio/wav", ["ogg"] = "audio/ogg", ["m4a"] = "audio/mp4", ["flac"] = "audio/flac",
        ["zip"] = "application/zip", ["7z"] = "application/x-7z-compressed", ["rar"] = "application/vnd.rar",
        ["tar"] = "application/x-tar", ["gz"] = "application/gzip", ["tgz"] = "application/gzip", ["bz2"] = "application/x-bzip2",
        ["xz"] = "application/x-xz",
        ["exe"] = "application/vnd.microsoft.portable-executable", ["msi"] = "application/x-msi", ["dmg"] = "application/x-apple-diskimage",
        ["iso"] = "application/x-iso9660-image", ["apk"] = "application/vnd.android.package-archive", ["jar"] = "application/java-archive",
        ["css"] = "text/css", ["js"] = "text/javascript", ["json"] = "application/json", ["xml"] = "application/xml",
        ["rss"] = "application/rss+xml", ["ics"] = "text/calendar", ["woff"] = "font/woff", ["woff2"] = "font/woff2", ["ttf"] = "font/ttf",
    };

    private readonly IReadOnlyList<UrlPattern> _include;
    private readonly IReadOnlyList<UrlPattern> _exclude;
    private readonly IReadOnlySet<string> _offSiteHosts;
    private readonly IReadOnlyList<string> _excludedTypes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _excludedTypesByHost;
    private readonly IReadOnlySet<string> _formats;
    private readonly IReadOnlySet<string> _accepted;
    private readonly Dictionary<string, IReadOnlySet<string>> _acceptedByHost;
    private readonly IReadOnlyList<(UrlPattern Pattern, double Factor)> _demote;

    private CrawlScope(
        IReadOnlySet<string> hosts,
        IReadOnlyList<UrlPattern> include,
        IReadOnlyList<UrlPattern> exclude,
        IReadOnlySet<string> offSiteHosts,
        IReadOnlyList<string> excludedTypes,
        IReadOnlySet<string> formats,
        IReadOnlyList<(UrlPattern Pattern, double Factor)> demote,
        IReadOnlyDictionary<string, IReadOnlyList<string>> excludedTypesByHost)
    {
        Hosts = hosts;
        _include = include;
        _exclude = exclude;
        _offSiteHosts = offSiteHosts;
        _excludedTypes = excludedTypes;
        _excludedTypesByHost = excludedTypesByHost;
        _formats = formats;
        _demote = demote;
        _accepted = Accepted(formats, excludedTypes);
        _acceptedByHost = excludedTypesByHost.ToDictionary(e => e.Key, e => Accepted(formats, e.Value), StringComparer.Ordinal);
    }

    /// <summary>The scope hosts (lower-case A-labels, no port).</summary>
    public IReadOnlySet<string> Hosts { get; }

    /// <param name="settings">The source's settings.</param>
    /// <param name="excludedTypes">The <c>excludedTypes</c> of hosts without a site profile; null for <see cref="DefaultExcludedTypes"/>.</param>
    /// <param name="formats">The pipeline's formats; null for <see cref="MediaTypes.DocumentFormats"/>.</param>
    public static CrawlScope Create(CrawlSettings settings, IReadOnlyList<string>? excludedTypes = null, IReadOnlySet<string>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in settings.StartUrls)
        {
            if (UrlCanonicalizer.Canonicalize(start).Value is { } canonical)
            {
                hosts.Add(new Uri(canonical).Host);
            }
        }

        foreach (var extra in settings.ExtraHosts)
        {
            if (UrlCanonicalizer.CanonicalHost(extra) is { } host)
            {
                hosts.Add(host);
            }
        }

        var include = settings.Include.Select(UrlPattern.Parse).ToList();
        var offSite = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in settings.Include.Where(p => !p.StartsWith(UrlPattern.RegexPrefix, StringComparison.Ordinal)).Select(UrlPattern.Normalize))
        {
            var slash = pattern.IndexOf('/', StringComparison.Ordinal);
            var host = slash < 0 ? pattern : pattern[..slash];
            if (!host.Contains('*', StringComparison.Ordinal) && UrlCanonicalizer.CanonicalHost(host.Split(':')[0]) is { } literal)
            {
                offSite.Add(literal);
            }
        }

        return new CrawlScope(hosts, include, [.. settings.Exclude.Select(UrlPattern.Parse)], offSite,
            excludedTypes ?? DefaultExcludedTypes, formats ?? MediaTypes.DocumentFormats,
            [.. settings.Demote.Select(d => (UrlPattern.Parse(d.Pattern), d.Factor))],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The scope with the site profiles' <c>excludedTypes</c> by host (<see cref="SiteProfileHost"/> keys, §5.6); a
    /// host without an entry keeps the default.
    /// </summary>
    public CrawlScope WithExcludedTypes(IReadOnlyDictionary<string, IReadOnlyList<string>> byHost)
    {
        ArgumentNullException.ThrowIfNull(byHost);
        return new CrawlScope(Hosts, _include, _exclude, _offSiteHosts, _excludedTypes, _formats, _demote, byHost);
    }

    /// <summary>
    /// The response media types a fetch of <paramref name="url"/> reads (connectors design §4, §5.2): the pipeline's formats
    /// less the URL's host's <c>excludedTypes</c>; any other type stops after the headers.
    /// </summary>
    public IReadOnlySet<string> AcceptedMediaTypes(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return SiteProfileHost.Of(url) is { } host && _acceptedByHost.TryGetValue(host, out var accepted) ? accepted : _accepted;
    }

    /// <summary>
    /// The scope decision for a URL in its path form (<see cref="UrlCanonicalizer.PathForm"/>: canonical but for the
    /// identity rule), the form an editor writes the patterns for; the host and type rules read the same host and extension
    /// in either form.
    /// </summary>
    public ScopeDecision Check(string pathUrl) => Decide(pathUrl, patterns: true);

    /// <summary>
    /// The scope decision for a URL known only by its canonical identity (a frontier row or a document without a kept path
    /// form, <see cref="CheckStored"/>).
    /// When its host's verified identity rule mapped the path (§3.1 rule 10), the path the patterns were written for is
    /// not known: <c>include</c> and <c>exclude</c> decide nothing and only the host and type rules apply, so an unknown
    /// path never narrows the scope. Any other identity is its own path form.
    /// </summary>
    public ScopeDecision CheckIdentity(string canonicalIdentity, CanonicalizerOptions canonicalizer)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        return Decide(canonicalIdentity, patterns: !UrlCanonicalizer.IsIdentityRuleForm(canonicalIdentity, canonicalizer));
    }

    /// <summary>
    /// The scope decision for a stored URL (a frontier row, a document, a canonical hint; #228): <see cref="Check"/> on its
    /// kept path form (<see cref="PathFormOf"/>), <see cref="CheckIdentity"/> where none is kept (a row or document last
    /// fetched under its identity, such as a redirect target).
    /// </summary>
    /// <param name="canonicalIdentity">The stored identity.</param>
    /// <param name="foundUrl">The URL it was found or fetched under (<c>CrawlFrontier.FetchUrl</c>, <c>Documents.Url</c>); null when not known.</param>
    /// <param name="canonicalizer">The source's canonicaliser with its hosts' rules.</param>
    public ScopeDecision CheckStored(string canonicalIdentity, string? foundUrl, CanonicalizerOptions canonicalizer) =>
        PathFormOf(canonicalIdentity, foundUrl, canonicalizer) is { } path ? Check(path) : CheckIdentity(canonicalIdentity, canonicalizer);

    /// <summary>
    /// The path form (<see cref="UrlCanonicalizer.PathForm"/>) of <paramref name="foundUrl"/> when it differs from
    /// <paramref name="canonicalIdentity"/>, so it still tells the path the identity rule mapped; null otherwise.
    /// </summary>
    public static string? PathFormOf(string canonicalIdentity, string? foundUrl, CanonicalizerOptions canonicalizer)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        return foundUrl is not null && UrlCanonicalizer.PathForm(foundUrl, canonicalizer).Value is { } path
            && !string.Equals(path, canonicalIdentity, StringComparison.Ordinal)
            ? path
            : null;
    }

    private ScopeDecision Decide(string canonicalUrl, bool patterns)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrl);
        if (patterns && _exclude.Any(p => p.Matches(canonicalUrl)))
        {
            return ScopeDecision.Out(Excluded);
        }

        var host = new Uri(canonicalUrl).Host;
        if (TypeOf(canonicalUrl) is { } type && (!_formats.Contains(type) || ExcludedTypesOf(host).Any(t => TypeMatches(t, type))))
        {
            return ScopeDecision.Out(ExcludedType);
        }

        if (!Hosts.Contains(host))
        {
            // Off-site: only through an include that names this host literally, and then as a leaf.
            return _offSiteHosts.Contains(host) && (!patterns || _include.Any(p => p.Matches(canonicalUrl))) ? ScopeDecision.OffSiteLeaf : ScopeDecision.Out(OffSite);
        }

        return !patterns || _include.Count == 0 || _include.Any(p => p.Matches(canonicalUrl)) ? ScopeDecision.In : ScopeDecision.Out(NotIncluded);
    }

    /// <summary>
    /// Whether a stored document's media type (parameters ignored) is one of its URL's host's <c>excludedTypes</c>: a type
    /// the scope no longer admits, also where the URL names no extension (connectors design §12.3).
    /// </summary>
    public bool ExcludesType(string canonicalUrl, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrl);
        ArgumentNullException.ThrowIfNull(mediaType);
        var type = mediaType.Split(';')[0].Trim();
        return type.Length > 0 && ExcludedTypesOf(new Uri(canonicalUrl).Host).Any(t => TypeMatches(t, type));
    }

    /// <summary>
    /// The rank multiplier of a URL in its path form, as <see cref="Check"/> (knowledge design §6.5): the lowest factor of
    /// the matching <c>demote</c> patterns, 1 when none matches.
    /// </summary>
    public double DemoteFactor(string pathUrl)
    {
        ArgumentNullException.ThrowIfNull(pathUrl);
        var factor = 1.0;
        foreach (var (pattern, value) in _demote)
        {
            if (value < factor && pattern.Matches(pathUrl))
            {
                factor = value;
            }
        }

        return factor;
    }

    /// <summary>The media type the URL's last path segment's extension names, or null when it names none we know.</summary>
    internal static string? TypeOf(string canonicalUrl)
    {
        var path = new Uri(canonicalUrl).AbsolutePath;
        return TypeOfName(path[(path.LastIndexOf('/') + 1)..]);
    }

    /// <summary>The media type a file name's extension names (a URL's last segment, a <c>Content-Disposition</c> name), or null.</summary>
    internal static string? TypeOfName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? null : TypesByExtension.GetValueOrDefault(name[(dot + 1)..]);
    }

    private IReadOnlyList<string> ExcludedTypesOf(string host) => _excludedTypesByHost.GetValueOrDefault(host) ?? _excludedTypes;

    private static IReadOnlySet<string> Accepted(IReadOnlySet<string> formats, IReadOnlyList<string> excludedTypes) =>
        excludedTypes.Count == 0 ? formats : formats.Where(f => !excludedTypes.Any(t => TypeMatches(t, f))).ToHashSet(StringComparer.Ordinal);

    private static bool TypeMatches(string pattern, string type) =>
        pattern.EndsWith("/*", StringComparison.Ordinal)
            ? type.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, type, StringComparison.OrdinalIgnoreCase);
}
