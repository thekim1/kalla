using Kalla.Worker.Parsing;

namespace Kalla.Worker.Processing.Pii;

/// <summary>A text unit sent to <c>/v1/ner</c> (NFC; offsets in the answer are code points of this text).</summary>
public sealed record NerText(string Id, string Text);

/// <summary>An entity of the answer (parser design §7.8): code-point offsets, label <c>person</c> or <c>person_ambiguous</c>.</summary>
public sealed record NerEntity(string TextId, int Start, int End, string Label, double Score);

/// <summary>The entities of one request and the model as <c>name@revision</c>.</summary>
public sealed record NerAnswer(IReadOnlyList<NerEntity> Entities, string Model);

/// <summary>
/// The parser's <c>POST /v1/ner</c> (processing design §10; parser design §7.8). <c>busy</c> and <c>not_ready</c> are thrown
/// as <see cref="ParserException"/> with 503. <see cref="Parse.ParserNerClient"/> is the implementation over
/// <see cref="ParserClient"/>; without a client registered, every gated set waits as <c>pending</c> (fail closed, §9.6).
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
