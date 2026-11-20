extern alias api;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Kalla.Answering;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Keys;
using Kalla.Pipelines.Definitions;
using Kalla.TestKit;
using Kalla.Worker.Tests.Processing;
using PiiMode = Kalla.Pipelines.Sources.PiiMode;
using SourceSettings = Kalla.Pipelines.Sources.SourceSettings;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// P1a item 18's dev seed (<c>scripts/dev/seed-url-list.sql</c>, run by <c>seed_url_list.py</c>): the same SQL file, with
/// its parameters as session settings, yields a public URL-list source on the platform url-list pipeline that a worker with
/// the FakeGateway syncs into live chunks from the test site; a re-run finds what it created; the config triggers bump the
/// config version. The source's <c>piiMode</c> is <c>report</c> unless <c>ume_seed.pii_mode</c> says <c>hold</c>. With a key (<c>--api-key</c>), kalla-api in process accepts the key for <c>/v1/retrieve</c> on the seeded
/// collection. The localhost-only guard, the key generation and the pepper handling are the Python script's
/// (<c>scripts/dev/test_seed_url_list.py</c>).
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class DevSeedScriptTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private const string ProfileName = "engine-test-embedding";
    private const string KeyPepper = "dev-seed-test-pepper-0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-devseed-blobs-");

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
        first["source"].ShouldEndWith("(created, 3 URLs, piiMode report)");
        var seed = await SourceSeedAsync(first);
        seed.PipelineId.ShouldBe(pipelineId);
        // The settings bind as the url-list record (contracts/sources/v1, unknown members refused) with piiMode report.
        (await PiiModeAsync(seed.SourceId)).ShouldBe(PiiMode.Report);
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
            second["source"].ShouldEndWith("(unchanged, 3 URLs, piiMode report)");
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
    public async Task A_rerun_with_other_urls_and_pii_mode_hold_replaces_the_settings_and_without_a_sync_enqueues_nothing()
    {
        await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = Settings([new Uri("http://site.test/p/a"), new Uri("http://site.test/p/b")]);
        settings["ume_seed.enqueue"] = "false";
        var seed = await SourceSeedAsync(await SeedAsync(settings));
        try
        {
            (await PiiModeAsync(seed.SourceId)).ShouldBe(PiiMode.Report);
            settings["ume_seed.urls"] = """["http://site.test/p/c"]""";
            settings["ume_seed.pii_mode"] = "hold";

            var rerun = await SeedAsync(settings);

            rerun["source"].ShouldEndWith("(updated, 1 URLs, piiMode hold)");
            rerun["sync_job"].ShouldStartWith("not enqueued (the scheduler");
            rerun["sync_job_id"].ShouldBeNull();
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Settings"::text FROM config."Sources" WHERE "Id" = $1""", seed.SourceId))
                .ShouldBe("""{"urls": ["http://site.test/p/c"], "piiMode": "hold"}""");
            (await PiiModeAsync(seed.SourceId)).ShouldBe(PiiMode.Hold);
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

    [Fact]
    public async Task A_pii_mode_other_than_hold_or_report_is_refused_and_nothing_is_written()
    {
        await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = Settings([new Uri("http://site.test/p/a")]);
        settings["ume_seed.pii_mode"] = "off";

        var error = await Should.ThrowAsync<PostgresException>(() => SeedAsync(settings));

        error.MessageText.ShouldBe("seed-url-list: ume_seed.pii_mode must be hold or report, not off");
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM config."Collections" WHERE "Slug" = $1""",
            settings["ume_seed.collection_slug"])).ShouldBe(0);
    }

    [Fact]
    public async Task With_a_key_rag_api_accepts_the_seeded_key_for_retrieval_on_the_collection_and_a_rerun_keeps_it()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name, marker))));
        await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = WithKey(Settings([site.Url("/p/a"), site.Url("/p/b")]), Hasher(KeyPepper).Generate(), out var key);
        await using var api = ApiHost();
        var snapshots = api.Services.GetRequiredService<IPolicySnapshotSource>();
        await WaitAsync(() => snapshots.IsConfirmed, "kalla-api confirms its first snapshot");
        var versionBefore = await ConfigVersionAsync();

        var first = await SeedAsync(settings);

        first["level_policy"].ShouldStartWith("public (");
        first["client"].ShouldEndWith("(created, internal, ceiling public)");
        first["api_key"].ShouldNotBeNull().ShouldContain("(created, scoped to " + settings["ume_seed.collection_slug"]);
        first["api_key_state"].ShouldBe("created");
        var seed = await SourceSeedAsync(first);
        var clientId = Guid.Parse(first["client_id"].ShouldNotBeNull());
        var version = long.Parse(first["config_version"].ShouldNotBeNull(), System.Globalization.CultureInfo.InvariantCulture);
        version.ShouldBeGreaterThan(versionBefore);
        (await Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(*) FROM config."ConfigOutbox" WHERE "EntityType" IN ('Clients', 'Keys') AND "EntityId" IN ($1, $2)""",
            clientId.ToString(), first["api_key_id"].ShouldNotBeNull())).ShouldBe(2);

        try
        {
            // The config version reaches the running api: its snapshot holds the new client.
            await WaitAsync(() => snapshots.Current.Version >= version, "kalla-api applies the seeded config version");
            snapshots.Current.Clients[clientId].Kind.ShouldBe(ClientKinds.Internal);

            var found = (await api.Services.GetRequiredService<KeyStore>().FindAsync(key.PlainText, Ct)).ShouldNotBeNull();
            found.ClientId.ShouldBe(clientId);
            found.Scopes.CollectionIds.ShouldBe(new[] { seed.CollectionId });
            KeyChecks.Refusal(found, key.PlainText, DateTimeOffset.UtcNow, null).ShouldBeNull();
            // The same key under another pepper is not a key of this platform.
            (await OtherPepperKeyStore(api).FindAsync(key.PlainText, Ct)).ShouldBeNull();

            await using (var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs))
            {
                (await EngineKit.WaitForRunAsync(database, Guid.Parse(first["sync_job_id"].ShouldNotBeNull())))
                    .ShouldBe(new RunCounters(RunStates.Succeeded, 2, 0, 0, 0, 0, 0));
            }

            using var http = api.CreateHttpsClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.PlainText);
            using var response = await http.PostAsync(new Uri("/v1/retrieve", UriKind.Relative),
                JsonContent.Create(new { query = "bygglov " + marker, collections = new[] { settings["ume_seed.collection_slug"] }, top_k = 5 }), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["results"]!.AsArray();
            results.ShouldNotBeEmpty();
            results.ShouldAllBe(r => r!["collection"]!.GetValue<string>() == settings["ume_seed.collection_slug"]);

            var rerun = await SeedAsync(WithKey(settings, Hasher(KeyPepper).Generate(), out _));

            rerun["client"].ShouldEndWith("(existing, internal, ceiling public)");
            rerun["api_key_state"].ShouldBe("existing");
            rerun["api_key_id"].ShouldBe(first["api_key_id"]);
            rerun["api_key"].ShouldNotBeNull().ShouldContain("is not printed again");
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM config."Keys" WHERE "ClientId" = $1""", clientId)).ShouldBe(1);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_key_hashed_with_another_pepper_is_refused_by_rag_api()
    {
        await EnsurePlatformPipelineAsync();
        await EnsureProfileAsync();
        var settings = Settings([new Uri("http://site.test/p/a")]);
        settings["ume_seed.enqueue"] = "false";
        var wrong = Hasher("not-the-apphost-pepper-0123456789abcdef").Generate();
        var seeded = await SeedAsync(WithKey(settings, wrong, out _));
        var seed = await SourceSeedAsync(seeded);
        try
        {
            seeded["api_key_state"].ShouldBe("created");
            await using var api = ApiHost();

            (await api.Services.GetRequiredService<KeyStore>().FindAsync(wrong.PlainText, Ct)).ShouldBeNull();
            using var http = api.CreateHttpsClient();
            http.DefaultRequestHeaders.Add("x-api-key", wrong.PlainText);
            using var response = await http.PostAsync(new Uri("/v1/retrieve", UriKind.Relative),
                JsonContent.Create(new { query = "bygglov", collections = new[] { settings["ume_seed.collection_slug"] } }), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>().ShouldBe(KeyChecks.InvalidKey);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>The Python script's key hash (<c>key_hash</c> in <c>seed_url_list.py</c>) has the same test vector.</summary>
    [Fact]
    public void The_key_hasher_matches_the_python_scripts_test_vector() =>
        Hasher("dev-seed-vector-pepper-0123456789abcdef").Hash("kalla-sk-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg")
            .ShouldBe("f2edf7e535b369dedbb773f85b74b5f2b2ef36cf126281929f0c75d2ca0624bf");

    /// <summary>The script's parameters with names unique to the test.</summary>
    private static Dictionary<string, string> Settings(IEnumerable<Uri> urls)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
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

    /// <summary>The source's <c>piiMode</c> as the worker reads it (<see cref="SourceSettings.PiiModeOf"/>).</summary>
    private async Task<PiiMode> PiiModeAsync(Guid sourceId)
    {
        var settings = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Settings"::text FROM config."Sources" WHERE "Id" = $1""", sourceId);
        using var json = System.Text.Json.JsonDocument.Parse(settings);
        return SourceSettings.PiiModeOf(SourceTypes.UrlList, json.RootElement);
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

    private static KeyHasher Hasher(string pepper) => new(Encoding.UTF8.GetBytes(pepper));

    /// <summary>The key parameters as <c>seed_url_list.py --api-key</c> sets them: the hash and display prefix of <paramref name="generated"/>.</summary>
    private static Dictionary<string, string> WithKey(Dictionary<string, string> settings, GeneratedKey generated, out GeneratedKey key)
    {
        key = generated;
        settings["ume_seed.key_hash"] = generated.Hash;
        settings["ume_seed.key_prefix"] = generated.Prefix;
        settings["ume_seed.client_name"] = "Seed client " + settings["ume_seed.collection_slug"];
        return settings;
    }

    /// <summary>kalla-api (knowledge role) on the test database as <c>kalla_api</c>, with its real key store and snapshot, and the FakeGateway.</summary>
    private WebApplicationFactory<api::Program> ApiHost() => new WebApplicationFactory<api::Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(RagDatabase.Api), ["Security:KeyPepper"] = KeyPepper,
            ["Security:CacheKeyPepper"] = new string('c', 32), ["Security:DownloadGrantKeys:k1"] = new string('g', 32),
            ["Gateway:BaseUrl"] = "http://fake-gateway", ["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey, ["BlobStore:Root"] = _blobs.FullName,
            ["Rag:Roles"] = "knowledge",
        })
        {
            web.UseSetting(key, value);
        }

        web.ConfigureTestServices(services =>
            services.UseGatewayCredentialCeiling().AddHttpClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
    });

    private static KeyStore OtherPepperKeyStore(WebApplicationFactory<api::Program> api) =>
        new(Hasher("another-pepper-than-the-api-0123456789"), api.Services.GetRequiredService<IServiceScopeFactory>(),
            api.Services.GetRequiredService<IOptionsMonitor<KeyStoreOptions>>(), TimeProvider.System);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), what);
            await Task.Delay(50, Ct);
        }
    }

    private Task<long> ConfigVersionAsync() =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");
}
