using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>
/// The one context of Ume.Rag (data-model §10), as the gateway's <c>GatewayDbContext</c>: EF default identifiers
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
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<WorkerInstance> WorkerInstances => Set<WorkerInstance>();
    public DbSet<RunItem> RunItems => Set<RunItem>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

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
            e.Property(x => x.Scopes).HasColumnType("jsonb").HasConversion(
                new ValueConverter<KeyScopes, string>(v => JsonSerializer.Serialize(v, ScopesJson), v => JsonSerializer.Deserialize<KeyScopes>(v, ScopesJson) ?? KeyScopes.None),
                new ValueComparer<KeyScopes>((a, b) => Equals(a, b), v => v.GetHashCode(), v => new KeyScopes(v.AssistantIds.ToArray(), v.CollectionIds.ToArray())));
        });

        modelBuilder.Entity<AdminSession>(e =>
        {
            e.ToTable("AdminSessions", ConfigSchema);
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAt);
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
    }

    /// <summary><c>CK_&lt;table&gt;_Classification</c> on a row that carries its level (data-model §2).</summary>
    private static void ClassificationCheck<T>(TableBuilder<T> table, string tableName) where T : class =>
        table.HasCheckConstraint($"CK_{tableName}_Classification", $"\"Classification\" IN ({SqlList(Classification.All)})");

    private static readonly JsonSerializerOptions ScopesJson = new(JsonSerializerDefaults.Web);

    private static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
