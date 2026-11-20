using Kalla.Pipelines.Definitions;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Clean;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Pii;
using Outcome = Kalla.Pipelines.Jobs.RunItemOutcome;

namespace Kalla.Worker.Engine;

/// <summary>Where a stage run reads and writes its outputs: the output cache (engine) or a preview's own store.</summary>
public interface IStageStore
{
    /// <summary>The value under <paramref name="key"/> with its output hash, or null on a miss.</summary>
    Task<(T Value, byte[] OutputHash)?> GetAsync<T>(string stage, byte[] key, CancellationToken cancellationToken);

    /// <summary>Stores a computed value; returns its output hash (the next stage's input).</summary>
    Task<byte[]> PutAsync<T>(string stage, byte[] key, byte[] settingsHash, byte[] inputHash, T value, CancellationToken cancellationToken);
}

/// <summary>Watches a stage run: which stages it runs (a preview's <c>untilStage</c>) and what each gave.</summary>
public interface IStageObserver
{
    /// <summary>False stops the run before <paramref name="stage"/>.</summary>
    bool Wants(string stage);

    void Begin(string stage);

    /// <summary>The stage begun last succeeded; <paramref name="run"/> holds its output and everything before it.</summary>
    ValueTask DoneAsync(string stage, bool cacheHit, StageRunState run, CancellationToken cancellationToken);
}

/// <summary>What the parse stage of one document runs with, chosen by the caller once <c>detect</c> is known.</summary>
/// <param name="SiteProfile">The host's site profile the HTML parse applies (its id is in the parse key); null for a preset.</param>
/// <param name="ReliableLastmod">A sitemap <c>lastmod</c> the chunk stage may use as the document date (processing design §7).</param>
/// <param name="ParserBuild">The parser build hash (parser route only; part of its key).</param>
/// <param name="Rules">The admin-made parse-rule candidates (parser route only; the <c>parseRule</c> input).</param>
public sealed record ParseContext((Guid Id, HtmlProfileVersion Profile)? SiteProfile, DateTimeOffset? ReliableLastmod, string? ParserBuild,
    IReadOnlyList<ParseRule> Rules);

/// <summary>One document's stage run: its content and what differs between the engine and a preview.</summary>
public sealed record StagePlan(ResolvedPipeline Pipeline, string Level, string CollectionLevel, byte[] ContentHash)
{
    /// <summary>The raw bytes; read once, on the first stage that misses the cache.</summary>
    public required Func<CancellationToken, Task<ReadOnlyMemory<byte>>> Raw { get; init; }

    /// <summary>The parse stage's site profile, rules, parser build and the reliable <c>lastmod</c>, once <c>detect</c> is known.</summary>
    public required Func<DetectResult, CancellationToken, Task<ParseContext>> ParseContext { get; init; }

    public string? DeclaredMediaType { get; init; }

    public Uri? Url { get; init; }

    public string? DisplayName { get; init; }

    public DateTimeOffset? HttpLastModified { get; init; }

    /// <summary>A forced reprocess's salt per stage (engine design §8; <see cref="StageBypass"/>); none in a preview.</summary>
    public Func<string, string?> Salt { get; init; } = _ => null;

    /// <summary>The <c>detect</c> key a <c>parse</c> job was enqueued with; null computes it.</summary>
    public byte[]? DetectKey { get; init; }

    /// <summary>Stop before <c>parse</c> when <c>detect</c> routes to the parser (a <c>process</c> job hands it to a <c>parse</c> job).</summary>
    public bool StopBeforeParserRoute { get; init; }

    /// <summary>The detection the parse runs with on a miss (a preview limits a paged document's pages).</summary>
    public Func<DetectResult, DetectResult> LimitParse { get; init; } = detected => detected;

    /// <summary>The parse's progress store for a ranged parse (a <c>parse</c> job), by parse key.</summary>
    public Func<byte[], ParseProgress?> Progress { get; init; } = _ => null;

    public string RequestId { get; init; } = Guid.CreateVersion7().ToString("N");
}

/// <summary>A stage run's outputs so far: each stage's value, key and output hash, in stage order.</summary>
public sealed class StageRunState
{
    internal StageRunState(StagePlan plan) => Plan = plan;

    public StagePlan Plan { get; }

    public byte[]? DetectKey { get; internal set; }

    public DetectResult? Detected { get; internal set; }

    public ParseContext? ParseContext { get; internal set; }

    public byte[]? ParseKey { get; internal set; }

    public ParsedDocument? Parsed { get; internal set; }

    public byte[]? ParsedHash { get; internal set; }

    /// <summary>The parse ran now on fewer pages than the document has (<see cref="StagePlan.LimitParse"/>).</summary>
    public bool ParseLimited { get; internal set; }

    public ParsedDocument? Cleaned { get; internal set; }

    public byte[]? CleanedHash { get; internal set; }

    public DocumentMetadata? Metadata { get; internal set; }

