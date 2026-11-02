using Npgsql;
using Ume.Rag.TestKit;

namespace Ume.Rag.IntegrationTests;

/// <summary>Knowledge design §6.4: an interrupted HNSW rebuild's invalid <c>_ccnew</c> index is dropped by the next migration run.</summary>
public sealed class InvalidRebuildIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_migration_run_drops_an_invalid_rebuild_index_and_keeps_valid_ones()
    {
        await using var database = new RagDatabase();
        await database.InitializeAsync();
        await Sql.ExecuteAsync(database.Connection(null), """
            CREATE INDEX "IX_Chunks_Embedding_00000000000000000000000000000001_ccnew" ON content."Chunks" ("Id");
            CREATE INDEX "IX_Chunks_Embedding_00000000000000000000000000000002_ccnew" ON content."Chunks" ("Id");
            UPDATE pg_index SET indisvalid = false
             WHERE indexrelid = 'content."IX_Chunks_Embedding_00000000000000000000000000000001_ccnew"'::regclass;
            """);

        await database.ApplyRolesAsync(Ct);

        await using var connection = new NpgsqlConnection(database.Connection(null, pooling: false));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'content' AND c.relname LIKE '%\_ccnew' ORDER BY 1
            """, connection);
        var left = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                left.Add(reader.GetString(0));
            }
        }

        left.ShouldBe(["IX_Chunks_Embedding_00000000000000000000000000000002_ccnew"], "only the invalid one is dropped; a valid index is never touched");
    }
}
