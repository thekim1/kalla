using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// Connectors design §18 "ClamAV" and "Uploads" through the engine on the test database: committed batches of an
/// <c>upload</c> source (manifest and slot blobs as the admin writes them, a <c>sync</c> with <c>trigger</c> <c>upload</c>)
/// go live; EICAR ends <c>malware</c> with its quarantine row and blob, event 5102 and a degraded run; the same bytes in a
/// second department are a second document whose embeddings come from the cache; a replace keeps the document and swaps
/// its set; a delete removes the document and an add on the deleted slot revives it.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class UploadEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-upload-engine-");
    private readonly CapturingLoggerProvider _logs = new();

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Eicar_is_quarantined_with_event_5102_the_run_degrades_and_the_same_bytes_in_another_department_embed_from_the_cache()
    {
        var marker = Guid.NewGuid().ToString("N");
        var note = Note("Bygglov " + marker);
        var seed = await SeedUploadAsync();
        var other = await SeedUploadAsync();
        var gate = new EmbeddingGate();
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, gate, _logs);
        try
        {
            var (a, b, virus) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var run = await CommitAsync(seed,
                (UploadOperation.Add, a, "anteckning.md", note),
                (UploadOperation.Add, b, "rutiner.md", Note("Rutiner " + marker)),
                (UploadOperation.Add, virus, "virus.pdf", Eicar));

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Degraded, 2, 0, 0, 0, 1, 0));
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(2);
            (await SlotStateAsync(virus)).ShouldBe(UploadStates.Malware);
            (await SlotStateAsync(a)).ShouldBe(UploadStates.Clean);
            var sha = Convert.ToHexStringLower(SHA256.HashData(Eicar));
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."QuarantinedFiles"
                WHERE "SourceId" = $1 AND "RunId" = $2 AND "Origin" = 'upload' AND "Name" = 'virus.pdf' AND "Sha256" = $3 AND "Signature" = $4
                """, seed.SourceId, run, Convert.FromHexString(sha), EicarOnlyScanner.Signature)).ShouldBe(1);
            File.Exists(Path.Combine(_blobs.FullName, "quarantine", seed.SourceId.ToString("D"), sha)).ShouldBeTrue();
            var events = _logs.Entries.Where(e => e.EventId.Id == 5102 && e.Message.Contains(seed.SourceId.ToString(), StringComparison.Ordinal)).ToList();
            events.Count.ShouldBe(1);
            events[0].Message.ShouldContain(sha);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId))
                .ShouldBe(2, "a quarantined file is no document");
            // The file name is the display name; the title comes from the parsed text (connectors design §9.4).
            (await Sql.ScalarAsync<string>(database.Connection(null),
                """SELECT "DisplayName" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2""", seed.SourceId, UploadConnector.IdentityOf(a)))
                .ShouldBe("anteckning.md");
            (await Sql.ScalarAsync<string>(database.Connection(null),
                """SELECT "Title" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2""", seed.SourceId, UploadConnector.IdentityOf(a)))
                .ShouldBe("Bygglov " + marker);

            var requests = gate.Requests;
            var copy = await CommitAsync(other, (UploadOperation.Add, Guid.NewGuid(), "anteckning.md", note));

            (await EngineKit.WaitForRunAsync(database, copy)).ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            (await EngineKit.LiveChunksAsync(database, other)).ShouldBeGreaterThanOrEqualTo(1);
            gate.Requests.ShouldBe(requests, "the second department's copy embeds from the cache (keyed by text, not by document)");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await EngineKit.PauseAsync(database, other);
        }
    }

    [Fact]
    public async Task A_replace_keeps_the_document_and_swaps_its_set_a_delete_removes_it_and_an_add_on_the_slot_revives_it()
    {
        var marker = Guid.NewGuid().ToString("N");
        var first = Note("Avfall " + marker);
        var seed = await SeedUploadAsync();
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            var (slot, keep) = (Guid.NewGuid(), Guid.NewGuid());
            var added = await CommitAsync(seed, (UploadOperation.Add, slot, "avfall.md", first), (UploadOperation.Add, keep, "taxa.md", Note("Taxa " + marker)));
            (await EngineKit.WaitForRunAsync(database, added)).ShouldBe(new RunCounters(RunStates.Succeeded, 2, 0, 0, 0, 0, 0));
            var (documentId, firstSet) = await DocumentAsync(seed, slot);

            var replaced = await CommitAsync(seed, (UploadOperation.Replace, slot, "avfall.md", Note("Avfall " + marker, "Nya hämtdagar gäller från mars.")));

            (await EngineKit.WaitForRunAsync(database, replaced)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            var (sameId, secondSet) = await DocumentAsync(seed, slot);
            sameId.ShouldBe(documentId);
            secondSet.ShouldNotBe(firstSet);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", firstSet!.Value))
                .ShouldBe("superseded");
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);

            var deleted = await CommitAsync(seed, (UploadOperation.Delete, slot, null, null));

            (await EngineKit.WaitForRunAsync(database, deleted)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 0, 1, 0, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Reason" FROM content."RunItems" WHERE "RunId" = $1 AND "DocumentId" = $2 AND "Outcome" = 'deleted'
                """, deleted, documentId)).ShouldBe("upload.deleted");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);

            var restored = await CommitAsync(seed, (UploadOperation.Add, slot, "avfall.md", Note("Avfall " + marker, "Nya hämtdagar gäller från mars.")));

            (await EngineKit.WaitForRunAsync(database, restored)).State.ShouldBe(RunStates.Succeeded);
            (await DocumentAsync(seed, slot)).Id.ShouldBe(documentId);
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(2);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private static byte[] Note(string title, string extra = "") => Encoding.UTF8.GetBytes(
        $"# {title}\n\nUmeå kommun ansvarar för {title}. Här finns information om hur du ansöker och vem du kontaktar.\n\n" +
        $"## Så ansöker du\n\nDu ansöker via e-tjänsten. Handläggningstiden är normalt fyra veckor. {extra}\n");

    /// <summary>A source of type <c>upload</c> on the <c>files</c> template (the kit seeds URL lists; the rows are turned into an upload source).</summary>
    private async Task<SourceSeed> SeedUploadAsync()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Type" = 'upload', "Settings" = '{}'::jsonb WHERE "Id" = $1""", seed.SourceId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Pipelines" SET "TemplateId" = 'files' WHERE "Id" = $1""", seed.PipelineId);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."PipelineVersions" SET "Definition" = $2::jsonb WHERE "Id" = $1""", seed.VersionId,
            """{"schema":"pipeline/v2","template":"files","templateVersion":1,"params":{}}""");
        return seed;
    }

    /// <summary>
    /// The admin's upload commit (connectors design §9.1 steps 1–2): each file at <c>uploads/{sourceId}/{slot}/{sha256}</c>, the
    /// batch manifest, and the <c>sync</c> job (dedup <c>sync-upload:{batchId}</c>); returns the run id (the job's).
    /// </summary>
    private async Task<Guid> CommitAsync(SourceSeed seed, params (UploadOperation Operation, Guid Slot, string? Name, byte[]? Bytes)[] files)
    {
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _blobs.FullName }), TimeProvider.System);
        var entries = new List<UploadManifestEntry>();
        foreach (var file in files)
        {
            if (file.Bytes is null)
            {
                entries.Add(new UploadManifestEntry { Operation = file.Operation, UploadId = file.Slot });
                continue;
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
            await using (var stream = new MemoryStream(file.Bytes))
            {
                await blobs.PutAsync(UploadConnector.BlobKeyOf(seed.SourceId, file.Slot, sha), stream, file.Bytes.Length, Ct);
            }

            entries.Add(new UploadManifestEntry
            {
                Operation = file.Operation, UploadId = file.Slot, FileName = file.Name, Bytes = file.Bytes.Length, Sha256 = sha,
            });
        }

        var batchId = Guid.NewGuid();
        var manifest = new UploadBatchManifest { BatchId = batchId, SourceId = seed.SourceId, Entries = entries, CommittedBy = "admin@umea.se" };
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(ContractJson.Serialize(manifest))))
        {
            await blobs.PutAsync(UploadConnector.ManifestKey(seed.SourceId, batchId), stream, null, Ct);
        }

        var payload = new SyncPayload { SourceId = seed.SourceId, Kind = SyncKind.Sync, Trigger = RunTrigger.Upload, BatchId = batchId };
        var job = new Domain.Ports.JobRequest(JobClasses.Sync, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload))
        {
            DedupKey = JobDedupKeys.SyncUpload(batchId),
            Priority = 10,
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    private Task<string> SlotStateAsync(Guid slot) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Uploads" WHERE "Id" = $1""", slot);

    private async Task<(Guid Id, Guid? LiveSet)> DocumentAsync(SourceSeed seed, Guid slot)
    {
        var id = await Sql.ScalarAsync<Guid>(database.Connection(null),
            """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2""", seed.SourceId, UploadConnector.IdentityOf(slot));
        var set = await Sql.ScalarAsync<Guid?>(database.Connection(null), """SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", id);
        return (id, set);
    }
}
