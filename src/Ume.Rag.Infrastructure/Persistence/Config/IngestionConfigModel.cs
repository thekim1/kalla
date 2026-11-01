using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using static Ume.Rag.Infrastructure.Persistence.RagDbContext;

namespace Ume.Rag.Infrastructure.Persistence.Config;

/// <summary>
/// The <c>config</c> tables of P1 ingestion (data-model §2.1): settings, data protection, embedding profiles,
/// collections, sources and pipelines. Every editable aggregate root carries <c>UpdatedAt</c>/<c>UpdatedBy</c> and maps
/// <c>xmin</c> as its concurrency token (admin design §4.3); the config outbox triggers are raw SQL in the migration.
/// </summary>
internal static class IngestionConfigModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformSettings>(e =>
        {
            e.ToTable("Settings", ConfigSchema, t =>
            {
                t.HasCheckConstraint("CK_Settings_Singleton", $"\"Id\" = {PlatformSettings.SingletonId}");
                t.HasCheckConstraint("CK_Settings_DeletionGuardPercent", "\"DeletionGuardPercent\" BETWEEN 1 AND 100");
                t.HasCheckConstraint("CK_Settings_DeletionGuardCounts", "\"DeletionGuardMinCount\" >= 1 AND \"DeletionGuardMaxCount\" >= \"DeletionGuardMinCount\"");
                // As the gateway's CK_GatewaySettings_AuditRetention: at least 90 days, or null for ever (data-model §8).
                t.HasCheckConstraint("CK_Settings_AuditRetention", "\"AuditRetentionDays\" IS NULL OR \"AuditRetentionDays\" BETWEEN 90 AND 3650");
                t.HasCheckConstraint("CK_Settings_BlobPurgeDelay", "\"BlobPurgeDelayDays\" BETWEEN 1 AND 3650");
                t.HasCheckConstraint("CK_Settings_QuarantineRetention", "\"QuarantineRetentionDays\" BETWEEN 1 AND 3650");
                t.HasCheckConstraint("CK_Settings_DegradedFailureRatio", "\"DegradedFailureRatio\" BETWEEN 0 AND 1");
                t.HasCheckConstraint("CK_Settings_ScheduleJitter", "\"ScheduleJitterMinutes\" BETWEEN 0 AND 1440");
                t.HasCheckConstraint("CK_Settings_PreviewMaxUsd", "\"PreviewMaxUsd\" >= 0");
                t.HasCheckConstraint("CK_Settings_NerMinScore", "\"NerMinScore\" BETWEEN 0 AND 1");
                t.HasCheckConstraint("CK_Settings_EvaluationMaxQuestions", "\"EvaluationMaxQuestions\" BETWEEN 1 AND 10000");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TrustedProxies).HasColumnType("text[]");
            e.Property(x => x.RetentionLastRunError).HasMaxLength(500);
            e.Property(x => x.WorkerConcurrency).HasColumnType("jsonb");
            e.Property(x => x.DegradedFailureRatio).HasPrecision(5, 4);
            e.Property(x => x.PreviewMaxUsd).HasPrecision(10, 4);
            e.Property(x => x.DefaultSchedules).HasColumnType("jsonb");
            e.Property(x => x.Crawl).HasColumnType("jsonb");
            e.Property(x => x.PiiFlagRules).HasColumnType("jsonb");
            e.Property(x => x.PiiOwnDomains).HasColumnType("text[]");
            e.Property(x => x.NerMinScore).HasPrecision(3, 2);
            e.Property(x => x.EvaluationModel).HasMaxLength(200);
            e.Property(x => x.EvaluationJudgeModel).HasMaxLength(200);
            e.Property(x => x.Retrieval).HasColumnType("jsonb");
            e.Property(x => x.Chat).HasColumnType("jsonb");
            ChangeStamps(e);
        });

        modelBuilder.Entity<DataProtectionPolicy>(e =>
        {
            e.ToTable("DataProtectionPolicies", ConfigSchema, t =>
            {
                ClassificationCheck(t, "DataProtectionPolicies");
                // data-model §2.1: internal and sensitive require encryption; default ≤ max ≤ 90 days.
                t.HasCheckConstraint("CK_DataProtectionPolicies_Encryption", "\"Classification\" = 'public' OR \"EncryptionRequired\"");
                t.HasCheckConstraint("CK_DataProtectionPolicies_Retention",
                    "\"RetentionDefaultDays\" BETWEEN 1 AND \"RetentionMaxDays\" AND \"RetentionMaxDays\" <= 90");
                // Only history mode none until A1 allows stored chat content (weakness review §4, data-model §15).
                t.HasCheckConstraint("CK_DataProtectionPolicies_HistoryModes",
                    $"\"AllowedHistoryModes\" <@ ARRAY[{SqlList(HistoryModes.All)}]::varchar(20)[] AND \"DefaultHistoryMode\" = ANY (\"AllowedHistoryModes\")");
            });
            e.HasKey(x => x.Classification);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.AllowedHistoryModes).HasColumnType("varchar(20)[]");
            e.Property(x => x.DefaultHistoryMode).HasMaxLength(20);
            e.Property(x => x.BlobMasterKeyName).HasMaxLength(100);
            ChangeStamps(e);
        });

        modelBuilder.Entity<EmbeddingProfile>(e =>
        {
            e.ToTable("EmbeddingProfiles", ConfigSchema, t =>
            {
                // halfvec HNSW indexes take up to 4,000 dimensions (data-model §3.2).
                t.HasCheckConstraint("CK_EmbeddingProfiles_Dimensions", "\"Dimensions\" BETWEEN 1 AND 4000");
                t.HasCheckConstraint("CK_EmbeddingProfiles_MaxInputTokens", "\"MaxInputTokens\" > 0");
                t.HasCheckConstraint("CK_EmbeddingProfiles_Distance", "\"Distance\" IN ('cosine')");
                ValuesCheck(t, "EmbeddingProfiles", "State", EmbeddingProfileStates.All);
                ValuesCheck(t, "EmbeddingProfiles", "MaxResidency", Enum.GetValues<Residency>().Select(ResidencyCeiling.ToHeader));
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.GatewayModel).HasMaxLength(200);
            e.Property(x => x.Distance).HasMaxLength(20);
            e.Property(x => x.RequestDimensions).HasDefaultValue(false);
            e.Property(x => x.State).HasMaxLength(20);
            // The header values OnPrem, Eu, External (P1 README §8 O1).
            e.Property(x => x.MaxResidency).HasMaxLength(10).HasConversion<string>();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsDefault).IsUnique().HasFilter("\"IsDefault\"");
            ChangeStamps(e);
        });

        modelBuilder.Entity<Collection>(e =>
        {
            e.ToTable("Collections", ConfigSchema, t => ClassificationCheck(t, "Collections"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Slug).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.RetrievalSettings).HasColumnType("jsonb");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.DepartmentId);
            e.HasOne<EmbeddingProfile>().WithMany().HasForeignKey(x => x.EmbeddingProfileId).OnDelete(DeleteBehavior.Restrict);
            ChangeStamps(e);
        });

        modelBuilder.Entity<Source>(e =>
        {
            e.ToTable("Sources", ConfigSchema, t =>
            {
                ClassificationCheck(t, "Sources");
                ValuesCheck(t, "Sources", "Type", SourceTypes.All);
                ValuesCheck(t, "Sources", "State", SourceStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Settings).HasColumnType("jsonb");
            e.Property(x => x.DemoteRules).HasColumnType("jsonb").HasComputedColumnSql("\"Settings\" -> 'demote'", stored: true);
            e.Property(x => x.Schedule).HasMaxLength(100);
            // NOT NULL DEFAULT true (data-model §2.1); the sentinel lets EF send false and leave true to the default.
            e.Property(x => x.ContainsThirdPartyText).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.State).HasMaxLength(20);
            // Immutable: chunks copy the collection id (data-model §2.2).
            e.HasOne<Collection>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
            // The scheduler's first schedule of a never-run source (engine design §6; #110).
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            ChangeStamps(e);
        });

        modelBuilder.Entity<Pipeline>(e =>
        {
            e.ToTable("Pipelines", ConfigSchema);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TemplateId).HasMaxLength(50);
            // Platform defaults have no department: their names are unique too.
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().AreNullsDistinct(false);
            e.HasOne<PipelineVersion>().WithMany().HasForeignKey(x => x.PublishedVersionId).OnDelete(DeleteBehavior.Restrict);
            ChangeStamps(e);
        });

        modelBuilder.Entity<PipelineVersion>(e =>
        {
            e.ToTable("PipelineVersions", ConfigSchema, t => ValuesCheck(t, "PipelineVersions", "State", PipelineVersionStates.All));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.PipelineId, x.Number }).IsUnique();
            e.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary><c>UpdatedAt</c>, <c>UpdatedBy</c> (the admin's audit actor) and <c>xmin</c> as the row version (data-model §2.1).</summary>
    internal static void ChangeStamps<T>(EntityTypeBuilder<T> e) where T : class
    {
        e.Property<string?>("UpdatedBy").HasMaxLength(200);
        e.Property<uint>("Version").IsRowVersion();
    }
}
