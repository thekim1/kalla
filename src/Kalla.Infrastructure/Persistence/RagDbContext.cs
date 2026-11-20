using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence.Config;
using Kalla.Infrastructure.Persistence.Content;

namespace Kalla.Infrastructure.Persistence;

/// <summary>
/// The one context of Källa (data-model §10), as the gateway's <c>GatewayDbContext</c>: EF default identifiers
/// (quoted PascalCase) inside the lower-case schemas <c>config</c>, <c>content</c>, <c>chat</c> and <c>audit</c>. What EF
/// cannot express (the partitioned <c>RunItems</c>, trigger and step functions) is raw SQL in the migrations.
/// </summary>
public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public const string ConfigSchema = "config";
    public const string ContentSchema = "content";
    public const string ChatSchema = "chat";
    public const string AuditSchema = "audit";

    public DbSet<ConfigVersion> ConfigVersion => Set<ConfigVersion>();
    public DbSet<ConfigOutboxEntry> ConfigOutbox => Set<ConfigOutboxEntry>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ApiKey> Keys => Set<ApiKey>();
    public DbSet<KeyLastUse> KeyLastUse => Set<KeyLastUse>();
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();
    public DbSet<SetupSession> SetupSessions => Set<SetupSession>();
    public DbSet<SetupTest> SetupTests => Set<SetupTest>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<GatewayKey> GatewayKeys => Set<GatewayKey>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<WorkerInstance> WorkerInstances => Set<WorkerInstance>();
    public DbSet<RunItem> RunItems => Set<RunItem>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    // Ingestion, P1 (data-model §2.1, §2.2): configured in Config/IngestionConfigModel.cs and Content/IngestionContentModel.cs.
    public DbSet<PlatformSettings> Settings => Set<PlatformSettings>();
    public DbSet<DataProtectionPolicy> DataProtectionPolicies => Set<DataProtectionPolicy>();
    public DbSet<EmbeddingProfile> EmbeddingProfiles => Set<EmbeddingProfile>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionShare> CollectionShares => Set<CollectionShare>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineVersion> PipelineVersions => Set<PipelineVersion>();
    public DbSet<NodeManifestEntry> NodeManifest => Set<NodeManifestEntry>();
    public DbSet<WorkerNodeSupport> WorkerNodeSupport => Set<WorkerNodeSupport>();
    public DbSet<CrawlHost> CrawlHosts => Set<CrawlHost>();
    public DbSet<SiteProfile> SiteProfiles => Set<SiteProfile>();
    public DbSet<SiteProfileVersion> SiteProfileVersions => Set<SiteProfileVersion>();
    public DbSet<CrawlFrontierEntry> CrawlFrontier => Set<CrawlFrontierEntry>();
    public DbSet<CrawlRun> CrawlRuns => Set<CrawlRun>();
    public DbSet<ConnectorCheckpoint> ConnectorCheckpoints => Set<ConnectorCheckpoint>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<DeletionProposal> DeletionProposals => Set<DeletionProposal>();
    public DbSet<QuarantinedFile> QuarantinedFiles => Set<QuarantinedFile>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<NodeOutput> NodeOutputs => Set<NodeOutput>();
    public DbSet<ChunkSet> ChunkSets => Set<ChunkSet>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<EmbeddingCacheEntry> EmbeddingCache => Set<EmbeddingCacheEntry>();
    public DbSet<KeywordStatistic> KeywordStatistics => Set<KeywordStatistic>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<ParseObservation> ParseObservations => Set<ParseObservation>();
    public DbSet<ParseProfile> ParseProfiles => Set<ParseProfile>();
    public DbSet<BlobTombstone> BlobTombstones => Set<BlobTombstone>();

    // Serving, P1 (data-model §2.1, §2.2): configured in Config/ServingConfigModel.cs and Content/ServingContentModel.cs.
    public DbSet<Assistant> Assistants => Set<Assistant>();
    public DbSet<AssistantVersion> AssistantVersions => Set<AssistantVersion>();
    public DbSet<AssistantAudience> AssistantAudiences => Set<AssistantAudience>();
    public DbSet<GatewayModel> GatewayModels => Set<GatewayModel>();
    public DbSet<EvaluationSet> EvaluationSets => Set<EvaluationSet>();
    public DbSet<EvaluationQuestion> EvaluationQuestions => Set<EvaluationQuestion>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<EvaluationResult> EvaluationResults => Set<EvaluationResult>();
    public DbSet<RetrievalStat> RetrievalStats => Set<RetrievalStat>();
    public DbSet<CollectionDailyUsage> CollectionDailyUsage => Set<CollectionDailyUsage>();

    /// <summary>The assistant versions whose <c>CollectionLinks</c> name any of <paramref name="collectionIds"/>; composable.</summary>
    public IQueryable<AssistantVersion> AssistantVersionsLinking(Guid[] collectionIds) => AssistantVersions.FromSql($"""
        SELECT v.*, v.xmin FROM config."AssistantVersions" v
        WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(v."CollectionLinks") l WHERE (l ->> 'collectionId')::uuid = ANY ({collectionIds}))
        """);

    // Chat, P1b (data-model §2.3): configured in Chat/ChatModel.cs.
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<DataKey> DataKeys => Set<DataKey>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageSource> MessageSources => Set<MessageSource>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();
    public DbSet<AssistantDailyTotal> AssistantDailyTotals => Set<AssistantDailyTotal>();
    public DbSet<AssistantDailyUsage> AssistantDailyUsage => Set<AssistantDailyUsage>();
    public DbSet<FeedbackImportState> FeedbackImportState => Set<FeedbackImportState>();

    /// <summary>Npgsql options every registration of the context uses (service, design time, tests).</summary>
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ConfigSchema);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // --- config ---------------------------------------------------------------------------
        modelBuilder.Entity<ConfigVersion>(e =>
        {
            e.ToTable("ConfigVersion", ConfigSchema, t => t.HasCheckConstraint("CK_ConfigVersion_Singleton", "\"Id\" = 1"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ConfigOutboxEntry>(e =>
        {
            e.ToTable("ConfigOutbox", ConfigSchema);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Timestamp).HasDefaultValueSql("now()");
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Operation).HasColumnType("char(1)");
            e.HasIndex(x => x.Version);
        });

        modelBuilder.Entity<DataProtectionKey>(e => e.ToTable("DataProtectionKeys", ConfigSchema));

        modelBuilder.Entity<Client>(e =>
        {
            e.ToTable("Clients", ConfigSchema, t =>
            {
                t.HasCheckConstraint("CK_Clients_Kind", $"\"Kind\" IN ({SqlList(ClientKinds.All)})");
                t.HasCheckConstraint("CK_Clients_ClassificationCeiling", $"\"ClassificationCeiling\" IN ({SqlList(Classification.All)})");
                t.HasCheckConstraint("CK_Clients_ToolPolicy", "\"ToolPolicy\" IN ('none', 'read')");
                // A client that keeps chat history never reaches sensitive assistants; nor does an MCP client.
                t.HasCheckConstraint("CK_Clients_History", "NOT \"StoresHistory\" OR \"ClassificationCeiling\" <> 'sensitive'");
                t.HasCheckConstraint("CK_Clients_Mcp", "\"Kind\" <> 'mcp-client' OR \"ClassificationCeiling\" <> 'sensitive'");
            });
            // One deployment-managed client per kind and name (identity design §4.1, §5.2; #712): the ones rag-migrate keeps, found by both.
            e.HasIndex(x => new { x.Kind, x.Name }).IsUnique().HasDatabaseName("IX_Clients_Managed").HasFilter("\"IsManagedByDeployment\"");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.OAuthClientId).HasMaxLength(200);
            e.Property(x => x.ClassificationCeiling).HasMaxLength(20);
            e.Property(x => x.ToolPolicy).HasMaxLength(10).HasDefaultValue("none");
            e.Property(x => x.PortalUrl).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.OAuthClientId).IsUnique();
            e.HasMany(x => x.Keys).WithOne(x => x.Client).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("Keys", ConfigSchema, t => t.HasCheckConstraint("CK_Keys_Kind", "\"Kind\" IN ('sk', 'pk')"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(2);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Prefix).HasMaxLength(20);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.EncryptedSecret).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
            // As the gateway: a concurrent rotate or revoke of the same key fails instead of overwriting.
            e.Property(x => x.RotatedToKeyId).IsConcurrencyToken();
            e.Property(x => x.RevokedAt).IsConcurrencyToken();
            // Keys belong to mirrored teams (identity design §6.4); teams are deactivated, never deleted, by the org sync.
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Scopes).HasColumnType("jsonb").HasConversion(
                new ValueConverter<KeyScopes, string>(v => JsonSerializer.Serialize(v, ScopesJson), v => JsonSerializer.Deserialize<KeyScopes>(v, ScopesJson) ?? KeyScopes.None),
                new ValueComparer<KeyScopes>((a, b) => Equals(a, b), v => v.GetHashCode(), v => new KeyScopes(v.AssistantIds.ToArray(), v.CollectionIds.ToArray())));
        });

        modelBuilder.Entity<Department>(e =>
        {
            e.ToTable("Departments", ConfigSchema);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CostCenterCode).HasMaxLength(50);
            e.Property(x => x.MemberGroups).HasColumnType("varchar(256)[]");
            e.HasIndex(x => x.CostCenterCode).IsUnique();
            // As the gateway: a department with teams cannot be deleted.
            e.HasMany(x => x.Teams).WithOne(x => x.Department).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("Teams", ConfigSchema);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Code).HasMaxLength(100);
            e.HasIndex(x => x.Code).IsUnique();
        });

        // The assigned keys' mirror (data-model §2.1, admin design §4.10). No FK to Teams or Departments: the gateway's key
        // list and organisation are read together, but a key's row must not block anything the org sync does. The status
        // and policy values are the gateway's and can grow, so they have no CHECK.
        modelBuilder.Entity<GatewayKey>(e =>
        {
            e.ToTable("GatewayKeys", ConfigSchema);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.AllowedModels).HasColumnType("varchar(200)[]");
            e.Property(x => x.AllowedResidencies).HasColumnType("varchar(10)[]");
            e.Property(x => x.PiiPolicy).HasMaxLength(20);
            e.Property(x => x.ContentGuardPolicy).HasMaxLength(20);
            e.HasIndex(x => x.DepartmentId);
        });

        modelBuilder.Entity<AdminSession>(e =>
        {
            e.ToTable("AdminSessions", ConfigSchema);
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAt);
        });

        // Setup mode (identity design §19): short-lived rows without the outbox trigger, so deleting them bumps no config version.
        modelBuilder.Entity<SetupSession>(e =>
        {
            e.ToTable("SetupSessions", ConfigSchema);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OpeningNonce).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        modelBuilder.Entity<SetupTest>(e =>
        {
            e.ToTable("SetupTests", ConfigSchema, t => t.HasCheckConstraint("CK_SetupTests_Target", $"\"Target\" IN ({SqlList(SetupTargets.All)})"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Target).HasMaxLength(30);
            e.Property(x => x.Protected).HasMaxLength(16000);
            e.Property(x => x.Reason).HasMaxLength(50);
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.HasIndex(x => x.MarkerHash).IsUnique();
            e.HasIndex(x => x.SessionId);
            e.HasOne<SetupSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KeyLastUse>(e =>
        {
            e.ToTable("KeyLastUse", ConfigSchema);
            e.HasKey(x => x.KeyId);
            e.HasOne<ApiKey>().WithOne().HasForeignKey<KeyLastUse>(x => x.KeyId).OnDelete(DeleteBehavior.Cascade);
        });

        // --- content --------------------------------------------------------------------------
        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("Jobs", ContentSchema, t =>
            {
                t.HasCheckConstraint("CK_Jobs_Class", $"\"Class\" IN ({SqlList(JobClasses.All)})");
                t.HasCheckConstraint("CK_Jobs_State", $"\"State\" IN ({SqlList(JobStates.All)})");
                ClassificationCheck(t, "Jobs");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EnqueuedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Class).HasMaxLength(20);
            e.Property(x => x.Priority).HasDefaultValue((short)0);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.State).HasMaxLength(20).HasDefaultValue(JobStates.Queued);
            e.Property(x => x.AvailableAt).HasDefaultValueSql("now()");
            e.Property(x => x.Attempts).HasDefaultValue((short)0);
            e.Property(x => x.Reclaims).HasDefaultValue((short)0);
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.Property(x => x.LastError).HasMaxLength(500);

            e.HasIndex(x => new { x.Class, x.DepartmentId, x.Priority, x.AvailableAt }, "IX_Jobs_Ready")
                .IsDescending(false, false, true, false)
                .HasFilter("\"State\" = 'queued'");
            e.HasIndex(x => x.LeaseUntil, "IX_Jobs_Leases").HasFilter("\"State\" = 'running'");
            e.HasIndex(x => x.DedupKey, "IX_Jobs_DedupKey").IsUnique().HasFilter("\"State\" IN ('queued','running')");
            e.HasIndex(x => x.CompletedAt, "IX_Jobs_Terminal").HasFilter("\"State\" IN ('succeeded','failed','dead','cancelled')");
            e.HasIndex(x => x.RunId, "IX_Jobs_RunId").HasFilter("\"State\" IN ('queued','running')");

            // data-model §4.1: vacuum the hot queue early.
            e.HasStorageParameter("autovacuum_vacuum_scale_factor", 0.02);
            e.HasStorageParameter("autovacuum_vacuum_cost_limit", 2000);
        });

        modelBuilder.Entity<WorkerInstance>(e =>
        {
            e.ToTable("WorkerInstances", ContentSchema);
            e.HasKey(x => x.InstanceId);
            e.Property(x => x.InstanceId).ValueGeneratedNever();
            e.Property(x => x.BuildVersion).HasMaxLength(100);
            e.Property(x => x.Classes).HasColumnType("varchar(20)[]");
        });

        modelBuilder.Entity<RunItem>(e =>
        {
            // Created by the migration as a table partitioned by day (data-model §4.4).
            e.ToTable("RunItems", ContentSchema, t => t.ExcludeFromMigrations());
            e.HasKey(x => new { x.CreatedAt, x.Id });
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Outcome).HasMaxLength(30);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.Strategy).HasMaxLength(30);
            e.Property(x => x.CostUsd).HasPrecision(18, 8);
        });

        // --- audit ----------------------------------------------------------------------------
        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog", AuditSchema, t => t.HasCheckConstraint("CK_AuditLog_Classification",
                $"\"Classification\" IS NULL OR \"Classification\" IN ({SqlList(Classification.All)})"));
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.Property(x => x.ActorName).HasMaxLength(200);
            e.Property(x => x.ActorRoles).HasMaxLength(200);
            e.Property(x => x.ActorDepartments).HasMaxLength(1000);
            e.Property(x => x.SourceAddress).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(200);
            e.Property(x => x.RecordedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Service).HasMaxLength(30);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Classification).HasMaxLength(20);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.DepartmentId);
        });

        IngestionConfigModel.Configure(modelBuilder);
        IngestionContentModel.Configure(modelBuilder);
        ServingConfigModel.Configure(modelBuilder);
        ServingContentModel.Configure(modelBuilder);
        Chat.ChatModel.Configure(modelBuilder);
    }

    /// <summary><c>CK_&lt;table&gt;_Classification</c> on a row that carries its level (data-model §2).</summary>
    internal static void ClassificationCheck<T>(TableBuilder<T> table, string tableName) where T : class =>
        table.HasCheckConstraint($"CK_{tableName}_Classification", $"\"Classification\" IN ({SqlList(Classification.All)})");

    /// <summary><c>CK_&lt;table&gt;_&lt;column&gt;</c>: the column holds one of <paramref name="values"/>.</summary>
    internal static void ValuesCheck<T>(TableBuilder<T> table, string tableName, string column, IEnumerable<string> values) where T : class =>
        table.HasCheckConstraint($"CK_{tableName}_{column}", $"\"{column}\" IN ({SqlList(values)})");

    /// <summary>
    /// A daily usage table's checks (<c>AssistantDailyUsage</c>, <c>CollectionDailyUsage</c>; data-model §2.2, §2.3): the
    /// level, a step that is not empty (any step name, so a new step needs no migration), and <c>CK_&lt;table&gt;_Counts</c>:
    /// nothing negative, the estimated calls among the calls, the cached input within the input.
    /// </summary>
    internal static void UsageChecks<T>(TableBuilder<T> table, string tableName) where T : class
    {
        ClassificationCheck(table, tableName);
        table.HasCheckConstraint($"CK_{tableName}_Step", "\"Step\" <> ''");
        table.HasCheckConstraint($"CK_{tableName}_Counts",
            "\"Calls\" >= 0 AND \"EstimatedCalls\" >= 0 AND \"EstimatedCalls\" <= \"Calls\" AND \"InputTokens\" >= 0 AND " +
            "\"CachedInputTokens\" >= 0 AND \"CachedInputTokens\" <= \"InputTokens\" AND \"OutputTokens\" >= 0 AND " +
            "\"CostUsd\" >= 0 AND \"EstimatedCostUsd\" >= 0");
    }

    /// <summary>
    /// A daily usage table's columns: <c>Step</c> up to 100 characters (the gateway keeps 100 of <c>x-fors-step</c>,
    /// gateway-changes §7.1), every counter <c>NOT NULL DEFAULT 0</c>, the costs <c>numeric(18,8)</c>, IX (<c>DepartmentId</c>,
    /// <c>Day</c>) for the cost report by department.
    /// </summary>
    internal static void UsageColumns<T>(EntityTypeBuilder<T> entity) where T : class
    {
        entity.Property<string>("Classification").HasMaxLength(20);
        entity.Property<string>("Step").HasMaxLength(100);
        foreach (var counter in new[] { "Calls", "EstimatedCalls" })
        {
            entity.Property<int>(counter).HasDefaultValue(0);
        }

        foreach (var tokens in new[] { "InputTokens", "CachedInputTokens", "OutputTokens" })
        {
            entity.Property<long>(tokens).HasDefaultValue(0L);
        }

        foreach (var cost in new[] { "CostUsd", "EstimatedCostUsd" })
        {
            entity.Property<decimal>(cost).HasPrecision(18, 8).HasDefaultValue(0m);
        }

        entity.HasIndex("DepartmentId", "Day");
    }

    private static readonly JsonSerializerOptions ScopesJson = new(JsonSerializerDefaults.Web);

    internal static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
