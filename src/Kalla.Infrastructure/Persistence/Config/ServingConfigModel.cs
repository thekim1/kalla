using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Kalla.Domain;
using Kalla.Domain.Entities;
using static Kalla.Infrastructure.Persistence.RagDbContext;

namespace Kalla.Infrastructure.Persistence.Config;

/// <summary>
/// The serving <c>config</c> tables of P1 (data-model §2.1; simplification pass §15; admin design §4.10): assistants,
/// their versions and audiences, and the gateway model mirror. Raw SQL in the migration adds what EF cannot express: the
/// view <c>config."PublishedAssistantVersions"</c> (the api's only way to a version), the audiences' unique key on
/// <c>coalesce("SubjectId"::text, "GroupValue")</c> and the outbox triggers, which skip draft versions (data-model §5).
/// </summary>
internal static class ServingConfigModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Assistant>(e =>
        {
            e.ToTable("Assistants", ConfigSchema, t =>
            {
                ClassificationCheck(t, "Assistants");
                ValuesCheck(t, "Assistants", "State", AssistantStates.All);
                // Only history mode none until A1 allows stored chat content (weakness review §4, data-model §15).
                ValuesCheck(t, "Assistants", "HistoryMode", HistoryModes.All);
                // Within every level's policy maximum, which is at most 90 days (data-model §2.1 DataProtectionPolicies).
                t.HasCheckConstraint("CK_Assistants_RetentionDays", "\"RetentionDays\" BETWEEN 1 AND 90");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Slug).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactEmail).HasMaxLength(254);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.HistoryMode).HasMaxLength(20);
            e.Property(x => x.AllowedOrigins).HasColumnType("text[]");
            e.Property(x => x.State).HasMaxLength(20);
            // Unique among assistants that are not deleted: a deleted one's name is free at once (Decisions "Deleting knowledge
            // bases, sources and assistants").
            e.HasIndex(x => x.Slug).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(x => x.DepartmentId);
            // A deleted assistant is gone for every EF reader (the admin): lists, routes (404), the rules and the counts. The
            // retention step and the API's snapshot read SQL and filter it themselves.
            e.HasQueryFilter(x => x.DeletedAt == null);
            // The pointers name a version of this assistant: (version id, assistant id) references the version's
            // (Id, AssistantId), so a pointer can never reach another assistant's version. NO ACTION, checked at the end
            // of the statement, so deleting an assistant cascades to its versions.
            e.HasOne<AssistantVersion>().WithMany().HasForeignKey(x => new { x.PublishedVersionId, x.Id })
                .HasPrincipalKey(v => new { v.Id, v.AssistantId }).OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("FK_Assistants_AssistantVersions_PublishedVersionId");
            e.HasOne<AssistantVersion>().WithMany().HasForeignKey(x => new { x.DraftVersionId, x.Id })
                .HasPrincipalKey(v => new { v.Id, v.AssistantId }).OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("FK_Assistants_AssistantVersions_DraftVersionId");
            IngestionConfigModel.ChangeStamps(e);
        });

        modelBuilder.Entity<AssistantVersion>(e =>
        {
            e.ToTable("AssistantVersions", ConfigSchema, t =>
            {
                ValuesCheck(t, "AssistantVersions", "State", AssistantVersionStates.All);
                t.HasCheckConstraint("CK_AssistantVersions_Number", "\"Number\" >= 1");
                t.HasCheckConstraint("CK_AssistantVersions_CollectionLinks",
                    $"jsonb_array_length(\"CollectionLinks\") <= {AssistantVersion.MaxCollections}");
                // A version that was ever published has its model and its publish stamps; a draft may lack the model.
                t.HasCheckConstraint("CK_AssistantVersions_Published",
                    "\"State\" = 'draft' OR (\"GatewayModel\" IS NOT NULL AND \"PublishedAt\" IS NOT NULL AND \"PublishedBy\" IS NOT NULL)");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.GatewayModel).HasMaxLength(200);
            e.Property(x => x.QuerySteps).HasColumnType("jsonb");
            e.Property(x => x.NotFound).HasColumnType("jsonb");
            e.Property(x => x.CollectionLinks).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").HasConversion(
                new ValueConverter<IReadOnlyList<CollectionLink>, string>(v => CollectionLink.Serialize(v), v => CollectionLink.ParseList(v)),
                new ValueComparer<IReadOnlyList<CollectionLink>>((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, l) => HashCode.Combine(h, l)),
                    v => v.ToArray()));
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.PublishedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.AssistantId, x.Number }).IsUnique();
            // One open draft and one published version per assistant (data-model §2.1).
            e.HasIndex(x => x.AssistantId, "IX_AssistantVersions_Draft").IsUnique().HasFilter("\"State\" = 'draft'");
            e.HasIndex(x => x.AssistantId, "IX_AssistantVersions_Published").IsUnique().HasFilter("\"State\" = 'published'");
            e.HasOne<Assistant>().WithMany().HasForeignKey(x => x.AssistantId).OnDelete(DeleteBehavior.Cascade);
            // Its own stamps and xmin: a draft save never touches the assistant (data-model §2.1, admin design §4.3).
            IngestionConfigModel.ChangeStamps(e);
        });

        // Starting points for new assistants (data-model AssistantTemplates, P2): admin-only, so no config-changed trigger.
        modelBuilder.Entity<AssistantTemplate>(e =>
        {
            e.ToTable("AssistantTemplates", ConfigSchema, t => t.HasCheckConstraint("CK_AssistantTemplates_Name", "btrim(\"Name\") <> ''"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(AssistantTemplate.MaxName);
            e.Property(x => x.Description).HasMaxLength(AssistantTemplate.MaxDescription);
            e.Property(x => x.NotFound).HasColumnType("jsonb");
            e.Property(x => x.QuerySteps).HasColumnType("jsonb");
            e.HasIndex(x => x.Name).IsUnique();
            IngestionConfigModel.ChangeStamps(e);
        });

        modelBuilder.Entity<AssistantAudience>(e =>
        {
            e.ToTable("AssistantAudiences", ConfigSchema, t =>
            {
                ValuesCheck(t, "AssistantAudiences", "Kind", AssistantAudienceKinds.All);
                // Exactly one of SubjectId and GroupValue, and GroupValue only for a group (data-model §2.1).
                t.HasCheckConstraint("CK_AssistantAudiences_Subject",
                    "(\"SubjectId\" IS NULL) <> (\"GroupValue\" IS NULL) AND (\"Kind\" = 'group') = (\"GroupValue\" IS NOT NULL)");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.GroupValue).HasMaxLength(256);
            e.HasOne<Assistant>().WithMany().HasForeignKey(x => x.AssistantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GatewayModel>(e =>
        {
            e.ToTable("GatewayModels", ConfigSchema, t =>
            {
                ValuesCheck(t, "GatewayModels", "Kind", GatewayModelKinds.All);
                ValuesCheck(t, "GatewayModels", "ModelKind", GatewayModelModelKinds.All);
                ValuesCheck(t, "GatewayModels", "Source", GatewayModelSources.All);
                ValuesCheck(t, "GatewayModels", "SupportsToolsSource", GatewayModelSources.All);
                ValuesCheck(t, "GatewayModels", "LimitsSource", GatewayModelSources.All);
                t.HasCheckConstraint("CK_GatewayModels_Residencies",
                    $"cardinality(\"Residencies\") >= 1 AND \"Residencies\" <@ ARRAY[{SqlList(Enum.GetValues<Residency>().Select(ResidencyCeiling.ToHeader))}]::varchar(10)[]");
                // Decisions "Answer length from the model": both limits positive, the output within the window when both are known.
                t.HasCheckConstraint("CK_GatewayModels_Limits",
                    "(\"ContextWindow\" IS NULL OR \"ContextWindow\" > 0) AND (\"MaxOutputTokens\" IS NULL OR \"MaxOutputTokens\" > 0) " +
                    "AND (\"ContextWindow\" IS NULL OR \"MaxOutputTokens\" IS NULL OR \"MaxOutputTokens\" <= \"ContextWindow\")");
            });
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.ModelKind).HasMaxLength(10).HasDefaultValue(GatewayModelModelKinds.Chat); // manual route rows are chat
            e.Property(x => x.Residencies).HasColumnType("varchar(10)[]");
            e.Property(x => x.Source).HasMaxLength(10);
            e.Property(x => x.SupportsToolsSource).HasMaxLength(10);
            e.Property(x => x.LimitsSource).HasMaxLength(10).HasDefaultValue(GatewayModelSources.Manual);
            e.Property(x => x.InputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.CachedInputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.OutputPerMillionUsd).HasPrecision(18, 6);
        });
    }
}
