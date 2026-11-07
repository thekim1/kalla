using System.Runtime.CompilerServices;
using System.Text.Json;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.UrlList;

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

    /// <summary>Send the stored validators (§4); off sends a plain GET every time.</summary>
    public bool ConditionalGet { get; init; } = true;

    public CanonicalizerOptions Canonicalizer() => new() { IgnoreQueryParams = IgnoreQueryParams, DirectoryIndexNames = DirectoryIndexNames };
}

/// <summary>The listing position, so a resumed <c>sync</c> continues after the last committed batch (§2.3).</summary>
public sealed record UrlListCheckpoint : ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int Position { get; init; }
}

/// <summary>
/// Exactly the listed URLs, at depth 0, never following links (connectors design §4): a conditional GET with the stored
/// validators, redirects re-checked hop by hop, robots.txt honoured as everywhere (§5.1), files malware-scanned before
/// they are stored (§11), and the content hash over noise-normalised HTML or the raw bytes (§12). The raw blob is written
/// once under <c>raw/{level}/{contentHash}</c>; the runner (engine) writes frontier rows, documents and run items.
/// </summary>
public sealed class UrlListConnector(
    HttpFetcher fetcher,
    RobotsCache robots,
    IMalwareScanner scanner,
    IBlobStore blobs,
    TimeProvider time) : IFetchConnector<UrlListCheckpoint>, IIdListingConnector, IRetryFailedConnector
{
    public const string Type = "url-list";

    /// <summary>Items between checkpoints: one committed batch of claims and run items (§2.3).</summary>
    public const int CheckpointEvery = 1000;

    private readonly RawContentWriter _writer = new(scanner, blobs, time);

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
        var result = await fetcher.FetchAsync(request, cancellationToken);
        if (result is not HttpFetchResult.Ok ok)
        {
            return FetchOutcomes.From(result);
        }

        await using (ok.Body)
        {
            return (await _writer.StoreAsync(context, ok, settings.NoiseSelectors, settings.Canonicalizer(), extractLinks: false, softNotFound: null, cancellationToken)).Outcome;
        }
    }

    private static UrlListCheckpoint Checkpoint(int position) =>
        new() { SchemaVersion = UrlListCheckpoint.CurrentSchemaVersion, Position = position };

    internal static UrlListSettings Settings(JsonElement settings) =>
        settings.Deserialize<UrlListSettings>(JsonSerializerOptions.Web) ?? new UrlListSettings();
}
