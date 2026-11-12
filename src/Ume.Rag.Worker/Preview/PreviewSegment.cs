using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors.Urls;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// What one preview job runs with: its pipeline version, the collection, the stages up to <c>untilStage</c>, its store, and
/// the attempt's model calls (an <c>embed: true</c> preview's embeddings, counted under the collection with the job's id as
/// <c>x-ume-run-id</c>, engine design §10.6).
/// </summary>
public sealed record PreviewRun(Guid JobId, PipelineRun Pipeline, PreviewCollectionRow Collection, IReadOnlyList<string> Stages, PreviewStore Store, JobUsage Usage)
{
    /// <summary>The knowledge base the preview's calls are counted under and run as: the payload's collection.</summary>
    public UsageOwner UsageOwner => new(Collection.Id, Collection.Classification, Collection.DepartmentId, Collection.GatewayKeyId);

    public bool Embed { get; init; }

    public bool PiiSpans { get; init; }
}

/// <summary>One document's fetched content: where its raw bytes are and what the stages read besides them.</summary>
public sealed record PreviewContent(int Index, string Level, byte[] ContentHash, BlobKey RawKey)
{
    public string? MediaType { get; init; }

    public string? Url { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>HTTP <c>Last-Modified</c> of the fetch, or the existing document's stored one.</summary>
    public DateTimeOffset? HttpLastModified { get; init; }

    /// <summary>How the content was fetched (<c>Documents.FetchMode</c>: <c>http</c>, or <c>browser</c> for a rendered DOM); null for an upload.</summary>
    public string? FetchMode { get; init; }

    /// <summary>An existing document's stored sitemap <c>lastmod</c>.</summary>
    public DateTimeOffset? SitemapLastMod { get; init; }

    /// <summary>An existing document (its PII clearance applies to the state shown).</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>The document's source, or the preview's; null for a URL previewed against the collection alone.</summary>
    public Guid? SourceId { get; init; }

    /// <summary>That source's <c>piiMode</c> (hold without a source), for the state shown.</summary>
    public PiiMode PiiMode { get; init; } = PiiMode.Hold;
}

/// <summary>The personal-data output of a preview: the counts, the rules that fired, the state the set would get and, with <c>piiSpans</c>, where each match is.</summary>
public sealed record PreviewPii(PiiCounts Counts, IReadOnlyList<string> TypesFired, string State)
{
    public IReadOnlyList<PreviewPiiSpan>? Spans { get; init; }
}

/// <summary>A match in a chunk's <c>Text</c>: offsets and type only, never the value.</summary>
public sealed record PreviewPiiSpan(int Ordinal, string Type, int Start, int Length);

/// <summary>The stage results of one document, in stage order; the stage begun last is the one a failure belongs to.</summary>
public sealed class PreviewStageLog(IReadOnlyList<string> stages, TimeProvider time)
{
    private readonly List<PreviewStageResult> _results = [];
    private string? _current;
    private long _started;

    public IReadOnlyList<PreviewStageResult> Results => _results;

    /// <summary>The tags <c>chunk</c> gave the document's chunks; null until it ran.</summary>
    public IReadOnlyList<string>? Tags { get; private set; }

    public void Tagged(IReadOnlyList<string> tags) => Tags = tags;

    public bool Wants(string stage) => stages.Contains(stage, StringComparer.Ordinal);

    public void Begin(string stage)
    {
        _current = stage;
        _started = time.GetTimestamp();
    }

    public void Done(bool cacheHit = false, object? facts = null, string? output = null, bool truncated = false)
    {
        _results.Add(new PreviewStageResult
        {
            Stage = _current ?? throw new InvalidOperationException("No stage begun."),
            State = PreviewStageState.Succeeded,
            DurationMs = Elapsed(),
            CacheHit = cacheHit,
            Facts = facts is null ? null : JsonSerializer.SerializeToElement(facts, ContractJson.Options),
            Output = output,
            Truncated = truncated,
        });
        _current = null;
    }

