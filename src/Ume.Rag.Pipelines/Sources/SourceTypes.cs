namespace Ume.Rag.Pipelines.Sources;

/// <summary><c>Sources.Type</c> values of P1 (connectors design §2.1). SharePoint is P2.</summary>
public static class SourceTypes
{
    public const string UrlList = "url-list";
    public const string Crawl = "crawl";
    public const string Upload = "upload";

    public static IReadOnlyList<string> All { get; } = [UrlList, Crawl, Upload];
}

/// <summary>
/// What a changed source setting needs (connectors design §12.3, §12.4): <c>cosmetic</c> nothing, <c>behaviour</c> the
/// next sync, <c>scope</c> an immediate sync for the newly in-scope items, <c>identity</c> a full forced re-sync.
/// </summary>
public enum SettingEffect
{
    Identity,
    Scope,
    Behaviour,
    Cosmetic,
}
