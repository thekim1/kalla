using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// P1a item 18's dev seed (<c>scripts/dev/seed-url-list.sql</c>, run by <c>seed_url_list.py</c>): the same SQL file, with
/// its parameters as session settings, yields a public URL-list source on the platform url-list pipeline that a worker with
/// the FakeGateway syncs into live chunks from the test site; a re-run finds what it created; the config triggers bump the
/// config version. The localhost-only guard is the Python script's (<c>scripts/dev/test_seed_url_list.py</c>).
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DevSeedScriptTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private const string ProfileName = "engine-test-embedding";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-devseed-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_seed_sql_yields_a_public_url_list_source_that_a_worker_syncs_into_live_chunks()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name, marker))));
        var urls = new[] { site.Url("/p/a"), site.Url("/p/b"), site.Url("/p/c") };
        var pipelineId = await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = Settings(urls);
        var versionBefore = await ConfigVersionAsync();

        var first = await SeedAsync(settings);

        first["department"].ShouldEndWith("(created)");
        first["collection"].ShouldEndWith("(created, public)");
        first["source"].ShouldEndWith("(created, 3 URLs)");
        var seed = await SourceSeedAsync(first);
        seed.PipelineId.ShouldBe(pipelineId);
        (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT s."Type" || ' ' || s."Classification" || ' ' || s."State" || ' ' || c."Classification" || ' ' || (c."PublishedAt" IS NOT NULL)
                   || ' ' || (c."EmbeddingProfileId" = $2) || ' ' || (c."DepartmentId" = $3)
            FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId" WHERE s."Id" = $1
            """, seed.SourceId, EngineKit.ProfileId, seed.DepartmentId)).ShouldBe("url-list public active public true true true");
        (await ConfigVersionAsync()).ShouldBeGreaterThan(versionBefore);
        (await Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(*) FROM config."ConfigOutbox" WHERE "EntityType" = 'Sources' AND "EntityId" = $1""", seed.SourceId.ToString()))
            .ShouldBe(1);
        var jobId = Guid.Parse(first["sync_job_id"].ShouldNotBeNull());
        var job = await Sql.JobAsync(database.Connection(null), jobId);
        job.State.ShouldBe(JobStates.Queued);
        job.DepartmentId.ShouldBe(seed.DepartmentId);

        try
        {
            await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            (await EngineKit.WaitForRunAsync(database, jobId)).ShouldBe(new RunCounters(RunStates.Succeeded, 3, 0, 0, 0, 0, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Trigger" FROM content."Runs" WHERE "Id" = $1""", jobId))
                .ShouldBe("manual");
            (await EngineKit.DocumentsAsync(database, seed)).ShouldBe(3);
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(3);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);

            var second = await SeedAsync(settings);

            second["department"].ShouldEndWith("(existing)");
            second["collection"].ShouldEndWith("(existing, public)");
            second["source"].ShouldEndWith("(unchanged, 3 URLs)");
            second["source_id"].ShouldBe(seed.SourceId.ToString());
            var rerun = Guid.Parse(second["sync_job_id"].ShouldNotBeNull());
            (await EngineKit.WaitForRunAsync(database, rerun)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 3, 0, 0, 0));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_rerun_with_other_urls_replaces_the_list_and_without_a_sync_enqueues_nothing()
    {
        await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = Settings([new Uri("http://site.test/p/a"), new Uri("http://site.test/p/b")]);
        settings["ume_seed.enqueue"] = "false";
        var seed = await SourceSeedAsync(await SeedAsync(settings));
        try
        {
            settings["ume_seed.urls"] = """["http://site.test/p/c"]""";

            var rerun = await SeedAsync(settings);

            rerun["source"].ShouldEndWith("(updated, 1 URLs)");
            rerun["sync_job"].ShouldStartWith("not enqueued (the scheduler");
            rerun["sync_job_id"].ShouldBeNull();
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Settings"::text FROM config."Sources" WHERE "Id" = $1""", seed.SourceId))
                .ShouldBe("""{"urls": ["http://site.test/p/c"]}""");
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "DedupKey" = $1""",
                "sync:" + seed.SourceId)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_unknown_profile_is_refused_and_nothing_is_written()
    {
        await EnsurePlatformPipelineAsync();
        var settings = Settings([new Uri("http://site.test/p/a")]);
        settings["ume_seed.profile"] = "no-such-profile-" + Guid.NewGuid().ToString("N");

        var error = await Should.ThrowAsync<PostgresException>(() => SeedAsync(settings));

        error.MessageText.ShouldStartWith("seed-url-list: no active embedding profile no-such-profile-");
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM config."Collections" WHERE "Slug" = $1""",
            settings["ume_seed.collection_slug"])).ShouldBe(0);
    }

    /// <summary>The script's parameters with names unique to the test.</summary>
    private static Dictionary<string, string> Settings(IEnumerable<Uri> urls)
    {
        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ume_seed.urls"] = System.Text.Json.JsonSerializer.Serialize(urls.Select(u => u.AbsoluteUri)),
            ["ume_seed.profile"] = ProfileName,
            ["ume_seed.department_name"] = "Seed " + suffix,
            ["ume_seed.collection_slug"] = "seed-" + suffix,
            ["ume_seed.collection_name"] = "Seed " + suffix,
            ["ume_seed.source_name"] = "Seed URL list " + suffix,
            ["ume_seed.enqueue"] = "true",
        };
    }

    /// <summary>Runs <c>scripts/dev/seed-url-list.sql</c> after the settings, on one connection, as psql does; returns its key/value rows.</summary>
    private async Task<Dictionary<string, string?>> SeedAsync(IReadOnlyDictionary<string, string> settings)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        foreach (var (key, value) in settings)
        {
            await using var set = new NpgsqlCommand("SELECT set_config($1, $2, false)", connection)
            {
                Parameters = { new NpgsqlParameter { Value = key }, new NpgsqlParameter { Value = value } },
            };
            await set.ExecuteNonQueryAsync(Ct);
        }

        var sql = await File.ReadAllTextAsync(TestPostgres.RepositoryFile(Path.Combine("scripts", "dev", "seed-url-list.sql")), Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        do
        {
            while (reader.FieldCount == 2 && await reader.ReadAsync(Ct))
            {
                result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        while (await reader.NextResultAsync(Ct));

        return result;
    }

    private async Task<SourceSeed> SourceSeedAsync(Dictionary<string, string?> result)
    {
        var sourceId = Guid.Parse(result["source_id"].ShouldNotBeNull());
        await using var db = TestPostgres.Context(database.Connection(null));
        var source = db.Sources.Single(s => s.Id == sourceId);
        var pipeline = db.Pipelines.Single(p => p.Id == source.PipelineId);
        return new SourceSeed(sourceId, source.CollectionId, Guid.Parse(result["department_id"].ShouldNotBeNull()), pipeline.Id,
            pipeline.PublishedVersionId.ShouldNotBeNull(), Classification.Public);
    }

    /// <summary>
    /// The platform url-list pipeline as the migrator's PipelineTemplateSeeder publishes it (no department, name and template
    /// id <c>url-list</c>, the latest template version with its default parameters), when it does not exist yet.
    /// </summary>
    private async Task<Guid> EnsurePlatformPipelineAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        if (db.Pipelines.SingleOrDefault(p => p.DepartmentId == null && p.Name == SourceTypes.UrlList) is { } existing)
        {
            return existing.Id;
        }

        var template = TemplateCatalog.BuiltIn.All.Where(t => t.Template.Name == SourceTypes.UrlList).MaxBy(t => t.Template.Version).ShouldNotBeNull();
        var pipeline = new Pipeline { Name = SourceTypes.UrlList, TemplateId = SourceTypes.UrlList, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "system:seed" };
        db.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        var version = new PipelineVersion
        {
            PipelineId = pipeline.Id,
            Number = 1,
            State = PipelineVersionStates.Published,
            Definition = System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = "pipeline/v2", template = SourceTypes.UrlList, templateVersion = template.Template.Version, @params = new { },
            }),
            CreatedBy = "system:seed",
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        pipeline.PublishedVersionId = version.Id;
        await db.SaveChangesAsync(Ct);
        return pipeline.Id;
    }

    /// <summary>The engine tests' 1024-dimension profile (<see cref="EngineKit.ProfileId"/>), as EngineKit seeds it.</summary>
    private Task EnsureProfileAsync() =>
        Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt")
            VALUES ($1, $2, 'bge-m3', $3, 'cosine', 8192, false, 'active', 'Eu', now())
            ON CONFLICT DO NOTHING
            """, EngineKit.ProfileId, ProfileName, EngineKit.Dimensions);

    private Task<long> ConfigVersionAsync() =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");
}
