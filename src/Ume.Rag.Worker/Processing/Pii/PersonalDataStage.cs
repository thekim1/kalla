using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ume.Rag.Domain;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Chunking;

namespace Ume.Rag.Worker.Processing.Pii;

/// <summary>What <c>personal-data</c> needs besides the chunks: the collection's level and the platform settings.</summary>
public sealed record PersonalDataContext(string CollectionClassification)
{
    /// <summary><c>Settings.PiiOwnDomains</c> (e.g. <c>umea.se</c>): e-mail outside them is <c>emailExternal</c>.</summary>
    public IReadOnlyList<string> OwnDomains { get; init; } = [];

    /// <summary><c>Settings.PiiFlagRules</c>.</summary>
    public PiiFlagRules Rules { get; init; } = PiiFlagRules.Default;

    /// <summary><c>Settings.NerMinScore</c>.</summary>
    public double NerMinScore { get; init; } = 0.80;
}

/// <summary>The stage's result: the counts (stored on the set) and which types fired (event 5103's <c>Types</c>).</summary>
public sealed record PiiAssessment(PiiCounts Counts, IReadOnlyList<string> TypesFired)
{
    /// <summary><c>ChunkSets."PiiFlagged"</c>: a rule fired.</summary>
    public bool Flagged => TypesFired.Count > 0;
}

/// <summary>
/// <c>personal-data</c> (processing design §9): the patterns over the chunk bodies with their overlap stripped (each text
/// counted once; table row groups as they are), then names through the parser's NER only when they can change the flag
/// (§9.3: a <c>public</c> collection, no other rule fired, ≥ 5 distinct contact values), then the counts and the flag
/// (§9.4). NER <c>busy</c>/<c>not_ready</c> requeue the job without an attempt; NER not ready for more than
/// <see cref="NerWait"/> completes with patterns only and <c>ner: pending</c> (§9.6). Values never leave the stage.
/// </summary>
public sealed partial class PersonalDataStage(PiiPatterns patterns, INerClient? ner, IFirstNameGazetteer gazetteer, TimeProvider clock)
{
    public const int Version = 1;
    public const int MaxTextsPerRequest = 64;
    public const int MaxTextChars = 4_000;
    public const int MaxRequestChars = 100_000;
    public static readonly TimeSpan NerWait = TimeSpan.FromMinutes(5);

    private readonly Lock _readinessLock = new();
    private DateTimeOffset? _notReadySince;

    public async Task<(ChunkSetDraft Chunks, PiiAssessment Assessment)> RunAsync(ChunkSetDraft chunks, PersonalDataContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(context);
        var bodies = chunks.Chunks.Select(c => c.OverlapChars > 0 && c.OverlapChars <= c.Text.Length ? c.Text[c.OverlapChars..] : c.Text).ToList();
        var own = OwnDomains(context.OwnDomains);
        var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in PiiTypes.All)
        {
            values[type] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var body in bodies)
        {
            foreach (var match in patterns.Scan(body))
            {
                values[match.Type].Add(match.Value);
                if (match.Type == PiiTypes.Email && !IsOwn(PiiPatterns.Domain(match.Value), own))
                {
                    values[PiiTypes.EmailExternal].Add(match.Value);
                }
            }
        }

        var counts = new PiiCounts
        {
            Personnummer = values[PiiTypes.Personnummer].Count,
            Samordningsnummer = values[PiiTypes.Samordningsnummer].Count,
            Email = values[PiiTypes.Email].Count,
            EmailExternal = values[PiiTypes.EmailExternal].Count,
            Phone = values[PiiTypes.Phone].Count,
            Ner = NerStatus.Skipped,
        };

        var fired = PiiFlagPolicy.Fired(counts, context.Rules);
        var contacts = values[PiiTypes.Phone].Count + values[PiiTypes.Email].Count(e => IsOwn(PiiPatterns.Domain(e), own));
        var needed = context.CollectionClassification == Classification.Public && fired.Count == 0 && context.Rules.ListLike is not null
            && contacts >= context.Rules.ListLike.MinContacts;
        if (needed)
        {
            counts = await NamesAsync(chunks.Chunks, bodies, counts, context, cancellationToken);
            fired = PiiFlagPolicy.Fired(counts, context.Rules);
        }

