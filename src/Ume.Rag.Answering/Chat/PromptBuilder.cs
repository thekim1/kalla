using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>A prompt source: the document's prompt number, its citation, its chunks in rank order.</summary>
public sealed record PromptSource(int Number, Citation Citation, IReadOnlyList<RetrievedChunk> Chunks);

/// <summary>The messages for the gateway and the sources they number.</summary>
public sealed record BuiltPrompt(IReadOnlyList<ChatMessageIn> Messages, IReadOnlyList<PromptSource> Sources)
{
    /// <summary><c>P</c> of chat design §6.3: the mandatory part, the sources and the history as estimated.</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>Which frame a prompt gets (chat design §7.3).</summary>
public enum PromptKind
{
    /// <summary>Retrieved sources in a <c>&lt;sources&gt;</c> block.</summary>
    Sources,

    /// <summary>A conversational message: no retrieval, the no-sources frame.</summary>
    Conversational,

    /// <summary>Coverage <c>Low</c> with <c>fallback</c>/<c>open</c>: no sources sent, the level's frame without sources (§10.4).</summary>
    General,
}

/// <summary>
/// What every prompt of a turn is built from: the assistant, the turn's language, the question and history, the settings, the
/// turn's token limits (<see cref="AnswerLimits"/>; null resolves the settings without model limits) and the platform's prompt
/// texts (null: the defaults).
/// </summary>
public sealed record PromptContext(string AssistantName, string SystemPrompt, string Language, string Question, IReadOnlyList<ChatMessageIn> History,
    AssistantChatSettings Settings, AnswerLimits? Limits = null, PromptTexts? Texts = null);

/// <summary>
/// The prompt (chat design §6.3, §7): the locked Swedish frame, the assistant's prompt and the language line as the system
/// message; the history window; the final user message with the <c>&lt;sources&gt;</c> block (one number per document in
/// best-rank order, untrusted, tags neutralised) and the question. Tokens are estimated as ⌈characters / 3⌉ + 4 per message.
/// </summary>
public static partial class PromptBuilder
{
    public const int CharsPerToken = 3;
    private const int SourcesMaxTokens = 8000;
    private const int MinSourcesTokens = 1000;

    public static int Tokens(string text) => ((text?.Length ?? 0) + CharsPerToken - 1) / CharsPerToken + 4;

    /// <summary>
    /// A prompt of <paramref name="kind"/>: <paramref name="results"/> (required for <see cref="PromptKind.Sources"/>, ignored
    /// otherwise: §6.2, a general prompt never sends the weak hits), their expanded <paramref name="sections"/> and the degraded
    /// line belong to the sources.
    /// </summary>
    public static BuiltPrompt Build(PromptKind kind, PromptContext context, IReadOnlyList<RetrievedChunk>? results = null, IReadOnlyList<SectionText>? sections = null,
        bool degraded = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (assistantName, systemPrompt, language, question, history, settings, limits, texts) = context;
        texts ??= PromptTexts.Default;
        ArgumentNullException.ThrowIfNull(settings);
        limits ??= AnswerLimits.Resolve(settings, null);
        if (kind == PromptKind.Sources)
        {
            ArgumentNullException.ThrowIfNull(results);
        }
        else
        {
            results = null;
        }

        var sectionText = sections?.ToDictionary(x => x.Id) ?? [];
        var alwaysSwedish = settings.AlwaysSwedish;
        var system = Frame(assistantName, kind, settings.GeneralKnowledge, alwaysSwedish, degraded, texts) +
            (string.IsNullOrWhiteSpace(systemPrompt) ? "" : "\n\n" + systemPrompt.Trim()) +
            "\n\nAnvändarens språk: " + (language == LanguageGuess.English && !alwaysSwedish ? "engelska." : "svenska.");
        var questionLine = "Fråga: " + question;
        var mandatory = Tokens(system) + Tokens(questionLine);
        if (mandatory + limits.Reserve + MinSourcesTokens > limits.Context)
        {
            throw new ChatException(400, "context_length_exceeded", "The question does not fit the assistant's context window.", "messages");
        }

        // Sources first: whole documents in order while they fit; the first is cut at a sentence boundary if it alone is too long.
        var sources = new List<PromptSource>();
        var sourceText = new StringBuilder();
        var budget = Math.Min(SourcesMaxTokens, limits.Context - limits.Reserve - mandatory);
        var used = 0;
        if (results is { Count: > 0 })
        {
            var byDocument = results.OrderBy(r => r.Rank).GroupBy(r => r.DocumentId).ToList();
            foreach (var document in byDocument)
            {
                var number = sources.Count + 1;
                var chunks = document.OrderBy(r => r.Rank).ToList();
                var block = SourceBlock(number, chunks, sectionText);
                var cost = Tokens(block);
                if (used + cost > budget)
                {
                    if (sources.Count > 0)
                    {
                        continue;
                    }

                    block = Cut(block, (budget - 4) * CharsPerToken);
                    cost = Tokens(block);
                }

                sources.Add(new PromptSource(number, chunks[0].Citation, chunks));
                sourceText.Append(block);
                used += cost;
            }
        }

        // History second, newest first, within its limits; an assistant turn without its user turn is dropped; markers stripped.
        var historyBudget = Math.Min(settings.HistoryMaxTokens, limits.Context - limits.Reserve - mandatory - used);
        var window = new List<ChatMessageIn>();
        var spent = 0;
        for (var i = history.Count - 1; i >= 0 && window.Count < settings.HistoryMaxMessages; i--)
        {
            var message = history[i].Role == "assistant" ? history[i] with { Text = StripMarkers(history[i].Text) } : history[i];
            var cost = Tokens(message.Text);
            if (spent + cost > historyBudget)
            {
                break;
            }

            window.Insert(0, message);
            spent += cost;
        }

        while (window.Count > 0 && window[0].Role == "assistant")
        {
            window.RemoveAt(0);
        }

        var user = results is null
            ? questionLine
            : "<sources note=\"" + Attribute(texts[PromptTexts.SourcesLabel] + " " + PromptTexts.Locked.SourcesNote, 1200) + "\">\n" + sourceText + "</sources>\n\n" +
              questionLine;
        List<ChatMessageIn> messages = [new("system", system), .. window, new("user", user)];
        return new BuiltPrompt(messages, sources) { EstimatedTokens = mandatory + used + spent };
    }

