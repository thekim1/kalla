using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using static Ume.Rag.Infrastructure.Persistence.RagDbContext;

namespace Ume.Rag.Infrastructure.Persistence.Content;

/// <summary>
/// The <c>content</c> tables of P1 ingestion (data-model §2.2; connectors design §2.3, §6.0, §9.1, §11, §13.1; issue
/// #34). Config rows are referenced by id without a foreign key. Raw SQL in the migration adds what EF cannot express:
/// <c>Chunks."Embedding"</c> and <c>"EmbeddingCache"."Embedding"</c> (<c>vector</c>), <c>EmbeddingProfiles."CanaryEmbedding"</c>,
/// the generated <c>Chunks."SearchVector"</c> with its partial GIN index, and <c>content.rag_ensure_profile_index</c>,
/// which creates each profile's partial HNSW index (data-model §3.2).
/// </summary>
internal static class IngestionContentModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeManifestEntry>(e =>
        {
            e.ToTable("NodeManifest", ContentSchema);
            e.HasKey(x => new { x.NodeType, x.TypeVersion });
            e.Property(x => x.NodeType).HasMaxLength(100);
            e.Property(x => x.ParamsSchema).HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            e.Property(x => x.FirstRegisteredAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WorkerNodeSupport>(e =>
        {
            e.ToTable("WorkerNodeSupport", ContentSchema);
            e.HasKey(x => new { x.InstanceId, x.NodeType, x.TypeVersion });
            e.Property(x => x.NodeType).HasMaxLength(100);
            // A clean stop deletes the instance; its support goes with it (engine design §5.2).
            e.HasOne<WorkerInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrawlHost>(e =>
        {
            e.ToTable("CrawlHosts", ContentSchema, t => ValuesCheck(t, "CrawlHosts", "Kind", CrawlHostKinds.All));
            e.HasKey(x => x.Host);
            e.Property(x => x.Host).HasMaxLength(253);
            e.Property(x => x.RequestsPerSecond).HasPrecision(5, 2);
            e.Property(x => x.RobotsBlobPath).HasMaxLength(1024);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.RobotsStatus).HasMaxLength(30);
            e.Property(x => x.BackoffFactor).HasPrecision(6, 2);
            // HOT updates of NextAllowedAt, vacuumed early (performance pass §3.2).
            e.HasStorageParameter("fillfactor", 50);
            HotTable(e);
        });

        modelBuilder.Entity<SiteProfile>(e =>
        {
            e.ToTable("SiteProfiles", ContentSchema, t => ClassificationCheck(t, "SiteProfiles"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Host).HasMaxLength(253);
            e.Property(x => x.PresetId).HasMaxLength(50);
            e.HasIndex(x => new { x.Classification, x.Host }).IsUnique();
        });

        modelBuilder.Entity<SiteProfileVersion>(e =>
        {
            e.ToTable("SiteProfileVersions", ContentSchema, t =>
            {
                ValuesCheck(t, "SiteProfileVersions", "Origin", SiteProfileOrigins.All);
                ValuesCheck(t, "SiteProfileVersions", "State", SiteProfileVersionStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Profile).HasColumnType("jsonb");
            e.Property(x => x.Origin).HasMaxLength(20);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.AppliedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.SiteProfileId, x.Number }).IsUnique();
            e.HasOne<SiteProfile>().WithMany().HasForeignKey(x => x.SiteProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrawlFrontierEntry>(e =>
        {
            e.ToTable("CrawlFrontier", ContentSchema, t =>
            {
                ClassificationCheck(t, "CrawlFrontier");
                ValuesCheck(t, "CrawlFrontier", "State", CrawlFrontierStates.All);
                // A removal-check row is marked by RemovalCheck, no longer by Depth = -1 (#102).
                t.HasCheckConstraint("CK_CrawlFrontier_Depth", "\"Depth\" >= 0");
            });
            // P1: one frontier per source (connectors design §6.0).
            e.HasKey(x => new { x.Classification, x.SourceId, x.UrlHash });
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Host).HasMaxLength(253);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.FetchUrl).HasMaxLength(2048);
            e.Property(x => x.ETag).HasMaxLength(500);
            e.Property(x => x.LastModified).HasMaxLength(100);
            e.HasIndex(x => new { x.Host, x.NextFetchAt }).HasFilter("\"State\" = 'pending'");
        });

        modelBuilder.Entity<CrawlRun>(e =>
        {
            e.ToTable("CrawlRuns", ContentSchema, t => ClassificationCheck(t, "CrawlRuns"));
            e.HasKey(x => x.RunId);
            e.Property(x => x.RunId).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.PausedHosts).HasColumnType("jsonb");
            e.Property(x => x.TrapStats).HasColumnType("jsonb");
            // HOT updates of Admitted (performance pass §3.2).
            e.HasStorageParameter("fillfactor", 50);
            HotTable(e);
        });

        modelBuilder.Entity<ConnectorCheckpoint>(e =>
        {
            e.ToTable("ConnectorCheckpoints", ContentSchema, t =>
            {
                ClassificationCheck(t, "ConnectorCheckpoints");
                ValuesCheck(t, "ConnectorCheckpoints", "Kind", ConnectorCheckpointKinds.All);
            });
            e.HasKey(x => new { x.SourceId, x.Kind });
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Checkpoint).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Upload>(e =>
        {
            e.ToTable("Uploads", ContentSchema, t =>
            {
                ClassificationCheck(t, "Uploads");
                ValuesCheck(t, "Uploads", "State", UploadStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.DeclaredMediaType).HasMaxLength(200);
            e.Property(x => x.SniffedMediaType).HasMaxLength(200);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.UploadedBy).HasMaxLength(200);
            e.HasIndex(x => x.SourceId);
        });

        modelBuilder.Entity<DeletionProposal>(e =>
        {
            e.ToTable("DeletionProposals", ContentSchema, t => ClassificationCheck(t, "DeletionProposals"));
            e.HasKey(x => new { x.RunId, x.DocumentId });
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(100);
            e.Property(x => x.Decision).HasMaxLength(20);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
            e.HasIndex(x => x.SourceId);
        });

        modelBuilder.Entity<QuarantinedFile>(e =>
        {
            e.ToTable("QuarantinedFiles", ContentSchema, t =>
            {
                ClassificationCheck(t, "QuarantinedFiles");
                ValuesCheck(t, "QuarantinedFiles", "Origin", QuarantineOrigins.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Signature).HasMaxLength(200);
            e.Property(x => x.Origin).HasMaxLength(10);
            e.Property(x => x.Name).HasMaxLength(300);
            e.HasIndex(x => x.SourceId);
            e.HasIndex(x => x.PurgeAfter);
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.ToTable("Documents", ContentSchema, t =>
            {
                ClassificationCheck(t, "Documents");
                ValuesCheck(t, "Documents", "IdentityKind", IdentityKinds.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.IdentityKind).HasMaxLength(20);
            e.Property(x => x.MediaType).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(20);
            e.Property(x => x.DisplayName).HasMaxLength(300);
            e.Property(x => x.ParseStrategy).HasMaxLength(30);
            e.Property(x => x.ParseOutcome).HasMaxLength(30);
            e.Property(x => x.PiiClearedBy).HasMaxLength(200);
            e.Property(x => x.PiiClearedCounts).HasColumnType("jsonb");
            e.Property(x => x.HeldBy).HasMaxLength(200);
            e.Property(x => x.ConsecutiveFailures).HasDefaultValue((short)0);
            e.HasIndex(x => new { x.SourceId, x.IdentityKind, x.IdentityHash }).IsUnique();
            e.HasIndex(x => x.PurgeAfter).HasFilter("\"DeletedAt\" IS NOT NULL");
            e.HasIndex(x => x.SourceId, "IX_Documents_SourceId_Live").HasFilter("\"DeletedAt\" IS NULL");
        });

        modelBuilder.Entity<NodeOutput>(e =>
        {
            e.ToTable("NodeOutputs", ContentSchema, t => ClassificationCheck(t, "NodeOutputs"));
            e.HasKey(x => x.CacheKey);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.NodeType).HasMaxLength(100);
            e.Property(x => x.BlobPath).HasMaxLength(1024);
            e.HasIndex(x => x.LastUsedAt);
        });

        modelBuilder.Entity<ChunkSet>(e =>
        {
            e.ToTable("ChunkSets", ContentSchema, t =>
            {
                ClassificationCheck(t, "ChunkSets");
                ValuesCheck(t, "ChunkSets", "State", ChunkSetStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.StageKeys).HasColumnType("jsonb");
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.PiiCounts).HasColumnType("jsonb");
            e.Property(x => x.ParseRuleKey).HasMaxLength(300);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            // The set's key; a page without a site profile version is one key (nulls not distinct), engine design §14.
            e.HasIndex(x => new { x.DocumentId, x.PipelineVersionId, x.SiteProfileVersionId, x.EmbeddingProfileId, x.ContentHash, x.ParseRuleKey }, "IX_ChunkSets_Key")
                .IsUnique().AreNullsDistinct(false);
            // The orphan sweep (engine design §14).
            e.HasIndex(x => new { x.State, x.CreatedAt }, "IX_ChunkSets_Writing").HasFilter("\"State\" = 'writing'");
            e.HasIndex(x => x.SupersededAt);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chunk>(e =>
        {
            e.ToTable("Chunks", ContentSchema, t =>
            {
                ClassificationCheck(t, "Chunks");
                ValuesCheck(t, "Chunks", "Kind", ChunkKinds.All);
                ValuesCheck(t, "Chunks", "ContentKind", ChunkContentKinds.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Kind).HasMaxLength(10).HasDefaultValue(ChunkKinds.Child);
            e.Property(x => x.ContentKind).HasMaxLength(16).HasDefaultValue(ChunkContentKinds.Text);
            e.Property(x => x.OverlapChars).HasDefaultValue((short)0);
            e.Property(x => x.Tags).HasColumnType("text[]");
            e.Property(x => x.DocumentType).HasMaxLength(30);
            // data-model §3.2: section expansion reads neighbours by (ChunkSetId, Ordinal).
            e.HasIndex(x => new { x.ChunkSetId, x.Ordinal }).IsUnique();
            e.HasIndex(x => x.CollectionId).HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(x => x.Tags).HasMethod("gin").HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(x => x.ParentId).HasFilter("\"ParentId\" IS NOT NULL");
            e.HasOne<ChunkSet>().WithMany().HasForeignKey(x => x.ChunkSetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmbeddingCacheEntry>(e =>
        {
            e.ToTable("EmbeddingCache", ContentSchema, t => ClassificationCheck(t, "EmbeddingCache"));
            e.HasKey(x => new { x.Classification, x.EmbeddingProfileId, x.TextHash });
            e.Property(x => x.Classification).HasMaxLength(20);
            e.HasIndex(x => x.LastUsedAt);
        });

        modelBuilder.Entity<Run>(e =>
        {
            e.ToTable("Runs", ContentSchema, t =>
            {
                ClassificationCheck(t, "Runs");
                ValuesCheck(t, "Runs", "Kind", RunKinds.All);
                ValuesCheck(t, "Runs", "Trigger", RunTriggers.All);
                ValuesCheck(t, "Runs", "State", RunStates.All);
                t.HasCheckConstraint("CK_Runs_Phase", $"\"Phase\" IS NULL OR \"Phase\" IN ({SqlList(RunPhases.All)})");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Trigger).HasMaxLength(20);
            e.Property(x => x.Phase).HasMaxLength(20);
            e.Property(x => x.Report).HasColumnType("jsonb");
            e.Property(x => x.SettingsHashes).HasColumnType("jsonb");
            e.Property(x => x.State).HasMaxLength(40);
            // Counters start at 0 in the database too, so a writer may leave them out (#110).
            foreach (var counter in new[] { nameof(Run.Added), nameof(Run.Updated), nameof(Run.Unchanged), nameof(Run.Deleted),
                nameof(Run.Failed), nameof(Run.ProposedDeletions) })
            {
                e.Property<int>(counter).HasDefaultValue(0);
            }

            e.Property(x => x.CostUsd).HasPrecision(18, 8).HasDefaultValue(0m);
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.SourceId, x.StartedAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<ParseObservation>(e =>
        {
            e.ToTable("ParseObservations", ContentSchema, t => ClassificationCheck(t, "ParseObservations"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Domain).HasMaxLength(253);
            e.Property(x => x.PathPattern).HasMaxLength(500);
            e.Property(x => x.DocumentType).HasMaxLength(30);
            e.Property(x => x.Strategy).HasMaxLength(30);
            e.Property(x => x.Score).HasPrecision(5, 4);
            e.Property(x => x.Metrics).HasColumnType("jsonb");
            e.Property(x => x.ObservedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.Domain, x.PathPattern, x.DocumentType });
        });

        modelBuilder.Entity<ParseProfile>(e =>
        {
            e.ToTable("ParseProfiles", ContentSchema, t =>
            {
                ClassificationCheck(t, "ParseProfiles");
                ValuesCheck(t, "ParseProfiles", "Origin", ParseProfileOrigins.All);
                ValuesCheck(t, "ParseProfiles", "State", ParseProfileStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Match).HasColumnType("jsonb");
            e.Property(x => x.Strategy).HasColumnType("jsonb");
            e.Property(x => x.CleanupRules).HasColumnType("jsonb");
            e.Property(x => x.Origin).HasMaxLength(20);
            e.Property(x => x.State).HasMaxLength(20);
            e.HasIndex(x => x.State);
        });

        modelBuilder.Entity<BlobTombstone>(e =>
        {
            e.ToTable("BlobTombstones", ContentSchema);
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(1024);
            e.Property(x => x.DeletedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Reason).HasMaxLength(100);
            // The purge sweep takes the oldest rows past Settings.BlobPurgeDelayDays (data-model §8).
            e.HasIndex(x => x.DeletedAt);
        });
    }

    /// <summary>Aggressive autovacuum for a small table of hot updates, as <c>Jobs</c> (performance pass §3.2).</summary>
    private static void HotTable<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class
    {
        e.HasStorageParameter("autovacuum_vacuum_scale_factor", 0.02);
        e.HasStorageParameter("autovacuum_vacuum_cost_limit", 2000);
    }
}
