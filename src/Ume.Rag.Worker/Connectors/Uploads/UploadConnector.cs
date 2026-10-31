using System.Runtime.CompilerServices;
using System.Text.Json;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors.Changes;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;

namespace Ume.Rag.Worker.Connectors.Uploads;

/// <summary>An upload listing has one step: the batch's plan.</summary>
public sealed record UploadCheckpoint : ConnectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The <c>upload</c> source by slot (connectors design §9): the <c>sync</c> of a committed batch reads its manifest and plans
/// the slots (<see cref="UploadBatchPlanner"/>); the <c>fetch</c> stage <c>upload</c> copies the slot's blob to a local temp
/// file (checking its SHA-256), scans it with ClamAV (fail closed; a finding is copied to
/// <c>quarantine/{sourceId}/{sha256}</c> for the runner's row, event and tombstone of the upload blob), sniffs and checks
/// the type, and returns the upload blob itself as the document's raw content (no copy). The content hash is the
/// file's SHA-256, so identical bytes in a replace are <c>unchanged</c>.
/// </summary>
public sealed class UploadConnector(IBlobStore blobs, IMalwareScanner scanner, IUploadSlotReader slots, TimeProvider time)
    : IFetchConnector<UploadCheckpoint>, IIdListingConnector
{
    public const string Type = SourceTypes.Upload;

    public const string ManifestMissing = "upload.manifest_missing";
    public const string BlobMissing = "upload.blob_missing";
    public const string HashMismatch = "upload.hash_mismatch";

    /// <summary>The platform cap of an upload (connectors design §9.1, answered 3).</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    public string SourceType => Type;

    public Type SettingsType => typeof(UploadSettings);

    public static string IdentityOf(Guid uploadId) => "upload:" + uploadId.ToString("D");

    public static BlobKey ManifestKey(Guid sourceId, Guid batchId) => new($"uploads/{sourceId:D}/batches/{batchId:D}.json");

    public static BlobKey BlobKeyOf(Guid sourceId, Guid uploadId, string sha256) => new($"uploads/{sourceId:D}/{uploadId:D}/{sha256}");

    /// <summary>The batch's effect on the slots; null when the listing has no batch or its manifest is missing.</summary>
    public async Task<UploadBatchPlan?> PlanAsync(ListingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.BatchId is not { } batchId)
        {
            return null;
        }

        await using var stream = await blobs.OpenReadAsync(ManifestKey(context.SourceId, batchId), null, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var manifest = ContractJson.Deserialize<UploadBatchManifest>(document.RootElement);
        if (manifest.SourceId != context.SourceId || manifest.BatchId != batchId)
        {
            throw new InvalidDataException("The upload manifest names another source or batch.");
        }

        return UploadBatchPlanner.Plan(manifest, context.Classification, await slots.ListAsync(context.SourceId, cancellationToken), time.GetUtcNow());
    }

    public async IAsyncEnumerable<ListingEvent<UploadCheckpoint>> ListAsync(
        ListingContext context, UploadCheckpoint from, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        if (plan is null)
        {
            yield return new ListingEvent<UploadCheckpoint>.Failure(context.BatchId?.ToString("D") ?? string.Empty, ManifestMissing);
            yield return new ListingEvent<UploadCheckpoint>.Checkpoint(new UploadCheckpoint { SchemaVersion = UploadCheckpoint.CurrentSchemaVersion });
            yield return new ListingEvent<UploadCheckpoint>.Done(Completeness.Partial(ManifestMissing));
            yield break;
        }

        foreach (var fetch in plan.Fetches)
        {
            yield return new ListingEvent<UploadCheckpoint>.Item(fetch);
        }

        yield return new ListingEvent<UploadCheckpoint>.Checkpoint(new UploadCheckpoint { SchemaVersion = UploadCheckpoint.CurrentSchemaVersion });
        yield return new ListingEvent<UploadCheckpoint>.Done(Completeness.Full);
    }

    /// <summary>The source's live slots (not deleted): what exists now (§2.1).</summary>
    public async IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var slot in await slots.ListAsync(context.SourceId, cancellationToken))
        {
            if (slot.DeletedAt is null)
            {
                yield return slot.Identity;
            }
        }
    }

    public async ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var upload = item.Hints.Upload ?? throw new ArgumentException("An upload item carries its slot.", nameof(item));
        var key = BlobKeyOf(context.SourceId, upload.UploadId, upload.Sha256);
        FetchedBody? body;
        await using (var stream = await blobs.OpenReadAsync(key, null, cancellationToken))
        {
            if (stream is null)
            {
                return new FetchOutcome.Failed(BlobMissing);
            }

            body = await FetchedBody.CaptureAsync(stream, MaxBytes, truncate: false, "application/octet-stream", null, cancellationToken);
        }

        if (body is null)
        {
            return new FetchOutcome.Refused(RunItemOutcome.TooLarge, "upload.too_large");
        }

        await using (body)
        {
            if (!string.Equals(body.Sha256, upload.Sha256, StringComparison.Ordinal))
            {
                return new FetchOutcome.Failed(HashMismatch);
            }

            ScanResult scan;
            await using (var stream = body.OpenRead())
            {
                scan = await scanner.ScanAsync(stream, "upload", cancellationToken);
            }

            switch (scan)
            {
                case ScanResult.Malware malware:
                    var quarantine = new BlobKey($"quarantine/{context.SourceId:D}/{body.Sha256}");
                    await using (var stream = body.OpenRead())
                    {
                        await blobs.PutAsync(quarantine, stream, body.Length, cancellationToken);
                    }

                    return new FetchOutcome.Quarantined(quarantine, body.Sha256, malware.Signature, malware.Reason);
                case ScanResult.TooLarge:
                    return new FetchOutcome.Refused(RunItemOutcome.TooLarge, "clamav.limit");
                case ScanResult.Unavailable unavailable:
                    return new FetchOutcome.Transient(unavailable.Reason, RawContentWriter.ScanRetry, unavailable.CountsAttempt);
            }

            TypeVerdict verdict;
            await using (var stream = body.OpenRead())
            {
                verdict = UploadTypeCheck.Check(FileTypeSniffer.Sniff(stream, upload.FileName), upload.FileName, upload.DeclaredMediaType);
            }

            if (!verdict.Allowed)
            {
                return new FetchOutcome.Refused(RunItemOutcome.Unsupported, verdict.Reason!);
            }

            var facts = new FetchFacts(null, item.CanonicalIdentity, [], null)
            {
                Notes = verdict.DeclaredDiffers ? ["upload.declared_type_differs"] : [],
            };
            return new FetchOutcome.Content(new RawContentRef(key, body.Sha256, HtmlNoiseNormalizer.RulesVersion, verdict.MediaType!, body.Length), Validators.None, facts);
        }
    }
}