    public ChunkSetDraft? Chunks { get; internal set; }

    public byte[]? ChunksHash { get; internal set; }

    public byte[]? PersonalDataKey { get; internal set; }

    public ChunkSetDraft? WithPii { get; internal set; }

    /// <summary>The stage the run stopped before (not wanted, or <c>parse</c> handed to a parse job); null when it ran through.</summary>
    public string? StoppedBefore { get; internal set; }

    internal ReadOnlyMemory<byte>? RawBytes { get; set; }

    /// <summary>The raw bytes (read once).</summary>
    public async Task<ReadOnlyMemory<byte>> RawAsync(CancellationToken cancellationToken)
    {
        if (RawBytes is { } loaded)
        {
            return loaded;
        }

        RawBytes = await Plan.Raw(cancellationToken);
        return RawBytes.Value;
    }
}

/// <summary>
/// The processing stages after <c>fetch</c> (engine design §7, §11), one implementation for the live segment
/// (<see cref="ProcessingSegment"/>) and the preview (<c>PreviewSegment</c>), so a preview keys, hashes, skips and orders the
/// stages exactly as the engine does (#508): <c>detect</c>, <c>parse</c>, <c>clean</c> (an empty result ends the document
/// <c>parse.empty</c>), <c>chunk</c> with its metadata, <c>personal-data</c>. Each stage reads the store first and runs only on
/// a miss. The store, the observer (a preview's stop stage and display) and the <see cref="StagePlan"/> carry the
/// differences.
/// </summary>
public sealed class StageRunner(DetectStage detect, StageParser parser, Chunker chunker, PersonalDataStage personalData, EngineSettings settings)
{
    /// <summary>The <c>detect</c> key of <paramref name="contentHash"/> under <paramref name="pipeline"/>.</summary>
    public static byte[] DetectKey(ResolvedPipeline pipeline, byte[] contentHash, string? salt = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return StageKeys.Key(contentHash, StageNames.Detect, StageVersion(pipeline, StageNames.Detect), DetectSettings(pipeline, salt));
    }

    public async Task<StageRunState> RunAsync(StagePlan plan, IStageStore store, IStageObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(observer);
        var state = new StageRunState(plan);
        var pipeline = plan.Pipeline;

        // detect
        if (!Wants(StageNames.Detect))
        {
            return state;
        }

        observer.Begin(StageNames.Detect);
        var detectSalt = plan.Salt(StageNames.Detect);
        state.DetectKey = plan.DetectKey ?? DetectKey(pipeline, plan.ContentHash, detectSalt);
        bool hit;
        if (await store.GetAsync<DetectResult>(StageNames.Detect, state.DetectKey, cancellationToken) is { } cachedDetect)
        {
            (state.Detected, hit) = (cachedDetect.Value, true);
        }
        else
        {
            var raw = new RawContent(await state.RawAsync(cancellationToken), plan.DeclaredMediaType)
            {
                Url = plan.Url, DisplayName = plan.DisplayName, HttpLastModified = plan.HttpLastModified,
            };
            state.Detected = await detect.RunAsync(raw, cancellationToken);
            await store.PutAsync(StageNames.Detect, state.DetectKey, DetectSettings(pipeline, detectSalt), plan.ContentHash, state.Detected, cancellationToken);
            hit = false;
        }

        await observer.DoneAsync(StageNames.Detect, hit, state, cancellationToken);
        var detected = state.Detected;
        if (plan.StopBeforeParserRoute && detected.Route == ParseRoute.Parser)
        {
            state.StoppedBefore = StageNames.Parse;
            return state;
        }

        // parse
        if (!Wants(StageNames.Parse))
        {
            return state;
        }

        observer.Begin(StageNames.Parse);
        var context = state.ParseContext = await plan.ParseContext(detected, cancellationToken);
        var (parseKey, parseSettings) = ProcessingSegment.ParseKey(pipeline, plan.ContentHash, detected.Route, context.SiteProfile?.Id, context.ParserBuild,
            AdminParseRuleSet.Key(context.Rules), plan.Salt(StageNames.Parse));
        state.ParseKey = parseKey;
        if (await store.GetAsync<ParsedDocument>(StageNames.Parse, parseKey, cancellationToken) is { } cachedParse)
        {
            (state.Parsed, state.ParsedHash, hit) = (cachedParse.Value, cachedParse.OutputHash, true);
        }
        else
        {
            var limited = plan.LimitParse(detected);
            state.ParseLimited = !ReferenceEquals(limited, detected);
            state.Parsed = await parser.ParseAsync(await state.RawAsync(cancellationToken), limited, context.SiteProfile?.Profile, StageInputs.Parse(pipeline),
                plan.RequestId, cancellationToken, plan.Progress(parseKey), context.Rules);
            state.ParsedHash = await store.PutAsync(StageNames.Parse, parseKey, parseSettings, plan.ContentHash, state.Parsed, cancellationToken);
            hit = false;
        }

        await observer.DoneAsync(StageNames.Parse, hit, state, cancellationToken);
        var parsed = state.Parsed;

        // clean
        if (!Wants(StageNames.Clean))
        {
            return state;
        }

        observer.Begin(StageNames.Clean);
        (state.Cleaned, state.CleanedHash, hit) = await StageAsync(store, pipeline, StageNames.Clean, state.ParsedHash, Salted(null, plan.Salt(StageNames.Clean)),
            () => Cleaner.Clean(parsed), cancellationToken);
        var cleaned = state.Cleaned;
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(Outcome.Skipped, "parse.empty");
        }

