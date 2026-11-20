using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Kalla.IntegrationTests;

/// <summary>Driver claims the design relies on: pgvector with Npgsql/EF Core 10 (to-verify #13), tracing (#18).</summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DriverTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pgvector_round_trips_through_npgsql_and_ef_core()
    {
        var builder = new NpgsqlDataSourceBuilder(database.Connection(null));
        builder.UseVector();
        await using var dataSource = builder.Build();
        await using var connection = await dataSource.OpenConnectionAsync(Ct);

        var embedding = new Vector(new float[] { 0.25f, -1f, 3.5f });
        await using (var command = new NpgsqlCommand("SELECT $1::vector(3)", connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = embedding });
            ((Vector)(await command.ExecuteScalarAsync(Ct))!).ToArray().ShouldBe(embedding.ToArray());
        }

        await using (var create = new NpgsqlCommand("""CREATE TEMP TABLE "VectorProbes" ("Id" integer PRIMARY KEY, "Embedding" vector(3))""", connection))
        {
            await create.ExecuteNonQueryAsync(Ct);
        }

        await using var db = new VectorProbeContext(new DbContextOptionsBuilder<VectorProbeContext>()
            .UseNpgsql(connection, o => o.UseVector()).Options);
        db.Probes.Add(new VectorProbe { Id = 1, Embedding = embedding });
        db.Probes.Add(new VectorProbe { Id = 2, Embedding = new Vector(new float[] { -0.25f, 1f, -3.5f }) });
        await db.SaveChangesAsync(Ct);

        var target = new Vector(new float[] { 0.2f, -1f, 3f });
        var nearest = await db.Probes.OrderBy(p => p.Embedding.CosineDistance(target)).FirstAsync(Ct);
        nearest.Id.ShouldBe(1);
        // Pgvector.EntityFrameworkCore 0.3.0 on EF Core 10 renders a constant vector as an unquoted literal ("<=> [0.2,-1,3]"),
        // which is a syntax error: pass vectors as parameters (the retrieval statements do, data-model §3.3).
        var constant = await Should.ThrowAsync<PostgresException>(() =>
            db.Probes.OrderBy(p => p.Embedding.CosineDistance(new Vector(new float[] { 0.2f, -1f, 3f }))).FirstAsync(Ct));
        constant.SqlState.ShouldBe(PostgresErrorCodes.SyntaxError);
    }

    [Fact]
    public async Task Npgsql_tracing_records_the_statement_but_never_parameter_values()
    {
        const string secret = "personnummer-19121212-1212";
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) { activities.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);

        await using (var connection = new NpgsqlConnection(database.Connection(RagDatabaseFixture.Api)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT length($1::text)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = secret });
            await command.ExecuteScalarAsync(Ct);
        }

        Activity[] recorded;
        lock (activities)
        {
            recorded = [.. activities];
        }

        var statement = recorded.SelectMany(a => a.TagObjects).Where(t => t.Key is "db.statement" or "db.query.text").Select(t => t.Value?.ToString());
        statement.ShouldContain(s => s != null && s.Contains("SELECT length($1::text)", StringComparison.Ordinal));
        foreach (var activity in recorded)
        {
            activity.DisplayName.ShouldNotContain(secret);
            foreach (var tag in activity.TagObjects)
            {
                (tag.Value?.ToString() ?? "").ShouldNotContain(secret, customMessage: tag.Key);
            }
        }
    }

    private sealed class VectorProbe
    {
        public int Id { get; set; }
        public required Vector Embedding { get; set; }
    }

    private sealed class VectorProbeContext(DbContextOptions<VectorProbeContext> options) : DbContext(options)
    {
        public DbSet<VectorProbe> Probes => Set<VectorProbe>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<VectorProbe>(e =>
            {
                e.ToTable("VectorProbes");
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Embedding).HasColumnType("vector(3)");
            });
    }
}
