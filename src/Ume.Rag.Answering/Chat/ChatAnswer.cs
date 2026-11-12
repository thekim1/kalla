using System.Runtime.CompilerServices;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

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
public sealed class ChatAnswer : IDisposable
{
    private readonly ChatService _service;
    private readonly GatewayChatClient _gateway;
    private readonly HttpResponseMessage? _stream;
    private readonly GatewayAnswer? _complete;
    private readonly AnswerTransducer? _transducer;
    private bool _streamRecorded;

    internal ChatAnswer(ChatPlan plan, ChatService service, GatewayChatClient gateway, HttpResponseMessage? stream, GatewayAnswer? complete)
    {
        Plan = plan;
        _service = service;
        _gateway = gateway;
        _stream = stream;
        _complete = complete;
        _transducer = plan.Fixed is null ? new AnswerTransducer(plan.Prompt!.Sources, plan.Platform.AllowedLinkHosts) : null;
    }

    public ChatPlan Plan { get; }

    /// <summary>The documents the answer cited, in citation order (complete once <see cref="ReadAsync"/> has ended).</summary>
    public IReadOnlyList<PromptSource> Cited => _transducer?.Cited ?? [];

    public async IAsyncEnumerable<AnswerEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Plan.Fixed is { } text)
        {
            yield return new AnswerEvent.Own(text);
            yield return new AnswerEvent.End("stop", 0, 0);
            yield break;
        }

        var transducer = _transducer!;
        var caveat = Plan.Caveat ?? "";
        if (caveat.Length > 0)
        {
            yield return new AnswerEvent.Own(caveat);
        }

        var finish = "stop";
        var (promptTokens, completionTokens) = (0, 0);
        if (_complete is { } answer)
        {
            yield return new AnswerEvent.Pieces(transducer.Feed(answer.Text), caveat.Length);
            (finish, promptTokens, completionTokens) = (answer.FinishReason, answer.PromptTokens, answer.CompletionTokens);
        }
        else
        {
            GatewayTokens? tokens = null;
            try
            {
                await foreach (var item in _gateway.ReadAsync(_stream!, ct))
                {
                    switch (item)
                    {
                        case GatewayStreamEvent.Delta delta:
                            yield return new AnswerEvent.Pieces(transducer.Feed(delta.Text), caveat.Length);
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

        yield return new AnswerEvent.Pieces(transducer.Flush(), caveat.Length);
        if (Plan.Trailer.Length > 0)
        {
            // §10.4: the contact links after a general-knowledge answer, from us, not through the model.
            yield return new AnswerEvent.Own(Plan.Trailer);
        }

        yield return new AnswerEvent.End(finish, promptTokens, completionTokens);
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

    /// <summary>
    /// The streamed answer's usage record, once (§8.3): at the stream's end the usage chunk's <paramref name="tokens"/> (the
    /// prompt estimate when it sent none) and the comment's <paramref name="costUsd"/>; a stream that breaks off (a disconnect,
    /// the idle or total timeout, an error event, a reader that stops) has neither and is recorded with the prompt estimate of
    /// §6.3 as input, no output, at an estimated cost. Nothing for a JSON answer or a fixed text.
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
