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

/// <summary>
/// The source setting <c>piiMode</c> (Decisions table, "Personal data on the municipality's own published site", 2026-11-01):
/// what a personal-data flag does in a <c>public</c> collection. Other collections never block, whatever the mode.
/// </summary>
public enum PiiMode
{
    /// <summary>Processing design §9.5: a flagged or NER-pending set is swapped in <c>held</c> until the document is cleared.</summary>
    Hold,

    /// <summary>
    /// The flags are still counted, raised as event 5103 and reported, but the set is swapped in <c>live</c>; a set whose
    /// personnummer or samordningsnummer rule fires is still held.
    /// </summary>
    Report,
}
