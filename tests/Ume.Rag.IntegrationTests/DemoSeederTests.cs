using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.MigrationService;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The dev stack's demo content (repo-and-deployment §4.4, #873): on a database without collections rag-migrate creates the
/// fake catalog's department, the public example collection with an upload source of the smoke documents (blobs, manifest
/// and the upload sync, as the admin's commit), and the published Demo-assistent offered to the dev client. A later start
/// adds the 30 smoke questions once the worker has indexed every expected document; after that it writes nothing. Its own
/// database: the demo collection exists once.
/// </summary>
public sealed class DemoSeederTests(RagDatabaseFixture database) : IClassFixture<RagDatabaseFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Smoke => Path.GetDirectoryName(TestPostgres.RepositoryFile(Path.Combine("tests", "evaluation", "smoke", "questions.json")))!;

    private readonly DirectoryInfo _blobRoot = Directory.CreateTempSubdirectory("ume-rag-demo-seed-");

    public void Dispose() => _blobRoot.Delete(recursive: true);

    [Fact]
    public async Task Rag_migrate_seeds_the_demo_once_and_adds_the_evaluation_set_when_the_documents_are_searchable()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        await PipelineTemplateSeeder.SeedAsync(db, [.. TemplateCatalog.BuiltIn.All.Select(t => t.Template)], TimeProvider.System, Ct);
        await SettingsSeeder.SeedAsync(db, new SettingsSeedOptions(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);
        await DevSeeder.SeedAsync(db, TimeProvider.System, Ct);
        await DevSeeder.SeedDevClientAsync(db, new Domain.Services.KeyHasher(System.Text.Encoding.UTF8.GetBytes(new string('p', 32))), new string('k', 43), null,
            TimeProvider.System, Ct);
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _blobRoot.FullName }), TimeProvider.System);
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.Connection(Migrator));
        var jobs = new PostgresJobQueue(new JobDatabase(dataSource), new JobWakeUps(TimeProvider.System));

        (await DemoSeeder.SeedAsync(db, blobs, jobs, Smoke, TimeProvider.System, Ct)).ShouldBe(DemoSeeder.Outcome.Created);
        db.ChangeTracker.Clear();

        // The department and team the fake catalog lists, so the organisation sync takes them over by id.
        (await db.Departments.AsNoTracking().SingleAsync(d => d.Id == DemoSeeder.DepartmentId, Ct)).CostCenterCode.ShouldBe("1000");
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == DemoSeeder.TeamId, Ct)).DepartmentId.ShouldBe(DemoSeeder.DepartmentId);

        var collection = await db.Collections.AsNoTracking().SingleAsync(Ct);
        (collection.Id, collection.Name, collection.Classification, collection.EmbeddingProfileId, collection.PublishedAt.HasValue, collection.DepartmentId).ShouldBe(
            (DemoSeeder.CollectionId, DemoSeeder.CollectionName, Classification.Public, DevSeeder.EmbeddingProfileId, true, DemoSeeder.DepartmentId));
        var source = await db.Sources.AsNoTracking().SingleAsync(Ct);
        (source.Id, source.Type, source.Classification, source.CollectionId).ShouldBe((DemoSeeder.SourceId, SourceTypes.Upload, Classification.Public, collection.Id));
        var pipeline = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == source.PipelineId, Ct);
        pipeline.DepartmentId.ShouldBeNull();
        pipeline.PublishedVersionId.ShouldNotBeNull();

        // The upload sync, queued as the admin's commit queues it, and the manifest it reads: every document, its blob in place.
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Class == JobClasses.Sync, Ct);
        (job.DepartmentId, job.TeamId, job.Classification).ShouldBe((DemoSeeder.DepartmentId, (Guid?)DemoSeeder.TeamId, Classification.Public));
        var payload = JsonSerializer.Deserialize<SyncPayload>(job.Payload, ContractJson.Options)!;
        (payload.SourceId, payload.Kind, payload.Trigger).ShouldBe((DemoSeeder.SourceId, SyncKind.Sync, RunTrigger.Upload));
        job.DedupKey.ShouldBe(JobDedupKeys.SyncUpload(payload.BatchId!.Value));
        await using var stored = (await blobs.OpenReadAsync(new BlobKey($"uploads/{DemoSeeder.SourceId:D}/batches/{payload.BatchId:D}.json"), null, Ct))!;
        var manifest = (await JsonSerializer.DeserializeAsync<UploadBatchManifest>(stored, ContractJson.Options, Ct))!;
        var files = DemoSeeder.Documents(Smoke);
        manifest.Entries.Select(e => e.FileName).ShouldBe(files.Select(Path.GetFileName));
        foreach (var entry in manifest.Entries)
        {
            entry.Operation.ShouldBe(UploadOperation.Add);
            (await blobs.ExistsAsync(new BlobKey($"uploads/{DemoSeeder.SourceId:D}/{entry.UploadId:D}/{entry.Sha256}"), Ct)).ShouldBeTrue();
        }

        // The assistant is published on the collection and offered to the dev client, so Open WebUI lists it.
        var assistant = await db.Assistants.AsNoTracking().SingleAsync(Ct);
        (assistant.Name, assistant.Classification, assistant.State, assistant.DraftVersionId).ShouldBe(
            (DemoSeeder.AssistantName, Classification.Public, AssistantStates.Active, (Guid?)null));
        assistant.Description.ShouldNotBeNullOrWhiteSpace();
        assistant.ContactEmail.ShouldNotBeNullOrWhiteSpace();
        var version = await db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == assistant.PublishedVersionId, Ct);
        (version.State, version.GatewayModel, version.LinkedCollectionIds().Single()).ShouldBe((AssistantVersionStates.Published, DemoSeeder.ChatModel, collection.Id));
        version.SystemPrompt.ShouldNotBeNullOrWhiteSpace();
        (await db.AssistantAudiences.AsNoTracking().SingleAsync(Ct)).SubjectId.ShouldBe(DevSeeder.DevClientId);

        // The next start, before the worker indexed them: no evaluation set yet, nothing else written.
        var configVersion = await VersionAsync();
        (await DemoSeeder.SeedAsync(db, blobs, jobs, Smoke, TimeProvider.System, Ct)).ShouldBe(DemoSeeder.Outcome.EvaluationSetPending);
        (await VersionAsync()).ShouldBe(configVersion);
        (await db.Jobs.AsNoTracking().CountAsync(j => j.Class == JobClasses.Sync, Ct)).ShouldBe(1);

        // What the worker's upload sync leaves: a slot per file and a searchable document per slot.
        await IndexAsync(db, manifest);
        (await DemoSeeder.SeedAsync(db, blobs, jobs, Smoke, TimeProvider.System, Ct)).ShouldBe(DemoSeeder.Outcome.EvaluationSetAdded);
        var set = await db.EvaluationSets.AsNoTracking().SingleAsync(s => s.CollectionId == collection.Id, Ct);
        var questions = await db.EvaluationQuestions.AsNoTracking().Where(q => q.SetId == set.Id).ToListAsync(Ct);
        var expected = await DemoSeeder.QuestionsAsync(Smoke, Ct);
        questions.Count.ShouldBe(30);
        questions.ShouldAllBe(q => q.Layer == EvaluationLayers.Human && q.ExpectedDocumentIds.Length > 0);
        var documents = await (from u in db.Uploads.AsNoTracking() join d in db.Documents.AsNoTracking() on "upload:" + u.Id.ToString() equals d.CanonicalIdentity
            select new { u.FileName, d.Id }).ToDictionaryAsync(x => x.Id, x => x.FileName, Ct);
        var first = questions.Single(q => q.Question == expected[0].Question);
        first.ExpectedDocumentIds.Select(id => documents[id]).ShouldBe(expected[0].Expected);

        (await DemoSeeder.SeedAsync(db, blobs, jobs, Smoke, TimeProvider.System, Ct)).ShouldBe(DemoSeeder.Outcome.Done);
        (await db.EvaluationQuestions.AsNoTracking().CountAsync(Ct)).ShouldBe(30);
    }

    private Task<long> VersionAsync() => Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");

    private async Task IndexAsync(Infrastructure.Persistence.RagDbContext db, UploadBatchManifest manifest)
    {
        foreach (var entry in manifest.Entries)
        {
            db.Uploads.Add(new Upload
            {
                Id = entry.UploadId, SourceId = DemoSeeder.SourceId, Classification = Classification.Public, BatchId = manifest.BatchId, FileName = entry.FileName!,
                Bytes = entry.Bytes!.Value, Sha256 = Convert.FromHexString(entry.Sha256!), State = UploadStates.Indexed, UploadedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        await using var writer = new ContentFixtureWriter(database.Connection(Worker), database.Connection(Admin),
            (_, dimensions) => [1f, .. new float[dimensions - 1]]);
        foreach (var entry in manifest.Entries)
        {
            await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = DemoSeeder.CollectionId, EmbeddingProfileId = DevSeeder.EmbeddingProfileId, SourceId = DemoSeeder.SourceId,
                IdentityKind = "upload", CanonicalIdentity = "upload:" + entry.UploadId.ToString("D"), DisplayName = entry.FileName,
            }, [new FixtureChunk(entry.FileName!)], Ct);
        }
    }
}
