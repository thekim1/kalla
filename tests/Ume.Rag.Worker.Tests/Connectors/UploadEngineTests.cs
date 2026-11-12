using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// Connectors design §18 "ClamAV" and "Uploads" through the engine on the test database: committed batches of an
/// <c>upload</c> source (manifest and slot blobs as the admin writes them, <c>internal</c> ones envelope-encrypted, a
/// <c>sync</c> with <c>trigger</c> <c>upload</c>) go live; EICAR, encrypted, ends <c>malware</c> with its quarantine row
/// and blob, event 5102 and a degraded run; the same bytes in a second department are a second document whose embeddings
/// come from the cache; a replace keeps the document and swaps its set; a delete removes the document and an add on the
/// deleted slot revives it. An <c>internal</c> upload stored plain or with a damaged segment fails
/// <c>upload.blob_damaged</c> once; a <c>public</c> upload is stored as received. A <c>path</c> rule of the
/// <c>files</c> template matches the upload's display name and tags its chunks (processing design §7). Each slot ends in
/// the state its bytes' processing reached (#533): <c>indexed</c> with its document (identical bytes again too),
/// <c>refused</c> with the reason when the parse or the embed ended it. A passed scan gives the slot its <c>ScannedAt</c>,
/// which <c>indexed</c> keeps and a replace clears until the new bytes pass (§9.1).
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
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
            (await SlotAsync(a)).ShouldBe((UploadStates.Indexed, null, (await DocumentAsync(seed, a)).Id));
            var stored = File.ReadAllBytes(UploadPath(seed, a, note));
            Encoding.ASCII.GetString(stored, 0, 4).ShouldBe("URB2", "an internal upload is stored encrypted, as the admin writes it");
            Contains(stored, marker).ShouldBeFalse("the stored upload holds no plaintext");
            Encoding.ASCII.GetString(File.ReadAllBytes(UploadPath(seed, virus, Eicar)), 0, 4).ShouldBe("URB2", "the EICAR upload is encrypted, so the finding is on the plaintext");
            var sha = Convert.ToHexStringLower(SHA256.HashData(Eicar));
            (await Sql.ScalarAsync<long>(database.Connection(null), """
                SELECT count(*) FROM content."QuarantinedFiles"
                WHERE "SourceId" = $1 AND "RunId" = $2 AND "Origin" = 'upload' AND "Name" = 'virus.pdf' AND "Sha256" = $3 AND "Signature" = $4
                """, seed.SourceId, run, Convert.FromHexString(sha), EicarOnlyScanner.Signature)).ShouldBe(1);
            Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_blobs.FullName, "quarantine", seed.SourceId.ToString("D"), sha)), 0, 4).ShouldBe("URB2");
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
            (await SlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, documentId));

            var replaced = await CommitAsync(seed, (UploadOperation.Replace, slot, "avfall.md", Note("Avfall " + marker, "Nya hämtdagar gäller från mars.")));

            (await EngineKit.WaitForRunAsync(database, replaced)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            var (sameId, secondSet) = await DocumentAsync(seed, slot);
            sameId.ShouldBe(documentId);
            secondSet.ShouldNotBe(firstSet);
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", firstSet!.Value))
                .ShouldBe("superseded");
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
            (await SlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, documentId));

            // The same bytes again (connectors design §9.3): unchanged, and the slot, received again by the batch, ends indexed.
            var again = await CommitAsync(seed, (UploadOperation.Replace, slot, "avfall.md", Note("Avfall " + marker, "Nya hämtdagar gäller från mars.")));

            (await EngineKit.WaitForRunAsync(database, again)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 1, 0, 0, 0));
            (await DocumentAsync(seed, slot)).ShouldBe((documentId, secondSet));
            (await SlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, documentId));

            var deleted = await CommitAsync(seed, (UploadOperation.Delete, slot, null, null));

            (await EngineKit.WaitForRunAsync(database, deleted)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 0, 1, 0, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Reason" FROM content."RunItems" WHERE "RunId" = $1 AND "DocumentId" = $2 AND "Outcome" = 'deleted'
                """, deleted, documentId)).ShouldBe("upload.deleted");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);

            var restored = await CommitAsync(seed, (UploadOperation.Add, slot, "avfall.md", Note("Avfall " + marker, "Nya hämtdagar gäller från mars.")));

            (await EngineKit.WaitForRunAsync(database, restored)).State.ShouldBe(RunStates.Succeeded);
            (await DocumentAsync(seed, slot)).Id.ShouldBe(documentId);
            (await SlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, documentId));
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(2);
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_scanned_upload_keeps_its_ScannedAt_when_indexed_and_a_replace_clears_it_until_the_new_bytes_pass_the_scan()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await SeedUploadAsync();
        var scanner = new GatedScanner();
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs,
            services: services => services.AddSingleton<IMalwareScanner>(scanner));
        try
        {
            var slot = Guid.NewGuid();
            var before = DateTime.UtcNow;
            var added = await CommitAsync(seed, (UploadOperation.Add, slot, "avfall.md", Note("Avfall " + marker)));

            (await EngineKit.WaitForRunAsync(database, added)).ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            var (state, first) = await ScanAsync(slot);
            state.ShouldBe(UploadStates.Indexed);
            first.ShouldNotBeNull("the scan passed, and indexed keeps its time").ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
            (await Sql.ScalarAsync<string?>(database.Connection(null), """SELECT "Report" ->> 'malwareScan' FROM content."Runs" WHERE "Id" = $1""", added))
                .ShouldBeNull("with the scan on the report has no malwareScan field");

            // The replace's scan waits: the slot holds the new bytes, received, with no ScannedAt.
            scanner.Arm();
            var newBytes = Note("Avfall " + marker, "Nya hämtdagar gäller från mars.");
            var replaced = await CommitAsync(seed, (UploadOperation.Replace, slot, "avfall.md", newBytes));
            await scanner.Blocked.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            (await ScanAsync(slot)).ShouldBe((UploadStates.Received, (DateTime?)null));
            (await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "Sha256" FROM content."Uploads" WHERE "Id" = $1""", slot))
                .ShouldBe(SHA256.HashData(newBytes));

            scanner.Open();
            (await EngineKit.WaitForRunAsync(database, replaced)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            var (after, second) = await ScanAsync(slot);
            after.ShouldBe(UploadStates.Indexed);
            second.ShouldNotBeNull().ShouldBeGreaterThan(first.Value, "the new bytes passed a scan of their own");
        }
        finally
        {
            scanner.Open();
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_internal_upload_stored_plain_or_with_a_damaged_segment_fails_upload_blob_damaged_without_retries()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await SeedUploadAsync();
        var (good, plain, damaged) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (goodNote, plainNote, damagedNote) = (Note("Skolval " + marker), Note("Färdtjänst " + marker), Note("Parkering " + marker));
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        try
        {
            var run = await CommitAsync(seed,
                (UploadOperation.Add, good, "skolval.md", goodNote),
                (UploadOperation.Add, plain, "fardtjanst.md", plainNote),
                (UploadOperation.Add, damaged, "parkering.md", damagedNote));
            // Before the worker starts: one upload stored as received (no URB2), one with a changed byte in its segment.
            File.WriteAllBytes(UploadPath(seed, plain, plainNote), plainNote);
            var stored = File.ReadAllBytes(UploadPath(seed, damaged, damagedNote));
            stored[^17] ^= 0x01; // the last ciphertext byte of the final segment, before its 16-byte tag
            File.WriteAllBytes(UploadPath(seed, damaged, damagedNote), stored);
            await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Degraded, 1, 0, 0, 0, 2, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg("Identity" || ' ' || "Reason", E'\n' ORDER BY "Identity") FROM content."RunItems" WHERE "RunId" = $1 AND "Outcome" = 'failed'
                """, run)).ShouldBe(string.Join('\n', new[] { UploadConnector.IdentityOf(plain), UploadConnector.IdentityOf(damaged) }
                .Order(StringComparer.Ordinal).Select(i => $"{i} {UploadConnector.BlobDamaged}")));
            (await SlotStateAsync(plain)).ShouldBe(UploadStates.Refused);
            (await SlotStateAsync(damaged)).ShouldBe(UploadStates.Refused);
            (await SlotStateAsync(good)).ShouldBe(UploadStates.Indexed);
            foreach (var slot in new[] { plain, damaged })
            {
                (await Sql.ScalarAsync<string>(database.Connection(null), """
                    SELECT string_agg("State" || ':' || "Attempts", ',' ORDER BY "Id") FROM content."Jobs"
                    WHERE "RunId" = $1 AND "Class" = 'fetch' AND "Payload" ->> 'uploadId' = $2
                    """, run, slot.ToString("D"))).ShouldBe("succeeded:1", "a damaged upload fails once; it is not retried");
            }

            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(1);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_public_upload_is_stored_as_received_and_goes_live()
    {
        var marker = Guid.NewGuid().ToString("N");
        var note = Note("Badplatser " + marker);
        var seed = await SeedUploadAsync(Classification.Public);
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            var slot = Guid.NewGuid();
            var run = await CommitAsync(seed, (UploadOperation.Add, slot, "badplatser.md", note));

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            File.ReadAllBytes(UploadPath(seed, slot, note)).ShouldBe(note, "a public upload is not envelope-encrypted");
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(1);
            (await SlotAsync(slot)).ShouldBe((UploadStates.Indexed, null, (await DocumentAsync(seed, slot)).Id));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_file_whose_parse_ends_without_text_ends_refused_with_the_reason_and_its_document()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await SeedUploadAsync();
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            var (empty, good) = (Guid.NewGuid(), Guid.NewGuid());
            var run = await CommitAsync(seed,
                (UploadOperation.Add, empty, "rubrik.md", Encoding.UTF8.GetBytes($"# Bara en rubrik {marker}\n")),
                (UploadOperation.Add, good, "taxa.md", Note("Taxa " + marker)));

            (await EngineKit.WaitForRunAsync(database, run)).State.ShouldNotBe(RunStates.Running);
            (await SlotAsync(empty)).ShouldBe((UploadStates.Refused, "parse.empty", (await DocumentAsync(seed, empty)).Id));
            (await SlotAsync(good)).ShouldBe((UploadStates.Indexed, null, (await DocumentAsync(seed, good)).Id));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_file_whose_embed_fails_ends_refused_with_the_reason()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await SeedUploadAsync();
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs,
            services: services => services.AddSingleton<IEmbeddingProfileStore, NoEmbeddingProfiles>());
        try
        {
            var slot = Guid.NewGuid();
            var run = await CommitAsync(seed, (UploadOperation.Add, slot, "avfall.md", Note("Avfall " + marker)));

            (await EngineKit.WaitForRunAsync(database, run)).Failed.ShouldBe(1);
            (await DocumentAsync(seed, slot)).LiveSet.ShouldBeNull();
            (await SlotAsync(slot)).ShouldBe((UploadStates.Refused, "embed.profile_unknown", (await DocumentAsync(seed, slot)).Id));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_path_rule_on_the_display_name_tags_the_chunks_of_a_matching_upload()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await SeedUploadAsync();
        await EngineKit.PublishAsync(database, seed, "files", """{"pathRules":[{"match":"bygglov-*.md","on":"path","tags":["Bygglov"]}]}""");
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            var run = await CommitAsync(seed,
                (UploadOperation.Add, Guid.NewGuid(), "bygglov-ansokan.md", Note("Ansökan " + marker)),
                (UploadOperation.Add, Guid.NewGuid(), "avfall.md", Note("Avfall " + marker)));

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 2, 0, 0, 0, 0, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg(DISTINCT d."DisplayName" || '=' || array_to_string(c."Tags", ','), ';'
                                  ORDER BY d."DisplayName" || '=' || array_to_string(c."Tags", ','))
                FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
                WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL
                """, seed.SourceId)).ShouldBe("avfall.md=;bygglov-ansokan.md=bygglov",
                "the rule's glob matches the file name, so only that upload's chunks carry the (lower-cased) topic");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private string UploadPath(SourceSeed seed, Guid slot, byte[] bytes) =>
        Path.Combine(_blobs.FullName, UploadConnector.BlobKeyOf(seed.SourceId, slot, Convert.ToHexStringLower(SHA256.HashData(bytes))).Value);

    private static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static byte[] Note(string title, string extra = "") => Encoding.UTF8.GetBytes(
        $"# {title}\n\nUmeå kommun ansvarar för {title}. Här finns information om hur du ansöker och vem du kontaktar.\n\n" +
        $"## Så ansöker du\n\nDu ansöker via e-tjänsten. Handläggningstiden är normalt fyra veckor. {extra}\n");

    private Task<SourceSeed> SeedUploadAsync(string level = Classification.Internal) => EngineKit.SeedUploadAsync(database, level);

    private Task<Guid> CommitAsync(SourceSeed seed, params (UploadOperation Operation, Guid Slot, string? Name, byte[]? Bytes)[] files) =>
        UploadKit.CommitAsync(database, _blobs, seed, files);

    private Task<string> SlotStateAsync(Guid slot) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Uploads" WHERE "Id" = $1""", slot);

    /// <summary>The slot's state and <c>ScannedAt</c> (connectors design §9.1).</summary>
    private async Task<(string State, DateTime? ScannedAt)> ScanAsync(Guid slot) =>
        (await SlotStateAsync(slot), await Sql.ScalarAsync<DateTime?>(database.Connection(null), """SELECT "ScannedAt" FROM content."Uploads" WHERE "Id" = $1""", slot));

    /// <summary>The slot's state, reason and document, as the worker wrote them (#533).</summary>
    private async Task<(string State, string? Reason, Guid? Document)> SlotAsync(Guid slot)
    {
        var row = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "State" || '|' || coalesce("Reason", '') || '|' || coalesce("DocumentId"::text, '') FROM content."Uploads" WHERE "Id" = $1
            """, slot);
        var parts = row.Split('|');
        return (parts[0], parts[1].Length == 0 ? null : parts[1], parts[2].Length == 0 ? null : Guid.Parse(parts[2]));
    }

    private async Task<(Guid Id, Guid? LiveSet)> DocumentAsync(SourceSeed seed, Guid slot)
    {
        var id = await Sql.ScalarAsync<Guid>(database.Connection(null),
            """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2""", seed.SourceId, UploadConnector.IdentityOf(slot));
        var set = await Sql.ScalarAsync<Guid?>(database.Connection(null), """SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", id);
        return (id, set);
    }
}

