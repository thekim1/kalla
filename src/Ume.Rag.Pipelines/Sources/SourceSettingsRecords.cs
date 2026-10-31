using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Sources;

/// <summary>Patterns shared by the source settings.</summary>
internal static class SettingPatterns
{
    public const string HttpUrl = "^https?://";
    public const string Host = "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$";
    public const string QueryParameter = "^[^\\s=&]+$";
    public const string CrawlWindow = "^([01][0-9]|2[0-3]):[0-5][0-9]-([01][0-9]|2[0-3]):[0-5][0-9]$";

    /// <summary>Five cron fields (as <c>Sources.Schedule</c>, engine design §9.6); the admin parses them with the scheduler's parser.</summary>
    public const string Cron = "^\\S+( \\S+){4}$";
}

/// <summary><c>Sources.Settings</c> of a URL-list source (connectors design §4, §12.3).</summary>
[Description("Settings of a URL-list source (Sources.Settings; connectors design §4, §12.3). Each member's x-effect says what a change needs.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UrlListSettings
{
    [Effect(SettingEffect.Scope)]
    [Description("Exactly the URLs to fetch, never following links; canonicalised by the admin on save.")]
    [MinLength(1)]
    [MaxLength(5000)]
    [SchemaItems(MaxLength = 2048, Pattern = SettingPatterns.HttpUrl)]
    public required IReadOnlyList<string> Urls { get; init; }

    [Effect(SettingEffect.Identity)]
    [Description("Query parameters dropped by the canonicaliser in addition to the fixed tracking and session list.")]
    [MaxLength(50)]
    [SchemaItems(MaxLength = 100, Pattern = SettingPatterns.QueryParameter)]
    public IReadOnlyList<string> IgnoreQueryParams { get; init; } = [];

    [Effect(SettingEffect.Identity)]
    [Description("File names treated as the directory index (index.html), so /a/ and /a/index.html are one identity.")]
    [MaxLength(10)]
    [SchemaItems(MinLength = 1, MaxLength = 100)]
    public IReadOnlyList<string> DirectoryIndexNames { get; init; } = [];

    [Effect(SettingEffect.Behaviour)]
    [Description("CSS selectors removed before the content hash (HTML noise normalisation, connectors design §12.2).")]
    [MaxLength(50)]
    [SchemaItems(MinLength = 1, MaxLength = 200)]
    public IReadOnlyList<string> NoiseSelectors { get; init; } = [];

    [Effect(SettingEffect.Behaviour)]
    [Description("Send stored validators (If-None-Match, If-Modified-Since).")]
    public bool ConditionalGet { get; init; } = true;

    [Effect(SettingEffect.Behaviour)]
    [Description("Every n-th run sends no validators, catching wrong 304s.")]
    [Range(1, 100)]
    public int RevalidateEvery { get; init; } = 4;

    [Effect(SettingEffect.Behaviour)]
    [Description("Whole-request timeout in seconds; the HTML and file body limits of connectors design §4 still apply.")]
    [Range(5, 120)]
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary><c>Sources.Settings</c> of a website crawl (connectors design §5, §12.3).</summary>
[Description("Settings of a website crawl source (Sources.Settings; connectors design §5, §12.3). Each member's x-effect says what a change needs.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CrawlSettings
{
    /// <summary>The <c>website</c> template form's default excludes (connectors design §5.3).</summary>
    public static IReadOnlyList<string> DefaultExcludes { get; } =
        ["**/sok?*", "**/search?*", "**?*sort=*", "**?*print=*", "**/login*", "**/logout*"];

    [Effect(SettingEffect.Scope)]
    [Description("Seeds; their hosts are the scope hosts.")]
    [MinLength(1)]
    [MaxLength(100)]
    [SchemaItems(MaxLength = 2048, Pattern = SettingPatterns.HttpUrl)]
    public required IReadOnlyList<string> StartUrls { get; init; }

    [Effect(SettingEffect.Scope)]
    [Description("Sitemaps besides the robots.txt Sitemap: lines and /sitemap.xml.")]
    [MaxLength(20)]
    [SchemaItems(MaxLength = 2048, Pattern = SettingPatterns.HttpUrl)]
    public IReadOnlyList<string> Sitemaps { get; init; } = [];

    [Effect(SettingEffect.Scope)]
    [Description("Further scope hosts (lower-case, A-labels).")]
    [MaxLength(50)]
    [SchemaItems(MaxLength = 253, Pattern = SettingPatterns.Host)]
    public IReadOnlyList<string> ExtraHosts { get; init; } = [];

    [Effect(SettingEffect.Scope)]
    [Description("Globs over host/path?query (* within a segment, ** across), or re:<regex>; empty = everything on the scope hosts.")]
    [MaxLength(200)]
    [SchemaItems(MinLength = 1, MaxLength = 500)]
    public IReadOnlyList<string> Include { get; init; } = [];

    [Effect(SettingEffect.Scope)]
    [Description("Globs or re:<regex>; exclude wins over include.")]
    [MaxLength(200)]
    [SchemaItems(MinLength = 1, MaxLength = 500)]
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExcludes;

    [Effect(SettingEffect.Cosmetic)]
    [Description("Ranking only (knowledge design §6.5): matching pages are multiplied by the lowest matching factor.")]
    [MaxLength(100)]
    public IReadOnlyList<DemoteRule> Demote { get; init; } = [];

    [Effect(SettingEffect.Scope)]
    [Description("Link distance from a seed.")]
    [Range(0, 50)]
    public int MaxDepth { get; init; } = 10;

    [Effect(SettingEffect.Scope)]
    [Description("Admitted claims per run, seeds included; the admin also checks the platform cap (V25).")]
    [Range(1, 1_000_000)]
    public int MaxPages { get; init; } = 10_000;

    [Effect(SettingEffect.Identity)]
    [Description("Query parameters dropped by the canonicaliser in addition to the fixed list and the site profile's.")]
    [MaxLength(50)]
    [SchemaItems(MaxLength = 100, Pattern = SettingPatterns.QueryParameter)]
    public IReadOnlyList<string> IgnoreQueryParams { get; init; } = [];

    [Effect(SettingEffect.Identity)]
    [Description("File names treated as the directory index (index.html), so /a/ and /a/index.html are one identity.")]
    [MaxLength(10)]
    [SchemaItems(MinLength = 1, MaxLength = 100)]
    public IReadOnlyList<string> DirectoryIndexNames { get; init; } = [];

    [Effect(SettingEffect.Behaviour)]
    [Description("CSS selectors removed before the content hash (HTML noise normalisation, connectors design §12.2).")]
    [MaxLength(50)]
    [SchemaItems(MinLength = 1, MaxLength = 200)]
    public IReadOnlyList<string> NoiseSelectors { get; init; } = [];

    [Effect(SettingEffect.Behaviour)]
    [Description("Send stored validators (If-None-Match, If-Modified-Since).")]
    public bool ConditionalGet { get; init; } = true;

    [Effect(SettingEffect.Behaviour)]
    [Description("Every n-th traversal sends no validators, catching wrong 304s.")]
    [Range(1, 100)]
    public int RevalidateEvery { get; init; } = 4;

    [Effect(SettingEffect.Behaviour)]
    [Description("Whole-request timeout in seconds; the HTML and file body limits of connectors design §4 still apply.")]
    [Range(5, 120)]
    public int TimeoutSeconds { get; init; } = 30;

    [Effect(SettingEffect.Cosmetic)]
    [Description("Night window HH:MM-HH:MM, Europe/Stockholm (connectors design §6.5); manual sync ignores it.")]
    [RegularExpression(SettingPatterns.CrawlWindow)]
    public string CrawlWindow { get; init; } = "22:00-06:00";

    [Effect(SettingEffect.Cosmetic)]
    [Description("Cron (5 fields, Europe/Stockholm) of incremental runs besides Sources.Schedule (connectors design §5.4); null = full traversals only. The scheduler runs them only while the host's site profile has lastmodReliable.")]
    [MaxLength(100)]
    [RegularExpression(SettingPatterns.Cron)]
    public string? IncrementalSchedule { get; init; }
}

/// <summary>A <c>demote</c> entry (weakness review §3): pages matching the pattern rank lower, nothing else changes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DemoteRule
{
    [Description("Glob or re:<regex>, as include and exclude.")]
    [MinLength(1)]
    [MaxLength(500)]
    public required string Pattern { get; init; }

    [Description("Score multiplier in (0, 1).")]
    [Range(0.0, 1.0, MinimumIsExclusive = true, MaximumIsExclusive = true)]
    public double Factor { get; init; } = 0.5;
}

/// <summary><c>Sources.Settings</c> of an upload source (connectors design §9).</summary>
[Description("Settings of an upload source (Sources.Settings; connectors design §9, §12.3). Each member's x-effect says what a change needs.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UploadSettings
{
    [Effect(SettingEffect.Behaviour)]
    [Description("Per-file limit in MB (platform cap 50); applies to new uploads only.")]
    [Range(1, 50)]
    public int MaxFileSizeMb { get; init; } = 50;
}
