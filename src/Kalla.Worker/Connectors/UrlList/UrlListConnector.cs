using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Robots;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Connectors.UrlList;

/// <summary>
/// The <c>url-list</c> source's settings (connectors design §4, §12.3; schema <c>contracts/sources/v1/url-list.schema.json</c>).
/// Schedules and names are read by the scheduler and the admin, not here.
/// </summary>
public sealed record UrlListSettings
{
    /// <summary>The listed URLs (≤ 5,000), normalised on save; never followed further.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    public IReadOnlyList<string> IgnoreQueryParams { get; init; } = [];

    public IReadOnlyList<string> DirectoryIndexNames { get; init; } = [];

    /// <summary>CSS selectors removed before the content hash (§12.2).</summary>
    public IReadOnlyList<string> NoiseSelectors { get; init; } = [];

    /// <summary>Send the stored validators (§4); off sends a plain GET every time. Never for a <c>browser</c> source (§8.5).</summary>
    public bool ConditionalGet { get; init; } = true;

    /// <summary><c>browser</c>: every HTML page is rendered (§8.1).</summary>
    public FetchMode FetchMode { get; init; } = FetchMode.Http;

    /// <summary>CSS naming the loaded content of a rendered page (§8.3); null for the quiet rule.</summary>
    public string? ReadySelector { get; init; }

    /// <summary>Scroll a rendered page until it stops growing (§8.3).</summary>
    public bool ScrollToLoad { get; init; }

    public CanonicalizerOptions Canonicalizer() => new() { IgnoreQueryParams = IgnoreQueryParams, DirectoryIndexNames = DirectoryIndexNames };

    public PageSettings Pages() => new(FetchMode, ReadySelector, ScrollToLoad, NoiseSelectors);
}

/// <summary>The listing position, so a resumed <c>sync</c> continues after the last committed batch (§2.3).</summary>
public sealed record UrlListCheckpoint : ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int Position { get; init; }
}

/// <summary>
/// Exactly the listed URLs, at depth 0, never following links (connectors design §4): through <see cref="PageFetcher"/>, a
/// conditional GET with the stored validators (none for a <c>browser</c> source, whose pages are rendered, §8), redirects
/// re-checked hop by hop, robots.txt honoured as everywhere (§5.1), files malware-scanned before they are stored (§11),
/// and the content hash over noise-normalised HTML or the raw bytes (§12). The raw blob is written once under
/// <c>raw/{level}/{contentHash}</c>; the runner (engine) writes frontier rows, documents and run items.
/// </summary>
public sealed class UrlListConnector(PageFetcher pages, RobotsCache robots, IBlobStore blobs)
    : IFetchConnector<UrlListCheckpoint>, IIdListingConnector, IRetryFailedConnector
{
    public const string Type = "url-list";

    /// <summary>Items between checkpoints: one committed batch of claims and run items (§2.3).</summary>
    public const int CheckpointEvery = 1000;

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    public string SourceType => Type;

    public Type SettingsType => typeof(UrlListSettings);

    public WorkItem? FromIdentity(string canonicalIdentity, FailedItem last) => RetryWork.FromUrl(canonicalIdentity);

    public async IAsyncEnumerable<ListingEvent<UrlListCheckpoint>> ListAsync(
        ListingContext context, UrlListCheckpoint from, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(from);
        var settings = Settings(context.Settings);
        var options = settings.Canonicalizer();
        // An unknown schema version (a rolled-back worker) restarts the listing, which is always safe (§2.3).
        var start = from.SchemaVersion == UrlListCheckpoint.CurrentSchemaVersion ? Math.Clamp(from.Position, 0, settings.Urls.Count) : 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < start; i++)
        {
            if (UrlCanonicalizer.Canonicalize(settings.Urls[i], options).Value is { } earlier)
            {
                seen.Add(earlier);
            }
        }

        for (var i = start; i < settings.Urls.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = UrlCanonicalizer.Canonicalize(settings.Urls[i], options);
            if (!canonical.IsValid)
            {
                yield return new ListingEvent<UrlListCheckpoint>.Failure(settings.Urls[i], canonical.Refusal!);
            }
            else if (seen.Add(canonical.Value!))
            {
                yield return new ListingEvent<UrlListCheckpoint>.Item(
                    new WorkItem(IdentityKind.Url, canonical.Value!, canonical.ToUri(), 0, WorkHints.None));
            }

            if ((i + 1) % CheckpointEvery == 0 && i + 1 < settings.Urls.Count)
            {
                yield return new ListingEvent<UrlListCheckpoint>.Checkpoint(Checkpoint(i + 1));
            }
        }

        yield return new ListingEvent<UrlListCheckpoint>.Checkpoint(Checkpoint(settings.Urls.Count));
        yield return new ListingEvent<UrlListCheckpoint>.Done(Completeness.Full);
    }

    public async IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await foreach (var item in ListAsync(context, new UrlListCheckpoint { SchemaVersion = UrlListCheckpoint.CurrentSchemaVersion }, cancellationToken))
        {
            if (item is ListingEvent<UrlListCheckpoint>.Item listed)
            {
                yield return listed.Work.CanonicalIdentity;
            }
        }
    }

    public async ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var url = item.Url ?? throw new ArgumentException("A url-list item has a URL.", nameof(item));
        var settings = Settings(context.Settings);
        var validators = settings.ConditionalGet && !context.Revalidate ? item.Hints.Stored : null;
        var request = new HttpFetchRequest(url, EgressPurpose.Fetch, validators, MediaTypes.DocumentFormats, settings.Canonicalizer(),
            robots.CheckAsync);
        return (await pages.FetchAsync(new PageRequest(context, request, blobs, settings.Pages()), cancellationToken)).Outcome;
    }

    private static UrlListCheckpoint Checkpoint(int position) =>
        new() { SchemaVersion = UrlListCheckpoint.CurrentSchemaVersion, Position = position };

    /// <summary>The settings this connector reads; members of other source types (a crawl source's, in a stage preview) are ignored.</summary>
    internal static UrlListSettings Settings(JsonElement settings) =>
        settings.Deserialize<UrlListSettings>(SettingsJson) ?? new UrlListSettings();
}