/// <summary>The development <see cref="EicarOnlyScanner"/>, whose scans wait while it is armed until <see cref="Open"/>.</summary>
internal sealed class GatedScanner : IMalwareScanner
{
    private readonly EicarOnlyScanner _inner = new();
    private volatile TaskCompletionSource _open = Opened();
    private volatile TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a scan waits at the armed gate.</summary>
    public Task Blocked => _blocked.Task;

    public void Arm()
    {
        _blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Open() => _open.TrySetResult();

    public async Task<ScanResult> ScanAsync(Stream content, string origin, CancellationToken cancellationToken)
    {
        var gate = _open;
        if (!gate.Task.IsCompleted)
        {
            _blocked.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        return await _inner.ScanAsync(content, origin, cancellationToken);
    }

    public Task<SignatureState> GetSignaturesAsync(CancellationToken cancellationToken) => _inner.GetSignaturesAsync(cancellationToken);

    private static TaskCompletionSource Opened()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>No embedding profile is known: every <c>embed</c> job ends <c>failed</c> <c>embed.profile_unknown</c>.</summary>
internal sealed class NoEmbeddingProfiles : IEmbeddingProfileStore
{
    public Task<EmbeddingProfileRecord?> GetAsync(Guid profileId, CancellationToken cancellationToken) => Task.FromResult<EmbeddingProfileRecord?>(null);

    public Task<IReadOnlyList<EmbeddingProfileRecord>> ActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EmbeddingProfileRecord>>([]);
}
