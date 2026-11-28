using System.Runtime.CompilerServices;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Answering.Chat;

/// <summary>What one answer is made of, in order (chat design §9.1–§9.4, §10.2, §10.4); the SSE writer and the JSON body both read it.</summary>
public abstract record AnswerEvent
{
    private AnswerEvent()
    {
    }

    /// <summary>Text from us, not the model: a fixed text, the caveat line or the contact links.</summary>
    public sealed record Own(string Text) : AnswerEvent;

    /// <summary>The model's text through the <see cref="AnswerTransducer"/>; its annotation offsets move by <paramref name="Shift"/> (the caveat).</summary>
    public sealed record Pieces(IReadOnlyList<AnswerPiece> Items, int Shift) : AnswerEvent;

    /// <summary>The end of the answer: finish reason and token usage (0 and 0 without a model call).</summary>
    public sealed record End(string FinishReason, int PromptTokens, int CompletionTokens) : AnswerEvent;
}

/// <summary>
/// One answer of a <see cref="ChatPlan"/>, opened by <see cref="ChatService.OpenAnswerAsync"/>: the gateway call has answered
/// (status and residency checked, chat design §8.2) before this exists, so a refusal is still a JSON error. Then
/// <see cref="ReadAsync"/> gives the events: the fixed text alone, or the caveat, the model's text through the transducer,
/// the contact links and the end. <see cref="Finish"/> records the cited ranks and logs the answer (§9.5). A streamed answer
/// adds its usage record to the plan's <see cref="ChatPlan.Usage"/> when the stream ends or breaks off (§8.3); a JSON answer's
/// was added when the call answered.
/// </summary>
/// <remarks>
/// An answer with no text (§6.3, #1106): the caveat and the model's first pieces are held until the model writes visible text.
/// A call that ends with <c>finish_reason: "length"</c> and none is made once more (<see cref="ChatService.RetryAnswerAsync"/>),
/// unseen by the client; still empty, or the retry refused, the answer is the unavailable text (§10.2) and <see cref="Plan"/>
/// becomes <see cref="ChatOutcome.Unavailable"/>. An answer that wrote text and then stopped at the limit stays answered.
/// </remarks>
public sealed class ChatAnswer : IDisposable
{
    private readonly ChatService _service;
    private readonly GatewayChatClient _gateway;
    private readonly bool _streamed;
    private HttpResponseMessage? _stream;
    private GatewayAnswer? _complete;
    private AnswerTransducer? _transducer;
    private bool _streamRecorded;
    private bool _visible;
    private readonly List<AnswerEvent> _held = [];
    private readonly int _shift; // the caveat's length: the model text's annotation offsets move by it

    internal ChatAnswer(ChatPlan plan, ChatService service, GatewayChatClient gateway, HttpResponseMessage? stream, GatewayAnswer? complete)
    {
        Plan = plan;
        _service = service;
        _gateway = gateway;
        _stream = stream;
        _streamed = stream is not null;
        _complete = complete;
        _transducer = plan.Fixed is null ? NewTransducer(plan) : null;
        _shift = plan.Caveat?.Length ?? 0;
    }

    /// <summary>The turn's plan; its outcome is <see cref="ChatOutcome.Unavailable"/> once an answer stayed empty after its retry.</summary>
    public ChatPlan Plan { get; private set; }

    /// <summary>The documents the answer cited, in citation order (complete once <see cref="ReadAsync"/> has ended).</summary>
    public IReadOnlyList<PromptSource> Cited => _transducer?.Cited ?? [];

