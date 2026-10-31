extern alias fakegateway;

using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Evaluation.Generation;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Pii;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Engine;
using FakeContent = fakegateway::Ume.Rag.FakeGateway.FakeContent;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// <c>generate-set</c> and <c>top-up-set</c> (P1b item 7; processing design §13.1–§13.3) from job rows on the test database
/// and the FakeGateway: a fixture collection gets a generated layer of its target size whose questions pass the filters;
/// each filtered-out case is dropped; a top-up retires the questions of a changed and a deleted document and regenerates
/// to size with the next round's seed; a model the gateway does not serve <c>OnPrem</c> refuses the job (O1).
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class EvalSetGeneratorTests(RagDatabaseFixture database, EvalGatewayFactory gateway) : IClassFixture<EvalGatewayFactory>, IAsyncLifetime
{
    private const string Model = "ume/chat-onprem";
    private const string Category = "Ume.Rag.Worker.Evaluation.Generation.EvalSetGenerator";

    /// <summary>Six Swedish questions with little wording in common; {0} is the topic, {1} the test's tag.</summary>
    private static readonly string[] Good =
    [
        "Vilken nämnd beslutar om {0} och hur lång väntan blir det för {1}?",
        "Kan man överklaga ett beslut om {0} om man inte håller med {1}?",
        "Var vänder jag mig med frågor om {0} på kvällar och helger hos {1}?",
        "Hur mycket betalar en familj per månad för {0} hos {1} under året?",
        "Måste jag boka tid innan jag besöker {0} med barnen hos {1}?",
        "Vem har ansvar för {0} när personalen är sjuk hos {1}?",
        "Går det att söka {0} digitalt med mobilt bankid hos {1}?",
        "Vilka papper behöver jag lämna när jag söker {0} hos {1}?",
    ];

    private static readonly string[] Topics = ["förskola", "bygglov", "snöröjning", "bibliotek", "parkering", "avfall", "färdtjänst", "simhall"];

    private PlatformSettings? _previous;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        gateway.Reset();
        _previous = await EvalSeeds.SetModelsAsync(database, Model);
    }

    public async ValueTask DisposeAsync() => await EvalSeeds.RestoreModelsAsync(database, _previous is not null, _previous);

    private static string Reply(string question, string answer, string type = "fakta") =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["fraga"] = question, ["svar"] = answer, ["typ"] = type });

    private static string Ask(int template, int topic, string tag) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Good[template], Topics[topic], tag);

    private static string Text(int topic) => EvalSeeds.Body(Topics[topic], Guid.NewGuid().ToString());

    [Fact]
    public async Task A_fixture_collection_gets_a_generated_layer_of_its_target_size_that_passes_the_filters()
    {
        var tag = EvalSeeds.Tag();
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var texts = Enumerable.Range(0, 6).Select(Text).ToList();
            var doc1 = await EvalSeeds.DocumentAsync(database, seed, "Barn " + tag, [new(texts[0]), new(texts[1]), new($"Kort text {Guid.NewGuid()}.")]);
            var doc2 = await EvalSeeds.DocumentAsync(database, seed, "Bygga " + tag, [new(texts[2]), new(texts[3])]);
            var doc3 = await EvalSeeds.DocumentAsync(database, seed, "Fritid " + tag, [new(texts[4], "pdf"), new(texts[5], "pdf")]);
            // Personal-data flagged: not sampled, but a duplicated page of doc1's first chunk.
            var flagged = await EvalSeeds.DocumentAsync(database, seed, "Kopia " + tag, [new(texts[0]), new(Text(6))], piiFlagged: true);
            for (var i = 0; i < 6; i++)
            {
                gateway.Generate[texts[i]] = Reply(Ask(i, i, tag), $"Svar nummer {i} {tag}.");
            }

            await using var rig = EvalRig.Start(database, gateway);
            var job = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = seed.CollectionId }, seed);
            (await rig.RunAsync(job)).ShouldBe(new JobResult.Committed(), rig.Logs.Text);

            var result = await CommandSeeds.ResultAsync(database, job.Id);
            var setId = result.GetProperty("setId").GetGuid();
            result.GetProperty("round").GetInt32().ShouldBe(1);
            result.GetProperty("generated").GetInt32().ShouldBe(6);
            result.GetProperty("kept").GetInt32().ShouldBe(6, "N = min(100, 3 × 4 documents, 6 eligible chunks)");
            result.GetProperty("tokens").GetInt32().ShouldBeGreaterThan(0);

            var questions = await EvalSeeds.QuestionsAsync(database, setId);
            questions.Count.ShouldBe(6);
            var filters = new QuestionFilters(new PiiPatterns(TimeProvider.System));
            var chunkTexts = new[] { doc1, doc2, doc3 }.SelectMany(d => d.ChunkIds.Zip(d == doc1 ? texts[..2] : d == doc2 ? texts[2..4] : texts[4..]))
                .ToDictionary(p => p.First, p => p.Second);
            var seedHex = Convert.ToHexStringLower(ChunkSampler.Seed(setId, 1));
            foreach (var question in questions)
            {
                chunkTexts.ShouldContainKey(question.SourceChunkId, "only eligible chunks are sampled (not the short one, not the flagged set)");
                filters.Rules(question.Question).ShouldBeNull();
                QuestionFilters.Leakage(question.Question, question.ExpectedAnswer, chunkTexts[question.SourceChunkId]).ShouldBeNull();
                question.State.ShouldBe(EvaluationQuestionStates.Active);
                question.Classification.ShouldBe(seed.Classification);
                question.CreatedBy.ShouldBe("system:generate-set");
                question.Origin["model"]!.GetValue<string>().ShouldBe(Model);
                question.Origin["judgeModel"]!.GetValue<string>().ShouldBe(Model);
                question.Origin["promptVersion"]!.GetValue<string>().ShouldBe(EvalPrompts.PromptVersion);
                question.Origin["round"]!.GetValue<int>().ShouldBe(1);
                question.Origin["seed"]!.GetValue<string>().ShouldBe(seedHex);
                question.Origin["documents"]!.GetValue<int>().ShouldBe(4, "D at the round, for the 25 % growth trigger");
                question.Origin["judge"]!["answerable"]!.GetValue<int>().ShouldBe(5);
                var source = new[] { doc1, doc2, doc3 }.Single(d => d.ChunkIds.Contains(question.SourceChunkId));
                question.ExpectedDocumentIds[0].ShouldBe(source.DocumentId);
                question.SourceContentHash.ShouldBe(source.ContentHash);
                question.Origin["strata"]!["documentType"]!.GetValue<string>().ShouldBe(source == doc3 ? "pdf" : "page");
            }

            questions.Single(q => q.SourceChunkId == doc1.ChunkIds[0]).ExpectedDocumentIds.ShouldBe([doc1.DocumentId, flagged.DocumentId],
                "a live document with a chunk of the same TextHash is expected too");
            questions.Where(q => q.SourceChunkId != doc1.ChunkIds[0]).ShouldAllBe(q => q.ExpectedDocumentIds.Length == 1);

            var calls = gateway.Calls.ToList();
            calls.ShouldAllBe(c => c.Ceiling == "OnPrem" && c.Model == Model && c.ResponseFormat == "json_object");
            calls.Where(c => c.Step == EvalModelClient.GenerateStep).ShouldAllBe(c => c.Temperature == 0.3);
            calls.Where(c => c.Step == EvalModelClient.JudgeStep).ShouldAllBe(c => c.Temperature == 0);
            calls.Count(c => c.Step == EvalModelClient.GenerateStep).ShouldBe(6);
            calls.Count(c => c.Step == EvalModelClient.JudgeStep).ShouldBe(6);

            // A second generate-set (a retry) finds the layer full and adds nothing.
            var again = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = seed.CollectionId }, seed);
            (await rig.RunAsync(again)).ShouldBe(new JobResult.Committed(), rig.Logs.Text);
            (await CommandSeeds.ResultAsync(database, again.Id)).GetProperty("kept").GetInt32().ShouldBe(0);
            (await EvalSeeds.QuestionsAsync(database, setId)).Count.ShouldBe(6);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Each_filtered_out_case_is_dropped_and_only_the_good_questions_are_stored()
    {
        var tag = EvalSeeds.Tag();
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var goodA = Ask(0, 0, tag);
            var goodB = Ask(1, 1, tag);
            var duplicate = goodB.Replace("håller med", "håller med alls", StringComparison.Ordinal);
            QuestionFilters.Cosine(FakeContent.Vector(goodB, 1024), FakeContent.Vector(duplicate, 1024)).ShouldBeGreaterThanOrEqualTo(QuestionFilters.DuplicateCosine);
            QuestionFilters.Cosine(FakeContent.Vector(goodA, 1024), FakeContent.Vector(goodB, 1024)).ShouldBeLessThan(QuestionFilters.DuplicateCosine);
            var judgedLow = Ask(2, 2, tag);
            var replies = new (string Reply, string? Reason)[]
            {
                (Reply(goodA, "Byggnadsnämnden."), null),
                (Reply(goodB, "Ja, inom tre veckor."), null),
                (Reply(duplicate, "Ja, inom tre veckor."), FilterReasons.Duplicate),
                (Reply($"How long does it take to get a decision about parking for {tag}?", "Four weeks."), FilterReasons.Language),
                (Reply($"Vad kostar {tag}?", "Ingenting."), FilterReasons.Length),
                (Reply($"Vilken nämnd beslutar om avfall och hur lång väntan blir det för {tag}.", "Nämnden."), FilterReasons.QuestionMark),
                (Reply($"Vad säger texten om vem som beslutar om färdtjänst för {tag}?", "Nämnden."), FilterReasons.Deictic),
                (Reply($"Kan jag mejla anna.berg@example.com om simhall hos {tag} redan idag?", "Ja."), FilterReasons.PersonalData),
                (Reply($"Varför tar handläggningen av förskola normalt fyra veckor hos {tag}?", "Fyra veckor."), FilterReasons.AnswerInQuestion),
                (Reply("Ansvarar Umeå kommun för bygglov i hela kommunen när ärendet är komplett?", "Ja."), FilterReasons.CopiedFromText),
                (Reply(judgedLow, "Kundtjänst."), FilterReasons.Judge),
                ("inte json", FilterReasons.Format),
            };
            var texts = new List<string>();
            for (var d = 0; d < 4; d++)
            {
                var chunks = Enumerable.Range(0, 3).Select(i => Text((3 * d) + i < 8 ? (3 * d) + i : 1)).ToList();
                texts.AddRange(chunks);
                await EvalSeeds.DocumentAsync(database, seed, $"Dokument {d} {tag}", [.. chunks.Select(c => new SeedChunk(c))]);
            }

            for (var i = 0; i < replies.Length; i++)
            {
                gateway.Generate[texts[i]] = replies[i].Reply;
            }

            gateway.Judge[judgedLow] = (5, 5, 2);

            await using var rig = EvalRig.Start(database, gateway);
            var job = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = seed.CollectionId }, seed);
            (await rig.RunAsync(job)).ShouldBe(new JobResult.Committed(), rig.Logs.Text);

            var result = await CommandSeeds.ResultAsync(database, job.Id);
            result.GetProperty("generated").GetInt32().ShouldBe(12);
            result.GetProperty("kept").GetInt32().ShouldBe(2, rig.Logs.Text);
            var stored = (await EvalSeeds.QuestionsAsync(database, result.GetProperty("setId").GetGuid())).Select(q => q.Question).ToList();
            stored.ShouldContain(goodA);
            stored.Count(q => q == goodB || q == duplicate).ShouldBe(1, "one of two near-duplicates is kept");
            var dropped = rig.Logs.Entries.Where(e => e.Category == Category && e.Message.Contains("was dropped", StringComparison.Ordinal)).Select(e => e.Message).ToList();
            foreach (var reason in replies.Select(r => r.Reason).OfType<string>())
            {
                dropped.Count(m => m.EndsWith($"({reason})", StringComparison.Ordinal)).ShouldBe(1, $"{reason}: {string.Join("; ", dropped)}");
            }

            gateway.Calls.Count(c => c.Step == EvalModelClient.JudgeStep).ShouldBe(4, "the judge sees only candidates that passed the cheap filters");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_top_up_retires_the_questions_of_changed_and_deleted_documents_and_regenerates_to_size_with_a_new_seed()
    {
        var tag = EvalSeeds.Tag();
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var texts = Enumerable.Range(0, 6).Select(Text).ToList();
            var doc1 = await EvalSeeds.DocumentAsync(database, seed, "Ett " + tag, [new(texts[0]), new(texts[1])]);
            var doc2 = await EvalSeeds.DocumentAsync(database, seed, "Två " + tag, [new(texts[2]), new(texts[3])]);
            await EvalSeeds.DocumentAsync(database, seed, "Tre " + tag, [new(texts[4]), new(texts[5])]);
            for (var i = 0; i < 6; i++)
            {
                gateway.Generate[texts[i]] = Reply(Ask(i, i, tag), $"Svar nummer {i} {tag}.");
            }

            await using var rig = EvalRig.Start(database, gateway);
            var first = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = seed.CollectionId }, seed);
            (await rig.RunAsync(first)).ShouldBe(new JobResult.Committed(), rig.Logs.Text);
            var setId = (await CommandSeeds.ResultAsync(database, first.Id)).GetProperty("setId").GetGuid();
            var before = await EvalSeeds.QuestionsAsync(database, setId);
            before.Count.ShouldBe(6);

            // doc1 changes: its first question is no longer answerable, its second still is; doc2 is deleted.
            var changedTexts = new[] { Text(6), Text(7) };
            var changed = await EvalSeeds.ChangeAsync(database, seed, doc1.DocumentId, [new(changedTexts[0]), new(changedTexts[1])]);
            gateway.Generate[changedTexts[0]] = Reply(Ask(6, 6, tag), $"Svar sex {tag}.");
            gateway.Generate[changedTexts[1]] = Reply(Ask(7, 7, tag), $"Svar sju {tag}.");
            var unanswerable = before.Single(q => q.SourceChunkId == doc1.ChunkIds[0]);
            var stillAnswered = before.Single(q => q.SourceChunkId == doc1.ChunkIds[1]);
            gateway.Judge[unanswerable.Question] = (2, 5, 5);
            await EvalSeeds.DeleteAsync(database, doc2.DocumentId);
            gateway.Calls.Clear();

            var topUp = await rig.LeaseAsync(database, new EvalTopUpSetPayload { CollectionId = seed.CollectionId }, seed);
            (await rig.RunAsync(topUp)).ShouldBe(new JobResult.Committed(), rig.Logs.Text);

            var result = await CommandSeeds.ResultAsync(database, topUp.Id);
            result.GetProperty("round").GetInt32().ShouldBe(2);
            result.GetProperty("retired").GetInt32().ShouldBe(3);
            result.GetProperty("kept").GetInt32().ShouldBe(1, "N = min(100, 3 × 2 documents, 4 eligible chunks) = 4, 3 remain");
            var after = await EvalSeeds.QuestionsAsync(database, setId);
            var stale = after.Where(q => q.State == EvaluationQuestionStates.Stale).ToList();
            stale.Select(q => q.Id).Order().ShouldBe(before.Where(q => q.Id == unanswerable.Id || q.ExpectedDocumentIds.Contains(doc2.DocumentId)).Select(q => q.Id).Order());
            stale.ShouldAllBe(q => q.Retired);
            var active = after.Where(q => q.State == EvaluationQuestionStates.Active && !q.Retired).ToList();
            active.Count.ShouldBe(4, "the layer is back at its target size");
            active.Single(q => q.Id == stillAnswered.Id).SourceContentHash.ShouldBe(changed.ContentHash, "a re-checked question takes the new content hash");
            var added = active.Single(q => q.Origin["round"]!.GetValue<int>() == 2);
            changed.ChunkIds.ShouldContain(added.SourceChunkId);
            added.Origin["seed"]!.GetValue<string>().ShouldBe(Convert.ToHexStringLower(ChunkSampler.Seed(setId, 2)));
            added.Origin["seed"]!.GetValue<string>().ShouldNotBe(before[0].Origin["seed"]!.GetValue<string>());
            added.CreatedBy.ShouldBe("system:top-up-set");
            gateway.Calls.Count(c => c.Step == EvalModelClient.JudgeStep).ShouldBe(3, "two re-checks of doc1's questions, one new candidate; none for doc3 or the deleted doc2");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Theory]
    [InlineData("ume/chat-eu", null, EvalModelClient.ModelNotOnPrem)]
    [InlineData(Model, "ume/chat-eu", EvalModelClient.ModelNotOnPrem)]
    [InlineData("fake-drained", null, EvalModelClient.ModelNotOnPrem)]
    [InlineData("fake-wrong-residency", null, EvalModelClient.ResidencyAboveCeiling)]
    [InlineData(null, null, EvalSetGenerator.ModelUnset)]
    public async Task A_model_that_cannot_run_on_prem_refuses_the_job_and_nothing_is_written(string? model, string? judgeModel, string code)
    {
        var tag = EvalSeeds.Tag();
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await EvalSeeds.SetModelsAsync(database, model, judgeModel);
            var text = Text(0);
            await EvalSeeds.DocumentAsync(database, seed, "Ett " + tag, [new(text)]);
            gateway.Generate[text] = Reply(Ask(0, 0, tag), "Nämnden.");

            await using var rig = EvalRig.Start(database, gateway);
            var job = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = seed.CollectionId }, seed);

            (await rig.RunAsync(job)).ShouldBe(new JobResult.FatalError(code));
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."EvaluationQuestions" q JOIN content."EvaluationSets" s ON s."Id" = q."SetId" WHERE s."CollectionId" = $1
                """, seed.CollectionId)).ShouldBe(0);
            if (code != EvalModelClient.ResidencyAboveCeiling)
            {
                gateway.Calls.ShouldBeEmpty("refused before any model call");
            }
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_worker_without_the_onprem_key_and_a_kind_that_is_not_a_set_kind_are_refused()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await using (var rig = EvalRig.Start(database, gateway, onPremKey: false))
            {
                var job = await rig.LeaseAsync(database, new EvalTopUpSetPayload { CollectionId = seed.CollectionId }, seed);
                (await rig.RunAsync(job)).ShouldBe(new JobResult.FatalError(EvalModelClient.NoOnPremKey));
            }

            await using (var rig = EvalRig.Start(database, gateway))
            {
                var nightly = await rig.LeaseAsync(database, new EvalNightlyPayload { CollectionId = seed.CollectionId, ScheduledFor = DateTimeOffset.UtcNow }, seed);
                (await rig.RunAsync(nightly)).ShouldBe(new JobResult.FatalError(EvalSetGenerator.UnknownKind));
                var unknown = await rig.LeaseAsync(database, new EvalGenerateSetPayload { CollectionId = Guid.CreateVersion7() }, seed);
                (await rig.RunAsync(unknown)).ShouldBe(new JobResult.FatalError(EvalSetGenerator.CollectionUnknown));
            }

            gateway.Calls.ShouldBeEmpty();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }
}
