using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.Infrastructure.Blobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Uploads;
using Kalla.Worker.Tests.Blobs;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>Uploads by slot (connectors design §9, §11, §18 "Uploads"): type sniffing, the batch plan, the upload fetch stage.</summary>
public sealed class UploadTests : IDisposable
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ume-rag-upload-tests-");
    private readonly FileSystemBlobStore _blobs;

    public UploadTests() =>
        _blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root.FullName }), TimeProvider.System);

    public void Dispose() => _root.Delete(recursive: true);

    public static TheoryData<string, string, string?, bool, string?> Files() => new()
    {
        // name, kind of bytes, declared type, allowed, reason
        { "beslut.pdf", "pdf", "application/pdf", true, null },
        { "beslut.docx", "pdf", FileTypeSniffer.Docx, true, null },
        { "rapport.docx", "docx", FileTypeSniffer.Docx, true, null },
        { "tabell.xlsx", "xlsx", null, true, null },
        { "text.odt", "odt", null, true, null },
        { "karta.png", "png", "image/png", true, null },
        { "anteckning.md", "text", "text/markdown", true, null },
        { "sida.html", "html", "text/html", true, null },
        { "faktura.pdf", "exe", "application/pdf", false, UploadTypeCheck.TypeMismatch },
        { "program.exe", "exe", "application/x-msdownload", false, UploadTypeCheck.TypeUnknown },
        { "makro.docm", "docm", "application/vnd.ms-word.document.macroEnabled.12", false, UploadTypeCheck.MacroEnabled },
        { "makro.docx", "docm", FileTypeSniffer.Docx, false, UploadTypeCheck.TypeMismatch },
        { "arkiv.zip", "zip", "application/zip", false, UploadTypeCheck.Archive },
        { "gammal.doc", "doc", "application/msword", true, null },
        { "brev.rtf", "rtf", "application/rtf", true, null },
        { "mejl.msg", "msg", "application/vnd.ms-outlook", false, UploadTypeCheck.FormatDeferred },
        { "mejl.eml", "eml", "message/rfc822", false, UploadTypeCheck.FormatDeferred },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void The_sniffed_type_wins_and_refused_types_get_their_reason(string name, string kind, string? declared, bool allowed, string? reason)
    {
        using var file = new MemoryStream(Bytes(kind));

        var verdict = UploadTypeCheck.Check(FileTypeSniffer.Sniff(file, name), name, declared);

        verdict.Allowed.ShouldBe(allowed);
        verdict.Reason.ShouldBe(reason);
    }

    [Fact]
    public void A_pdf_named_docx_is_parsed_as_pdf_with_the_difference_recorded()
    {
        using var file = new MemoryStream(Bytes("pdf"));

        UploadTypeCheck.Check(FileTypeSniffer.Sniff(file, "beslut.docx"), "beslut.docx", FileTypeSniffer.Docx)
            .ShouldBe(new TypeVerdict(true, "application/pdf", null, DeclaredDiffers: true));
    }

    [Fact]
    public void A_folder_path_is_typed_by_its_file_name()
    {
        // admin design §4.5: the slot's file name may be a relative path; the extension is the last segment's, never a folder's.
        using var text = new MemoryStream(Bytes("text"));
        UploadTypeCheck.Check(FileTypeSniffer.Sniff(text, "Rutiner.v2/HR/anteckning.md"), "Rutiner.v2/HR/anteckning.md", "text/markdown")
            .ShouldBe(new TypeVerdict(true, "text/markdown", null, DeclaredDiffers: false));
        using var pdf = new MemoryStream(Bytes("pdf"));
        UploadTypeCheck.Check(FileTypeSniffer.Sniff(pdf, "HR/Policys/semester.pdf"), "HR/Policys/semester.pdf", "application/pdf")
            .ShouldBe(new TypeVerdict(true, "application/pdf", null, DeclaredDiffers: false));
    }

    [Fact]
    public void A_batch_adds_replaces_and_deletes_slots_and_skips_a_duplicate_add()
    {
        var sourceId = Guid.NewGuid();
        var existing = Slot(sourceId, "a.pdf", Sha("a"));
        var other = Slot(sourceId, "b.pdf", Sha("b"));
        var manifest = new UploadBatchManifest
        {
            BatchId = Guid.NewGuid(),
            SourceId = sourceId,
            CommittedBy = "admin@umea.se",
            Entries =
            [
                new UploadManifestEntry { Operation = UploadOperation.Add, UploadId = Guid.NewGuid(), FileName = "ny.pdf", Bytes = 10, Sha256 = Sha("new") },
                new UploadManifestEntry { Operation = UploadOperation.Add, UploadId = Guid.NewGuid(), FileName = "kopia.pdf", Bytes = 10, Sha256 = Sha("a") },
                new UploadManifestEntry { Operation = UploadOperation.Replace, UploadId = existing.Id, FileName = "a.pdf", Bytes = 11, Sha256 = Sha("a2") },
                new UploadManifestEntry { Operation = UploadOperation.Delete, UploadId = other.Id },
                new UploadManifestEntry { Operation = UploadOperation.Replace, UploadId = Guid.NewGuid(), FileName = "x.pdf", Bytes = 1, Sha256 = Sha("x") },
            ],
        };

        var plan = UploadBatchPlanner.Plan(manifest, "internal", [existing, other], Now);

        plan.Fetches.Select(f => f.CanonicalIdentity).ShouldBe([UploadConnector.IdentityOf(manifest.Entries[0].UploadId), existing.Identity]);
        plan.Fetches.ShouldAllBe(f => f.Kind == IdentityKind.Upload && f.Url == null);
        plan.Skipped.Select(s => s.Reason).ShouldBe([UploadBatchPlanner.Duplicate, UploadBatchPlanner.UnknownSlot]);
        plan.Deleted.Single().Id.ShouldBe(other.Id);
        plan.Deleted.Single().DeletedAt.ShouldBe(Now);
        var replaced = plan.Writes.Single(w => w.Id == existing.Id);
        (replaced.Sha256, replaced.ReplacedAt, replaced.DocumentId).ShouldBe((Sha("a2"), (DateTimeOffset?)Now, existing.DocumentId));
        plan.Writes.Single(w => w.Id == manifest.Entries[0].UploadId).UploadedBy.ShouldBe("admin@umea.se");

        // Re-planned after a crash once its slots were written: the added slot is its own, not a conflict, and is fetched again.
        var again = UploadBatchPlanner.Plan(manifest, "internal", plan.Writes, Now);
        again.Fetches.Select(f => f.CanonicalIdentity).ShouldContain(UploadConnector.IdentityOf(manifest.Entries[0].UploadId));
        again.Skipped.ShouldNotContain(s => s.Reason == UploadBatchPlanner.SlotInUse);
    }

    [Fact]
    public void An_add_on_a_deleted_slot_restores_it()
    {
        var sourceId = Guid.NewGuid();
        var deleted = Slot(sourceId, "a.pdf", Sha("a")) with { State = UploadState.Deleted, DeletedAt = Now.AddDays(-3) };
        var manifest = new UploadBatchManifest
        {
            BatchId = Guid.NewGuid(),
            SourceId = sourceId,
            Entries = [new UploadManifestEntry { Operation = UploadOperation.Add, UploadId = deleted.Id, FileName = "a.pdf", Bytes = 1, Sha256 = Sha("a") }],
        };

        var plan = UploadBatchPlanner.Plan(manifest, "internal", [deleted], Now);

        plan.Writes.Single().DeletedAt.ShouldBeNull();
        plan.Fetches.Single().CanonicalIdentity.ShouldBe(deleted.Identity);
    }

    [Fact]
    public async Task The_sync_reads_the_manifest_and_lists_the_files_to_fetch()
    {
        var sourceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = new UploadBatchManifest
        {
            BatchId = batchId,
            SourceId = sourceId,
            Entries = [new UploadManifestEntry { Operation = UploadOperation.Add, UploadId = Guid.NewGuid(), FileName = "a.pdf", Bytes = 1, Sha256 = Sha("a") }],
        };
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(ContractJson.Serialize(manifest))))
        {
            await _blobs.PutAsync(UploadConnector.ManifestKey(sourceId, batchId), stream, null, TestContext.Current.CancellationToken);
        }

        var connector = new UploadConnector(_blobs, new EicarOnlyScanner(), new InMemoryUploadSlots(), TimeProvider.System);
        var events = new List<ListingEvent<UploadCheckpoint>>();
        var context = new ListingContext(sourceId, "internal", Guid.NewGuid(), default) { BatchId = batchId };
        await foreach (var e in connector.ListAsync(context, new UploadCheckpoint(), TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<ListingEvent<UploadCheckpoint>.Item>().Single().Work.CanonicalIdentity.ShouldBe(UploadConnector.IdentityOf(manifest.Entries[0].UploadId));
        events[^1].ShouldBe(new ListingEvent<UploadCheckpoint>.Done(Completeness.Full));

        var missing = new List<ListingEvent<UploadCheckpoint>>();
        await foreach (var e in connector.ListAsync(context with { BatchId = Guid.NewGuid() }, new UploadCheckpoint(), TestContext.Current.CancellationToken))
        {
            missing.Add(e);
        }

        missing[^1].ShouldBe(new ListingEvent<UploadCheckpoint>.Done(Completeness.Partial(UploadConnector.ManifestMissing)));
    }

    [Fact]
    public async Task A_clean_upload_is_its_own_raw_content_and_eicar_goes_to_quarantine()
    {
        var sourceId = Guid.NewGuid();
        var connector = new UploadConnector(_blobs, new EicarOnlyScanner(), new InMemoryUploadSlots(), TimeProvider.System);
        var context = new FetchContext(sourceId, "internal", Guid.NewGuid(), default, false);

        var pdf = await StoreAsync(sourceId, "beslut.pdf", Bytes("pdf"));
        var content = (await connector.FetchAsync(context, pdf, TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Content>();
        content.Raw.Key.ShouldBe(UploadConnector.BlobKeyOf(sourceId, pdf.Hints.Upload!.UploadId, pdf.Hints.Upload.Sha256));
        content.Raw.ContentHash.ShouldBe(pdf.Hints.Upload.Sha256);
        content.Raw.MediaType.ShouldBe("application/pdf");
        content.Facts.CanonicalIdentity.ShouldBe(pdf.CanonicalIdentity);

        var virus = await StoreAsync(sourceId, "virus.pdf", Eicar);
        var quarantined = (await connector.FetchAsync(context, virus, TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Quarantined>();
        quarantined.Key.Value.ShouldBe($"quarantine/{sourceId:D}/{virus.Hints.Upload!.Sha256}");
        quarantined.Signature.ShouldBe(EicarOnlyScanner.Signature);
        (await _blobs.ExistsAsync(quarantined.Key, TestContext.Current.CancellationToken)).ShouldBeTrue();
        var row = QuarantineRecord.For(quarantined, context, "virus.pdf", null, Now);
        (row.Origin, row.Name, row.PurgeAfter, row.RunId).ShouldBe((QuarantineRecord.UploadOrigin, "virus.pdf", Now.AddDays(30), (Guid?)context.RunId));

        var exe = await StoreAsync(sourceId, "faktura.pdf", Bytes("exe"));
        (await connector.FetchAsync(context, exe, TestContext.Current.CancellationToken))
            .ShouldBe(new FetchOutcome.Refused(Worker.Connectors.RunItemOutcome.Unsupported, UploadTypeCheck.TypeMismatch));
    }

    [Fact]
    public async Task No_verdict_means_the_file_waits_and_a_changed_blob_is_refused()
    {
        var sourceId = Guid.NewGuid();
        var context = new FetchContext(sourceId, "internal", Guid.NewGuid(), default, false);
        var item = await StoreAsync(sourceId, "a.pdf", Bytes("pdf"));
        var down = new UploadConnector(_blobs, new ScriptedScanner(new ScanResult.Unavailable("clamav.unavailable", false)), new InMemoryUploadSlots(), TimeProvider.System);

        (await down.FetchAsync(context, item, TestContext.Current.CancellationToken))
            .ShouldBe(new FetchOutcome.Transient("clamav.unavailable", TimeSpan.FromSeconds(30), CountsAttempt: false));

        var wrongHash = item with { Hints = item.Hints with { Upload = item.Hints.Upload! with { Sha256 = Sha("other") } } };
        (await down.FetchAsync(context, wrongHash, TestContext.Current.CancellationToken)).ShouldBe(new FetchOutcome.Failed(UploadConnector.BlobMissing));
    }

    [Fact]
    public async Task An_internal_upload_written_through_the_envelope_is_read_as_plaintext_and_eicar_is_still_found()
    {
        var sourceId = Guid.NewGuid();
        var store = TestBlobKeys.Store(_root);
        var connector = new UploadConnector(store, new EicarOnlyScanner(), new InMemoryUploadSlots(), TimeProvider.System);
        var context = new FetchContext(sourceId, Classification.Internal, Guid.NewGuid(), default, false);

        var pdf = await SealAsync(sourceId, "beslut.pdf", Bytes("pdf"), Classification.Internal);
        StoredHead(sourceId, pdf).ShouldBe("URB2");
        var content = (await connector.FetchAsync(context, pdf, TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Content>();
        content.Raw.ContentHash.ShouldBe(pdf.Hints.Upload!.Sha256, "the SHA-256 is checked over the plaintext");
        content.Raw.MediaType.ShouldBe("application/pdf");
        content.Raw.Bytes.ShouldBe(Bytes("pdf").Length);

        var virus = await SealAsync(sourceId, "virus.pdf", Eicar, Classification.Internal);
        StoredHead(sourceId, virus).ShouldBe("URB2");
        var quarantined = (await connector.FetchAsync(context, virus, TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Quarantined>();
        quarantined.Signature.ShouldBe(EicarOnlyScanner.Signature, "the scanner sees the plaintext, not the ciphertext");
        Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_root.FullName, quarantined.Key.Value))[..4]).ShouldBe("URB2");
    }

    [Fact]
    public async Task An_internal_upload_stored_plain_or_with_a_damaged_segment_fails_and_a_public_upload_is_read_as_stored()
    {
        var sourceId = Guid.NewGuid();
        var store = TestBlobKeys.Store(_root);
        var connector = new UploadConnector(store, new EicarOnlyScanner(), new InMemoryUploadSlots(), TimeProvider.System);
        var context = new FetchContext(sourceId, Classification.Internal, Guid.NewGuid(), default, false);

        var plain = await SealAsync(sourceId, "beslut.pdf", Bytes("pdf"), Classification.Public);
        (await connector.FetchAsync(context, plain, TestContext.Current.CancellationToken)).ShouldBe(new FetchOutcome.Failed(UploadConnector.BlobDamaged));

        var damaged = await SealAsync(sourceId, "beslut.pdf", Bytes("pdf"), Classification.Internal);
        var path = Path.Combine(_root.FullName, UploadConnector.BlobKeyOf(sourceId, damaged.Hints.Upload!.UploadId, damaged.Hints.Upload.Sha256).Value);
        var stored = File.ReadAllBytes(path);
        stored[^17] ^= 0x01; // the last ciphertext byte of the final segment, before its 16-byte tag
        File.WriteAllBytes(path, stored);
        (await connector.FetchAsync(context, damaged, TestContext.Current.CancellationToken)).ShouldBe(new FetchOutcome.Failed(UploadConnector.BlobDamaged));

        var open = await SealAsync(sourceId, "beslut.pdf", Bytes("pdf"), Classification.Public);
        File.ReadAllBytes(Path.Combine(_root.FullName, UploadConnector.BlobKeyOf(sourceId, open.Hints.Upload!.UploadId, open.Hints.Upload.Sha256).Value))
            .ShouldBe(Bytes("pdf"));
        (await connector.FetchAsync(context with { Classification = Classification.Public }, open, TestContext.Current.CancellationToken))
            .ShouldBeOfType<FetchOutcome.Content>().Raw.ContentHash.ShouldBe(open.Hints.Upload.Sha256);
    }

    /// <summary>An upload as the admin writes it: through the envelope at the source's <paramref name="level"/>.</summary>
    private async Task<WorkItem> SealAsync(Guid sourceId, string name, byte[] bytes, string level)
    {
        var slot = Guid.NewGuid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using (var stream = new MemoryStream(bytes))
        {
            await TestBlobKeys.Envelope(_root).PutAsync(UploadConnector.BlobKeyOf(sourceId, slot, sha), level, stream, bytes.Length, TestContext.Current.CancellationToken);
        }

        return new WorkItem(IdentityKind.Upload, UploadConnector.IdentityOf(slot), null, 0, new WorkHints(Upload: new UploadHint(slot, sha, name, null)));
    }

    private string StoredHead(Guid sourceId, WorkItem item) => Encoding.ASCII.GetString(
        File.ReadAllBytes(Path.Combine(_root.FullName, UploadConnector.BlobKeyOf(sourceId, item.Hints.Upload!.UploadId, item.Hints.Upload.Sha256).Value))[..4]);

    private async Task<WorkItem> StoreAsync(Guid sourceId, string name, byte[] bytes)
    {
        var slot = Guid.NewGuid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using (var stream = new MemoryStream(bytes))
        {
            await _blobs.PutAsync(UploadConnector.BlobKeyOf(sourceId, slot, sha), stream, bytes.Length, TestContext.Current.CancellationToken);
        }

        return new WorkItem(IdentityKind.Upload, UploadConnector.IdentityOf(slot), null, 0, new WorkHints(Upload: new UploadHint(slot, sha, name, null)));
    }

    private static UploadSlot Slot(Guid sourceId, string name, string sha) => new()
    {
        Id = Guid.NewGuid(),
        SourceId = sourceId,
        Classification = "internal",
        BatchId = Guid.NewGuid(),
        FileName = name,
        Sha256 = sha,
        State = UploadState.Indexed,
        DocumentId = Guid.NewGuid(),
        UploadedAt = Now.AddDays(-10),
    };

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static byte[] Bytes(string kind) => kind switch
    {
        "pdf" => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n"),
        "png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13],
        "text" => Encoding.UTF8.GetBytes("# Rubrik\n\nÅäö text.\n"),
        "html" => Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><p>Hej</p></body></html>"),
        "exe" => [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0],
        "rtf" => Encoding.ASCII.GetBytes("{\\rtf1\\ansi Hej}"),
        "eml" => Encoding.ASCII.GetBytes("From: a@umea.se\r\nTo: b@umea.se\r\nSubject: Hej\r\n\r\nText\r\n"),
        "docx" => Ooxml("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", "/word/document.xml"),
        "xlsx" => Ooxml("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", "/xl/workbook.xml"),
        "docm" => Ooxml("application/vnd.ms-word.document.macroEnabled.main+xml", "/word/document.xml"),
        "odt" => Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", "<office:document-content/>")),
        "zip" => Zip(("readme.txt", "hej")),
        "doc" => Ole(new Guid("00020906-0000-0000-C000-000000000046")),
        "msg" => Ole(new Guid("00020D0B-0000-0000-C000-000000000046")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] Ooxml(string mainType, string part) => Zip(
        ("[Content_Types].xml", $"""<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="{part}" ContentType="{mainType}"/></Types>"""),
        (part.TrimStart('/'), "<x/>"));

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A compound file with 512-byte sectors whose directory starts at sector 0 and whose root entry has <paramref name="clsid"/>.</summary>
    private static byte[] Ole(Guid clsid)
    {
        var file = new byte[1024];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(file, 0);
        BitConverter.GetBytes((ushort)9).CopyTo(file, 30);
        BitConverter.GetBytes(0u).CopyTo(file, 48);
        clsid.ToByteArray().CopyTo(file, 512 + 80);
        return file;
    }
}
