using Kalla.Worker.Parsing;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// The preview-first gate in front of the parser (engine design §9.5): a freed slot goes to a waiting preview before any
/// waiting <c>parse</c> range, waiters of one priority are served in order, and a cancelled waiter never takes a slot.
/// </summary>
public sealed class ParserSlotGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_waiting_preview_takes_the_next_free_slot_before_a_waiting_parse_range()
    {
        var gate = new ParserSlotGate(1);
        var running = await gate.EnterAsync(ParserSlotPriority.Parse, Ct);
        var nextRange = gate.EnterAsync(ParserSlotPriority.Parse, Ct).AsTask();
        var preview = gate.EnterAsync(ParserSlotPriority.Preview, Ct).AsTask();
        gate.Waiting.ShouldBe(2);

        running.Dispose();
        var previewSlot = await preview.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        nextRange.IsCompleted.ShouldBeFalse("the parse job's next range waits for the preview");

        previewSlot.Dispose();
        (await nextRange.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        gate.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task Waiters_of_one_priority_are_served_in_order_and_a_cancelled_one_is_skipped()
    {
        var gate = new ParserSlotGate(1);
        var running = await gate.EnterAsync(ParserSlotPriority.Parse, Ct);
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(ParserSlotPriority.Preview, cancel.Token).AsTask();
        var first = gate.EnterAsync(ParserSlotPriority.Parse, Ct).AsTask();
        var second = gate.EnterAsync(ParserSlotPriority.Parse, Ct).AsTask();

        await cancel.CancelAsync();
        await Should.ThrowAsync<TaskCanceledException>(() => cancelled);
        running.Dispose();
        var slot = await first.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        second.IsCompleted.ShouldBeFalse();
        slot.Dispose();
        slot.Dispose(); // a second dispose frees nothing more
        (await second.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();

        // All slots free again: two callers enter at once on a two-slot gate.
        var wide = new ParserSlotGate(2);
        using var a = await wide.EnterAsync(ParserSlotPriority.Parse, Ct);
        using var b = await wide.EnterAsync(ParserSlotPriority.Parse, Ct);
        wide.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task The_gated_converter_waits_at_the_flows_priority()
    {
        var gate = new ParserSlotGate(1);
        var inner = new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To));
        var converter = new GatedParserConverter(inner, gate);
        var held = await gate.EnterAsync(ParserSlotPriority.Parse, Ct);

        var parse = converter.ConvertAsync(new byte[1], Options("parse"), Ct);
        Task<ParserConvertResult> preview;
        using (ParserSlots.AsPreview())
        {
            ParserSlots.Priority.ShouldBe(ParserSlotPriority.Preview);
            preview = converter.ConvertAsync(new byte[1], Options("preview"), Ct);
        }

        ParserSlots.Priority.ShouldBe(ParserSlotPriority.Parse);
        gate.Waiting.ShouldBe(2);
        held.Dispose();
        await preview.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await parse.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        inner.Requests.Select(r => r.RequestId).ShouldBe(["preview", "parse"], "the preview queued second and went first");
    }

    private static ParserConvertOptions Options(string requestId) => new(requestId, ".pdf") { Pages = new ParserPageRange(1, 1) };
}
