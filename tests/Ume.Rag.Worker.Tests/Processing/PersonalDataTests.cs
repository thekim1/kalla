using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// NER gating and the client side of <c>/v1/ner</c> (processing design §9.3, §9.6, §10, §17 "NER gating and client"):
/// names are checked only for a <c>public</c> collection, no other rule fired and ≥ 5 contact values; offsets are code
/// points; only <c>person</c> at or above the minimum score counts; <c>busy</c> requeues without an attempt; NER down for
/// more than 5 minutes completes as <c>pending</c>.
/// </summary>
public sealed class PersonalDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ListLike =
    [
        "Anna Lindqvist, 070-311 22 33", "Erik Johansson, 070-322 33 44", "Maria Nilsson, 070-333 44 55",
        "Johan Berglund, 070-344 55 66", "Sara Ek, 070-355 66 77",
    ];

    private static ChunkSetDraft Chunks(params string[] bodies) => new()
    {
        Chunks = [.. bodies.Select((b, i) => new ChunkDraft
        {
            Ordinal = i, Prefix = "Protokoll", HeadingPath = "Protokoll", Text = b, SearchText = b, OverlapChars = 0,
            TextHash = ProcessingJson.Sha256Hex(b), TokenCount = 10, DocumentType = "pdf",
        })],
    };

    private static (PersonalDataStage Stage, FakeNer Ner, FakeTimeProvider Clock) Stage()
    {
        var clock = new FakeTimeProvider(ProcessingHarness.Now);
        var ner = new FakeNer();
        foreach (var line in ListLike)
        {
            ner.Names.Add((line[..line.IndexOf(',', StringComparison.Ordinal)], "person", 0.95));
        }

        return (new PersonalDataStage(new PiiPatterns(clock), ner, new EmptyGazetteer(), clock), ner, clock);
    }

    [Fact]
    public async Task Names_are_checked_for_a_list_like_set_of_a_public_collection_and_flag_it()
    {
        var (stage, ner, _) = Stage();
        var (chunks, assessment) = await stage.RunAsync(Chunks(string.Join('\n', ListLike)), new PersonalDataContext(Classification.Public), Ct);

        ner.Calls.Count.ShouldBe(1);
        assessment.Counts.Ner.ShouldBe(NerStatus.Done);
        assessment.Counts.Name.ShouldBe(5);
        assessment.Counts.Phone.ShouldBe(5);
        assessment.TypesFired.ShouldBe([PiiTypes.Name]);
        assessment.Flagged.ShouldBeTrue();
        chunks.Pii.ShouldBe(assessment.Counts);
    }

    [Theory]
    [InlineData(Classification.Internal)]
    [InlineData(Classification.Sensitive)]
    public async Task Names_are_not_checked_outside_a_public_collection(string level)
    {
        var (stage, ner, _) = Stage();
        var (_, assessment) = await stage.RunAsync(Chunks(string.Join('\n', ListLike)), new PersonalDataContext(level), Ct);

        ner.Calls.ShouldBeEmpty();
        assessment.Counts.Ner.ShouldBe(NerStatus.Skipped);
        assessment.Counts.Name.ShouldBe(0);
    }

    [Fact]
    public async Task Names_are_not_checked_when_another_rule_already_flags_the_set()
    {
        var (stage, ner, _) = Stage();
        var (_, assessment) = await stage.RunAsync(Chunks(string.Join('\n', ListLike), "Sökande 19121212-1212."), new PersonalDataContext(Classification.Public), Ct);

        ner.Calls.ShouldBeEmpty();
        assessment.TypesFired.ShouldBe([PiiTypes.Personnummer]);
    }

    [Fact]
    public async Task Names_are_not_checked_below_five_contact_values()
    {
        var (stage, ner, _) = Stage();
        var (_, assessment) = await stage.RunAsync(Chunks(string.Join('\n', ListLike.Take(4))), new PersonalDataContext(Classification.Public), Ct);

        ner.Calls.ShouldBeEmpty();
        assessment.Counts.Ner.ShouldBe(NerStatus.Skipped);
        assessment.Flagged.ShouldBeFalse();
    }

    [Fact]
    public async Task Overlap_is_counted_once()
    {
        var (stage, _, _) = Stage();
        var first = "Ring 070-311 22 33.";
        var second = first + " Ring 070-322 33 44.";
        var draft = Chunks(first, second);
        draft = draft with { Chunks = [draft.Chunks[0], draft.Chunks[1] with { OverlapChars = first.Length }] };

        var (_, assessment) = await stage.RunAsync(draft, new PersonalDataContext(Classification.Internal), Ct);

        assessment.Counts.Phone.ShouldBe(2);
    }

    [Fact]
    public async Task External_email_flags_and_own_domain_email_does_not()
    {
        var (stage, _, _) = Stage();
        var context = new PersonalDataContext(Classification.Internal) { OwnDomains = ["umea.se"] };

        (await stage.RunAsync(Chunks("Skriv till info@umea.se eller skola.info@skola.umea.se."), context, Ct)).Assessment.Flagged.ShouldBeFalse();
        var (_, external) = await stage.RunAsync(Chunks("Skriv till anna@gmail.com."), context, Ct);
        external.TypesFired.ShouldBe([PiiTypes.EmailExternal]);
        external.Counts.EmailExternal.ShouldBe(1);
    }

    [Fact]
    public async Task Offsets_are_code_points_with_emoji_and_combining_characters()
    {
        var (stage, ner, _) = Stage();
        ner.Names.Clear();
        ner.Names.AddRange([("Åsa Ström", "person", 0.9), ("Anna-Karin Berg", "person", 0.9), ("Annas", "person", 0.9), ("Anna", "person", 0.9),
            ("Lars Berg", "person", 0.9), ("Ola Ek", "person", 0.9), ("Umeå", "person_ambiguous", 0.99), ("Per Holm", "person", 0.5)]);
        var text = "😀👍🏽 Möte med Åsa Ström och Anna-Karin Berg. 😀 Annas bil, Anna ringer, Lars Berg och Ola Ek i Umeå med Per Holm.\n"
            + string.Join('\n', ListLike.Select(l => "Tel " + l[(l.IndexOf(',', StringComparison.Ordinal) + 2)..]));

        var (_, assessment) = await stage.RunAsync(Chunks(text), new PersonalDataContext(Classification.Public), Ct);

        // Åsa Ström, Anna-Karin Berg, Anna (Annas folded), Lars Berg, Ola Ek; not Umeå (ambiguous) nor Per Holm (score 0.5).
        assessment.Counts.Name.ShouldBe(5);
        assessment.TypesFired.ShouldBe([PiiTypes.Name]);
    }

    [Fact]
    public void Code_points_map_to_utf16_ranges()
    {
        const string text = "😀 Åsa"; // U+1F600 is two UTF-16 units
        CodePoints.ToUtf16(text, 2, 5).ShouldBe((3, 3));
        CodePoints.ToUtf16(text, 0, 1).ShouldBe((0, 2));
        CodePoints.ToUtf16(text, 4, 9).ShouldBeNull();
    }

    [Fact]
    public async Task Busy_requeues_without_an_attempt()
    {
        var (stage, ner, _) = Stage();
        ner.Fail = "busy";

        var error = await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(Chunks(string.Join('\n', ListLike)), new PersonalDataContext(Classification.Public), Ct));

        error.CountsAttempt.ShouldBeFalse();
        error.Code.ShouldBe("parser.ner_busy");
    }

    [Fact]
    public async Task Ner_down_requeues_for_five_minutes_then_completes_as_pending()
    {
        var (stage, ner, clock) = Stage();
        ner.Ready = false;
        var chunks = Chunks(string.Join('\n', ListLike));
        var context = new PersonalDataContext(Classification.Public);

        (await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(chunks, context, Ct))).CountsAttempt.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(4));
        await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(chunks, context, Ct));
        clock.Advance(TimeSpan.FromMinutes(2));

        var (_, assessment) = await stage.RunAsync(chunks, context, Ct);
        assessment.Counts.Ner.ShouldBe(NerStatus.Pending);
        assessment.Flagged.ShouldBeFalse(); // no rule fired; pending blocks a public set by itself (lifecycle)
        ner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_ner_client_a_gated_set_is_pending()
    {
        var clock = new FakeTimeProvider(ProcessingHarness.Now);
        var stage = new PersonalDataStage(new PiiPatterns(clock), ner: null, new EmptyGazetteer(), clock);

        var (_, assessment) = await stage.RunAsync(Chunks(string.Join('\n', ListLike)), new PersonalDataContext(Classification.Public), Ct);

        assessment.Counts.Ner.ShouldBe(NerStatus.Pending);
    }

    [Fact]
    public void The_prefilter_sends_units_with_inner_capitals_or_all_caps_only()
    {
        var (stage, _, _) = Stage();
        stage.Prefilter("Möte med Anna i dag.").ShouldBeTrue();
        stage.Prefilter("ANNA LINDQVIST ORDFÖRANDE").ShouldBeTrue();
        stage.Prefilter("Inga övriga frågor anmäldes.").ShouldBeFalse();
    }
}
