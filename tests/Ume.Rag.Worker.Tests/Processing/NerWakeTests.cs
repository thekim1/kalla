using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// A cold <c>ner-0</c> (parser <c>NER_IDLE_STOP_S</c>, stream A-6): the parser starts it on the first <c>/v1/ner</c>, so
/// the stage sends the request even when NER is not ready, and its <c>not_ready</c> answer is the readiness signal for the
/// 5-minute wait (processing design §9.6).
/// </summary>
public sealed class NerWakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ListLike =
    [
        "Anna Lindqvist, 070-311 22 33", "Erik Johansson, 070-322 33 44", "Maria Nilsson, 070-333 44 55",
        "Johan Berglund, 070-344 55 66", "Sara Ek, 070-355 66 77",
    ];

    private static readonly PersonalDataContext Public = new(Classification.Public);

    private static ChunkSetDraft Chunks() => new()
    {
        Chunks = [new ChunkDraft
        {
            Ordinal = 0, Prefix = "Protokoll", HeadingPath = "Protokoll", Text = string.Join('\n', ListLike), SearchText = "x",
            OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex("x"), TokenCount = 10, DocumentType = "pdf",
        }],
    };

    private static (PersonalDataStage Stage, FakeNer Ner, FakeTimeProvider Clock) Stage()
    {
        var clock = new FakeTimeProvider(ProcessingHarness.Now);
        var ner = new FakeNer { Ready = false };
        ner.Names.Add(("Anna Lindqvist", "person", 0.95));
        return (new PersonalDataStage(new PiiPatterns(clock), ner, new EmptyGazetteer(), clock), ner, clock);
    }

    [Fact]
    public async Task A_cold_ner_is_woken_by_the_request_and_the_job_requeues_until_it_is_warm()
    {
        var (stage, ner, _) = Stage();

        var error = await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Chunks(), Public, Ct));
        error.Code.ShouldBe("parser.ner_not_ready");
        error.CountsAttempt.ShouldBeFalse();
        ner.Wakes.ShouldBe(1);

        ner.Ready = true; // ner-0 warmed after the wake
        var (_, assessment) = await stage.RunAsync(Chunks(), Public, Ct);
        assessment.Counts.Ner.ShouldBe(NerStatus.Done);
        assessment.Counts.Name.ShouldBe(1);
    }

    [Fact]
    public async Task After_a_long_quiet_spell_the_first_not_ready_starts_a_new_wait_instead_of_settling_for_pending()
    {
        var (stage, ner, clock) = Stage();
        await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Chunks(), Public, Ct));
        clock.Advance(TimeSpan.FromMinutes(6));
        (await stage.RunAsync(Chunks(), Public, Ct)).Assessment.Counts.Ner.ShouldBe(NerStatus.Pending, "not ready for more than 5 minutes");

        // Hours later NER has gone cold again (idle stop): the request wakes it and the job waits, it is not pending.
        clock.Advance(TimeSpan.FromHours(3));
        await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Chunks(), Public, Ct));
        ner.Wakes.ShouldBe(3);
        ner.Ready = true;
        (await stage.RunAsync(Chunks(), Public, Ct)).Assessment.Counts.Ner.ShouldBe(NerStatus.Done);
    }
}