        await observer.DoneAsync(StageNames.Clean, hit, state, cancellationToken);

        // chunk (with metadata)
        if (!Wants(StageNames.Chunk))
        {
            return state;
        }

        observer.Begin(StageNames.Chunk);
        var metadataInput = StageInputs.Metadata(detected.MediaType, cleaned, plan.Url, plan.DisplayName, context.ReliableLastmod, plan.HttpLastModified);
        var metadata = state.Metadata = Metadata.Apply(StageInputs.PathRules(pipeline), metadataInput);
        var chunkInputs = Salted(new Dictionary<string, string>(StringComparer.Ordinal) { ["metadata"] = StageKeys.ToHex(ProcessingJson.OutputHash(metadataInput)) },
            plan.Salt(StageNames.Chunk));
        (state.Chunks, state.ChunksHash, hit) = await StageAsync(store, pipeline, StageNames.Chunk, state.CleanedHash, chunkInputs,
            () => chunker.Chunk(cleaned, StageInputs.Chunk(pipeline), metadata), cancellationToken);
        await observer.DoneAsync(StageNames.Chunk, hit, state, cancellationToken);
        var chunks = state.Chunks;

        // personal-data
        if (!Wants(StageNames.PersonalData))
        {
            return state;
        }

        observer.Begin(StageNames.PersonalData);
        var values = await settings.GetAsync(cancellationToken);
        var pdInputs = Salted(new Dictionary<string, string>(StringComparer.Ordinal) { ["collectionLevel"] = plan.CollectionLevel },
            plan.Salt(StageNames.PersonalData));
        var pdSettings = StageKeys.SettingsHash(pipeline, StageNames.PersonalData, null, pdInputs);
        var pdKey = state.PersonalDataKey = StageKeys.Key(state.ChunksHash, StageNames.PersonalData, StageVersion(pipeline, StageNames.PersonalData), pdSettings);
        if (await store.GetAsync<ChunkSetDraft>(StageNames.PersonalData, pdKey, cancellationToken) is { } cachedPii)
        {
            (state.WithPii, hit) = (cachedPii.Value, true);
        }
        else
        {
            (state.WithPii, _) = await personalData.RunAsync(chunks, values.PersonalData(plan.CollectionLevel), cancellationToken);
            await store.PutAsync(StageNames.PersonalData, pdKey, pdSettings, state.ChunksHash, state.WithPii, cancellationToken);
            hit = false;
        }

        await observer.DoneAsync(StageNames.PersonalData, hit, state, cancellationToken);
        return state;

        bool Wants(string stage)
        {
            if (observer.Wants(stage))
            {
                return true;
            }

            state.StoppedBefore = stage;
            return false;
        }
    }

    private static async Task<(T Value, byte[] Hash, bool Hit)> StageAsync<T>(IStageStore store, ResolvedPipeline pipeline, string stage, byte[] inputHash,
        IReadOnlyDictionary<string, string>? inputs, Func<T> run, CancellationToken cancellationToken)
    {
        var settingsHash = StageKeys.SettingsHash(pipeline, stage, null, inputs);
        var key = StageKeys.Key(inputHash, stage, StageVersion(pipeline, stage), settingsHash);
        if (await store.GetAsync<T>(stage, key, cancellationToken) is { } cached)
        {
            return (cached.Value, cached.OutputHash, true);
        }

        var value = run();
        return (value, await store.PutAsync(stage, key, settingsHash, inputHash, value, cancellationToken), false);
    }

    private static byte[] DetectSettings(ResolvedPipeline pipeline, string? salt) =>
        StageKeys.SettingsHash(pipeline, StageNames.Detect, null, Salted(null, salt));

    /// <summary><paramref name="inputs"/> with the bypass salt (<see cref="StageBypass.Input"/>) when <paramref name="salt"/> is set.</summary>
    internal static Dictionary<string, string>? Salted(Dictionary<string, string>? inputs, string? salt)
    {
        if (salt is null)
        {
            return inputs;
        }

        inputs ??= new Dictionary<string, string>(StringComparer.Ordinal);
        inputs[StageBypass.Input] = salt;
        return inputs;
    }

    internal static int StageVersion(ResolvedPipeline pipeline, string stage) =>
        pipeline.Template.Template.Stages.TryGetValue(stage, out var version) ? version : StageManifest.P1Version;
}