    /// <summary>The stage begun last (or the first not yet recorded) failed with <paramref name="error"/>; every later stage is skipped.</summary>
    public void Fail(string error)
    {
        var stage = _current ?? stages.FirstOrDefault(s => _results.All(r => r.Stage != s));
        if (stage is not null)
        {
            _results.Add(new PreviewStageResult { Stage = stage, State = PreviewStageState.Failed, DurationMs = _current is null ? 0 : Elapsed(), Error = error });
        }

        _current = null;
        foreach (var rest in stages.Where(s => _results.All(r => r.Stage != s)))
        {
            _results.Add(new PreviewStageResult { Stage = rest, State = PreviewStageState.Skipped });
        }
    }

    /// <summary>A stage that does not run in a preview (<c>index</c>), with what it would have done.</summary>
    public void Skip(string stage, object? facts)
    {
        _results.Add(new PreviewStageResult
        {
            Stage = stage,
            State = PreviewStageState.Skipped,
            Facts = facts is null ? null : JsonSerializer.SerializeToElement(facts, ContractJson.Options),
        });
    }

    private int Elapsed() => (int)Math.Min(int.MaxValue, time.GetElapsedTime(_started).TotalMilliseconds);
}

/// <summary>
/// The processing stages of one previewed document (engine design §11), from <c>detect</c> up to the run's last stage,
/// keyed exactly as the engine keys them (<see cref="ProcessingSegment"/>), so outputs the live cache already holds are
/// read instead of computed. Isolated by construction: it has no index writer, no document or cache writer and no
/// database access but reads; every output goes to the run's <see cref="PreviewStore"/>. A parser-bound document is parsed
/// on its first 50 pages through the normal range planning; <c>embed</c> reports the chunk count and tokens and calls the
/// gateway only with <c>embed: true</c> (its embedding cache is read, never written); <c>index</c> never runs. An HTML
/// page with a URL also gets its extraction with the built-in defaults only, next to the one under the host's site
/// profile (or the preset its fingerprint selects).
/// </summary>
public sealed class PreviewSegment(
    DetectStage detect,
    HtmlExtractor html,
    IHtmlPresetCatalog presets,
    Chunker chunker,
    PersonalDataStage personalData,
    PiiPatterns patterns,
    IParsePlanner planner,
    IOptions<ParseOptions> parseOptions,
    EngineSettings settings,
    SiteProfiles siteProfiles,
    IChunkSetStore chunkSets,
    EmbeddingClient embeddings,
    IEmbeddingProfileStore profiles,
    IEmbeddingCache embeddingCache,
    EmbeddingConcurrency concurrency,
    TimeProvider time,
    IParserConverter? converter = null,
    IParserBuild? parserBuild = null,
    ParseRuleCatalog? parseRules = null)
{
    /// <summary>The pages a preview parses of a paged document (engine design §11).</summary>
    public const int MaxPages = 50;

    private readonly EmbedStage _embed = new(embeddings, profiles, new ReadOnlyEmbeddingCache(embeddingCache), concurrency, time);
    private readonly StageRunner _runner = new(detect, new StageParser(html, planner, parseOptions, converter), chunker, personalData, settings);

    /// <summary>Runs the stages after <c>fetch</c>; returns the site profile's before/after of an HTML page with a URL.</summary>
    public async Task<PreviewSiteProfile?> RunAsync(PreviewRun run, PreviewContent content, PreviewStageLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(log);
        var store = run.Store;
        var pipeline = run.Pipeline.Pipeline;
        var level = content.Level;
        var plan = new StagePlan(pipeline, level, run.Collection.Classification, content.ContentHash)
        {
            // An upload key names no level: it is read by the document's, which may be below the preview's (data-model §7).
            Raw = async ct =>
            {
                await using var stream = await store.Blobs.ForLevel(level).OpenReadAsync(content.RawKey, null, ct)
                    ?? throw new StageFatalException(RunItemOutcome.Failed, "raw.missing");
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                return buffer.ToArray();
            },
            ParseContext = async (detected, ct) =>
            {
                // The host's applied profile: the HTML parse applies it, and its lastmodReliable admits a stored sitemap
                // lastmod as the chunk date of any route, as in the live segment (processing design §7).
                var applied = detected.Route == ParseRoute.Html || content.SitemapLastMod is not null
                    ? await siteProfiles.AppliedAsync(level, content.Url, ct)
                    : null;
                // The same key as the live segment (incl. the parser build), so a preview reuses live parses read-only.
                string? parserBuildHash = null;
                if (detected.Route == ParseRoute.Parser)
                {
                    parserBuildHash = parserBuild is not null && converter is not null
                        ? await parserBuild.GetAsync(ct)
                        : throw new StageFatalException(RunItemOutcome.Failed, "parser.not_configured");
                }

                var rules = detected.Route == ParseRoute.Parser && parseRules is not null
                    ? await ParseRuleCandidatesAsync(parseRules, run, content, detected.MediaType, ct)
                    : [];
                return new ParseContext(detected.Route == ParseRoute.Html ? applied : null,
                    StageInputs.ReliableLastmod(content.SitemapLastMod, applied?.Profile.Profile), parserBuildHash, rules);
            },
            DeclaredMediaType = content.MediaType,
            Url = content.Url is { } address ? new Uri(address) : null,
            DisplayName = content.DisplayName,
            HttpLastModified = content.HttpLastModified,
            LimitParse = detected => detected.Inspect is { Pages.Count: > MaxPages } inspect
                ? detected with { Inspect = inspect with { Pages = [.. inspect.Pages.OrderBy(p => p.Page).Take(MaxPages)] } }
                : detected,
        };

        var observer = new Observer(this, run, content, log, settings, chunkSets);
        var state = await _runner.RunAsync(plan, new Store(store), observer, cancellationToken);
        if (state.StoppedBefore is not null || !log.Wants(StageNames.Embed))
        {
            return observer.SiteProfile;
        }

        // embed: counts only, unless asked to call the gateway
        var withPii = state.WithPii!;
        log.Begin(StageNames.Embed);
        var tokens = withPii.Chunks.Sum(c => (long)c.TokenCount);
        if (run.Embed)
        {
            var embeddingProfile = await profiles.GetAsync(run.Collection.EmbeddingProfileId, cancellationToken)
                ?? throw new StageFatalException(RunItemOutcome.Failed, "embed.profile_unknown");
            var embedded = await _embed.RunAsync(withPii, level, embeddingProfile.Id, new EmbeddingCall(run.JobId, new UsageSink(run.Usage, run.UsageOwner)),
                cancellationToken);
            log.Done(embedded.CacheHits == withPii.Chunks.Count, new
            {
                chunks = withPii.Chunks.Count,
                tokens,
                embedded = true,
                embeddingTokens = embedded.EmbeddingTokens,
                cacheHits = embedded.CacheHits,
                dimensions = embedded.Vectors.Count > 0 ? embedded.Vectors[0].Length : 0,
            });
        }
        else
        {
            log.Done(facts: new { chunks = withPii.Chunks.Count, tokens, embedded = false });
        }

        // index never runs in a preview: "would write N chunks"
        if (log.Wants(StageNames.Index))
        {
            log.Skip(StageNames.Index, new { wouldWrite = withPii.Chunks.Count });
        }

        return observer.SiteProfile;
    }

    /// <summary>
    /// What a preview shows of each stage: its facts and output in the log, an HTML page's site-profile before/after, and the
    /// state the set would get in the collection.
    /// </summary>
    private sealed class Observer(PreviewSegment segment, PreviewRun run, PreviewContent content, PreviewStageLog log, EngineSettings settings,
        IChunkSetStore chunkSets) : IStageObserver
    {
        public PreviewSiteProfile? SiteProfile { get; private set; }

        public bool Wants(string stage) => log.Wants(stage);

        public void Begin(string stage) => log.Begin(stage);

        public async ValueTask DoneAsync(string stage, bool cacheHit, StageRunState state, CancellationToken cancellationToken)
        {
            var store = run.Store;
            switch (stage)
            {
                case StageNames.Detect:
                    var detected = state.Detected!;
                    log.Done(cacheHit, new { detected.MediaType, route = RouteName(detected.Route), detected.Language, detected.PageCount });
                    break;
                case StageNames.Parse:
                    var parsed = state.Parsed!;
                    var shownParse = await store.ShowAsync(content.Index, StageNames.Parse, StageNames.Parse, parsed, parsed.Blocks.Count, CutBlocks, cancellationToken);
                    if (state.Detected!.Route == ParseRoute.Html && state.Plan.Url is { } url)
                    {
                        SiteProfile = await segment.BeforeAfterAsync(run, content, url, await state.RawAsync(cancellationToken), state.ParseContext!.SiteProfile,
                            shownParse.Path, cancellationToken);
                    }

                    log.Done(cacheHit, new { parsed.Strategy, blocks = parsed.Blocks.Count, pages = parsed.Pages.Count, pagesLimited = state.ParseLimited },
                        shownParse.Path, shownParse.Truncated);
                    break;
                case StageNames.Clean:
                    var cleaned = state.Cleaned!;
                    var shownClean = await store.ShowAsync(content.Index, StageNames.Clean, StageNames.Clean, cleaned, cleaned.Blocks.Count, CutBlocks, cancellationToken);
                    log.Done(cacheHit, new { blocks = cleaned.Blocks.Count, cleaned.Language }, shownClean.Path, shownClean.Truncated);
                    break;
                case StageNames.Chunk:
                    var chunks = state.Chunks!;
                    var shownChunks = await store.ShowAsync(content.Index, StageNames.Chunk, StageNames.Chunk, chunks, chunks.Chunks.Count, CutChunks, cancellationToken);
                    log.Done(cacheHit, new { chunks = chunks.Chunks.Count, tokens = chunks.Chunks.Sum(c => c.TokenCount) }, shownChunks.Path, shownChunks.Truncated);
                    log.Tagged(state.Metadata!.Tags);
                    break;
                case StageNames.PersonalData:
                    await PersonalDataAsync(state, cacheHit, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown stage {stage}.");
            }
        }

        /// <summary>personal-data, with the state the set would get in the collection.</summary>
        private async Task PersonalDataAsync(StageRunState state, bool cacheHit, CancellationToken cancellationToken)
        {
            var withPii = state.WithPii!;
            var values = await settings.GetAsync(cancellationToken);
            var collectionLevel = run.Collection.Classification;
            var counts = withPii.Pii ?? new PiiCounts();
            var fired = PiiFlagPolicy.Fired(counts, values.PiiFlagRules);
            var clearance = content.DocumentId is { } documentId ? await chunkSets.GetClearanceAsync(documentId, cancellationToken) : null;
            var wouldBe = new ChunkSetRecord(Guid.Empty,
                new ChunkSetKey(content.DocumentId ?? Guid.Empty, run.Pipeline.VersionId, state.ParseContext!.SiteProfile?.Id, run.Collection.EmbeddingProfileId,
                    content.ContentHash),
                content.Level, run.Collection.Id, state.ChunksHash!) { PiiFlagged = fired.Count > 0, PiiCounts = counts };
            var setState = PiiLifecycle.StateFor(wouldBe, collectionLevel, content.PiiMode, clearance, values.PiiFlagRules);
            var pii = new PreviewPii(counts, fired, setState) { Spans = run.PiiSpans ? segment.Spans(withPii) : null };
            var shownPii = await run.Store.ShowAsync(content.Index, StageNames.PersonalData, StageNames.PersonalData, pii, pii.Spans?.Count ?? 0,
                (p, n) => p with { Spans = p.Spans?.Take(n).ToList() }, cancellationToken);
            log.Done(cacheHit, new { state = setState, flagged = fired.Count > 0, types = fired, ner = counts.Ner }, shownPii.Path, shownPii.Truncated);
        }
    }

    /// <summary>The preview store as a stage run's store (its own prefix, the live cache read-only, a base job's outputs).</summary>
    private sealed class Store(PreviewStore store) : IStageStore
    {
        public Task<(T Value, byte[] OutputHash)?> GetAsync<T>(string stage, byte[] key, CancellationToken cancellationToken) =>
            store.GetAsync<T>(stage, key, cancellationToken);

        public Task<byte[]> PutAsync<T>(string stage, byte[] key, byte[] settingsHash, byte[] inputHash, T value, CancellationToken cancellationToken) =>
            store.PutAsync(stage, key, value, cancellationToken);
    }

    private async Task<PreviewSiteProfile> BeforeAfterAsync(PreviewRun run, PreviewContent content, Uri url, ReadOnlyMemory<byte> bytes,
        (Guid Id, HtmlProfileVersion Profile)? profile, string after, CancellationToken cancellationToken)
    {
        var host = profile?.Profile.Host ?? SiteProfileHost.Of(url) ?? url.IdnHost;
        var defaults = html.Extract(bytes, new HtmlProfileVersion(host, 0, HtmlPresets.Generic.Profile)).Document;
        var before = await run.Store.ShowAsync(content.Index, "site-profile-before", StageNames.Parse, defaults, defaults.Blocks.Count, CutBlocks, cancellationToken);
        string? preset = null;
        if (profile is null)
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
            preset = presets.Select(document).Name;
        }

        return new PreviewSiteProfile
        {
            Host = host,
            Origin = profile is null ? SiteProfileOrigin.Preset : SiteProfileOrigin.Manual,
            SiteProfileVersionId = profile?.Id,
            PresetId = preset,
            Before = before.Path,
            After = after,
        };
    }

    private IReadOnlyList<PreviewPiiSpan> Spans(ChunkSetDraft chunks) =>
        [.. chunks.Chunks.SelectMany(c => patterns.Scan(c.Text).Select(m => new PreviewPiiSpan(c.Ordinal, m.Type, m.Start, m.Length)))];

    /// <summary>
    /// The document's admin-made parse-rule candidates, matched as the live segment matches them (the source's department,
    /// or the collection's for a URL without a source), so the preview's parse key is the live one.
    /// </summary>
    private static Task<IReadOnlyList<ParseRule>> ParseRuleCandidatesAsync(ParseRuleCatalog catalog, PreviewRun run, PreviewContent content, string mediaType,
        CancellationToken cancellationToken) =>
        content.SourceId is { } sourceId
            ? catalog.CandidatesAsync(content.Level, sourceId, content.Url, mediaType, cancellationToken)
            : catalog.CandidatesForDepartmentAsync(content.Level, run.Collection.DepartmentId, content.Url, mediaType, cancellationToken);

    private static ParsedDocument CutBlocks(ParsedDocument document, int count) => document with { Blocks = [.. document.Blocks.Take(count)] };

    private static ChunkSetDraft CutChunks(ChunkSetDraft chunks, int count) => chunks with { Chunks = [.. chunks.Chunks.Take(count)] };

    private static string RouteName(ParseRoute route) => route switch
    {
        ParseRoute.Html => ParseRoutes.Html,
        ParseRoute.Text => ParseRoutes.Text,
        ParseRoute.Csv => ParseRoutes.Csv,
        _ => ParseRoutes.Parser,
    };
}

/// <summary>The embedding cache as a preview sees it: lookups only; puts and touches do nothing (engine design §11: no cache writes).</summary>
public sealed class ReadOnlyEmbeddingCache(IEmbeddingCache inner) : IEmbeddingCache
{
    public Task<IReadOnlyDictionary<string, float[]>> GetAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes,
        CancellationToken cancellationToken) => inner.GetAsync(classification, profileId, textHashes, cancellationToken);

    public Task PutAsync(string classification, EmbeddingProfileRecord embeddedWith, IReadOnlyDictionary<string, float[]> vectors, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task TouchAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
