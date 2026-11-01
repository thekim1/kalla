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
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Clean;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;
using Ume.Rag.Worker.Processing.Text;

namespace Ume.Rag.Worker.Preview;

/// <summary>What one preview job runs with: its pipeline version, the collection, the stages up to <c>untilStage</c> and its store.</summary>
public sealed record PreviewRun(Guid JobId, PipelineRun Pipeline, PreviewCollectionRow Collection, IReadOnlyList<string> Stages, PreviewStore Store)
{
    public bool Embed { get; init; }

    public bool PiiSpans { get; init; }
}

/// <summary>One document's fetched content: where its raw bytes are and what the stages read besides them.</summary>
public sealed record PreviewContent(int Index, string Level, byte[] ContentHash, BlobKey RawKey)
{
    public string? MediaType { get; init; }

    public string? Url { get; init; }

    public string? DisplayName { get; init; }

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
    EmbeddingCanary canary,
    EmbeddingConcurrency concurrency,
    IOptions<EmbedOptions> embedOptions,
    TimeProvider time,
    IParserConverter? converter = null,
    IParserBuild? parserBuild = null,
    ParseRuleCatalog? parseRules = null)
{
    /// <summary>The pages a preview parses of a paged document (engine design §11).</summary>
    public const int MaxPages = 50;

    private readonly EmbedStage _embed = new(embeddings, profiles, new ReadOnlyEmbeddingCache(embeddingCache), canary, concurrency, embedOptions, time);

    /// <summary>Runs the stages after <c>fetch</c>; returns the site profile's before/after of an HTML page with a URL.</summary>
    public async Task<PreviewSiteProfile?> RunAsync(PreviewRun run, PreviewContent content, PreviewStageLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(log);
        var store = run.Store;
        var pipeline = run.Pipeline.Pipeline;
        var level = content.Level;
        var url = content.Url is { } address ? new Uri(address) : null;
        ReadOnlyMemory<byte>? raw = null;
        async Task<ReadOnlyMemory<byte>> RawAsync()
        {
            if (raw is { } loaded)
            {
                return loaded;
            }

            // An upload key names no level: it is read by the document's, which may be below the preview's (data-model §7).
            await using var stream = await store.Blobs.ForLevel(level).OpenReadAsync(content.RawKey, null, cancellationToken)
                ?? throw new StageFatalException(RunItemOutcome.Failed, "raw.missing");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            raw = buffer.ToArray();
            return raw.Value;
        }

        if (!log.Wants(StageNames.Detect))
        {
            return null;
        }

        // detect
        log.Begin(StageNames.Detect);
        var detectSettings = StageKeys.SettingsHash(pipeline, StageNames.Detect, null);
        var detectKey = StageKeys.Key(content.ContentHash, StageNames.Detect, StageVersion(pipeline, StageNames.Detect), detectSettings);
        var detected = (await store.GetAsync<DetectResult>(StageNames.Detect, detectKey, cancellationToken))?.Value;
        var detectHit = detected is not null;
        if (detected is null)
        {
            detected = await detect.RunAsync(new RawContent(await RawAsync(), content.MediaType) { Url = url, DisplayName = content.DisplayName }, cancellationToken);
            await store.PutAsync(StageNames.Detect, detectKey, detected, cancellationToken);
        }

        log.Done(detectHit, new { detected.MediaType, route = RouteName(detected.Route), detected.Language, detected.PageCount });
        if (!log.Wants(StageNames.Parse))
        {
            return null;
        }

        // parse, and for an HTML page the extraction without and with the site profile
        log.Begin(StageNames.Parse);
        var profile = detected.Route == ParseRoute.Html ? await siteProfiles.AppliedAsync(level, content.Url, cancellationToken) : null;
        // The same key as the live segment (incl. the parser build), so a preview reuses live parses read-only.
        string? parserBuildHash = null;
        if (detected.Route == ParseRoute.Parser)
        {
            parserBuildHash = parserBuild is not null && converter is not null
                ? await parserBuild.GetAsync(cancellationToken)
                : throw new StageFatalException(RunItemOutcome.Failed, "parser.not_configured");
        }

        var rules = detected.Route == ParseRoute.Parser && parseRules is not null
            ? await ParseRuleCandidatesAsync(parseRules, run, content, detected.MediaType, cancellationToken)
            : [];
        var (parseKey, _) = ProcessingSegment.ParseKey(pipeline, content.ContentHash, detected.Route, profile?.Id, parserBuildHash, AdminParseRuleSet.Key(rules));
        var limited = detected.Inspect is { Pages.Count: > MaxPages } inspect
            ? detected with { Inspect = inspect with { Pages = [.. inspect.Pages.OrderBy(p => p.Page).Take(MaxPages)] } }
            : detected;
        var parseHit = await store.GetAsync<ParsedDocument>(StageNames.Parse, parseKey, cancellationToken);
        ParsedDocument parsed;
        byte[] parsedHash;
        if (parseHit is { } cachedParse)
        {
            (parsed, parsedHash) = cachedParse;
        }
        else
        {
            parsed = await ParseAsync(await RawAsync(), limited, profile?.Profile, pipeline, rules, cancellationToken);
            parsedHash = await store.PutAsync(StageNames.Parse, parseKey, parsed, cancellationToken);
        }

        var shownParse = await store.ShowAsync(content.Index, StageNames.Parse, StageNames.Parse, parsed, parsed.Blocks.Count, CutBlocks, cancellationToken);
        PreviewSiteProfile? siteProfile = null;
        if (detected.Route == ParseRoute.Html && url is not null)
        {
            siteProfile = await BeforeAfterAsync(run, content, url, await RawAsync(), profile, shownParse.Path, cancellationToken);
        }

        log.Done(parseHit is not null, new
        {
            parsed.Strategy,
            blocks = parsed.Blocks.Count,
            pages = parsed.Pages.Count,
            pagesLimited = !ReferenceEquals(limited, detected) && parseHit is null,
        }, shownParse.Path, shownParse.Truncated);
        if (!log.Wants(StageNames.Clean))
        {
            return siteProfile;
        }

        // clean
        log.Begin(StageNames.Clean);
        var (cleaned, cleanedHash, cleanHit) = await StageAsync(store, pipeline, StageNames.Clean, parsedHash, null, () => Cleaner.Clean(parsed), cancellationToken);
        var shownClean = await store.ShowAsync(content.Index, StageNames.Clean, StageNames.Clean, cleaned, cleaned.Blocks.Count, CutBlocks, cancellationToken);
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(RunItemOutcome.Skipped, "parse.empty");
        }

        log.Done(cleanHit, new { blocks = cleaned.Blocks.Count, cleaned.Language }, shownClean.Path, shownClean.Truncated);
        if (!log.Wants(StageNames.Chunk))
        {
            return siteProfile;
        }

        // chunk (with metadata)
        log.Begin(StageNames.Chunk);
        var metadataInput = new MetadataInput(detected.MediaType)
        {
            Url = url,
            DisplayName = content.DisplayName,
            Title = cleaned.Title,
            VisibleDate = cleaned.VisibleDate,
            DocumentDate = cleaned.DocumentDate,
        };
        var chunkInputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["metadata"] = StageKeys.ToHex(ProcessingJson.OutputHash(metadataInput)) };
        var (chunks, chunksHash, chunkHit) = await StageAsync(store, pipeline, StageNames.Chunk, cleanedHash, chunkInputs, () =>
        {
            var metadata = Metadata.Apply(Param<IReadOnlyList<PathRule>>(pipeline, "pathRules") ?? [], metadataInput);
            var options = new ChunkOptions(Param<int?>(pipeline, "chunkTokens") ?? 400, Param<int?>(pipeline, "overlapPercent") ?? 15);
            return chunker.Chunk(cleaned, options, metadata);
        }, cancellationToken);
        var shownChunks = await store.ShowAsync(content.Index, StageNames.Chunk, StageNames.Chunk, chunks, chunks.Chunks.Count, CutChunks, cancellationToken);
        log.Done(chunkHit, new { chunks = chunks.Chunks.Count, tokens = chunks.Chunks.Sum(c => c.TokenCount) }, shownChunks.Path, shownChunks.Truncated);
        if (!log.Wants(StageNames.PersonalData))
        {
            return siteProfile;
        }

        // personal-data, with the state the set would get in the collection
        log.Begin(StageNames.PersonalData);
        var values = await settings.GetAsync(cancellationToken);
        var collectionLevel = run.Collection.Classification;
        var pdInputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["collectionLevel"] = collectionLevel };
        var pdSettings = StageKeys.SettingsHash(pipeline, StageNames.PersonalData, null, pdInputs);
        var pdKey = StageKeys.Key(chunksHash, StageNames.PersonalData, StageVersion(pipeline, StageNames.PersonalData), pdSettings);
        var pdHit = await store.GetAsync<ChunkSetDraft>(StageNames.PersonalData, pdKey, cancellationToken);
        ChunkSetDraft withPii;
        if (pdHit is { } cachedPii)
        {
            withPii = cachedPii.Value;
        }
        else
        {
            (withPii, _) = await personalData.RunAsync(chunks, values.PersonalData(collectionLevel), cancellationToken);
            await store.PutAsync(StageNames.PersonalData, pdKey, withPii, cancellationToken);
        }

        var counts = withPii.Pii ?? new PiiCounts();
        var fired = PiiFlagPolicy.Fired(counts, values.PiiFlagRules);
        var clearance = content.DocumentId is { } documentId ? await chunkSets.GetClearanceAsync(documentId, cancellationToken) : null;
        var wouldBe = new ChunkSetRecord(Guid.Empty,
            new ChunkSetKey(content.DocumentId ?? Guid.Empty, run.Pipeline.VersionId, profile?.Id, run.Collection.EmbeddingProfileId, content.ContentHash),
            level, run.Collection.Id, chunksHash) { PiiFlagged = fired.Count > 0, PiiCounts = counts };
        var state = PiiLifecycle.StateFor(wouldBe, collectionLevel, content.PiiMode, clearance, values.PiiFlagRules);
        var pii = new PreviewPii(counts, fired, state) { Spans = run.PiiSpans ? Spans(withPii) : null };
        var shownPii = await store.ShowAsync(content.Index, StageNames.PersonalData, StageNames.PersonalData, pii, pii.Spans?.Count ?? 0,
            (p, n) => p with { Spans = p.Spans?.Take(n).ToList() }, cancellationToken);
        log.Done(pdHit is not null, new { state, flagged = fired.Count > 0, types = fired, ner = counts.Ner }, shownPii.Path, shownPii.Truncated);
        if (!log.Wants(StageNames.Embed))
        {
            return siteProfile;
        }

        // embed: counts only, unless asked to call the gateway
        log.Begin(StageNames.Embed);
        var tokens = withPii.Chunks.Sum(c => (long)c.TokenCount);
        if (run.Embed)
        {
            var embeddingProfile = await profiles.GetAsync(run.Collection.EmbeddingProfileId, cancellationToken)
                ?? throw new StageFatalException(RunItemOutcome.Failed, "embed.profile_unknown");
            if (canary.For(embeddingProfile.Id) is null)
            {
                await canary.CheckAsync(embeddingProfile, cancellationToken);
            }

            var embedded = await _embed.RunAsync(withPii, level, embeddingProfile.Id, new EmbeddingCall(null), cancellationToken);
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

        return siteProfile;
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

    private static async Task<(T Value, byte[] Hash, bool Hit)> StageAsync<T>(PreviewStore store, ResolvedPipeline pipeline, string stage, byte[] inputHash,
        IReadOnlyDictionary<string, string>? inputs, Func<T> run, CancellationToken cancellationToken)
    {
        var settingsHash = StageKeys.SettingsHash(pipeline, stage, null, inputs);
        var key = StageKeys.Key(inputHash, stage, StageVersion(pipeline, stage), settingsHash);
        if (await store.GetAsync<T>(stage, key, cancellationToken) is { } cached)
        {
            return (cached.Value, cached.OutputHash, true);
        }

        var value = run();
        return (value, await store.PutAsync(stage, key, value, cancellationToken), false);
    }

    /// <summary>
    /// The document's admin-made parse-rule candidates, matched as the live segment matches them (the source's department,
    /// or the collection's for a URL without a source), so the preview's parse key is the live one.
    /// </summary>
    private static Task<IReadOnlyList<ParseRule>> ParseRuleCandidatesAsync(ParseRuleCatalog catalog, PreviewRun run, PreviewContent content, string mediaType,
        CancellationToken cancellationToken) =>
        content.SourceId is { } sourceId
            ? catalog.CandidatesAsync(content.Level, sourceId, content.Url, mediaType, cancellationToken)
            : catalog.CandidatesForDepartmentAsync(content.Level, run.Collection.DepartmentId, content.Url, mediaType, cancellationToken);

    private async Task<ParsedDocument> ParseAsync(ReadOnlyMemory<byte> bytes, DetectResult detected, HtmlProfileVersion? profile, ResolvedPipeline pipeline,
        IReadOnlyList<ParseRule> rules, CancellationToken cancellationToken) =>
        detected.Route switch
        {
            ParseRoute.Html => html.Extract(bytes, profile).Document,
            ParseRoute.Text => PlainTextParser.Parse(bytes.Span, markdown: detected.MediaType == ProcessingMediaTypes.Markdown),
            ParseRoute.Csv => CsvTableParser.Parse(bytes.Span),
            ParseRoute.Parser when converter is not null => await new ParserRoute(converter, planner, parseOptions).ParseAsync(bytes, detected,
                new ParseParams(Param<string>(pipeline, "ocr") ?? "auto", Param<string>(pipeline, "tables") ?? "accurate"),
                Guid.CreateVersion7().ToString("N"), cancellationToken, rules: rules),
            ParseRoute.Parser => throw new StageFatalException(RunItemOutcome.Failed, "parser.not_configured"),
            _ => throw new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format"),
        };

    private static ParsedDocument CutBlocks(ParsedDocument document, int count) => document with { Blocks = [.. document.Blocks.Take(count)] };

    private static ChunkSetDraft CutChunks(ChunkSetDraft chunks, int count) => chunks with { Chunks = [.. chunks.Chunks.Take(count)] };

    private static int StageVersion(ResolvedPipeline pipeline, string stage) =>
        pipeline.Template.Template.Stages.TryGetValue(stage, out var version) ? version : StageManifest.P1Version;

    private static T? Param<T>(ResolvedPipeline pipeline, string name) =>
        pipeline.Params[name] is { } node ? node.Deserialize<T>(ContractJson.Options) : default;

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

    public Task PutAsync(string classification, Guid profileId, IReadOnlyDictionary<string, float[]> vectors, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task TouchAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
