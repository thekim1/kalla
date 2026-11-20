using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// A-38 (processing design §13.3): a question whose expected document is gone (its source deleted, the documents
/// soft-deleted as <c>delete-source</c> does) is left out of every run and counted as <c>documentGone</c>, so recall@10 and MRR are
/// those of the set without it; the run makes a <c>human</c> one <c>stale</c> (not retired) and leaves a <c>generated</c> one
/// to the next top-up; a revived document makes the <c>stale</c> human question active again at the next run.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class EvaluationGoneDocumentTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-eval-gone-blobs-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_deleted_sources_questions_are_counted_as_document_gone_and_its_human_question_is_stale_until_the_document_is_revived()
    {
        var fixture = await EvalKit.SeedAsync(database);
        await using var worker = await EvalKit.StartWorkerAsync(database, gateway, _blobs);
        var without = await OnDemandAsync(fixture);
        without.DocumentGone.ShouldBe(0);

        // A second source of the collection with one document, expected by a human and a generated question.
        var tag = Guid.NewGuid().ToString("N")[..12];
        var sourceId = await SourceAsync(fixture, tag);
        FixtureDocument document;
        await using (var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin), EvalKit.Embed))
        {
            document = await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = fixture.CollectionId,
                EmbeddingProfileId = EngineKit.ProfileId,
                SourceId = sourceId,
                Classification = fixture.Seed.Classification,
                Url = $"https://site.test/simhall/{tag}",
                Title = "simhall",
            }, [new FixtureChunk($"Simhallen på Navet stänger för renovering i juni. Ärende {tag}.")], Ct);
        }

        var human = await QuestionAsync(fixture, EvaluationLayers.Human, "När stänger simhallen på Navet för renovering?", document.Id);
        var generated = await QuestionAsync(fixture, EvaluationLayers.Generated, "Stänger simhallen på Navet i juni?", document.Id);
        var added = await OnDemandAsync(fixture);
        (added.QuestionCount, added.DocumentGone).ShouldBe((without.QuestionCount + 2, 0), "both questions run while their document is there");

        // The admin's delete sets Sources.DeletedAt, and delete-source soft-deletes the source's documents. The documents are
        // soft-deleted here directly, as delete-source does (IndexWriter), not through the job queue: the nightly purge's
        // re-queue of delete-source for a deleted source with live content (#884) raced the queued command on CI (#896).
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "DeletedAt" = now() WHERE "Id" = $1""", sourceId);
        (await new IndexWriter(database.Database(RagDatabase.Worker)).SoftDeleteDocumentsAsync([document.Id], DateTimeOffset.UtcNow, Ct)).ShouldBe(1);

        var gone = await OnDemandAsync(fixture);
        (gone.QuestionCount, gone.Found, gone.RecallAtK, gone.Mrr).ShouldBe((without.QuestionCount, without.Found, without.RecallAtK, without.Mrr),
            "the run is the set without the two questions: a delete never lowers recall@10 or MRR");
        gone.DocumentGone.ShouldBe(2);
        gone.Layers.Select(l => (l.Layer, l.Questions, l.Found, l.DocumentGone))
            .ShouldBe(without.Layers.Select(l => (l.Layer, l.Questions, l.Found, 1)), "one gone question in each layer");
        var baseline = await EvalKit.RunAsync(database, without.EvaluationRunId);
        var stored = await EvalKit.RunAsync(database, gone.EvaluationRunId);
        (stored.QuestionCount, stored.Found, stored.RecallAtK, stored.Mrr).ShouldBe((baseline.QuestionCount, baseline.Found, baseline.RecallAtK, baseline.Mrr));
        var results = await EvalKit.ResultsAsync(database, gone.EvaluationRunId);
        results.Keys.ShouldNotContain(human, "not searched, so no miss is recorded");
        results.Keys.ShouldNotContain(generated);
        (await StateAsync(human)).ShouldBe("stale|false", "a human question goes stale, not retired; the editor re-points or retires it");
        (await StateAsync(generated)).ShouldBe("active|false", "a generated question is retired at the next top-up, not by the run");

        // The nightly run counts them the same way, and the stale question stays out.
        await EvalKit.PublishAsync(database, fixture);
        try
        {
            var nightly = EvalKit.Result<EvalRunResult>(await EvalKit.RunJobAsync(database, fixture,
                new EvalNightlyPayload { CollectionId = fixture.CollectionId, ScheduledFor = DateTimeOffset.UtcNow }, JobDedupKeys.EvalNightly(fixture.CollectionId)));
            (nightly.QuestionCount, nightly.Found, nightly.RecallAtK, nightly.DocumentGone).ShouldBe((without.QuestionCount, without.Found, without.RecallAtK, 2));
            nightly.Layers.Select(l => l.DocumentGone).ShouldBe([1, 1]);
        }
        finally
        {
            await EvalKit.PublishAsync(database, fixture, published: false);
        }

        (await StateAsync(human)).ShouldBe("stale|false");

        // Revived within its 14 days (connectors §12): the document and its live set's chunks come back.
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "DeletedAt" = NULL WHERE "Id" = $1""", sourceId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "DeletedAt" = NULL, "PurgeAfter" = NULL WHERE "Id" = $1""", document.Id);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Chunks" SET "DeletedAt" = NULL WHERE "ChunkSetId" = $1""", document.LiveChunkSetId!.Value);

        var revived = await OnDemandAsync(fixture);
        (await StateAsync(human)).ShouldBe("active|false", "the next run makes the stale human question active again");
        (revived.QuestionCount, revived.Found, revived.RecallAtK, revived.DocumentGone).ShouldBe((added.QuestionCount, added.Found, added.RecallAtK, 0));
        var back = await EvalKit.ResultsAsync(database, revived.EvaluationRunId);
        back.Keys.ShouldContain(human);
        back.Keys.ShouldContain(generated);
    }

    /// <summary>A hybrid <c>on-demand</c> run of the fixture's collection; its one run.</summary>
    private async Task<EvaluationRunRef> OnDemandAsync(EvalFixture fixture) =>
        EvalKit.Result<EvalOnDemandResult>(await EvalKit.RunJobAsync(database, fixture,
            new EvalOnDemandPayload { CollectionId = fixture.CollectionId, Variants = [EvaluationVariant.Hybrid] }, JobDedupKeys.Eval(fixture.CollectionId)))
            .Runs.ShouldHaveSingleItem();

    /// <summary>A paused upload source of the fixture's collection on its pipeline.</summary>
    private async Task<Guid> SourceAsync(EvalFixture fixture, string tag)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var source = new Source
        {
            CollectionId = fixture.CollectionId,
            Type = SourceTypes.Upload,
            Name = "Raderad " + tag,
            Classification = fixture.Seed.Classification,
            PipelineId = fixture.Seed.PipelineId,
            State = SourceStates.Paused,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(source);
        await db.SaveChangesAsync(Ct);
        return source.Id;
    }

    private async Task<Guid> QuestionAsync(EvalFixture fixture, string layer, string question, Guid expected)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
            VALUES ($1, $2, $3, $4, $5, $6, 'active')
            """, id, fixture.SetId, fixture.Seed.Classification, layer, question, new[] { expected });
        return id;
    }

    /// <summary>The question's state and whether it is retired, as <c>state|retired</c>.</summary>
    private Task<string> StateAsync(Guid question) =>
        Sql.ScalarAsync<string>(database.Connection(null),
            """SELECT "State" || '|' || ("RetiredAt" IS NOT NULL)::text FROM content."EvaluationQuestions" WHERE "Id" = $1""", question);
}
