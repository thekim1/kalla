extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Editable embedding profiles before use (B-30; admin design §4.5, Decisions "Editable embedding profiles before use"):
/// the save tests first and stores the new canary, a used profile changes only its ceiling, and <b>Test</b> reports the
/// gateway's refusal, the residency and the dimension count against the FakeGateway.
/// </summary>
public sealed class EmbeddingProfileEndpointsTests(GatewayClientFixture database) : IClassFixture<GatewayClientFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The FakeGateway plus two embedding models: <c>fake-eu-embed</c> served only at <c>Eu</c>, and <c>fake-denied-embed</c>,
    /// which only the <c>External</c> key may use (the gateway's per-key allowlist).
    /// </summary>
    private WebApplicationFactory<FakeGatewayProgram> Gateway() => database.Gateway().WithWebHostBuilder(web =>
    {
        web.UseSetting("FakeGateway:Models:40:Name", "fake-eu-embed");
        web.UseSetting("FakeGateway:Models:40:Kind", "embedding");
        web.UseSetting("FakeGateway:Models:40:Targets:0:Residency", "Eu");
        web.UseSetting("FakeGateway:Models:41:Name", "fake-denied-embed");
        web.UseSetting("FakeGateway:Models:41:Kind", "embedding");
        web.UseSetting("FakeGateway:Models:41:KeyLevels:0", "OnPrem"); // keys up to OnPrem only: rag-admin's credential key may not
        web.UseSetting("FakeGateway:Models:41:Targets:0:Residency", "OnPrem");
    });

    /// <summary>An unused <c>bge-m3</c> profile at 1024 with its index and a canary recorded from another text.</summary>
    private async Task<EmbeddingProfile> ProfileAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var profile = new EmbeddingProfile
        {
            Name = "edit-" + Guid.NewGuid().ToString("N"), GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
            CanaryText = EmbeddingCanaryText.Default, CanaryCheckedAt = DateTimeOffset.UtcNow, CanarySimilarity = 1f, UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        db.EmbeddingProfiles.Add(profile);
        await db.SaveChangesAsync(Ct);
        var old = Vector("Ett gammalt kanariefågelord.", 1024);
        await db.Database.ExecuteSqlAsync($"""UPDATE config."EmbeddingProfiles" SET "CanaryEmbedding" = {old}::vector WHERE "Id" = {profile.Id}""", Ct);
        await db.Database.ExecuteSqlAsync($"SELECT content.rag_ensure_profile_index({profile.Id}, 1024)", Ct);
        return await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id, Ct);
    }

    private sealed record SeededDocument(Guid DocumentId, Guid CollectionId, Guid PipelineVersionId);

    /// <summary>A collection on the profile with one document and no chunk set yet.</summary>
    private async Task<SeededDocument> DocumentAsync(Guid profileId)
    {
        var code = "E" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Profile " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "profile-" + code.ToLowerInvariant(), Name = "Webb " + code, DepartmentId = department.Id, Classification = Classification.Public,
            EmbeddingProfileId = profileId, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "profile-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var version = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, Definition = "{}", PublishedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Webb", Classification = Classification.Public, PipelineId = pipeline.Id,
            Settings = $$"""{"urls":["https://www.umea.se/{{code}}"]}""", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, pipeline, version, source);
        await db.SaveChangesAsync(Ct);
        var document = new Document
        {
            SourceId = source.Id, Classification = Classification.Public, IdentityKind = "url", CanonicalIdentity = $"https://www.umea.se/{code}",
            IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes(code)), Title = code, Url = $"https://www.umea.se/{code}",
        };
        db.Add(document);
        await db.SaveChangesAsync(Ct);
        return new SeededDocument(document.Id, collection.Id, version.Id);
    }

    /// <summary>One chunk set names the profile: from now on it is in use.</summary>
    private async Task UseAsync(Guid profileId)
    {
        var seeded = await DocumentAsync(profileId);
        await using var db = TestPostgres.Context(database.Connection(null));
        db.Add(new ChunkSet
        {
            DocumentId = seeded.DocumentId, Classification = Classification.Public, CollectionId = seeded.CollectionId, PipelineVersionId = seeded.PipelineVersionId,
            EmbeddingProfileId = profileId, ContentHash = RandomNumberGenerator.GetBytes(32), InputHash = RandomNumberGenerator.GetBytes(32),
            State = ChunkSetStates.Held, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static string Vector(string text, int dimensions) =>
        "[" + string.Join(',', fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector(text, dimensions)
            .Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";

    private async Task<(int? Dims, string? Text)> CanaryAsync(Guid id)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var dims = await db.Database.SqlQuery<int?>($"""SELECT vector_dims("CanaryEmbedding") AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {id}""")
            .SingleAsync(Ct);
        var text = await db.Database.SqlQuery<string?>($"""SELECT "CanaryEmbedding"::text AS "Value" FROM config."EmbeddingProfiles" WHERE "Id" = {id}""")
            .SingleAsync(Ct);
        return (dims, text);
    }

    private async Task<List<string>> IndexDefinitionsAsync(Guid id)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var predicate = $"%'{id}'::uuid%";
        return await db.Database.SqlQuery<string>($"""
            SELECT indexdef AS "Value" FROM pg_indexes WHERE schemaname = 'content' AND tablename = 'Chunks' AND indexdef LIKE {predicate}
            """).ToListAsync(Ct);
    }

    private static object Body(EmbeddingProfile p, string? model = null, int? dimensions = null, bool? requestDimensions = null, int? maxInputTokens = null,
        string? maxResidency = null) => new
    {
        gatewayModel = model ?? p.GatewayModel, dimensions = dimensions ?? p.Dimensions, requestDimensions = requestDimensions ?? p.RequestDimensions,
        maxInputTokens = maxInputTokens ?? p.MaxInputTokens, maxResidency = maxResidency ?? ResidencyCeiling.ToHeader(p.MaxResidency),
        version = p.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task An_unused_profile_changes_model_dimensions_and_request_dimensions_records_the_canary_again_and_has_one_index_at_the_new_size()
    {
        var profile = await ProfileAsync();
        var (_, oldCanary) = await CanaryAsync(profile.Id);
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var response = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}",
            Body(profile, model: "fake-eu-embed", dimensions: 768, requestDimensions: true, maxInputTokens: 8191), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var saved = (await response.Content.ReadFromJsonAsync<EmbeddingProfileDto>(Ct))!;
        saved.GatewayModel.ShouldBe("fake-eu-embed");
        saved.Dimensions.ShouldBe(768);
        saved.RequestDimensions.ShouldBeTrue();
        saved.MaxInputTokens.ShouldBe(8191);
        saved.InUse.ShouldBeFalse();
        saved.CanaryRecorded.ShouldBeTrue();
        saved.CanaryCheckedAt.ShouldBeNull(); // the last check was of the old values
        saved.CanarySimilarity.ShouldBeNull();
        var (dims, canary) = await CanaryAsync(profile.Id);
        dims.ShouldBe(768);
        canary.ShouldNotBe(oldCanary);
        var indexes = await IndexDefinitionsAsync(profile.Id);
        indexes.ShouldHaveSingleItem().ShouldContain("halfvec(768)");

        await using var db = TestPostgres.Context(database.Connection(null));
        var audit = await db.AuditLog.Where(a => a.EntityId == profile.Id.ToString() && a.Action == "update").ToListAsync(Ct);
        audit.ShouldHaveSingleItem().EntityType.ShouldBe("embedding-profile");
        audit[0].Details!.ShouldContain("fake-eu-embed");

        // A save that keeps the size keeps the index as it is.
        using var again = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}",
            new { gatewayModel = "fake-eu-embed", dimensions = 768, requestDimensions = true, maxInputTokens = 4000, maxResidency = "Eu", version = saved.Version }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync(Ct));
        (await IndexDefinitionsAsync(profile.Id)).ShouldHaveSingleItem().ShouldContain("halfvec(768)");
    }

    [Fact]
    public async Task A_save_whose_test_fails_is_refused_with_profile_test_failed_and_changes_nothing()
    {
        var profile = await ProfileAsync();
        var (_, oldCanary) = await CanaryAsync(profile.Id);
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        // 768 without sending dimensions: bge-m3 answers 1024.
        using var response = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}", Body(profile, dimensions: 768), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("profile_test_failed");
        problem.GetProperty("params").GetProperty("error").GetString().ShouldBe("bad_vector");
        problem.GetProperty("test").GetProperty("dimensions").GetInt32().ShouldBe(1024);
        await using var db = TestPostgres.Context(database.Connection(null));
        var stored = await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id, Ct);
        stored.Dimensions.ShouldBe(1024);
        stored.Version.ShouldBe(profile.Version);
        stored.CanaryCheckedAt.ShouldNotBeNull();
        (await CanaryAsync(profile.Id)).Text.ShouldBe(oldCanary);
        (await IndexDefinitionsAsync(profile.Id)).ShouldHaveSingleItem().ShouldContain("halfvec(1024)");
    }

    [Fact]
    public async Task A_profile_with_a_chunk_set_refuses_every_field_but_max_residency_with_profile_in_use()
    {
        var profile = await ProfileAsync();
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        // Record the canary of today's values, then put a chunk set on the profile.
        using (var record = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}", Body(profile), Ct))
        {
            record.StatusCode.ShouldBe(HttpStatusCode.OK, await record.Content.ReadAsStringAsync(Ct));
        }

        await UseAsync(profile.Id);
        var current = (await platform.GetFromJsonAsync<EmbeddingProfileDto>($"/api/embedding-profiles/{profile.Id}", Ct))!;
        current.InUse.ShouldBeTrue();
        (await platform.GetFromJsonAsync<EmbeddingProfileDto[]>("/api/embedding-profiles", Ct))!.Single(p => p.Id == profile.Id).InUse.ShouldBeTrue();
        var (_, canary) = await CanaryAsync(profile.Id);
        object Changed(string field) => new
        {
            gatewayModel = field == "model" ? "fake-eu-embed" : "bge-m3", dimensions = field == "dimensions" ? 768 : 1024,
            requestDimensions = field == "requestDimensions", maxInputTokens = field == "maxInputTokens" ? 512 : 8192, maxResidency = "Eu",
            version = current.Version,
        };

        foreach (var field in new[] { "model", "dimensions", "requestDimensions", "maxInputTokens" })
        {
            using var refused = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}", Changed(field), Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, field);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("profile_in_use");
        }

        using var lowered = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}",
            new { gatewayModel = "bge-m3", dimensions = 1024, requestDimensions = false, maxInputTokens = 8192, maxResidency = "OnPrem", version = current.Version }, Ct);
        lowered.StatusCode.ShouldBe(HttpStatusCode.OK, await lowered.Content.ReadAsStringAsync(Ct));
        (await lowered.Content.ReadFromJsonAsync<EmbeddingProfileDto>(Ct))!.MaxResidency.ShouldBe("OnPrem");
        (await CanaryAsync(profile.Id)).Text.ShouldBe(canary); // a used profile keeps its canary

        using var above = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}",
            new { gatewayModel = "bge-m3", dimensions = 1024, requestDimensions = false, maxInputTokens = 8192, maxResidency = "External", version = current.Version }, Ct);
        above.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // above the platform ceiling Eu
    }

    [Fact]
    public async Task Test_reports_model_not_allowed_max_residency_not_met_and_a_wrong_dimension_count()
    {
        var profile = await ProfileAsync();
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        async Task<EmbeddingProfileTestResult> TestAsync(object body)
        {
            using var response = await platform.PostAsJsonAsync($"/api/embedding-profiles/{profile.Id}/test", body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<EmbeddingProfileTestResult>(Ct))!;
        }

        var ok = await TestAsync(new { });
        ok.Ok.ShouldBeTrue();
        ok.Dimensions.ShouldBe(1024);
        ok.Residency.ShouldBe("OnPrem"); // bge-m3's only target, within the Eu ceiling

        var denied = await TestAsync(new { gatewayModel = "fake-denied-embed" });
        denied.Ok.ShouldBeFalse();
        denied.Error.ShouldBe("model_not_allowed");

        var residency = await TestAsync(new { gatewayModel = "fake-eu-embed", maxResidency = "OnPrem" });
        residency.Error.ShouldBe("max_residency_not_met");

        var missing = await TestAsync(new { gatewayModel = "no-such-embed" });
        missing.Error.ShouldBe("model_not_found");

        var wrongSize = await TestAsync(new { dimensions = 768 });
        wrongSize.Error.ShouldBe("bad_vector");
        wrongSize.Dimensions.ShouldBe(1024);

        var shortened = await TestAsync(new { dimensions = 768, requestDimensions = true });
        shortened.Ok.ShouldBeTrue();
        shortened.Dimensions.ShouldBe(768);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id, Ct)).Version.ShouldBe(profile.Version); // nothing stored
    }

    [Fact]
    public async Task A_model_change_removes_the_profiles_cached_embeddings_and_the_workers_guard_sees_the_new_values()
    {
        var profile = await ProfileAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var cached = Vector("Ett cachat stycke.", 1024);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO content."EmbeddingCache" ("Classification", "EmbeddingProfileId", "TextHash", "LastUsedAt", "Embedding")
                VALUES ('public', {profile.Id}, {RandomNumberGenerator.GetBytes(32)}, now(), {cached}::vector)
                """, Ct);
        }

        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        using var response = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}",
            Body(profile, dimensions: 768, requestDimensions: true), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        await using var worker = new Npgsql.NpgsqlConnection(database.Connection(RagDatabase.Worker));
        await worker.OpenAsync(Ct);
        async Task<bool> GuardAsync(int dims, bool request)
        {
            await using var command = new Npgsql.NpgsqlCommand("SELECT content.rag_lock_profile_if_unchanged($1, $2, $3, $4)", worker)
            {
                Parameters = { new() { Value = profile.Id }, new() { Value = "bge-m3" }, new() { Value = dims }, new() { Value = request } },
            };
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        (await GuardAsync(1024, false)).ShouldBeFalse(); // a worker that embedded with the old values inserts nothing
        (await GuardAsync(768, true)).ShouldBeTrue();
        await using var count = new Npgsql.NpgsqlCommand("""SELECT count(*) FROM content."EmbeddingCache" WHERE "EmbeddingProfileId" = $1""", worker)
        {
            Parameters = { new() { Value = profile.Id } },
        };
        ((long)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0);
    }

    [Fact]
    public async Task A_save_waits_for_a_worker_inserting_a_chunk_set_and_then_refuses_with_profile_in_use()
    {
        // #554 review: the worker holds the profile row FOR SHARE from its guard to its commit; the save's FOR UPDATE waits,
        // and its in-use check then sees the set.
        var profile = await ProfileAsync();
        var seeded = await DocumentAsync(profile.Id);
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        await using var worker = new Npgsql.NpgsqlConnection(database.Connection(RagDatabase.Worker));
        await worker.OpenAsync(Ct);
        await using var transaction = await worker.BeginTransactionAsync(Ct);
        await using (var insert = new Npgsql.NpgsqlCommand("""
            INSERT INTO content."ChunkSets" ("Id", "DocumentId", "Classification", "CollectionId", "PipelineVersionId", "EmbeddingProfileId",
                                            "ContentHash", "InputHash", "State", "PiiFlagged", "ChunkCount")
            SELECT $1, $2, 'public', $3, $4, $5, $6, $7, 'writing', false, 0
            WHERE content.rag_lock_profile_if_unchanged($5, 'bge-m3', 1024, false)
            """, worker, transaction)
        {
            Parameters =
            {
                new() { Value = Guid.CreateVersion7() }, new() { Value = seeded.DocumentId }, new() { Value = seeded.CollectionId },
                new() { Value = seeded.PipelineVersionId }, new() { Value = profile.Id }, new() { Value = RandomNumberGenerator.GetBytes(32) },
                new() { Value = RandomNumberGenerator.GetBytes(32) },
            },
        })
        {
            (await insert.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        var save = platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}", Body(profile, model: "fake-eu-embed"), Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        save.IsCompleted.ShouldBeFalse(); // blocked on the row lock
        await transaction.CommitAsync(Ct);

        using var response = await save;
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("profile_in_use");
        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id, Ct)).GatewayModel.ShouldBe("bge-m3");
    }

    [Fact]
    public async Task Without_a_gateway_the_test_says_so_and_the_save_is_refused()
    {
        var profile = await ProfileAsync();
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu).WithWebHostBuilder(web => web.UseSetting("Gateway:BaseUrl", ""));
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);

        using var test = await platform.PostAsJsonAsync($"/api/embedding-profiles/{profile.Id}/test", new { }, Ct);
        (await test.Content.ReadFromJsonAsync<EmbeddingProfileTestResult>(Ct))!.Error.ShouldBe("gateway_not_configured");
        using var save = await platform.PutAsJsonAsync($"/api/embedding-profiles/{profile.Id}", Body(profile), Ct);
        save.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>The mirror synced from <see cref="Gateway"/>, and <c>fake-eu-embed</c>'s context window set by hand (the FakeGateway sends none).</summary>
    private async Task SyncAsync(HttpClient platform, int contextWindow)
    {
        using (var sync = await platform.PostAsync("/api/gateway/sync", null, Ct))
        {
            sync.StatusCode.ShouldBe(HttpStatusCode.OK, await sync.Content.ReadAsStringAsync(Ct));
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        await db.Database.ExecuteSqlAsync($"""UPDATE config."GatewayModels" SET "ContextWindow" = {contextWindow} WHERE "Name" = 'fake-eu-embed'""", Ct);
    }

    [Fact]
    public async Task A_new_profile_takes_its_ceiling_and_input_limit_from_the_model_measures_its_size_and_records_the_canary()
    {
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        await SyncAsync(platform, 8191);
        var name = "new-" + Guid.NewGuid().ToString("N");

        using var response = await platform.PostAsJsonAsync("/api/embedding-profiles", new { name, gatewayModel = "fake-eu-embed" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = (await response.Content.ReadFromJsonAsync<EmbeddingProfileDto>(Ct))!;
        response.Headers.Location!.ToString().ShouldBe($"/api/embedding-profiles/{created.Id}");
        created.Name.ShouldBe(name);
        created.GatewayModel.ShouldBe("fake-eu-embed");
        created.Dimensions.ShouldBe(1024); // measured: the FakeGateway's size when no dimensions are sent
        created.RequestDimensions.ShouldBeFalse();
        created.MaxInputTokens.ShouldBe(8191); // the model's context window
        created.MaxResidency.ShouldBe("Eu"); // the model's only residency
        created.IsDefault.ShouldBeFalse(); // the fixture's profile is the default
        created.InUse.ShouldBeFalse();
        created.CanaryRecorded.ShouldBeTrue();
        created.CanaryCheckedAt.ShouldBeNull();
        (await CanaryAsync(created.Id)).Dims.ShouldBe(1024);

        await using var db = TestPostgres.Context(database.Connection(null));
        var audit = await db.AuditLog.Where(a => a.EntityId == created.Id.ToString() && a.Action == "create").ToListAsync(Ct);
        audit.ShouldHaveSingleItem().EntityType.ShouldBe("embedding-profile");

        // The same name again: already_exists.
        using var again = await platform.PostAsJsonAsync("/api/embedding-profiles", new { name, gatewayModel = "fake-eu-embed" }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync(Ct));
        (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("already_exists");
    }

    [Fact]
    public async Task A_new_profile_that_requests_dimensions_has_that_size_and_its_given_values()
    {
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        await SyncAsync(platform, 8191);

        using var missing = await platform.PostAsJsonAsync("/api/embedding-profiles",
            new { name = "x-" + Guid.NewGuid().ToString("N"), gatewayModel = "fake-eu-embed", requestDimensions = true }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // requestDimensions needs the size

        var name = "short-" + Guid.NewGuid().ToString("N");
        using var response = await platform.PostAsJsonAsync("/api/embedding-profiles",
            new { name, gatewayModel = "fake-eu-embed", requestDimensions = true, dimensions = 768, maxInputTokens = 4000 }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = (await response.Content.ReadFromJsonAsync<EmbeddingProfileDto>(Ct))!;
        created.Dimensions.ShouldBe(768);
        created.RequestDimensions.ShouldBeTrue();
        created.MaxInputTokens.ShouldBe(4000);
        (await CanaryAsync(created.Id)).Dims.ShouldBe(768);
    }

    [Fact]
    public async Task A_new_profile_needs_an_embedding_model_of_the_mirror_and_a_passing_test_embedding()
    {
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        await SyncAsync(platform, 8191);
        await using var db = TestPostgres.Context(database.Connection(null));
        var chat = await db.GatewayModels.AsNoTracking().Where(m => m.ModelKind == GatewayModelModelKinds.Chat).Select(m => m.Name).FirstAsync(Ct);

        foreach (var model in new[] { chat, "no-such-embed" })
        {
            using var refused = await platform.PostAsJsonAsync("/api/embedding-profiles", new { gatewayModel = model, maxInputTokens = 512 }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, model);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("errorCodes").GetProperty("gatewayModel")[0].GetProperty("code").GetString().ShouldBe("settings_model_residency");
        }

        // The admin's key may not use the model: the test fails and nothing is stored.
        var name = "denied-" + Guid.NewGuid().ToString("N");
        using var denied = await platform.PostAsJsonAsync("/api/embedding-profiles",
            new { name, gatewayModel = "fake-denied-embed", maxInputTokens = 512, maxResidency = "OnPrem" }, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await denied.Content.ReadAsStringAsync(Ct));
        var deniedProblem = await denied.Content.ReadFromJsonAsync<JsonElement>(Ct);
        deniedProblem.GetProperty("code").GetString().ShouldBe("profile_test_failed");
        deniedProblem.GetProperty("test").GetProperty("error").GetString().ShouldBe("model_not_allowed");
        (await db.EmbeddingProfiles.AnyAsync(p => p.Name == name, Ct)).ShouldBeFalse();

        // Only a platform admin creates profiles.
        using var editor = await AdminDatabaseFixture.UserAsync(admin, RagRoles.Editor);
        using var forbidden = await editor.PostAsJsonAsync("/api/embedding-profiles", new { gatewayModel = "fake-eu-embed" }, Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_overview_asks_a_platform_admin_for_the_profile_only_while_there_is_no_default()
    {
        await using var gateway = Gateway();
        await using var admin = database.AdminApi(gateway, Residency.Eu);
        using var platform = await AdminDatabaseFixture.UserAsync(admin, RagRoles.PlatformAdmin);
        using var editor = await AdminDatabaseFixture.UserAsync(admin, RagRoles.Editor);

        // The fixture's profile is the default.
        (await platform.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("needsEmbeddingProfile").GetBoolean().ShouldBeFalse();
        (await editor.GetFromJsonAsync<JsonElement>("/api/overview", Ct)).GetProperty("needsEmbeddingProfile").GetBoolean().ShouldBeFalse();
    }
}
