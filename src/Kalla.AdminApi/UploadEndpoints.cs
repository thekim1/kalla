using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;

namespace Kalla.AdminApi;

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

    /// <summary>A deleted file can be restored this long after its delete (connectors §9.3, §13.4: the document's grace).</summary>
    public static readonly TimeSpan RestoreGrace = TimeSpan.FromDays(14);

    public const string FileNameHeader = "X-File-Name";
    public const string UploadIdHeader = "X-Upload-Id";

    /// <summary>An upload's relative path: at most this many segments (admin design §4.5).</summary>
    public const int MaxPathSegments = 10;

    /// <summary>An upload's relative path in all (<c>Uploads.FileName</c>, <c>Documents.DisplayName</c>: 300).</summary>
    public const int MaxFileNameLength = 300;

    /// <summary>Extensions the worker accepts (connectors §9.2); legacy Word, Excel, PowerPoint and RTF are converted by the parser's LibreOffice.</summary>
    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".odt", ".ods", ".odp", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".txt", ".md", ".csv", ".html", ".htm",
        ".doc", ".xls", ".ppt", ".rtf",
    };

    /// <summary>E-mail, deferred to P2 (connectors §9.2: <c>upload.format_deferred</c>).</summary>
    public static IReadOnlySet<string> Deferred { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".eml", ".msg",
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

        // §4.5 "Sources, runs, uploads": the source's upload slots (connectors §9.1, §9.3), deleted ones only with deleted=true
        // (restorable within the 14-day grace), and its quarantined files (connectors §11: metadata only, never the bytes).
        // q, state and sort serve the Files tab (§5.5.7); the cursor is opaque and holds the last row's sort key and id.
        api.MapGet("/sources/{id:guid}/files", async (Guid id, bool? deleted, string? q, string? state, string? sort, string? after, int? limit, AdminContext admin,
                CancellationToken ct) =>
            {
                _ = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var take = Keyset.Take(limit);
                var order = sort is null or "" ? FileSorts.Name : FileSorts.All.Contains(sort) ? sort : throw AdminFaults.Field("sort", AdminErrorCodes.InvalidValue);
                var query = Files(admin, id, deleted == true);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var pattern = Like.Contains(q.Trim());
                    query = query.Where(f => EF.Functions.ILike(f.Upload.FileName, pattern, Like.EscapeCharacter));
                }

                if (!string.IsNullOrEmpty(state))
                {
                    var states = FileStates.Of(state) ?? throw AdminFaults.Field("state", AdminErrorCodes.InvalidValue);
                    query = query.Where(f => states.Contains(f.Upload.State));
                }

                var cursor = after is { Length: > 0 } ? FileCursor.Read(after, order) : null;
                if (order == FileSorts.Name)
                {
                    if (cursor is { Name: { } name, Id: var lastId })
                    {
#pragma warning disable CA1309 // translated to SQL ">" under COLLATE "C"; no .NET comparison runs
                        query = query.Where(f => string.Compare(EF.Functions.Collate(f.SortName, PathCollation), name) > 0
                            || (EF.Functions.Collate(f.SortName, PathCollation) == name && f.Upload.Id.CompareTo(lastId) > 0));
#pragma warning restore CA1309
                    }

                    query = query.OrderBy(f => EF.Functions.Collate(f.SortName, PathCollation)).ThenBy(f => f.Upload.Id);
                }
                else
                {
                    if (cursor is { At: { } at, Id: var lastId })
                    {
                        query = query.Where(f => f.Upload.UploadedAt < at || (f.Upload.UploadedAt == at && f.Upload.Id.CompareTo(lastId) < 0));
                    }

                    query = query.OrderByDescending(f => f.Upload.UploadedAt).ThenByDescending(f => f.Upload.Id);
                }

                var rows = await query.Take(take + 1).ToListAsync(ct);
                var page = rows.Take(take).ToList();
                var next = rows.Count > take
                    ? FileCursor.Write(order == FileSorts.Name
                        ? new FileCursor(order, page[^1].SortName, null, page[^1].Upload.Id)
                        : new FileCursor(order, null, page[^1].Upload.UploadedAt, page[^1].Upload.Id))
                    : null;
                return new KeysetPage<UploadFileDto, string?>([.. page.Select(f => UploadFileDto.Of(f.Upload))], next);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Uploads").WithName("ListSourceFiles")
            .WithSummary("A source's upload slots (keyset; q, state processing|indexed|failed, sort name|uploaded; deleted=true for deleted ones)");

        api.MapGet("/sources/{id:guid}/files/summary", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var groups = await Files(admin, id, deleted: false).GroupBy(f => f.Upload.State)
                    .Select(g => new { State = g.Key, Count = g.Count(), Bytes = g.Sum(f => f.Upload.Bytes), Last = g.Max(f => (DateTimeOffset?)f.Upload.UploadedAt) })
                    .ToListAsync(ct);
                int InStates(IReadOnlyList<string> states) => groups.Where(g => states.Contains(g.State)).Sum(g => g.Count);
                return new FilesSummary(groups.Sum(g => g.Count), InStates(FileStates.Processing), InStates(FileStates.Indexed), InStates(FileStates.Failed),
                    groups.Sum(g => g.Bytes), groups.Max(g => g.Last));
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Uploads").WithName("SourceFilesSummary")
            .WithSummary("A source's file counts by state, bytes and the last upload (deleted files not counted)");

        api.MapGet("/sources/{id:guid}/quarantine", async (Guid id, Guid? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                _ = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var take = Keyset.Take(limit);
                var query = admin.Db.QuarantinedFiles.AsNoTracking().Where(q => q.SourceId == id);
                if (after is { } last)
                {
                    query = query.Where(q => q.Id.CompareTo(last) > 0);
                }

                var rows = await query.OrderBy(q => q.Id).Take(take + 1)
                    .Select(q => new QuarantinedFileDto(q.Id, q.Origin, q.Name, q.Url, q.Signature, q.RunId, q.DetectedAt, q.PurgeAfter)).ToListAsync(ct);
                return KeysetPage.From(rows, take, q => q.Id);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Uploads").WithName("ListQuarantine").WithSummary("A source's quarantined files: metadata only (keyset)");

        // Restore within the grace (connectors §9.3: "restore adds the entry back and the document is revived", §13.4): a new
        // batch whose manifest re-adds each deleted slot with its own name, size and hash, so the worker's sync treats it as an
        // add on a deleted slot (a restore), fetches the kept upload blob again and revives the document. Platform admin, as the
        // UI's "the platform team can bring them back" says.
        api.MapPost("/sources/{id:guid}/files/restore", async (Guid id, FileRestoreRequest request, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                if (source.Type != Domain.Entities.SourceTypes.Upload)
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotAnUploadSource);
                }

                var ids = request.UploadIds.Distinct().ToList();
                if (ids.Count is 0 or > MaxFilesPerBatch)
                {
                    throw AdminFaults.Field("uploadIds", AdminErrorCodes.InvalidValue);
                }

                var since = admin.Now - RestoreGrace;
                var slots = await admin.Db.Uploads.AsNoTracking().Where(u => u.SourceId == source.Id && ids.Contains(u.Id)).ToListAsync(ct);
                foreach (var uploadId in ids)
                {
                    var slot = slots.SingleOrDefault(s => s.Id == uploadId) ?? throw AdminFaults.Field("uploadIds", AdminErrorCodes.NotFound);
                    if (slot.DeletedAt is not { } deletedAt || deletedAt < since ||
                        !await blobs.ExistsAsync(new BlobKey($"uploads/{source.Id:D}/{slot.Id:D}/{Convert.ToHexStringLower(slot.Sha256)}"), ct))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.FileNotRestorable, new Dictionary<string, string> { ["name"] = slot.FileName });
                    }
                }

                var batchId = Guid.CreateVersion7();
                var manifest = new UploadBatchManifest
                {
                    BatchId = batchId,
                    SourceId = source.Id,
                    CommittedBy = admin.Actor,
                    CommittedAt = admin.Now,
                    Entries =
                    [
                        .. slots.OrderBy(s => s.Id).Select(s => new UploadManifestEntry
                        {
                            Operation = UploadOperation.Add, UploadId = s.Id, FileName = s.FileName, DeclaredMediaType = s.DeclaredMediaType, Bytes = s.Bytes,
                            Sha256 = Convert.ToHexStringLower(s.Sha256),
                        }),
                    ],
                };
                await blobs.PutAsync(new BlobKey($"uploads/{source.Id:D}/batches/{batchId:D}.json"),
                    new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options)), null, ct);
                return await admin.WriteAsync(async attempt =>
                {
                    var collection = await admin.Db.Collections.AsNoTracking().SingleAsync(c => c.Id == source.CollectionId, attempt);
                    var job = new JobRequest(JobClasses.Sync, collection.DepartmentId, source.Classification,
                        JsonSerializer.Serialize(new SyncPayload { SourceId = source.Id, Kind = SyncKind.Sync, Trigger = RunTrigger.Upload, BatchId = batchId }, ContractJson.Options))
                    {
                        DedupKey = JobDedupKeys.SyncUpload(batchId),
                        TeamId = collection.OwnerTeamId,
                    };
                    return new AdminChange<UploadCommitted>(new UploadCommitted(batchId, job.Id, manifest.Entries.Count), "upload-restore", AuditEntityTypes.Source, source.Id,
                        null, new { batchId, entries = manifest.Entries.Select(e => new { e.UploadId, e.FileName, e.Bytes, e.Sha256 }) })
                    {
                        DepartmentId = collection.DepartmentId, Classification = source.Classification, Jobs = [job],
                    };
                }, ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Uploads").WithName("RestoreSourceFiles")
            .WithSummary("Restore deleted files within the 14-day grace (platform admin): a batch that re-adds the slots");

        var batches = api.MapGroup("/upload-batches").WithTags("Uploads");

        batches.MapPut("/{batchId:guid}/files/{n}", async (Guid batchId, string n, HttpContext http, AdminContext admin, IBlobStore blobs, IBlobEnvelope envelope,
                CancellationToken ct) =>
            {
                var number = FileNumber(n);
                var (_, source) = await OpenAsync(admin, blobs, batchId, ct, open: true);
                var name = FileName(http.Request.Headers[FileNameHeader].ToString());
                var extension = Path.GetExtension(name[(name.LastIndexOf('/') + 1)..]);
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

                // Staged under a fresh key, hashed while streaming (over the received plaintext) and written at the source's level:
                // internal uploads are envelope-encrypted (URB2, data-model §7; #181). The commit promotes the blob unchanged.
                var staged = new BlobKey($"tmp/upload-batches/{batchId:D}/data/{Guid.CreateVersion7():N}");
                await using var counting = new HashingStream(http.Request.Body, maxBytes);
                try
                {
                    await envelope.PutAsync(staged, source.Classification, counting, http.Request.ContentLength, ct);
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

                return new UploadedFile(number, file.UploadId, file.Operation.ToString().ToLowerInvariant(), file.FileName, file.Bytes, file.Sha256);
            })
            // No Accepts<> metadata: it would make routing answer 415 by content type before authorization; any type is taken.
            .RequireAuthorization(AdminPolicies.Edit).WithName("UploadFile").WithSummary("Send one file of a batch (raw body, X-File-Name; X-Upload-Id replaces a slot)")
            // This request alone may be large (to-verify #32): the endpoint middleware sets Kestrel's limit from the metadata.
            .WithMetadata(new FileRequestSizeLimit());

        batches.MapPost("/{batchId:guid}/commit", async (Guid batchId, UploadCommitRequest request, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                var (_, source) = await OpenAsync(admin, blobs, batchId, ct);
                var files = new List<StagedFile>();
                var requested = new CommitRecord([.. request.Files.Distinct().Order()], [.. request.Deletes.Distinct().Order()], Guid.Empty, 0);
                // A committed batch answers a repeat of the same commit with its result (a lost response, a double click).
                if (await ReadAsync<CommitRecord>(blobs, CommittedKey(batchId), ct) is { } done)
                {
                    return done.Files.SequenceEqual(requested.Files) && done.Deletes.SequenceEqual(requested.Deletes)
                        ? new UploadCommitted(batchId, done.JobId, done.Entries)
                        : throw AdminFaults.Of(AdminErrorCodes.UploadBatchCommitted);
                }

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
                var manifestKey = new BlobKey($"uploads/{source.Id:D}/batches/{batchId:D}.json");
                if (await blobs.PutAsync(manifestKey, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options)), null, ct)
                    == BlobWriteResult.AlreadyExisted)
                {
                    // An earlier commit wrote the manifest and failed after it: go on only when it lists the same entries.
                    var stored = await ReadAsync<UploadBatchManifest>(blobs, manifestKey, ct) ?? throw new InvalidOperationException("The manifest vanished.");
                    if (!Same(stored.Entries, manifest.Entries))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.UploadBatchCommitted);
                    }
                }

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

                // Keep batch.json and the commit record (the 24 h sweep removes them); the staged data is promoted, the file records done.
                await blobs.PutAsync(CommittedKey(batchId), new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
                    requested with { JobId = await JobIdAsync(admin, batchId, result.JobId, ct), Entries = result.Entries }, ContractJson.Options)), null, ct);
                await blobs.DeletePrefixAsync(new BlobKey($"tmp/upload-batches/{batchId:D}/data"), ct);
                await blobs.DeletePrefixAsync(new BlobKey($"tmp/upload-batches/{batchId:D}/files"), ct);
                return result with { JobId = await JobIdAsync(admin, batchId, result.JobId, ct) };
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CommitUploadBatch").WithSummary("Commit a batch: manifest and the upload sync");

        batches.MapDelete("/{batchId:guid}", async (Guid batchId, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
            {
                _ = await OpenAsync(admin, blobs, batchId, ct, open: true);
                await blobs.DeletePrefixAsync(new BlobKey($"tmp/upload-batches/{batchId:D}"), ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("DropUploadBatch").WithSummary("Drop an uncommitted batch and its files");

        return api;
    }

    /// <summary>
    /// The name sort compares bytes of the lower-cased path, so every file under a folder prefix sorts together (the database's
    /// language collation may weigh <c>/</c> below letters and interleave <c>HR/…</c> with <c>HRx…</c>).
    /// </summary>
    private const string PathCollation = "C";

    /// <summary>
    /// A source's upload slots with their lower-cased path for the name sort. The worker writes each slot's state, reason and
    /// document (connectors design §9.1): <c>indexed</c> when its bytes' chunk set went live, <c>refused</c> or
    /// <c>malware</c> when their processing ended without one.
    /// </summary>
    private static IQueryable<FileRow> Files(AdminContext admin, Guid sourceId, bool deleted) =>
        admin.Db.Uploads.AsNoTracking()
            .Where(u => u.SourceId == sourceId && (deleted ? u.DeletedAt != null : u.DeletedAt == null))
            .Select(u => new FileRow
            {
                Upload = u,
#pragma warning disable CA1304, CA1311 // translated to SQL lower(); no .NET culture is involved
                SortName = u.FileName.ToLower(),
#pragma warning restore CA1304, CA1311
            });

    private sealed class FileRow
    {
        public required Upload Upload { get; init; }

        public required string SortName { get; init; }
    }

    /// <summary>The list's opaque <c>after</c>: base64url JSON of the sort it was made for, the last row's key and id.</summary>
    private sealed record FileCursor(string Sort, string? Name, DateTimeOffset? At, Guid Id)
    {
        public static string Write(FileCursor cursor) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(cursor, JsonSerializerOptions.Web));

        public static FileCursor Read(string after, string sort)
        {
            FileCursor? cursor;
            try
            {
                cursor = JsonSerializer.Deserialize<FileCursor>(Base64Url.DecodeFromChars(after), JsonSerializerOptions.Web);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                cursor = null;
            }

            return cursor is not null && cursor.Sort == sort && (sort == FileSorts.Name ? cursor.Name is not null : cursor.At is not null)
                ? cursor
                : throw AdminFaults.Field("after", AdminErrorCodes.InvalidValue);
        }
    }

    private static BlobKey BatchKey(Guid batchId) => new($"tmp/upload-batches/{batchId:D}/batch.json");

    private static BlobKey FileKey(Guid batchId, int n) => new($"tmp/upload-batches/{batchId:D}/files/{n}.json");

    private static BlobKey CommittedKey(Guid batchId) => new($"tmp/upload-batches/{batchId:D}/committed.json");

    /// <summary>
    /// The batch and its source, both in the caller's scope; 404 for an unknown or dropped batch. With <paramref name="open"/>
    /// (adding files, dropping) a committed batch is 409 <c>upload_batch_committed</c>.
    /// </summary>
    private static async Task<(BatchState Batch, Source Source)> OpenAsync(AdminContext admin, IBlobStore blobs, Guid batchId, CancellationToken ct, bool open = false)
    {
        var batch = await ReadAsync<BatchState>(blobs, BatchKey(batchId), ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == batch.SourceId, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        if (open && await blobs.ExistsAsync(CommittedKey(batchId), ct))
        {
            throw AdminFaults.Of(AdminErrorCodes.UploadBatchCommitted);
        }

        return (batch, source);
    }

    private static async Task<T?> ReadAsync<T>(IBlobStore blobs, BlobKey key, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(key, null, ct);
        return stream is null ? default : await JsonSerializer.DeserializeAsync<T>(stream, ContractJson.Options, ct);
    }

    /// <summary>The job of the batch's sync: the one enqueued now, or the one an earlier attempt enqueued (dedup <c>sync-upload:{batchId}</c>).</summary>
    private static async Task<Guid> JobIdAsync(AdminContext admin, Guid batchId, Guid enqueued, CancellationToken ct)
    {
        var dedup = JobDedupKeys.SyncUpload(batchId);
        return await admin.Db.Jobs.AsNoTracking().Where(j => j.DedupKey == dedup).OrderBy(j => j.EnqueuedAt).Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct) ?? enqueued;
    }

    private static bool Same(IReadOnlyList<UploadManifestEntry> a, IReadOnlyList<UploadManifestEntry> b) =>
        a.Count == b.Count && a.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Key(UploadManifestEntry e) => $"{e.Operation}|{e.UploadId:N}|{e.Sha256}";

    /// <summary>The commit as requested and its result, kept with the batch so a repeat is answered the same.</summary>
    private sealed record CommitRecord(IReadOnlyList<int> Files, IReadOnlyList<Guid> Deletes, Guid JobId, int Entries);

    private static int FileNumber(string n) =>
        int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= MaxFilesPerBatch
            ? number
            : throw AdminFaults.Field("n", AdminErrorCodes.InvalidValue);

    /// <summary>
    /// The header's relative path (percent-encoded UTF-8, admin design §4.5, §5.5.7): <c>\</c> read as <c>/</c>, leading
    /// <c>/</c>s dropped, 1–10 segments of 1–255 characters each trimmed, no <c>.</c> or <c>..</c> segment, no drive letter, no
    /// control characters, at most 300 characters in all (<c>Uploads.FileName</c>). <c>HR/Policys/semester.pdf</c> is kept
    /// as sent, so it is the document's display name and path rules match its folders.
    /// </summary>
    private static string FileName(string header)
    {
        var path = Uri.UnescapeDataString(header).Trim().Replace('\\', '/').TrimStart('/');
        var segments = path.Split('/').Select(s => s.Trim()).ToList();
        var valid = segments.Count <= MaxPathSegments
            && segments.All(s => s.Length is >= 1 and <= 255 && s is not "." and not ".." && !s.Any(char.IsControl))
            && !(segments[0].Length >= 2 && char.IsAsciiLetter(segments[0][0]) && segments[0][1] == ':');
        var name = string.Join('/', segments);
        return valid && name.Length <= MaxFileNameLength ? name : throw AdminFaults.Field(FileNameHeader, AdminErrorCodes.InvalidValue);
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

/// <summary>
/// An upload slot as the Files list shows it: its path, size, state with its reason, the document, who and when.
/// <see cref="ScannedAt"/> is when its current bytes passed a malware scan; null for a slot that was not scanned (#535).
/// </summary>
public sealed record UploadFileDto(Guid Id, string FileName, long Bytes, string State, string? Reason, Guid? DocumentId, string? UploadedBy, DateTimeOffset UploadedAt,
    DateTimeOffset? ReplacedAt, DateTimeOffset? DeletedAt, DateTimeOffset? ScannedAt)
{
    internal static UploadFileDto Of(Upload u) =>
        new(u.Id, u.FileName, u.Bytes, u.State, u.Reason, u.DocumentId, u.UploadedBy, u.UploadedAt, u.ReplacedAt, u.DeletedAt, u.ScannedAt);
}

/// <summary>The list's <c>state</c> filter (admin design §5.5.7): each value and the <see cref="UploadStates"/> it covers.</summary>
public static class FileStates
{
    /// <summary>On the way to <c>indexed</c>: received, scanned clean, or passed without a scan (<c>ClamAv:Mode = off</c>, #535).</summary>
    public static IReadOnlyList<string> Processing { get; } = [UploadStates.Received, UploadStates.Clean, UploadStates.Unscanned];
    public static IReadOnlyList<string> Indexed { get; } = [UploadStates.Indexed];
    public static IReadOnlyList<string> Failed { get; } = [UploadStates.Refused, UploadStates.Malware];

    public static IReadOnlyList<string>? Of(string filter) => filter switch
    {
        "processing" => Processing,
        "indexed" => Indexed,
        "failed" => Failed,
        _ => null,
    };
}

/// <summary>The list's <c>sort</c>: <c>name</c> (path order, default) or <c>uploaded</c> (newest first).</summary>
public static class FileSorts
{
    public const string Name = "name";
    public const string Uploaded = "uploaded";

    public static IReadOnlyList<string> All { get; } = [Name, Uploaded];
}

/// <summary>A source's non-deleted files by state (§5.5.7 summary bar): processing, indexed, failed, their bytes and the last upload.</summary>
public sealed record FilesSummary(int Total, int Processing, int Indexed, int Failed, long Bytes, DateTimeOffset? LastUploadAt);

/// <summary>A quarantined file (connectors §11): where it came from, the signature, when; never the content or its hash.</summary>
public sealed record QuarantinedFileDto(Guid Id, string Origin, string? Name, string? Url, string Signature, Guid? RunId, DateTimeOffset DetectedAt, DateTimeOffset PurgeAfter);

public sealed record UploadBatchCreated(Guid BatchId, int MaxFiles, long MaxBytes);

public sealed record UploadedFile(int N, Guid UploadId, string Operation, string FileName, long Bytes, string Sha256);

public sealed record UploadCommitted(Guid BatchId, Guid JobId, int Entries);

public sealed record FileRestoreRequest : AdminRequest
{
    /// <summary>Deleted upload slots of the source to restore (connectors §9.3).</summary>
    [MaxLength(UploadEndpoints.MaxFilesPerBatch)]
    public IReadOnlyList<Guid> UploadIds { get; init; } = [];
}

public sealed record UploadCommitRequest : AdminRequest
{
    /// <summary>The numbers of the batch's files to commit.</summary>
    [MaxLength(UploadEndpoints.MaxFilesPerBatch)]
    public IReadOnlyList<int> Files { get; init; } = [];

    /// <summary>Upload slots of the source to delete (connectors §9.3).</summary>
    [MaxLength(UploadEndpoints.MaxFilesPerBatch)]
    public IReadOnlyList<Guid> Deletes { get; init; } = [];
}
