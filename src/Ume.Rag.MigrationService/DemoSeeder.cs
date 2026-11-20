using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;

namespace Ume.Rag.MigrationService;

/// <summary>
/// The demo content of repo-and-deployment §4.4 "Seed", run after <see cref="DevSeeder"/> when <c>Seed:DemoDirectory</c>
/// is set (the AppHost sets it to <c>tests/evaluation/smoke</c> when it runs the FakeGateway, whose catalog the department
/// comes from). On a database without collections it creates the department Kommunledningskontoret and its team
/// Digitalisering &amp; AI with the fake catalog's ids (the organisation sync then keeps them), the <c>public</c> collection
/// <see cref="CollectionName"/> with an upload source holding the smoke documents (their blobs, the batch manifest and the
/// upload sync, as the admin's commit writes them, so the worker indexes them on its first start), and the published
/// <c>public</c> assistant <see cref="AssistantName"/> offered to the dev Open WebUI client. The evaluation set needs the
/// indexed documents: a later start adds the 30 smoke questions once every expected document is searchable.
/// </summary>
public static class DemoSeeder
{
    /// <summary><c>FakeCatalog.KommunledningskontoretId</c> (cost center 1000).</summary>
    public static readonly Guid DepartmentId = new("019a0000-0000-7000-8000-000000001000");

    /// <summary><c>FakeCatalog.DigitaliseringTeamId</c>.</summary>
    public static readonly Guid TeamId = new("019a0000-0000-7000-8000-000000001001");

    public static readonly Guid CollectionId = new("01929b6e-0000-7000-8000-0000000de4c0");
    public static readonly Guid SourceId = new("01929b6e-0000-7000-8000-0000000de4c1");
    public static readonly Guid AssistantId = new("01929b6e-0000-7000-8000-0000000de4a0");

    public const string CollectionName = "Umeå kommun – exempel";
    public const string AssistantName = "Demo-assistent";

    /// <summary>A FakeGateway chat model served from the EU and on-prem, so any key or ceiling serves it.</summary>
    public const string ChatModel = "ume/chat-standard";

    /// <summary>
    /// FakeGateway's feature-hashing embeddings put a question's best chunk 0.33–0.78 away, so the smoke set's coverage
    /// distance (<c>SmokeSet.RetrievalSettings</c>), not the BGE-M3 default of 0.50, which answers most questions "not found".
    /// </summary>
    public const string RetrievalSettings = """{"coverageMaxDistance":0.85}""";

    public enum Outcome
    {
        Skipped,
        Created,
        EvaluationSetAdded,
        EvaluationSetPending,
        Done,
    }

    public static async Task<Outcome> SeedAsync(RagDbContext db, IBlobStore blobs, IJobQueue jobs, string directory, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(clock);
        if (!await db.Collections.AnyAsync(cancellationToken))
        {
            await CreateAsync(db, blobs, jobs, directory, clock, cancellationToken);
            return Outcome.Created;
        }

        return await EvaluationSetAsync(db, directory, clock, cancellationToken);
    }

    private static async Task CreateAsync(RagDbContext db, IBlobStore blobs, IJobQueue jobs, string directory, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pipeline = await DefaultUploadPipelineAsync(db, ct);

        // The files first, as the admin's commit does: blobs are write-once, so each attempt has its own batch and slots.
        var batchId = Guid.CreateVersion7();
        var entries = new List<UploadManifestEntry>();
        foreach (var path in Documents(directory))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var slot = Guid.CreateVersion7();
            await blobs.PutAsync(new BlobKey($"uploads/{SourceId:D}/{slot:D}/{sha}"), new MemoryStream(bytes), bytes.Length, ct);
            entries.Add(new UploadManifestEntry
            {
                Operation = UploadOperation.Add, UploadId = slot, FileName = Path.GetFileName(path), DeclaredMediaType = "text/markdown", Bytes = bytes.Length, Sha256 = sha,
            });
        }

