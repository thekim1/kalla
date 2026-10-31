using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>clear-pii-flag</c> (P1b item 8; processing design §9.5, engine design §14): the clearance is recorded with the held
/// set's counts and the held set becomes searchable (state <c>live</c>, its chunks' <c>DeletedAt</c> cleared) and nothing
/// else changes; without a held set only the clearance is recorded; unknown and deleted documents are refused; a job that
/// lost its lease releases nothing.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PiiClearCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Admin = "admin-oid";

    private static readonly PiiCounts Flagged = new() { Personnummer = 1, Email = 2 };

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task Clearing_releases_the_held_set_to_searchable_and_changes_nothing_else()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var old = await CommandSeeds.SetAsync(database, seed, document, ChunkSetStates.Live, earlier: true);
            var held = await CommandSeeds.SetAsync(database, seed, document, ChunkSetStates.Held, Flagged, chunks: 3);
            var neighbour = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var neighbourHeld = await CommandSeeds.SetAsync(database, seed, neighbour, ChunkSetStates.Held, Flagged);
            (await CommandSeeds.VisibleChunksAsync(database, held)).ShouldBe(0);
            var before = await SnapshotAsync(seed, document);

            var clear = await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = document, DecidedBy = Admin }, seed.DepartmentId, Classification.Public);

            clear.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, clear.Job.Id)).GetProperty("releasedChunkSetId").GetGuid().ShouldBe(held);
            (await CommandSeeds.SetStateAsync(database, held)).ShouldBe(ChunkSetStates.Live);
            (await CommandSeeds.VisibleChunksAsync(database, held)).ShouldBe(3);
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "PiiClearedBy" || '|' || ("PiiClearedCounts" ->> 'personnummer') || '|' || ("PiiClearedCounts" ->> 'email') || '|' || ("PiiClearedAt" IS NOT NULL)::text
                FROM content."Documents" WHERE "Id" = $1
                """, document)).ShouldBe($"{Admin}|1|2|true");
            (await Sql.ScalarAsync<bool>(database.Connection(null), """SELECT "PiiFlagged" FROM content."ChunkSets" WHERE "Id" = $1""", held))
                .ShouldBeTrue("flag and counts stay on the set");
            (await CommandSeeds.SetStateAsync(database, old)).ShouldBe(ChunkSetStates.Superseded);
            (await CommandSeeds.VisibleChunksAsync(database, old)).ShouldBe(0);
            (await CommandSeeds.SetStateAsync(database, neighbourHeld)).ShouldBe(ChunkSetStates.Held);
            (await CommandSeeds.VisibleChunksAsync(database, neighbourHeld)).ShouldBe(0);
            (await SnapshotAsync(seed, document)).ShouldBe(before, "no other document, set or job changed");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Without_a_held_set_the_clearance_is_recorded_and_wrong_documents_are_refused()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var live = await CommandSeeds.SetAsync(database, seed, document, ChunkSetStates.Live);
            var deleted = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "DeletedAt" = now() WHERE "Id" = $1""", deleted);

            var clear = await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = document, DecidedBy = Admin }, seed.DepartmentId, Classification.Public);

            clear.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, clear.Job.Id)).TryGetProperty("releasedChunkSetId", out _).ShouldBeFalse();
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "PiiClearedBy" FROM content."Documents" WHERE "Id" = $1""", document)).ShouldBe(Admin);
            (await CommandSeeds.VisibleChunksAsync(database, live)).ShouldBe(2);
            (await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = Guid.NewGuid(), DecidedBy = Admin }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(ClearPiiFlagHandler.DocumentUnknown));
            (await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = deleted, DecidedBy = Admin }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(ClearPiiFlagHandler.DocumentDeleted));
            (await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = document, DecidedBy = "" }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_clearance_whose_lease_was_lost_keeps_the_set_held_and_its_retry_releases_it()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var held = await CommandSeeds.SetAsync(database, seed, document, ChunkSetStates.Held, Flagged);
            var job = await _runner.LeaseAsync(new ClearPiiFlagCommand { DocumentId = document, DecidedBy = Admin }, seed.DepartmentId, Classification.Public);
            await _runner.StealLeaseAsync(job.Id);

            (await _runner.RunAsync(job)).ShouldBeOfType<JobResult.Success>();
            (await CommandSeeds.SetStateAsync(database, held)).ShouldBe(ChunkSetStates.Held);
            (await CommandSeeds.VisibleChunksAsync(database, held)).ShouldBe(0);
            (await Sql.ScalarAsync<bool>(database.Connection(null), """SELECT "PiiClearedAt" IS NULL FROM content."Documents" WHERE "Id" = $1""", document)).ShouldBeTrue();

            var retry = await _runner.RunAsync(new ClearPiiFlagCommand { DocumentId = document, DecidedBy = Admin }, seed.DepartmentId, Classification.Public);
            retry.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.VisibleChunksAsync(database, held)).ShouldBe(2);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>Every document, set and chunk of the source except <paramref name="cleared"/>'s, and the non-command jobs.</summary>
    private Task<string> SnapshotAsync(SourceSeed seed, Guid cleared) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT coalesce((SELECT string_agg(d."Id"::text || coalesce(d."PiiClearedBy", '-') || coalesce(d."LiveChunkSetId"::text, '-') || coalesce(d."HeldBy", '-'), ',' ORDER BY d."Id")
                             FROM content."Documents" d WHERE d."SourceId" = $1 AND d."Id" <> $2), '')
                || '#' || coalesce((SELECT string_agg(s."Id"::text || s."State" || (SELECT count(*) FROM content."Chunks" c WHERE c."ChunkSetId" = s."Id" AND c."DeletedAt" IS NULL)::text, ',' ORDER BY s."Id")
                             FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId" WHERE d."SourceId" = $1 AND d."Id" <> $2), '')
                || '#' || (SELECT count(*) FROM content."Jobs" WHERE "Class" <> 'command')::text
            """, seed.SourceId, cleared);
}
