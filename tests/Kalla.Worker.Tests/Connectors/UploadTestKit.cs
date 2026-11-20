using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Blobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Connectors.Uploads;
using Kalla.Worker.Tests.Blobs;
using Kalla.Worker.Tests.Engine;

namespace Kalla.Worker.Tests.Connectors;

internal static class UploadKit
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The admin's upload commit (connectors design §9.1 steps 1–2): each file at <c>uploads/{sourceId}/{slot}/{sha256}</c>
    /// under <paramref name="blobs"/>, written through the envelope at the source's level (<c>internal</c>: <c>URB2</c>), the
    /// batch manifest, and the <c>sync</c> job (dedup <c>sync-upload:{batchId}</c>); returns the run id (the job's). A delete
    /// has no name and no bytes.
    /// </summary>
    public static async Task<Guid> CommitAsync(RagDatabaseFixture database, DirectoryInfo blobs, SourceSeed seed,
        params (UploadOperation Operation, Guid Slot, string? Name, byte[]? Bytes)[] files)
    {
        var store = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = blobs.FullName }), TimeProvider.System);
        var envelope = TestBlobKeys.Envelope(blobs);
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
                await envelope.PutAsync(UploadConnector.BlobKeyOf(seed.SourceId, file.Slot, sha), seed.Classification, stream, file.Bytes.Length, Ct);
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
            await store.PutAsync(UploadConnector.ManifestKey(seed.SourceId, batchId), stream, null, Ct);
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
}