        counts = counts with { Rules = fired.Count };
        return (chunks with { Pii = counts }, new PiiAssessment(counts, fired));
    }

    private async Task<PiiCounts> NamesAsync(IReadOnlyList<ChunkDraft> chunks, List<string> bodies, PiiCounts counts, PersonalDataContext context, CancellationToken cancellationToken)
    {
        if (ner is null || !await ReadyAsync(cancellationToken))
        {
            return counts with { Ner = NerStatus.Pending };
        }

        var units = Units(chunks, bodies);
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? model = null;
        foreach (var batch in Batches(units))
        {
            NerAnswer answer;
            try
            {
                answer = await ner.RecognizeAsync(batch, context.NerMinScore, cancellationToken);
            }
            catch (ParserException e) when (e.Error?.Error.Code is "busy" or "not_ready" || e.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                throw new StageTransientException("parser.ner_" + (e.Error?.Error.Code ?? "unavailable"), e.RetryAfter ?? TimeSpan.FromSeconds(5), countsAttempt: false);
            }

            model = answer.Model;
            var texts = batch.ToDictionary(t => t.Id, t => t.Text, StringComparer.Ordinal);
            foreach (var entity in answer.Entities.Where(e => e.Label == "person" && e.Score >= context.NerMinScore))
            {
                if (texts.TryGetValue(entity.TextId, out var text) && Span(text, entity) is { Length: > 0 } name)
                {
                    names.Add(NameKey(name));
                }
            }
        }

        // Genitive "Annas" folds into "Anna" when both occur.
        var distinct = names.Where(n => !(n.EndsWith('s') && names.Contains(n[..^1]))).Count();
        return counts with { Name = distinct, Ner = NerStatus.Done, Model = model };
    }

    /// <summary>Readiness from <c>/v1/info</c>; not ready for up to <see cref="NerWait"/> requeues the job without an attempt.</summary>
    private async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        var ready = await ner!.IsReadyAsync(cancellationToken);
        var now = clock.GetUtcNow();
        lock (_readinessLock)
        {
            if (ready)
            {
                _notReadySince = null;
                return true;
            }

            _notReadySince ??= now;
            if (now - _notReadySince.Value > NerWait)
            {
                return false;
            }
        }

        throw new StageTransientException("parser.ner_not_ready", TimeSpan.FromSeconds(30), countsAttempt: false);
    }

    /// <summary>Text units (§9.3): sentences of prose bodies and rows of tables, after the prefilter.</summary>
    internal List<NerText> Units(IReadOnlyList<ChunkDraft> chunks, IReadOnlyList<string> bodies)
    {
        var units = new List<NerText>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var pieces = chunks[i].IsTable ? bodies[i].Split('\n') : SentenceSplitter.Split(bodies[i]);
            var n = 0;
            foreach (var piece in pieces.Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                foreach (var part in Cut(piece))
                {
                    if (Prefilter(part))
                    {
                        units.Add(new NerText(string.Create(CultureInfo.InvariantCulture, $"c{i}.{n++}"), part.Normalize(NormalizationForm.FormC)));
                    }
                }
            }
        }

        return units;
    }

    /// <summary>A unit is sent if it has a capitalised word that is not its first word, a first word in the gazetteer, or is ALL CAPS.</summary>
    internal bool Prefilter(string unit)
    {
        var words = Word().Matches(unit).Select(m => m.Value).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        if (words.All(w => w.All(c => !char.IsLower(c))) && words.Any(w => w.Any(char.IsLetter)))
        {
            return true;
        }

        return words.Skip(1).Any(w => char.IsUpper(w[0])) || gazetteer.Contains(words[0]);
    }

    private static IEnumerable<string> Cut(string unit)
    {
        var rest = unit;
        while (rest.Length > MaxTextChars)
        {
            var cut = rest.LastIndexOf(' ', MaxTextChars);
            cut = cut <= 0 ? MaxTextChars : cut;
            yield return rest[..cut];
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    private static IEnumerable<List<NerText>> Batches(List<NerText> units)
    {
        var batch = new List<NerText>();
        var chars = 0;
        foreach (var unit in units)
        {
            if (batch.Count == MaxTextsPerRequest || chars + unit.Text.Length > MaxRequestChars)
            {
                yield return batch;
                batch = [];
                chars = 0;
            }

            batch.Add(unit);
            chars += unit.Text.Length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>An address's domain is own when it is an own domain or a subdomain of one (<c>sub.umea.se</c>), in any case.</summary>
    private static bool IsOwn(string domain, IReadOnlyList<string> own) =>
        own.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>Settings.PiiOwnDomains</c> as bare domains: entered as <c>@umea.se</c>, <c>*.umea.se</c> or <c> umea.se. </c> they still match.</summary>
    private static string[] OwnDomains(IReadOnlyList<string> domains) =>
        [.. domains.Select(d => d.Trim().TrimStart('@', '*', '.').TrimEnd('.')).Where(d => d.Length > 0)];

    /// <summary>The entity's text by its code-point offsets (<see cref="ParserText"/>), or null when they are outside the text.</summary>
    private static string? Span(string text, NerEntity entity)
    {
        try
        {
            return entity.End < entity.Start ? null : text[ParserText.Utf16Index(text, entity.Start)..ParserText.Utf16Index(text, entity.End)];
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>NFC, case-folded, hyphenated parts joined ("Anna-Karin" = "Anna Karin" = "annakarin").</summary>
    private static string NameKey(string name) =>
        new string([.. name.Normalize(NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetter)]);

    [GeneratedRegex(@"\p{L}[\p{L}'-]*")]
    private static partial Regex Word();
}

/// <summary>The flag rules (processing design §9.4) and the clearance rule (§9.5).</summary>
public static class PiiFlagPolicy
{
    /// <summary>The types whose rule fires; the list-like rule fires as <c>name</c>, <c>phone</c>/<c>email</c> together and is reported as <c>name</c>.</summary>
    public static IReadOnlyList<string> Fired(PiiCounts counts, PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(rules);
        var fired = new List<string>();
        foreach (var (type, rule) in rules.Types)
        {
            var min = rule.Flag switch
            {
                PiiFlagMode.Always => 1,
                PiiFlagMode.Threshold => Math.Max(1, rule.Min),
                _ => int.MaxValue,
            };
            if (PiiTypes.All.Contains(type, StringComparer.Ordinal) && counts.Count(type) >= min)
            {
                fired.Add(type);
            }
        }

        if (rules.ListLike is { } list && counts.Name >= list.MinNames && counts.Phone + counts.Email >= list.MinContacts)
        {
            fired.Add(PiiTypes.Name);
        }

        return [.. fired.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The types that can flag under <paramref name="rules"/> (the clearance compares these).</summary>
    public static IReadOnlyList<string> Flagging(PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var types = rules.Types.Where(t => t.Value.Flag != PiiFlagMode.Never).Select(t => t.Key).ToList();
        if (rules.ListLike is not null)
        {
            types.AddRange([PiiTypes.Name, PiiTypes.Phone, PiiTypes.Email]);
        }

        return [.. types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A clearance covers a set when no type that can flag has a count above the cleared count (§9.5). A set whose names
    /// are still <c>pending</c> is not covered (its name count is unknown; fail closed until the sweep checks it).
    /// </summary>
    public static bool Covered(PiiCounts counts, PiiCounts? cleared, PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (cleared is null || counts.Ner == NerStatus.Pending)
        {
            return false;
        }

        return Flagging(rules).All(type => counts.Count(type) <= cleared.Count(type));
    }
}
