using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Upload batches (admin design §4.5; connectors design §9.1; #92): create a batch for an upload source, send one request
/// per file (raw body, <c>X-File-Name</c>, optional <c>X-Upload-Id</c> to replace a slot), commit, or drop the batch. Files are
/// staged under <c>tmp/upload-batches/{batchId}/</c> (swept after 24 h, data-model §9) with SHA-256 computed while
/// streaming; the commit promotes each to <c>uploads/{sourceId}/{uploadId}/{sha256}</c>, writes the manifest
/// <c>uploads/{sourceId}/batches/{batchId}.json</c> and enqueues the <c>sync</c> (<c>trigger</c> <c>upload</c>, dedup
/// <c>sync-upload:{batchId}</c>) with its audit entry. The file request alone may carry up to 60 MB (to-verify #32);
/// every other admin request keeps Kestrel's 1 MB.
/// </summary>
public static class UploadEndpoints
{
    public const int MaxFilesPerBatch = 100;
    public const long MaxBatchBytes = 500L * 1024 * 1024;

    /// <summary>The upload request's body limit: a 50 MB file plus headroom (to-verify #32).</summary>
    public const long FileRequestLimit = 60L * 1024 * 1024;

    public const string FileNameHeader = "X-File-Name";
    public const string UploadIdHeader = "X-Upload-Id";

    /// <summary>Extensions the worker accepts in P1 (connectors §9.2).</summary>
    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".odt", ".ods", ".odp", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".txt", ".md", ".csv", ".html", ".htm",
    };

    /// <summary>Legacy formats deferred to P2 (connectors §9.2: <c>upload.format_deferred</c>).</summary>
    public static IReadOnlySet<string> Deferred { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt", ".rtf", ".eml", ".msg",
    };

    public static RouteGroupBuilder MapUploads(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/sources/{id:guid}/upload-batches", async (Guid id, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                if (source.Type != Domain.Entities.SourceTypes.Upload)
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotAnUploadSource);
                }

                var batch = new BatchState(Guid.CreateVersion7(), source.Id, admin.Actor, admin.Now);
                await blobs.PutAsync(BatchKey(batch.BatchId), new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(batch, ContractJson.Options)), null, ct);
                return Results.Created($"/api/upload-batches/{batch.BatchId}", new UploadBatchCreated(batch.BatchId, MaxFilesPerBatch, MaxBatchBytes));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithTags("Uploads").WithName("CreateUploadBatch").WithSummary("Start an upload batch");

        var batches = api.MapGroup("/upload-batches").WithTags("Uploads");

        batches.MapPut("/{batchId:guid}/files/{n}", async (Guid batchId, string n, HttpContext http, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                var number = FileNumber(n);
                var (batch, source) = await OpenAsync(admin, blobs, batchId, ct);
                var name = FileName(http.Request.Headers[FileNameHeader].ToString());
                var extension = Path.GetExtension(name);
                if (Deferred.Contains(extension))
                {
                    throw AdminFaults.Of(AdminErrorCodes.UploadFormatDeferred, new Dictionary<string, string> { ["extension"] = extension });
                }

                if (!Allowed.Contains(extension))
                {
                    throw AdminFaults.Of(AdminErrorCodes.UploadTypeRefused, new Dictionary<string, string> { ["extension"] = extension.Length > 0 ? extension : "(none)" });
                }

                Guid? replaces = null;
                if (http.Request.Headers[UploadIdHeader].ToString() is { Length: > 0 } slot)
                {
                    replaces = Guid.TryParse(slot, out var parsed) && await admin.Db.Uploads.AsNoTracking().AnyAsync(u => u.Id == parsed && u.SourceId == source.Id && u.DeletedAt == null, ct)
                        ? parsed
                        : throw AdminFaults.Field(UploadIdHeader, AdminErrorCodes.InvalidValue);
                }

                if (await blobs.ExistsAsync(FileKey(batchId, number), ct))
                {
                    throw AdminFaults.Of(AdminErrorCodes.UploadFileExists, new Dictionary<string, string> { ["n"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }

                var maxBytes = MaxFileBytes(source);
                if (http.Request.ContentLength is { } declared && declared > maxBytes)
                {
                    throw AdminFaults.Of(AdminErrorCodes.UploadTooLarge, new Dictionary<string, string> { ["mb"] = (maxBytes / 1024 / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }

                // Staged under a fresh key, hashed while streaming; the file record makes it part of the batch.
                var staged = new BlobKey($"tmp/upload-batches/{batchId:D}/data/{Guid.CreateVersion7():N}");
                await using var counting = new HashingStream(http.Request.Body, maxBytes);
                try
                {
                    await blobs.PutAsync(staged, counting, http.Request.ContentLength, ct);
                }
                catch (FileTooLargeException)
                {
                    await blobs.DeleteAsync(staged, ct);
                    throw AdminFaults.Of(AdminErrorCodes.UploadTooLarge, new Dictionary<string, string> { ["mb"] = (maxBytes / 1024 / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }

                var file = new StagedFile(number, replaces ?? Guid.CreateVersion7(), replaces is null ? UploadOperation.Add : UploadOperation.Replace, name,
                    string.IsNullOrWhiteSpace(http.Request.ContentType) ? null : http.Request.ContentType, counting.Length, counting.Sha256Hex(), staged.Value);
                var written = await blobs.PutAsync(FileKey(batchId, number), new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(file, ContractJson.Options)), null, ct);
                if (written == BlobWriteResult.AlreadyExisted)
                {
                    await blobs.DeleteAsync(staged, ct);
                    throw AdminFaults.Of(AdminErrorCodes.UploadFileExists, new Dictionary<string, string> { ["n"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }

                _ = batch;
                return new UploadedFile(number, file.UploadId, file.Operation.ToString().ToLowerInvariant(), file.FileName, file.Bytes, file.Sha256);
            })
            // No Accepts<> metadata: it would make routing answer 415 by content type before authorization; any type is taken.
            .RequireAuthorization(AdminPolicies.Edit).WithName("UploadFile").WithSummary("Send one file of a batch (raw body, X-File-Name; X-Upload-Id replaces a slot)")
            // This request alone may be large (to-verify #32): the endpoint middleware sets Kestrel's limit from the metadata.
            .WithMetadata(new FileRequestSizeLimit());

        batches.MapPost("/{batchId:guid}/commit", async (Guid batchId, UploadCommitRequest request, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                var (batch, source) = await OpenAsync(admin, blobs, batchId, ct);
                var files = new List<StagedFile>();
                foreach (var number in request.Files.Distinct())
                {
                    await using var stream = await blobs.OpenReadAsync(FileKey(batchId, number), null, ct)
                        ?? throw AdminFaults.Field("files", AdminErrorCodes.UploadFileMissing, new Dictionary<string, string> { ["n"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    files.Add(await JsonSerializer.DeserializeAsync<StagedFile>(stream, ContractJson.Options, ct) ?? throw new JsonException("Empty file record."));
                }

                var deletes = request.Deletes.Distinct().ToList();
                if (deletes.Count > 0)
                {
                    var live = await admin.Db.Uploads.AsNoTracking().Where(u => u.SourceId == source.Id && u.DeletedAt == null && deletes.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
                    if (live.Count != deletes.Count)
                    {
                        throw AdminFaults.Field("deletes", AdminErrorCodes.InvalidValue);
                    }
                }

                if (files.Count + deletes.Count is 0 or > MaxFilesPerBatch || files.Sum(f => f.Bytes) > MaxBatchBytes)
                {
                    throw AdminFaults.Of(AdminErrorCodes.UploadBatchTooLarge);
                }

                // Promote each staged file to its slot's key (write-once; a retried commit finds them in place).
                foreach (var file in files)
                {
                    var target = new BlobKey($"uploads/{source.Id:D}/{file.UploadId:D}/{file.Sha256}");
                    if (!await blobs.ExistsAsync(target, ct))
                    {
                        await blobs.PromoteAsync(new BlobKey(file.Staged), target, ct);
                    }
                }

                var manifest = new UploadBatchManifest
                {
                    BatchId = batchId,
                    SourceId = source.Id,
                    CommittedBy = admin.Actor,
                    CommittedAt = admin.Now,
                    Entries =
                    [
                        .. files.OrderBy(f => f.Number).Select(f => new UploadManifestEntry
                        {
                            Operation = f.Operation, UploadId = f.UploadId, FileName = f.FileName, DeclaredMediaType = f.DeclaredMediaType, Bytes = f.Bytes, Sha256 = f.Sha256,
                        }),
                        .. deletes.Select(d => new UploadManifestEntry { Operation = UploadOperation.Delete, UploadId = d }),
                    ],
                };
                await blobs.PutAsync(new BlobKey($"uploads/{source.Id:D}/batches/{batchId:D}.json"),
                    new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options)), null, ct);

                var result = await admin.WriteAsync(async attempt =>
                {
                    var collection = await admin.Db.Collections.AsNoTracking().SingleAsync(c => c.Id == source.CollectionId, attempt);
                    var job = new JobRequest(JobClasses.Sync, collection.DepartmentId, source.Classification,
                        JsonSerializer.Serialize(new SyncPayload { SourceId = source.Id, Kind = SyncKind.Sync, Trigger = RunTrigger.Upload, BatchId = batchId }, ContractJson.Options))
                    {
                        DedupKey = JobDedupKeys.SyncUpload(batchId),
                        TeamId = collection.OwnerTeamId,
                    };
                    return new AdminChange<UploadCommitted>(new UploadCommitted(batchId, job.Id, manifest.Entries.Count), "upload-commit", AuditEntityTypes.Source, source.Id,
                        null, new { batchId, entries = manifest.Entries.Select(e => new { operation = e.Operation.ToString().ToLowerInvariant(), e.UploadId, e.FileName, e.Bytes, e.Sha256 }) })
                    {
                        DepartmentId = collection.DepartmentId, Classification = source.Classification, Jobs = [job],
                    };
                }, ct);

                await blobs.DeletePrefixAsync(new BlobKey($"tmp/upload-batches/{batchId:D}"), ct);
                _ = batch;
                return result;
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CommitUploadBatch").WithSummary("Commit a batch: manifest and the upload sync");

        batches.MapDelete("/{batchId:guid}", async (Guid batchId, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                _ = await OpenAsync(admin, blobs, batchId, ct);
                await blobs.DeletePrefixAsync(new BlobKey($"tmp/upload-batches/{batchId:D}"), ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("DropUploadBatch").WithSummary("Drop an uncommitted batch and its files");

        return api;
    }

    private static BlobKey BatchKey(Guid batchId) => new($"tmp/upload-batches/{batchId:D}/batch.json");

    private static BlobKey FileKey(Guid batchId, int n) => new($"tmp/upload-batches/{batchId:D}/files/{n}.json");

    /// <summary>The batch and its source, both in the caller's scope; 404 for an unknown, committed or dropped batch.</summary>
    private static async Task<(BatchState Batch, Source Source)> OpenAsync(AdminContext admin, IBlobStore blobs, Guid batchId, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(BatchKey(batchId), null, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var batch = await JsonSerializer.DeserializeAsync<BatchState>(stream, ContractJson.Options, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == batch.SourceId, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        return (batch, source);
    }

    private static int FileNumber(string n) =>
        int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= MaxFilesPerBatch
            ? number
            : throw AdminFaults.Field("n", AdminErrorCodes.InvalidValue);

    /// <summary>The header's file name (percent-encoded UTF-8 allowed), without a path, 1–300 characters, no control characters.</summary>
    private static string FileName(string header)
    {
        var name = Uri.UnescapeDataString(header).Trim();
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length is >= 1 and <= 300 && !name.Any(char.IsControl) ? name : throw AdminFaults.Field(FileNameHeader, AdminErrorCodes.InvalidValue);
    }

    private static long MaxFileBytes(Source source)
    {
        using var json = JsonDocument.Parse(source.Settings);
        var settings = (UploadSettings)SourceSettings.Bind(source.Type, json.RootElement);
        return settings.MaxFileSizeMb * 1024L * 1024L;
    }

    private sealed record BatchState(Guid BatchId, Guid SourceId, string CreatedBy, DateTimeOffset CreatedAt);

    private sealed record StagedFile(int Number, Guid UploadId, UploadOperation Operation, string FileName, string? DeclaredMediaType, long Bytes, string Sha256, string Staged);

    private sealed class FileTooLargeException : IOException;

    /// <summary>Passes the request body through, counting and hashing it; past <c>max</c> bytes it throws.</summary>
    private sealed class HashingStream(Stream inner, long max) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override long Length => _read;
        private long _read;

        public string Sha256Hex() => Convert.ToHexStringLower(_hash.GetCurrentHash());

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            return Count(buffer.Span[..n]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(ReadOnlySpan<byte> read)
        {
            _read += read.Length;
            if (_read > max)
            {
                throw new FileTooLargeException();
            }

            _hash.AppendData(read);
            return read.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>The upload file endpoint's body limit (<see cref="UploadEndpoints.FileRequestLimit"/>).</summary>
public sealed class FileRequestSizeLimit : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => UploadEndpoints.FileRequestLimit;
}

public sealed record UploadBatchCreated(Guid BatchId, int MaxFiles, long MaxBytes);

public sealed record UploadedFile(int N, Guid UploadId, string Operation, string FileName, long Bytes, string Sha256);

public sealed record UploadCommitted(Guid BatchId, Guid JobId, int Entries);

public sealed record UploadCommitRequest : AdminRequest
{
    /// <summary>The numbers of the batch's files to commit.</summary>
    [MaxLength(UploadEndpoints.MaxFilesPerBatch)]
    public IReadOnlyList<int> Files { get; init; } = [];

    /// <summary>Upload slots of the source to delete (connectors §9.3).</summary>
    [MaxLength(UploadEndpoints.MaxFilesPerBatch)]
    public IReadOnlyList<Guid> Deletes { get; init; } = [];
}
