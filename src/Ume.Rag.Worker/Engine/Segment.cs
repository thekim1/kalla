using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.Urls;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Clean;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The output cache (engine design §8): a stage output is a content-addressed blob (<c>stages/{level}/{stage}/{key}.json.gz</c>,
/// the parse output <c>parse/{level}/{key}/result.json</c>, data-model §9) written before the commit, and a
/// <c>NodeOutputs</c> row written in the segment commit. A hit reads the blob and extends <c>LastUsedAt</c> at most daily; a
/// row whose blob is gone is a miss.
/// </summary>
public sealed class StageOutputs(INodeOutputStore outputs, IBlobStore blobs, TimeProvider time)
{
    public const string ResultFile = "result.json";

    /// <summary>The cached value under <paramref name="key"/>, with its output hash, or null.</summary>
    public async Task<(T Value, byte[] OutputHash)?> GetAsync<T>(byte[] key, CancellationToken cancellationToken)
    {
        if (await outputs.FindAsync(key, cancellationToken) is not { } row)
        {
            return null;
        }

        var blob = new BlobKey(row.IsParseOutput ? $"{row.BlobPath}/{ResultFile}" : row.BlobPath);
        await using var stream = await blobs.OpenReadAsync(blob, null, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        T? value;
        if (row.IsParseOutput)
        {
            value = await JsonSerializer.DeserializeAsync<T>(stream, ProcessingJson.Options, cancellationToken);
        }
        else
        {
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            value = await JsonSerializer.DeserializeAsync<T>(gzip, ProcessingJson.Options, cancellationToken);
        }

        if (value is null)
        {
            return null;
        }

        await outputs.TouchAsync(key, time.GetUtcNow(), cancellationToken);
        return (value, row.OutputHash);
    }

    /// <summary>Writes the value's blob (write-once) and returns the <c>NodeOutputs</c> row for the segment commit.</summary>
    public async Task<NodeOutputRecord> PutAsync<T>(string classification, string stage, byte[] key, byte[] paramsHash, byte[] inputHash, T value,
        CancellationToken cancellationToken)
    {
        var hex = StageKeys.ToHex(key);
        var parse = stage == StageNames.Parse;
        var path = parse ? $"parse/{classification}/{hex}" : $"stages/{classification}/{stage}/{hex}.json.gz";
        using var buffer = new MemoryStream();
        if (parse)
        {
            await JsonSerializer.SerializeAsync(buffer, value, ProcessingJson.Options, cancellationToken);
        }
        else
        {
            await using var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true);
            await JsonSerializer.SerializeAsync(gzip, value, ProcessingJson.Options, cancellationToken);
        }

        buffer.Position = 0;
        await blobs.PutAsync(new BlobKey(parse ? $"{path}/{ResultFile}" : path), buffer, buffer.Length, cancellationToken);
        var now = time.GetUtcNow();
        return new NodeOutputRecord(key, classification, stage, StageManifest.P1Version, paramsHash, inputHash, ProcessingJson.OutputHash(value), path,
            parse, now, now);
    }
}

/// <summary>What one document's processing segment needs (engine design §7.1): the document, its pinned pipeline version and the inputs that are not parameters.</summary>
public sealed record SegmentInput(DocumentRow Document, PipelineRun Pipeline, string CollectionClassification, BlobKey RawKey)
{
    public string? DeclaredMediaType { get; init; }

    /// <summary>
    /// The applied site profile of the document's host (URL documents), with its version id; null applies a preset.
    /// The HTML parse applies it; its <c>lastmodReliable</c> admits the sitemap <c>lastmod</c> as a date
    /// (<see cref="ReliableLastmod"/>).
    /// </summary>
    public (Guid Id, HtmlProfileVersion Profile)? SiteProfile { get; init; }

    /// <summary>The document's HTTP <c>Last-Modified</c>, stored with its last 200 (processing design §7).</summary>
    public DateTimeOffset? HttpLastModified => Document.HttpLastModified;

    /// <summary>The document's sitemap <c>lastmod</c> when its host's profile marks it reliable.</summary>
    public DateTimeOffset? ReliableLastmod =>
        StageInputs.ReliableLastmod(Document.SitemapLastMod, SiteProfile?.Profile.Profile);

    /// <summary>A <c>parse</c> job's slice budget (engine design §9.5); null never yields.</summary>
    public ParseSliceClock? Slice { get; init; }

    /// <summary>A forced reprocess's first bypassed stage (<see cref="StageBypass"/>); null looks every stage up in the cache.</summary>
    public string? BypassFrom { get; init; }
}

/// <summary>
/// The explicit cache bypass of a forced reprocess (engine design §7.5, §8): every cached stage at or after the
/// <c>fromStage</c> takes the document's execution id as the non-parameter input <c>bypass</c> of its settings (§3.3), so
/// its key is new and its lookup misses; the stage runs and stores its output under that key, next to the old output,
/// which stays (write-once). Stages before <c>fromStage</c> keep their keys and are served from the cache. A retry or a
/// later slice of the same execution finds what the execution already stored.
/// </summary>
public static class StageBypass
{
    /// <summary>The settings input that carries the execution id.</summary>
    public const string Input = "bypass";

    /// <summary>A reprocess never fetches: a bypass from <c>fetch</c> is refused.</summary>
    public const string FetchUnsupported = "reprocess.fetch_unsupported";

    /// <summary>True when <paramref name="stage"/> is a P1 stage name.</summary>
    public static bool IsStage(string? stage) => stage is not null && StageNames.Order.Contains(stage, StringComparer.Ordinal);

    /// <summary>True when a bypass from <paramref name="fromStage"/> covers <paramref name="stage"/>.</summary>
    public static bool Covers(string? fromStage, string stage) =>
        IsStage(fromStage) && Index(stage) >= Index(fromStage!);

    private static int Index(string stage) => StageNames.Order.TakeWhile(s => !string.Equals(s, stage, StringComparison.Ordinal)).Count();
}

/// <summary>Where a segment ended.</summary>
public abstract record SegmentResult(IReadOnlyList<NodeOutputRecord> Outputs)
{
    /// <summary>Through <c>personal-data</c>: the <c>embed</c> job continues from <paramref name="PersonalDataKey"/>.</summary>
    public sealed record Embed(byte[] PersonalDataKey, DocumentFacts Facts, IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs)
    {
        /// <summary>The salt of the set the <c>embed</c> job writes (<see cref="ChunkSetSalt"/>): the admin rules the parse took, and a forced run's execution.</summary>
        public string? ParseRuleKey { get; init; }
    }

    /// <summary>A parser-bound document at the end of the <c>fetch</c> job: the <c>parse</c> job continues from <paramref name="DetectKey"/>.</summary>
    public sealed record Parse(byte[] DetectKey, DocumentFacts Facts, IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs)
    {
        /// <summary>The segment's bypass (<see cref="SegmentInput.BypassFrom"/>), carried to the <c>parse</c> job.</summary>
        public string? BypassFrom { get; init; }
    }

    /// <summary>A stage ended the document (<c>skipped</c>, <c>unsupported</c>, …; engine design §7.1).</summary>
    public sealed record Ended(Outcome Outcome, string Reason, IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs);

    /// <summary>The <c>parse</c> job's slice is used up at a range boundary: the job continues in its next slice (engine design §9.5).</summary>
    public sealed record Yielded(IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs);
}

/// <summary>
/// The processing stages of one document as the engine runs them (engine design §7.1, §7.4, §8): <c>detect</c>,
/// <c>parse</c>, <c>clean</c>, <c>chunk</c> and <c>personal-data</c> in order, each looked up by its stage key first
/// (<see cref="StageKeys"/>) and stored after a miss. In a <c>fetch</c> job a parser-bound document stops after
/// <c>detect</c> (its <c>parse</c> job continues); in a <c>parse</c> job the segment starts at <c>parse</c> from the cached
/// detect output. Keys: <c>detect</c> and <c>parse</c> take the raw content hash as input (the parse key with the route and,
/// for HTML, the site-profile version as inputs; a parser-bound parse is keyed by content, parser build and parser
/// settings, <see cref="StageKeys.ParserKey"/>, plus the document's admin-made parse-rule candidates when it has any,
/// <see cref="ParseRuleCatalog"/>), every later stage its predecessor's output hash; <c>chunk</c> also takes
/// the document's metadata inputs (URL, name, dates) and <c>personal-data</c> the collection's level, since both read them.
/// In a <c>parse</c> job the parser route stores each range under the parse output's prefix (<see cref="BlobParseRanges"/>)
/// and resumes from them, and yields at a range boundary when the job's slice is used up (§9.5). A forced reprocess salts
/// the keys of the stages it bypasses with the execution id (<see cref="StageBypass"/>), so they run again instead of being
/// served from the cache; the set the <c>embed</c> job writes is salted separately (<see cref="SegmentResult.Embed.ParseRuleKey"/>),
/// with the admin rules the parse took and, when forced, the execution id, so it is a new set that is swapped in.
/// </summary>
public sealed class ProcessingSegment(
    StageOutputs outputs,
    IBlobStore blobs,
    DetectStage detect,
    HtmlExtractor html,
    Chunker chunker,
    PersonalDataStage personalData,
    IParsePlanner planner,
    IOptions<ParseOptions> parseOptions,
    EngineSettings settings,
    IParserConverter? converter = null,
    IParserBuild? parserBuild = null,
    ParseRuleCatalog? parseRules = null)
{
    private readonly StageParser _parser = new(html, planner, parseOptions, converter);

    public async Task<SegmentResult> RunAsync(SegmentInput input, string jobClass, byte[]? detectKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var written = new List<NodeOutputRecord>();
        try
        {
            return await RunStagesAsync(input, jobClass, detectKey, written, cancellationToken);
        }
        catch (StageFatalException fatal)
        {
            return new SegmentResult.Ended(fatal.Outcome, fatal.Reason, written);
        }
        catch (ParseYieldException)
        {
            return new SegmentResult.Yielded(written);
        }
    }

    /// <summary>The <c>detect</c> key of content <paramref name="contentHash"/> under <paramref name="pipeline"/>: the input key of a <c>parse</c> job.</summary>
    public static byte[] DetectKey(ResolvedPipeline pipeline, byte[] contentHash, string? bypass = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return StageKeys.Key(contentHash, StageNames.Detect, StageVersion(pipeline, StageNames.Detect),
            StageKeys.SettingsHash(pipeline, StageNames.Detect, null, Bypassed(null, bypass)));
    }

    private async Task<SegmentResult> RunStagesAsync(SegmentInput input, string jobClass, byte[]? detectKey, List<NodeOutputRecord> written,
        CancellationToken cancellationToken)
    {
        var document = input.Document;
        var level = document.Classification;
        var pipeline = input.Pipeline.Pipeline;
        var contentHash = document.ContentHash ?? throw new InvalidOperationException("The document has no content yet.");
        var forcedExecutionId = input.BypassFrom is null
            ? (Guid?)null
            : document.CurrentExecutionId ?? throw new InvalidOperationException("A forced reprocess has an execution id.");
        var salt = forcedExecutionId?.ToString("N");
        string? Salt(string stage) => StageBypass.Covers(input.BypassFrom, stage) ? salt : null;
        ReadOnlyMemory<byte>? raw = null;
        async Task<ReadOnlyMemory<byte>> RawAsync()
        {
            if (raw is { } loaded)
            {
                return loaded;
            }

            // An upload key names no level: it is read by the document's (data-model §7).
            await using var stream = await blobs.ForLevel(level).OpenReadAsync(input.RawKey, null, cancellationToken)
                ?? throw new StageTransientException("raw.missing", TimeSpan.FromMinutes(1), countsAttempt: true);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            raw = buffer.ToArray();
            return raw.Value;
        }

        RawContent Raw(ReadOnlyMemory<byte> bytes) => new(bytes, input.DeclaredMediaType)
        {
            Url = document.Url is { } url ? new Uri(url) : null,
            DisplayName = document.DisplayName,
            HttpLastModified = input.HttpLastModified,
        };

        // detect
        var detectSettings = StageKeys.SettingsHash(pipeline, StageNames.Detect, null, Bypassed(null, Salt(StageNames.Detect)));
        var dKey = detectKey ?? DetectKey(pipeline, contentHash, Salt(StageNames.Detect));
        DetectResult detected;
        if (await outputs.GetAsync<DetectResult>(dKey, cancellationToken) is { } cachedDetect)
        {
            detected = cachedDetect.Value;
        }
        else
        {
            detected = await detect.RunAsync(Raw(await RawAsync()), cancellationToken);
            written.Add(await outputs.PutAsync(level, StageNames.Detect, dKey, detectSettings, contentHash, detected, cancellationToken));
        }

        if (detected.Route == ParseRoute.Parser && jobClass != Domain.Entities.JobClasses.Parse)
        {
            return new SegmentResult.Parse(dKey, Facts(detected, null, null, null, input), written) { BypassFrom = input.BypassFrom };
        }

        // parse
        var parserBuildHash = detected.Route == ParseRoute.Parser ? await ParserBuildAsync(cancellationToken) : null;
        var rules = detected.Route == ParseRoute.Parser && parseRules is not null
            ? await parseRules.CandidatesAsync(level, document.SourceId, document.Url, detected.MediaType, cancellationToken)
            : [];
        var (parseKey, parseSettings) = ParseKey(pipeline, contentHash, detected.Route, input.SiteProfile?.Id, parserBuildHash, AdminParseRuleSet.Key(rules),
            Salt(StageNames.Parse));
        ParsedDocument parsed;
        byte[] parsedHash;
        if (await outputs.GetAsync<ParsedDocument>(parseKey, cancellationToken) is { } cachedParse)
        {
            (parsed, parsedHash) = cachedParse;
        }
        else
        {
            var progress = jobClass == Domain.Entities.JobClasses.Parse ? new ParseProgress(new BlobParseRanges(blobs, level, parseKey), input.Slice) : null;
            var requestId = document.CurrentExecutionId?.ToString("N") ?? Guid.CreateVersion7().ToString("N");
            parsed = await _parser.ParseAsync(await RawAsync(), detected, input.SiteProfile?.Profile,
                StageInputs.Parse(pipeline), requestId, cancellationToken, progress, rules);
            var row = await outputs.PutAsync(level, StageNames.Parse, parseKey, parseSettings, contentHash, parsed, cancellationToken);
            written.Add(row);
            parsedHash = row.OutputHash;
        }

        // clean
        var (cleaned, cleanedHash) = await StageAsync(level, pipeline, StageNames.Clean, parsedHash, Bypassed(null, Salt(StageNames.Clean)), written,
            () => Task.FromResult(Cleaner.Clean(parsed)), cancellationToken);
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(Pipelines.Jobs.RunItemOutcome.Skipped, "parse.empty");
        }

        // chunk (with metadata)
        var metadataInput = StageInputs.Metadata(detected.MediaType, cleaned,
            document.Url is { } address ? new Uri(address) : null, document.DisplayName, input.ReliableLastmod,
            input.HttpLastModified);
        var chunkInputs = Bypassed(new Dictionary<string, string>(StringComparer.Ordinal) { ["metadata"] = StageKeys.ToHex(ProcessingJson.OutputHash(metadataInput)) },
            Salt(StageNames.Chunk));
        var (chunks, chunksHash) = await StageAsync(level, pipeline, StageNames.Chunk, cleanedHash, chunkInputs, written, () =>
        {
            var metadata = Metadata.Apply(StageInputs.PathRules(pipeline), metadataInput);
            return Task.FromResult(chunker.Chunk(cleaned, StageInputs.Chunk(pipeline), metadata));
        }, cancellationToken);

        // personal-data
        var values = await settings.GetAsync(cancellationToken);
        var pdInputs = Bypassed(new Dictionary<string, string>(StringComparer.Ordinal) { ["collectionLevel"] = input.CollectionClassification },
            Salt(StageNames.PersonalData));
        var pdSettings = StageKeys.SettingsHash(pipeline, StageNames.PersonalData, null, pdInputs);
        var pdKey = StageKeys.Key(chunksHash, StageNames.PersonalData, StageVersion(pipeline, StageNames.PersonalData), pdSettings);
        if (await outputs.GetAsync<ChunkSetDraft>(pdKey, cancellationToken) is null)
        {
            var (withPii, _) = await personalData.RunAsync(chunks, values.PersonalData(input.CollectionClassification), cancellationToken);
            written.Add(await outputs.PutAsync(level, StageNames.PersonalData, pdKey, pdSettings, chunksHash, withPii, cancellationToken));
        }

        return new SegmentResult.Embed(pdKey, Facts(detected, parsed, cleaned, parseKey, input), written)
        {
            ParseRuleKey = ChunkSetSalt.Of(ChunkSetSalt.Rule(AdminParseRuleSet.Key(rules)), forcedExecutionId),
        };
    }

    private async Task<(T Value, byte[] Hash)> StageAsync<T>(string level, ResolvedPipeline pipeline, string stage, byte[] inputHash,
        IReadOnlyDictionary<string, string>? inputs, List<NodeOutputRecord> written, Func<Task<T>> run, CancellationToken cancellationToken)
    {
        var settingsHash = StageKeys.SettingsHash(pipeline, stage, null, inputs);
        var key = StageKeys.Key(inputHash, stage, StageVersion(pipeline, stage), settingsHash);
        if (await outputs.GetAsync<T>(key, cancellationToken) is { } cached)
        {
            return cached;
        }

        var value = await run();
        var row = await outputs.PutAsync(level, stage, key, settingsHash, inputHash, value, cancellationToken);
        written.Add(row);
        return (value, row.OutputHash);
    }

    private async Task<string> ParserBuildAsync(CancellationToken cancellationToken) =>
        parserBuild is null || converter is null
            ? throw new StageTransientException("parser.not_configured", TimeSpan.FromMinutes(1), countsAttempt: false)
            : await parserBuild.GetAsync(cancellationToken);

    private static DocumentFacts Facts(DetectResult detected, ParsedDocument? parsed, ParsedDocument? cleaned, byte[]? parseKey, SegmentInput input) =>
        new(detected.MediaType, cleaned?.Language ?? detected.Language, cleaned?.Title, cleaned?.Description, cleaned?.Keywords ?? [], parsed?.Strategy,
            parseKey, detected.PageCount ?? (parsed is { Pages.Count: > 0 } ? parsed.Pages.Count : null), input.SiteProfile?.Id, parsed is null ? "pending" : "success");

    /// <summary>
    /// The <c>parse</c> stage key and settings hash, shared with the preview so both look up the same cache entries.
    /// Parser-bound output is keyed by (content hash, parser build, parser settings), so a parser upgrade never reuses a
    /// cached parse (simplification pass §2; parser design §7.6; engine design §3.3); the planner's thresholds belong to the
    /// parse stage's version (processing design §3.1), so that version is a parser setting. <paramref name="parseRule"/> is the
    /// document's admin-made candidates (<see cref="AdminParseRuleSet.Key"/>), the <c>parseRule</c> input (engine design §3.3):
    /// a rule change re-keys only the documents in its scope, and without candidates the key is the built-in one.
    /// </summary>
    public static (byte[] Key, byte[] Settings) ParseKey(ResolvedPipeline pipeline, byte[] contentHash, ParseRoute route, Guid? siteProfileVersionId, string? parserBuildHash,
        string? parseRule = null, string? bypass = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var parseVersion = StageVersion(pipeline, StageNames.Parse);
        var parseInputs = route switch
        {
            ParseRoute.Html => new Dictionary<string, string>(StringComparer.Ordinal) { ["siteProfileVersion"] = siteProfileVersionId?.ToString("D") ?? "preset" },
            ParseRoute.Parser => new Dictionary<string, string>(StringComparer.Ordinal) { ["stageVersion"] = parseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            _ => null,
        };
        if (parseInputs is not null && route == ParseRoute.Parser && parseRule is not null)
        {
            parseInputs["parseRule"] = parseRule;
        }

        var settings = StageKeys.SettingsHash(pipeline, StageNames.Parse, RouteName(route), Bypassed(parseInputs, bypass));
        var key = route == ParseRoute.Parser
            ? StageKeys.ParserKey(contentHash, parserBuildHash ?? throw new ArgumentNullException(nameof(parserBuildHash)), settings)
            : StageKeys.Key(contentHash, StageNames.Parse, parseVersion, settings);
        return (key, settings);
    }

    /// <summary><paramref name="inputs"/> with the bypass salt (<see cref="StageBypass.Input"/>) when <paramref name="salt"/> is set.</summary>
    private static Dictionary<string, string>? Bypassed(Dictionary<string, string>? inputs, string? salt)
    {
        if (salt is null)
        {
            return inputs;
        }

        inputs ??= new Dictionary<string, string>(StringComparer.Ordinal);
        inputs[StageBypass.Input] = salt;
        return inputs;
    }

    private static int StageVersion(ResolvedPipeline pipeline, string stage) =>
        pipeline.Template.Template.Stages.TryGetValue(stage, out var version) ? version : StageManifest.P1Version;

    private static string RouteName(ParseRoute route) => route switch
    {
        ParseRoute.Html => ParseRoutes.Html,
        ParseRoute.Text => ParseRoutes.Text,
        ParseRoute.Csv => ParseRoutes.Csv,
        _ => ParseRoutes.Parser,
    };
}

/// <summary>Where a document's raw content lives (data-model §9): the fetched blob by content hash, or the upload itself.</summary>
public static class RawContentKeys
{
    public static BlobKey Of(DocumentRow document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var hash = Convert.ToHexStringLower(document.ContentHash ?? throw new InvalidOperationException("The document has no content yet."));
        return document.IdentityKind == Domain.Entities.IdentityKinds.Upload && Guid.TryParse(document.CanonicalIdentity.AsSpan("upload:".Length), out var slot)
            ? UploadConnector.BlobKeyOf(document.SourceId, slot, hash)
            : new BlobKey($"raw/{document.Classification}/{hash}");
    }
}

/// <summary>
/// The applied site profile of a (level, host) (connectors design §5.6; P1: manual and preset versions); the host is the
/// URL's <see cref="SiteProfileHost"/> key, so every port of the host finds the same profile.
/// </summary>
public sealed class SiteProfiles(JobDatabase database)
{
    private const string AppliedSql = """
        SELECT v."Id", v."Number", v."Profile"::text
        FROM content."SiteProfiles" p JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId"
        WHERE p."Classification" = $1 AND p."Host" = $2
        """;

    public async Task<(Guid Id, HtmlProfileVersion Profile)?> AppliedAsync(string classification, string? url, CancellationToken cancellationToken)
    {
        if (SiteProfileHost.Of(url) is not { } host)
        {
            return null;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(AppliedSql, connection) { Parameters = { Db.P(classification), Db.P(host) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var json = JsonDocument.Parse(reader.GetString(2));
        return (reader.GetGuid(0), new HtmlProfileVersion(host, reader.GetInt32(1), ContractJson.Deserialize<SiteProfile>(json.RootElement)));
    }
}