    /// <summary>
    /// The frame (§7.3): the editable texts from <paramref name="texts"/> (<c>Settings.Chat.prompts</c>, each at its default
    /// unless set) around the locked lines. With sources: the identity, the level's rule line, the citation, no-invented-numbers
    /// and <c>&lt;sources&gt;</c> lines; for a general-knowledge answer (§10.4) the level's line without sources and the
    /// no-invented-numbers line only (no block is sent); for a conversational message (every level) the no-sources line. The
    /// degraded line when the search was degraded, and the locked language line last.
    /// </summary>
    public static string Frame(string assistantName, PromptKind kind, GeneralKnowledge level, bool alwaysSwedish, bool degraded, PromptTexts? texts = null)
    {
        texts ??= PromptTexts.Default;
        var lines = new List<string> { texts.IdentityFor(assistantName) };
        switch (kind)
        {
            case PromptKind.Sources:
                lines.Add(texts[level switch
                {
                    GeneralKnowledge.Supplement or GeneralKnowledge.Fallback => PromptTexts.RulesSupplement,
                    GeneralKnowledge.Open => PromptTexts.RulesOpen,
                    _ => PromptTexts.RulesOff,
                }]);
                lines.Add(PromptTexts.Locked.Citation);
                lines.Add(PromptTexts.Locked.NoInventedNumbers);
                lines.Add(PromptTexts.Locked.Injection);
                break;
            case PromptKind.General:
                lines.Add(texts[level switch
                {
                    GeneralKnowledge.Fallback => PromptTexts.RulesFallbackGeneral,
                    GeneralKnowledge.Open => PromptTexts.RulesOpenGeneral,
                    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Only fallback and open answer without sources."),
                }]);
                lines.Add(PromptTexts.Locked.NoInventedNumbers);
                break;
            default:
                lines.Add(texts[PromptTexts.Conversational]);
                break;
        }

        if (degraded)
        {
            lines.Add(texts[PromptTexts.Degraded]);
        }

        lines.Add(alwaysSwedish ? PromptTexts.Locked.LanguageSwedish : PromptTexts.Locked.Language);
        return string.Join('\n', lines);
    }

    /// <summary>One document's block: its chunks' sections (knowledge §9, expanded once each) or the chunk texts.</summary>
    private static string SourceBlock(int number, List<RetrievedChunk> chunks, Dictionary<int, SectionText> sections)
    {
        var first = chunks[0];
        var attributes = new StringBuilder($"n=\"{number}\" title=\"{Attribute(first.Citation.Title, 200)}\"");
        if (!string.IsNullOrWhiteSpace(first.HeadingPath))
        {
            attributes.Append(CultureInfo.InvariantCulture, $" path=\"{Attribute(first.HeadingPath, 300)}\"");
        }

        var pages = chunks.Where(c => c.Page is not null).Select(c => c.Page!.Value).ToList();
        if (pages.Count > 0)
        {
            var from = pages.Min();
            var to = chunks.Where(c => c.Page is not null).Max(c => c.PageEnd ?? c.Page!.Value);
            attributes.Append(CultureInfo.InvariantCulture, $" pages=\"{(from == to ? $"{from}" : $"{from}–{to}")}\"");
        }

        if (first.Citation.Modified is { } modified)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" modified=\"{modified:yyyy-MM-dd}\"");
        }

        var texts = chunks.Select(c => c.SectionRef is { } id && sections.TryGetValue(id, out var section) ? (Key: "s" + id, section.Text) : (Key: "c" + c.ChunkId, c.Text))
            .DistinctBy(t => t.Key).Select(t => Neutralise(t.Text));
        return $"<source {attributes}>\n{string.Join("\n…\n", texts)}\n</source>\n";
    }

    private static string Attribute(string? value, int max)
    {
        var text = (value ?? "").Length > max ? value![..max] : value ?? "";
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>§7.2: <c>&lt;source</c>/<c>&lt;/sources</c> tags get a zero-width space after <c>&lt;</c>; controls removed; blank runs collapsed.</summary>
    public static string Neutralise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = new string([.. text.Where(c => !char.IsControl(c) || c is '\n' or '\t')]);
        cleaned = SourceTag().Replace(cleaned, m => "<​" + m.Value[1..]);
        return BlankRuns().Replace(cleaned, "\n\n\n");
    }

    private static string Cut(string block, int chars)
    {
        if (block.Length <= chars)
        {
            return block;
        }

        var cut = block[..Math.Max(0, chars)];
        var sentence = cut.LastIndexOfAny(['.', '!', '?', '\n']);
        return (sentence > cut.Length / 2 ? cut[..(sentence + 1)] : cut) + "\n</source>\n";
    }

    /// <summary>Earlier turns' markers (their numbers would collide with this turn's sources).</summary>
    public static string StripMarkers(string text) => Markers().Replace(text ?? "", "").Replace("  ", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"<\s*/?\s*sources?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceTag();

    [GeneratedRegex(@"\n{4,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"\s?\[(?:(?:Källa|Source)\s+)?\d+(?:\s*,\s*\d+)*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Markers();
}
