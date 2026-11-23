using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Kalla.Answering.Chat;

/// <summary>
/// The step times of one chat turn (chat design §9.10), on <see cref="Stopwatch"/> timestamps from <c>ReceivedAt</c>: the condense
/// call (only when one was made), the retrieval, the answer call from its opening to the model's first event (none for a fixed
/// text), and the end of the answer's text. The steps run one after another inside the turn, so the parts never add up to more
/// than the total; <see cref="Member"/> gives them in whole milliseconds, each rounded down, which keeps that true. Measured on
/// every turn (a few timestamps, no call, no statement); only a request with <c>kalla.timings</c> sees them.
/// </summary>
public sealed class TurnTimings(long receivedAt)
{
    private (long Start, long End)? _condense;
    private (long Start, long End)? _retrieve;
    private long? _answerOpened;
    private long? _firstEvent;
    private long? _textEnded;

    /// <summary>A turn received now (a caller that has no earlier <c>ReceivedAt</c>).</summary>
    public static TurnTimings Now() => new(Stopwatch.GetTimestamp());

    /// <summary>The condense call ran from <paramref name="start"/> until now.</summary>
    public void Condensed(long start) => _condense = (start, Stopwatch.GetTimestamp());

    /// <summary>The retrieval ran from <paramref name="start"/> until now.</summary>
    public void Retrieved(long start) => _retrieve = (start, Stopwatch.GetTimestamp());

    /// <summary>The answer call is being opened now.</summary>
    public void AnswerOpening() => _answerOpened = Stopwatch.GetTimestamp();

    /// <summary>The answer call's first event has arrived (only the first counts).</summary>
    public void FirstEvent()
    {
        if (_answerOpened is not null)
        {
            _firstEvent ??= Stopwatch.GetTimestamp();
        }
    }

    /// <summary>The answer's text has ended (only the first counts).</summary>
    public void TextEnded() => _textEnded ??= Stopwatch.GetTimestamp();

    /// <summary>The answer's <c>timings</c> member (§9.10): whole milliseconds, each step left out when it did not run.</summary>
    public KallaTimings Member() => new(
        _condense is { } condense ? Milliseconds(condense.Start, condense.End) : null,
        _retrieve is { } retrieve ? Milliseconds(retrieve.Start, retrieve.End) : null,
        _answerOpened is { } opened && _firstEvent is { } first ? Milliseconds(opened, first) : null,
        Milliseconds(receivedAt, _textEnded ?? Stopwatch.GetTimestamp()));

    /// <summary>Whole milliseconds between two timestamps, rounded down (integer arithmetic, so the parts' sum stays within the total).</summary>
    private static long Milliseconds(long start, long end) => Math.Max(0, end - start) * 1000 / Stopwatch.Frequency;
}

/// <summary>
/// The answer's <c>timings</c> (chat design §3.8, §9.10): the turn's step times in whole milliseconds, each left out when its step
/// did not run; <c>first_token</c> is the answer call's wait for the model's first event, <c>total</c> from <c>ReceivedAt</c> to the
/// end of the answer's text.
/// </summary>
public sealed record KallaTimings(
    [property: JsonPropertyName("condense"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Condense,
    [property: JsonPropertyName("retrieve"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Retrieve,
    [property: JsonPropertyName("first_token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? FirstToken,
    [property: JsonPropertyName("total")] long Total)
{
    /// <summary>The same times as a <c>Server-Timing</c> header value (§9.10): <c>condense;dur=840, retrieve;dur=120, first_token;dur=610, total;dur=3900</c>.</summary>
    public string ServerTiming()
    {
        var parts = new List<string>(4);
        void Add(string name, long? value)
        {
            if (value is { } ms)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name};dur={ms}"));
            }
        }

        Add("condense", Condense);
        Add("retrieve", Retrieve);
        Add("first_token", FirstToken);
        Add("total", Total);
        return string.Join(", ", parts);
    }
}