        var manifest = new UploadBatchManifest { BatchId = batchId, SourceId = SourceId, Entries = entries, CommittedBy = SettingsSeeder.Actor, CommittedAt = now };
        await blobs.PutAsync(new BlobKey($"uploads/{SourceId:D}/batches/{batchId:D}.json"),
            new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options)), null, ct);

        // One config write, as the admin's: the version lock, the rows, the upload sync in the same transaction.
        await db.ExecuteConfigWriteAsync(async (_, attempt) =>
        {
            if (!await db.Departments.AnyAsync(d => d.Id == DepartmentId, attempt))
            {
                db.Departments.Add(new Department { Id = DepartmentId, Name = "Kommunledningskontoret", CostCenterCode = "1000", SyncedAt = now });
            }

            if (!await db.Teams.AnyAsync(t => t.Id == TeamId, attempt))
            {
                db.Teams.Add(new Team { Id = TeamId, DepartmentId = DepartmentId, Name = "Digitalisering & AI", Code = "digitalisering-ai", SyncedAt = now });
            }

            db.Collections.Add(new Collection
            {
                Id = CollectionId,
                Slug = "umea-kommun-exempel",
                Name = CollectionName,
                DepartmentId = DepartmentId,
                OwnerTeamId = TeamId,
                Classification = Classification.Public,
                EmbeddingProfileId = DevSeeder.EmbeddingProfileId,
                RetrievalSettings = RetrievalSettings,
                PublishedAt = now,
                ContactName = "Kontaktcenter",
                ContactEmail = "kontaktcenter@umea.se",
                UpdatedAt = now,
                UpdatedBy = SettingsSeeder.Actor,
            });
            db.Sources.Add(new Source
            {
                Id = SourceId,
                CollectionId = CollectionId,
                Type = Domain.Entities.SourceTypes.Upload,
                Name = "Exempeldokument",
                Classification = Classification.Public,
                PipelineId = pipeline,
                ContainsThirdPartyText = false,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = SettingsSeeder.Actor,
            });

            var assistant = new Assistant
            {
                Id = AssistantId,
                Slug = "demo-assistent",
                Name = AssistantName,
                Description = "Svarar på frågor om kommunens tjänster utifrån exempeldokumenten.",
                ContactName = "Kontaktcenter",
                ContactEmail = "kontaktcenter@umea.se",
                DepartmentId = DepartmentId,
                OwnerTeamId = TeamId,
                Classification = Classification.Public,
                UpdatedAt = now,
                UpdatedBy = SettingsSeeder.Actor,
            };
            db.Assistants.Add(assistant);
            await db.SaveChangesAsync(ct);
            var version = new AssistantVersion
            {
                AssistantId = AssistantId,
                Number = 1,
                State = AssistantVersionStates.Published,
                GatewayModel = ChatModel,
                SystemPrompt = await DefaultSystemPromptAsync(db, attempt),
                CollectionLinks = [CollectionLink.Whole(CollectionId)],
                CreatedBy = SettingsSeeder.Actor,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = SettingsSeeder.Actor,
                PublishedBy = SettingsSeeder.Actor,
                PublishedAt = now,
            };
            db.AssistantVersions.Add(version);
            await db.SaveChangesAsync(ct);
            assistant.PublishedVersionId = version.Id;
            if (await db.Clients.AnyAsync(c => c.Id == DevSeeder.DevClientId, attempt))
            {
                db.AssistantAudiences.Add(new AssistantAudience { AssistantId = AssistantId, SubjectId = DevSeeder.DevClientId });
            }

            await db.SaveChangesAsync(ct);

            var sync = new JobRequest(JobClasses.Sync, DepartmentId, Classification.Public,
                JsonSerializer.Serialize(new SyncPayload { SourceId = SourceId, Kind = SyncKind.Sync, Trigger = RunTrigger.Upload, BatchId = batchId }, ContractJson.Options))
            {
                DedupKey = JobDedupKeys.SyncUpload(batchId),
                TeamId = TeamId,
            };
            await jobs.EnqueueAsync([sync], db.Database.CurrentTransaction!.GetDbTransaction(), attempt);
            return true;
        }, ct);
    }

    /// <summary>
    /// The 30 smoke questions as the collection's human evaluation set, once: when the demo collection has no set and every
    /// expected document is searchable (a live chunk set), each resolved from its file name through the upload slot.
    /// </summary>
    private static async Task<Outcome> EvaluationSetAsync(RagDbContext db, string directory, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.Collections.AnyAsync(c => c.Id == CollectionId && c.DeletedAt == null, ct) ||
            await db.EvaluationSets.AnyAsync(s => s.CollectionId == CollectionId, ct))
        {
            return Outcome.Done;
        }

        var documents = await (
                from upload in db.Uploads.AsNoTracking()
                where upload.SourceId == SourceId && upload.DeletedAt == null
                join document in db.Documents.AsNoTracking() on "upload:" + upload.Id.ToString() equals document.CanonicalIdentity
                where document.SourceId == SourceId && document.DeletedAt == null && document.LiveChunkSetId != null
                select new { upload.FileName, document.Id })
            .ToListAsync(ct);
        var byName = documents.GroupBy(d => d.FileName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var questions = await QuestionsAsync(directory, ct);
        if (questions.SelectMany(q => q.Expected).Any(name => !byName.ContainsKey(name)))
        {
            return Outcome.EvaluationSetPending;
        }

        var set = new EvaluationSet { CollectionId = CollectionId, Classification = Classification.Public, CreatedAt = clock.GetUtcNow() };
        db.EvaluationSets.Add(set);
        db.EvaluationQuestions.AddRange(questions.Select(q => new EvaluationQuestion
        {
            SetId = set.Id,
            Classification = Classification.Public,
            Layer = EvaluationLayers.Human,
            Question = q.Question,
            ExpectedDocumentIds = [.. q.Expected.Select(name => byName[name])],
            CreatedBy = SettingsSeeder.Actor,
        }));
        await db.SaveChangesAsync(ct);
        return Outcome.EvaluationSetAdded;
    }

    /// <summary>The documents of <c>documents/</c>, in name order.</summary>
    public static IReadOnlyList<string> Documents(string directory) =>
        [.. Directory.EnumerateFiles(Path.Combine(directory, "documents"), "*.md").Order(StringComparer.Ordinal)];

    public static async Task<IReadOnlyList<SmokeQuestion>> QuestionsAsync(string directory, CancellationToken ct)
    {
        await using var stream = File.OpenRead(Path.Combine(directory, "questions.json"));
        var file = await JsonSerializer.DeserializeAsync<SmokeQuestions>(stream, JsonOptions, ct) ?? throw new JsonException("Empty questions.json.");
        return file.Questions;
    }

    /// <summary>The pipeline the admin gives a new upload source: the first published platform pipeline of an upload template by name.</summary>
    private static async Task<Guid> DefaultUploadPipelineAsync(RagDbContext db, CancellationToken ct)
    {
        string[] templates =
            [.. TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == Domain.Entities.SourceTypes.Upload).Select(t => t.Template.Name).Distinct()];
        return await db.Pipelines.AsNoTracking()
                .Where(p => templates.Contains(p.TemplateId) && p.PublishedVersionId != null && p.DepartmentId == null)
                .OrderBy(p => p.Name).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No published platform pipeline for upload sources.");
    }

    /// <summary><c>Settings.Chat.defaultSystemPrompt</c>, as the admin starts an assistant's draft from it; the code default when unset.</summary>
    private static async Task<string> DefaultSystemPromptAsync(RagDbContext db, CancellationToken ct)
    {
        var chat = await db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct);
        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(chat) ? "{}" : chat);
        return parsed.RootElement.ValueKind == JsonValueKind.Object &&
               parsed.RootElement.TryGetProperty(ChatDefaults.SystemPromptKey, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : ChatDefaults.SystemPrompt;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record SmokeQuestion(string Id, string Question, IReadOnlyList<string> Expected);

    private sealed record SmokeQuestions(IReadOnlyList<SmokeQuestion> Questions);
}