    public async IAsyncEnumerable<AnswerEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Plan.Fixed is { } text)
        {
            yield return new AnswerEvent.Own(text);
            Plan.Timings.TextEnded();
            yield return new AnswerEvent.End("stop", 0, 0);
            yield break;
        }

        var finish = "stop";
        var (promptTokens, completionTokens) = (0, 0);
        int? firstOutput = null;
        for (var attempt = 0; ; attempt++)
        {
            var transducer = _transducer!;
            (finish, promptTokens, completionTokens) = ("stop", 0, 0);
            if (_complete is { } answer)
            {
                foreach (var item in Show(transducer.Feed(answer.Text)))
                {
                    yield return item;
                }

                (finish, promptTokens, completionTokens) = (answer.FinishReason, answer.PromptTokens, answer.CompletionTokens);
            }
            else
            {
                GatewayTokens? tokens = null;
                try
                {
                    await foreach (var item in _gateway.ReadAsync(_stream!, ct))
                    {
                        Plan.Timings.FirstEvent(); // §9.10 first_token: from opening the call to the model's first event
                        switch (item)
                        {
                            case GatewayStreamEvent.Delta delta when _visible:
                                // The hot path once text is shown: no iterator per token (#333).
                                yield return new AnswerEvent.Pieces(transducer.Feed(delta.Text), _shift);
                                break;
                            case GatewayStreamEvent.Delta delta:
                                foreach (var shown in Show(transducer.Feed(delta.Text)))
                                {
                                    yield return shown;
                                }

                                break;
                            case GatewayStreamEvent.Finish f:
                                finish = f.Reason;
                                break;
                            case GatewayStreamEvent.Usage u:
                                tokens = u.Tokens;
                                (promptTokens, completionTokens) = ((int)u.Tokens.InputTokens, (int)u.Tokens.OutputTokens);
                                break;
                            case GatewayStreamEvent.End e:
                                RecordStream(tokens, e.CostUsd);
                                break;
                        }
                    }
                }
                finally
                {
                    RecordStream(null, null); // nothing when the end was recorded; else the stream broke off
                }
            }

            foreach (var item in Show(transducer.Flush()))
            {
                yield return item;
            }

            if (_visible || finish != "length" || attempt > 0)
            {
                break;
            }

            // §6.3 (#1106): no visible text at the limit, and nothing but the role chunk and the progress reached the client.
            firstOutput = completionTokens;
            _held.Clear();
            try
            {
                var (stream, complete) = await _service.RetryAnswerAsync(Plan, _streamed, ct);
                _stream?.Dispose();
                (_stream, _complete, _streamRecorded) = (stream, complete, false);
                _transducer = NewTransducer(Plan);
            }
            catch (ChatException)
            {
                completionTokens = -1; // refused: logged as -1 output tokens
                break;
            }
        }

        var spent = (firstOutput ?? 0) + Math.Max(0, completionTokens);
        if (!_visible && firstOutput is { } first)
        {
            // Empty twice, or the retry refused: the unavailable text (§10.2), outcome unavailable, the cause in the log.
            _service.LogAnswerEmpty(Plan, Plan.MaxTokens, first, Plan.FullMaxTokens, completionTokens);
            Plan = Plan with { Outcome = ChatOutcome.Unavailable };
            _transducer = NewTransducer(Plan);
            yield return new AnswerEvent.Own(FixedTexts.Unavailable(Plan.Assistant.Published?.NotFound, Plan.Platform.NotFound, Plan.Language));
            Plan.Timings.TextEnded();
            yield return new AnswerEvent.End("stop", promptTokens, spent);
            yield break;
        }

        // An answer with no visible text that did not stop at the limit: what was held, as before #1106.
        if (!_visible && Plan.Caveat is { Length: > 0 } caveat)
        {
            yield return new AnswerEvent.Own(caveat);
        }

        foreach (var item in _held)
        {
            yield return item;
        }

        _held.Clear();
        if (Plan.Trailer.Length > 0)
        {
            // §10.4: the contact links after a general-knowledge answer, from us, not through the model.
            yield return new AnswerEvent.Own(Plan.Trailer);
        }

        Plan.Timings.TextEnded();
        _service.RecordPromptEstimate(Plan, promptTokens);
        yield return new AnswerEvent.End(finish, promptTokens, spent);
    }

    /// <summary>
    /// The events for the model's <paramref name="items"/>: held while the answer has no visible text; at its first visible text
    /// the caveat (§10.2, §10.4) and what was held go first.
    /// </summary>
    private IEnumerable<AnswerEvent> Show(IReadOnlyList<AnswerPiece> items)
    {
        if (!_visible && !HasText(items))
        {
            _held.Add(new AnswerEvent.Pieces(items, _shift));
            yield break;
        }

        if (!_visible)
        {
            _visible = true;
            if (Plan.Caveat is { Length: > 0 } caveat)
            {
                yield return new AnswerEvent.Own(caveat);
            }

            foreach (var earlier in _held)
            {
                yield return earlier;
            }

            _held.Clear();
        }

        yield return new AnswerEvent.Pieces(items, _shift);
    }

    /// <summary>After the answer (§9.5): the counters with only the cited ranks, and a log line; nothing for a fixed text.</summary>
    public void Finish()
    {
        if (_transducer is not null)
        {
            _service.Finish(Plan, _transducer);
        }
    }

    /// <summary>
    /// Disposes the stream (which cancels the gateway's); a stream that was opened and never read to its end is recorded as
    /// broken off first (<see cref="RecordStream"/>).
    /// </summary>
    public void Dispose()
    {
        RecordStream(null, null);
        _stream?.Dispose();
    }

    private static AnswerTransducer NewTransducer(ChatPlan plan) => new(plan.Prompt!.Sources, plan.Platform.AllowedLinkHosts, plan.Language);

    /// <summary>Whether the pieces hold visible text: anything but white space (a citation alone is not an answer).</summary>
    private static bool HasText(IReadOnlyList<AnswerPiece> items) =>
        items.OfType<AnswerPiece.Text>().Any(t => !string.IsNullOrWhiteSpace(t.Content));

    /// <summary>
    /// The streamed answer's usage record, once per call (§8.3): at the stream's end the usage chunk's <paramref name="tokens"/>
    /// (the prompt estimate when it sent none) and the comment's <paramref name="costUsd"/>; a stream that breaks off (a
    /// disconnect, the idle or total timeout, an error event, a reader that stops) has neither and is recorded with the prompt
    /// estimate of §6.3 as input, no output, at an estimated cost. Nothing for a JSON answer or a fixed text.
    /// </summary>
    private void RecordStream(GatewayTokens? tokens, decimal? costUsd)
    {
        if (_stream is null || _streamRecorded)
        {
            return;
        }

        _streamRecorded = true;
        Plan.Usage.Add(new GatewayCallUsage(GatewayChatClient.AnswerStep, Plan.Assistant.Published!.GatewayModel,
            tokens ?? new GatewayTokens(Plan.Prompt!.EstimatedTokens, 0, 0), costUsd));
    }
}
