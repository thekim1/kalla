using System.Runtime.CompilerServices;

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
/// the contact links and the end. <see cref="Finish"/> records the cited ranks and logs the answer (§9.5).
/// </summary>
public sealed class ChatAnswer : IDisposable
{
    private readonly ChatService _service;
    private readonly GatewayChatClient _gateway;
    private readonly HttpResponseMessage? _stream;
    private readonly GatewayAnswer? _complete;
    private readonly AnswerTransducer? _transducer;

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
                        (promptTokens, completionTokens) = (u.PromptTokens, u.CompletionTokens);
                        break;
                }
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

    public void Dispose() => _stream?.Dispose();
}
