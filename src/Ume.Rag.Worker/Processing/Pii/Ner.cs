using System.Text;
using Ume.Rag.Worker.Parsing;

namespace Ume.Rag.Worker.Processing.Pii;

/// <summary>A text unit sent to <c>/v1/ner</c> (NFC; offsets in the answer are code points of this text).</summary>
public sealed record NerText(string Id, string Text);

/// <summary>An entity of the answer (parser design §7.8): code-point offsets, label <c>person</c> or <c>person_ambiguous</c>.</summary>
public sealed record NerEntity(string TextId, int Start, int End, string Label, double Score);

/// <summary>The entities of one request and the model as <c>name@revision</c>.</summary>
public sealed record NerAnswer(IReadOnlyList<NerEntity> Entities, string Model);

/// <summary>
/// The parser's <c>POST /v1/ner</c> (processing design §10; parser design §7.8). <c>busy</c> and <c>not_ready</c> are thrown
/// as <see cref="ParserException"/> with 503. The production implementation is a thin adapter over <c>ParserClient</c>'s NER
/// call, which P1a item 5 adds to the parser client; until it is merged the worker has no NER and every gated set waits
/// as <c>pending</c> (fail closed, §9.6).
/// </summary>
public interface INerClient
{
    /// <summary><c>/v1/info</c> <c>capacity.ner_ready</c>.</summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    Task<NerAnswer> RecognizeAsync(IReadOnlyList<NerText> texts, double minScore, CancellationToken cancellationToken);
}

/// <summary>A first-name gazetteer for the NER prefilter (§9.3; SCB name statistics, licence to verify, to-verify #111). Empty until chosen.</summary>
public interface IFirstNameGazetteer
{
    bool Contains(string word);
}

/// <summary>No gazetteer: the prefilter sends units with a capitalised word that is not their first word, and ALL-CAPS units.</summary>
public sealed class EmptyGazetteer : IFirstNameGazetteer
{
    public bool Contains(string word) => false;
}

/// <summary>Code points (the NER offsets) to UTF-16 indexes, by <see cref="Rune"/> enumeration (surrogate pairs).</summary>
public static class CodePoints
{
    /// <summary>The UTF-16 range of the code points <c>[start, end)</c> of <paramref name="text"/>, or null when out of range.</summary>
    public static (int Start, int Length)? ToUtf16(string text, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (start < 0 || end < start)
        {
            return null;
        }

        int index = 0, point = 0, from = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            if (point == start)
            {
                from = index;
            }

            if (point == end)
            {
                return from < 0 ? null : (from, index - from);
            }

            index += rune.Utf16SequenceLength;
            point++;
        }

        if (point == start)
        {
            from = index;
        }

        return point == end && from >= 0 ? (from, index - from) : null;
    }
}
