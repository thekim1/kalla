using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.SiteProfiles;

/// <summary>
/// <c>content."SiteProfileVersions"."Profile"</c> (connectors design §5.6; processing design §3.7): how the HTML of one
/// host at one level is extracted and identified. In P1 every version is <c>manual</c>, every field set by hand and
/// checked against the fixture corpus; learning (P2) writes the same schema.
/// </summary>
[Description("A site profile version (connectors design §5.6; processing design §3.7): HTML extraction, identity and scope settings of one host. P1 profiles are manual.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SiteProfile
{
    [Description("The CMS fingerprint, e.g. sitevision; selects the built-in preset that seeded the fields.")]
    [RegularExpression("^[a-z][a-z0-9-]{0,49}$")]
    public string? Cms { get; init; }

    [Description("CSS selector of the main-content root (Sitevision: main); fallback landmarks main, article, role=main.")]
    [MinLength(1)]
    [MaxLength(500)]
    public required string MainRoot { get; init; }

    [Description("Blocks and phrases inside the root that are stripped (share widgets, short-URL lines).")]
    public SiteProfileBoilerplate Boilerplate { get; init; } = new();

    [Description("CSS selectors of link lists inside the root (umea.se: Undersidor) kept for link discovery, never as chunk text.")]
    [MaxLength(50)]
    [SchemaItems(MinLength = 1, MaxLength = 500)]
    public IReadOnlyList<string> LinkBlocks { get; init; } = [];

    [Description("CSS selector of the breadcrumb; absent = aria-label Brödsmulor/breadcrumb, then BreadcrumbList.")]
    [MaxLength(500)]
    public string? Breadcrumb { get; init; }

    [Description(".NET regular expression of the visible date with a group named d (YYYY-MM-DD), e.g. Sidan uppdaterades (?<d>\\d{4}-\\d{2}-\\d{2}).")]
    [MaxLength(500)]
    [SchemaKeyword("format", "\"regex\"")]
    public string? DatePattern { get; init; }

    [Description("Maps a URL path to its stable identity (connectors design §3.1 rule 10); used only when verified.")]
    public SiteProfileIdentityRule? IdentityRule { get; init; }

    [Description("Session and noise query parameters of the host, dropped by the canonicaliser (connectors design §3.1 rule 7).")]
    [MaxLength(50)]
    [SchemaItems(MinLength = 1, MaxLength = 100, Pattern = "^[^\\s=&]+$")]
    public IReadOnlyList<string> IgnoreQueryParams { get; init; } = [];

    [Description("Media-type patterns not admitted to the crawl (connectors design §5.2).")]
    [MaxLength(50)]
    [SchemaItems(MaxLength = 100, Pattern = "^[a-z0-9.+-]+/([a-z0-9.+-]+|\\*)$")]
    public IReadOnlyList<string> ExcludedTypes { get; init; } = ["image/*", "video/*"];

    [Description("Set by hand only from the recorded 500-URL lastmod sample (to-verify #170); until then false and the host gets full traversals.")]
    public bool LastmodReliable { get; init; }

    [Description("Raw text yield says the host needs JavaScript (the renderer is P2).")]
    public bool JavascriptNeeded { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SiteProfileBoilerplate
{
    [Description("CSS selectors of blocks removed inside the root.")]
    [MaxLength(100)]
    [SchemaItems(MinLength = 1, MaxLength = 500)]
    public IReadOnlyList<string> Selectors { get; init; } = [];

    [Description("Exact phrases removed inside the root (after whitespace normalisation).")]
    [MaxLength(100)]
    [SchemaItems(MinLength = 1, MaxLength = 500)]
    public IReadOnlyList<string> Phrases { get; init; } = [];
}

/// <summary>The identity rule: <c>pattern</c> on the URL path, rewritten to <c>replacement</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SiteProfileIdentityRule
{
    [Description(".NET regular expression over the URL path, e.g. ^/(?:.*/)?[^/]*?\\.?(?<id>\\d+\\.[0-9a-f]+)\\.html$ for Sitevision node ids.")]
    [MinLength(1)]
    [MaxLength(500)]
    [SchemaKeyword("format", "\"regex\"")]
    public required string Pattern { get; init; }

    [Description("The identity path, with ${name} for named groups, e.g. /${id}.html.")]
    [MinLength(1)]
    [MaxLength(500)]
    public required string Replacement { get; init; }

    [Description("True once id-only URLs returned the same content hash as the full URLs on sampled pages; an unverified rule is not applied.")]
    public bool Verified { get; init; }
}
