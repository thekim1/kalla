using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Ports;
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
using Ume.Rag.Worker.Processing.Text;
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

    public DateTimeOffset? HttpLastModified { get; init; }

    /// <summary>The applied site profile of the document's host (HTML), with its version id; null applies a preset.</summary>
    public (Guid Id, HtmlProfileVersion Profile)? SiteProfile { get; init; }

    /// <summary>A <c>parse</c> job's slice budget (engine design §9.5); null never yields.</summary>
    public ParseSliceClock? Slice { get; init; }
}

/// <summary>Where a segment ended.</summary>
public abstract record SegmentResult(IReadOnlyList<NodeOutputRecord> Outputs)
{
    /// <summary>Through <c>personal-data</c>: the <c>embed</c> job continues from <paramref name="PersonalDataKey"/>.</summary>
    public sealed record Embed(byte[] PersonalDataKey, DocumentFacts Facts, IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs);

    /// <summary>A parser-bound document at the end of the <c>fetch</c> job: the <c>parse</c> job continues from <paramref name="DetectKey"/>.</summary>
    public sealed record Parse(byte[] DetectKey, DocumentFacts Facts, IReadOnlyList<NodeOutputRecord> Outputs) : SegmentResult(Outputs);

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
/// and resumes from them, and yields at a range boundary when the job's slice is used up (§9.5).
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
    public static byte[] DetectKey(ResolvedPipeline pipeline, byte[] contentHash)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return StageKeys.Key(contentHash, StageNames.Detect, StageVersion(pipeline, StageNames.Detect), StageKeys.SettingsHash(pipeline, StageNames.Detect, null));
    }

    private async Task<SegmentResult> RunStagesAsync(SegmentInput input, string jobClass, byte[]? detectKey, List<NodeOutputRecord> written,
        CancellationToken cancellationToken)
    {
        var document = input.Document;
        var level = document.Classification;
        var pipeline = input.Pipeline.Pipeline;
        var contentHash = document.ContentHash ?? throw new InvalidOperationException("The document has no content yet.");
        ReadOnlyMemory<byte>? raw = null;
        async Task<ReadOnlyMemory<byte>> RawAsync()
        {
            if (raw is { } loaded)
            {
                return loaded;
            }

            await using var stream = await blobs.OpenReadAsync(input.RawKey, null, cancellationToken)
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
        var detectSettings = StageKeys.SettingsHash(pipeline, StageNames.Detect, null);
        var dKey = detectKey ?? DetectKey(pipeline, contentHash);
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
            return new SegmentResult.Parse(dKey, Facts(detected, null, null, null, input), written);
        }

        // parse
        var parserBuildHash = detected.Route == ParseRoute.Parser ? await ParserBuildAsync(cancellationToken) : null;
        var rules = detected.Route == ParseRoute.Parser && parseRules is not null
            ? await parseRules.CandidatesAsync(level, document.SourceId, document.Url, detected.MediaType, cancellationToken)
            : [];
        var (parseKey, parseSettings) = ParseKey(pipeline, contentHash, detected.Route, input.SiteProfile?.Id, parserBuildHash, AdminParseRuleSet.Key(rules));
        ParsedDocument parsed;
        byte[] parsedHash;
        if (await outputs.GetAsync<ParsedDocument>(parseKey, cancellationToken) is { } cachedParse)
        {
            (parsed, parsedHash) = cachedParse;
        }
        else
        {
            var progress = jobClass == Domain.Entities.JobClasses.Parse ? new ParseProgress(new BlobParseRanges(blobs, level, parseKey), input.Slice) : null;
            parsed = await ParseAsync(await RawAsync(), detected, input, pipeline, progress, rules, cancellationToken);
            var row = await outputs.PutAsync(level, StageNames.Parse, parseKey, parseSettings, contentHash, parsed, cancellationToken);
            written.Add(row);
            parsedHash = row.OutputHash;
        }

        // clean
        var (cleaned, cleanedHash) = await StageAsync(level, pipeline, StageNames.Clean, parsedHash, null, written,
            () => Task.FromResult(Cleaner.Clean(parsed)), cancellationToken);
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(Pipelines.Jobs.RunItemOutcome.Skipped, "parse.empty");
        }

        // chunk (with metadata)
        var metadataInput = new MetadataInput(detected.MediaType)
        {
            Url = document.Url is { } address ? new Uri(address) : null,
            DisplayName = document.DisplayName,
            Title = cleaned.Title,
            VisibleDate = cleaned.VisibleDate,
            DocumentDate = cleaned.DocumentDate,
            HttpLastModified = input.HttpLastModified,
        };
        var chunkInputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["metadata"] = StageKeys.ToHex(ProcessingJson.OutputHash(metadataInput)) };
        var (chunks, chunksHash) = await StageAsync(level, pipeline, StageNames.Chunk, cleanedHash, chunkInputs, written, () =>
        {
            var metadata = Metadata.Apply(Param<IReadOnlyList<PathRule>>(pipeline, "pathRules") ?? [], metadataInput);
            var options = new ChunkOptions(Param<int?>(pipeline, "chunkTokens") ?? 400, Param<int?>(pipeline, "overlapPercent") ?? 15);
            return Task.FromResult(chunker.Chunk(cleaned, options, metadata));
        }, cancellationToken);

        // personal-data
        var values = await settings.GetAsync(cancellationToken);
        var pdInputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["collectionLevel"] = input.CollectionClassification };
        var pdSettings = StageKeys.SettingsHash(pipeline, StageNames.PersonalData, null, pdInputs);
        var pdKey = StageKeys.Key(chunksHash, StageNames.PersonalData, StageVersion(pipeline, StageNames.PersonalData), pdSettings);
        if (await outputs.GetAsync<ChunkSetDraft>(pdKey, cancellationToken) is null)
        {
            var (withPii, _) = await personalData.RunAsync(chunks, values.PersonalData(input.CollectionClassification), cancellationToken);
            written.Add(await outputs.PutAsync(level, StageNames.PersonalData, pdKey, pdSettings, chunksHash, withPii, cancellationToken));
        }

        return new SegmentResult.Embed(pdKey, Facts(detected, parsed, cleaned, parseKey, input), written);
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

    private async Task<ParsedDocument> ParseAsync(ReadOnlyMemory<byte> bytes, DetectResult detected, SegmentInput input, ResolvedPipeline pipeline,
        ParseProgress? progress, IReadOnlyList<ParseRule> rules, CancellationToken cancellationToken) =>
        detected.Route switch
        {
            ParseRoute.Html => html.Extract(bytes, input.SiteProfile?.Profile).Document,
            ParseRoute.Text => PlainTextParser.Parse(bytes.Span, markdown: detected.MediaType == ProcessingMediaTypes.Markdown),
            ParseRoute.Csv => CsvTableParser.Parse(bytes.Span),
            ParseRoute.Parser when converter is not null => await new ParserRoute(converter, planner, parseOptions).ParseAsync(bytes, detected,
                new ParseParams(Param<string>(pipeline, "ocr") ?? "auto", Param<string>(pipeline, "tables") ?? "accurate"),
                input.Document.CurrentExecutionId?.ToString("N") ?? Guid.CreateVersion7().ToString("N"), cancellationToken, progress, rules),
            ParseRoute.Parser => throw new StageTransientException("parser.not_configured", TimeSpan.FromMinutes(1), countsAttempt: false),
            _ => throw new StageFatalException(Pipelines.Jobs.RunItemOutcome.Unsupported, "parser.unsupported_format"),
        };

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
        string? parseRule = null)
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

        var settings = StageKeys.SettingsHash(pipeline, StageNames.Parse, RouteName(route), parseInputs);
        var key = route == ParseRoute.Parser
            ? StageKeys.ParserKey(contentHash, parserBuildHash ?? throw new ArgumentNullException(nameof(parserBuildHash)), settings)
            : StageKeys.Key(contentHash, StageNames.Parse, parseVersion, settings);
        return (key, settings);
    }

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
