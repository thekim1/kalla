using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The api's access snapshot (identity design §4.2, knowledge §12.1) read with the api role's grants: published versions
/// only, the granted columns of Sources, and a reload only when a table it holds changed.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PolicySnapshotTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PolicySnapshotStore Store(NpgsqlDataSource dataSource) => new(dataSource,
        new ConfigVersionState(Options.Create(new ConfigOptions()), TimeProvider.System, new HostingEnvironment { EnvironmentName = "Development" },
            NullLoggerFactory.Instance),
        NullLogger<PolicySnapshotStore>.Instance);

    [Fact]
    public async Task The_snapshot_holds_published_versions_audiences_sources_and_levels_as_the_api_reads_them()
    {
        var seeded = await SeedAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.Connection(Api));

        var snapshot = await Store(dataSource).LoadAsync(42, Ct);

        snapshot.Version.ShouldBe(42);
        snapshot.Clients[seeded.ClientId].ShouldBe(new ClientEntry(seeded.ClientId, "Open WebUI test", ClientKinds.OpenWebUi, Classification.Internal, true, true));
        var assistant = snapshot.Assistants[seeded.AssistantId];
        assistant.Published.ShouldNotBeNull();
        assistant.Published.Number.ShouldBe(1); // the newer draft never reaches the api
        assistant.Published.CollectionIds.ShouldBe([seeded.CollectionId]);
        assistant.Audiences.ShouldBe([new AudienceEntry(AssistantAudienceKinds.Client, seeded.ClientId, null)]);
        snapshot.Collections[seeded.CollectionId].IsLive.ShouldBeTrue();
        snapshot.Collections[seeded.UnpublishedCollectionId].IsLive.ShouldBeFalse();
        snapshot.CollectionsBySlug[snapshot.Collections[seeded.CollectionId].Slug].Id.ShouldBe(seeded.CollectionId);
        snapshot.SourcesByCollection[seeded.CollectionId].ShouldBe([seeded.SourceId]);
        snapshot.EmbeddingProfiles[seeded.ProfileId].MaxResidency.ShouldBe(Residency.Eu);
        Classification.All.ShouldBeSubsetOf(snapshot.LevelPolicies.Keys);

        // The policy decides on it: the client's own audience, at its level.
        var who = new RagPrincipal(SubjectKind.OwuiUser, "u", null, null, snapshot.Clients[seeded.ClientId], CredentialKind.SecretKey, null,
            new HashSet<string>(), MembershipFacts.None, snapshot.Version);
        new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds).Decide(who, ResourceRef.Assistant(seeded.AssistantId), RagAction.Chat, snapshot, true)
            .Allowed.ShouldBeTrue();
    }

    [Fact]
    public void A_level_without_a_saved_row_gets_the_admins_defaults()
    {
        // data-model §2.1 seeds no DataProtectionPolicies rows; the admin shows the code defaults until a row is saved, so
        // the snapshot applies the same ones instead of refusing every assistant at step 9 (found in the item 15 live check).
        var levels = new Dictionary<string, LevelPolicyEntry>(StringComparer.Ordinal)
        {
            [Classification.Public] = new(Classification.Public, [HistoryModes.None], false),
        };

        PolicySnapshotStore.WithDefaults(levels);

        levels.Keys.ShouldBe(Classification.All, ignoreOrder: true);
        levels[Classification.Internal].ShouldBe(new LevelPolicyEntry(Classification.Internal, levels[Classification.Internal].AllowedHistoryModes, false));
        levels[Classification.Internal].AllowedHistoryModes.ShouldBe([HistoryModes.None]);
        levels[Classification.Sensitive].ClientsWithoutHistoryOnly.ShouldBeTrue(); // still refuses Open WebUI (it stores history)
        var admin = DataProtectionPolicy.DefaultFor(Classification.Sensitive);
        (admin.RetentionMaxDays, admin.EncryptionRequired, admin.BlobMasterKeyName).ShouldBe((30, true, "rag-blob-sensitive"));
        DataProtectionPolicy.DefaultFor(Classification.Public).BlobMasterKeyName.ShouldBeNull();
    }

    [Fact]
    public async Task A_change_to_a_table_it_does_not_hold_only_moves_the_version()
    {
        await SeedAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.Connection(Api));
        var store = Store(dataSource);
        await store.ApplyAsync(new ConfigChange(-1, 10, FullReload: true, new HashSet<string>(), true), Ct);
        var first = store.Current;

        await store.ApplyAsync(new ConfigChange(10, 11, false, new HashSet<string> { "Keys" }, true), Ct);
        store.Current.Version.ShouldBe(11);
        store.Current.Clients.ShouldBeSameAs(first.Clients);

        await store.ApplyAsync(new ConfigChange(11, 12, false, new HashSet<string> { "Collections" }, true), Ct);
        store.Current.Version.ShouldBe(12);
        store.Current.Clients.ShouldNotBeSameAs(first.Clients);
    }

    private sealed record Seeded(Guid ClientId, Guid AssistantId, Guid CollectionId, Guid UnpublishedCollectionId, Guid SourceId, Guid ProfileId);

    /// <summary>A client, a profile, a published and an unpublished collection, a source, and an assistant with v1 published and a v2 draft.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO config."DataProtectionPolicies" ("Classification", "AllowedHistoryModes", "DefaultHistoryMode", "RetentionDefaultDays",
                "RetentionMaxDays", "EncryptionRequired", "ReviewSecondApprover", "ClientsWithoutHistoryOnly", "UpdatedAt")
            SELECT l, ARRAY['none']::varchar(20)[], 'none', 30, 90, l <> 'public', false, l = 'sensitive', now()
            FROM unnest(ARRAY['public', 'internal', 'sensitive']) AS l
            ON CONFLICT DO NOTHING
            """, Ct);
        var client = new Client { Name = "Open WebUI test", Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, StoresHistory = true, UpdatedAt = now };
        var profile = new EmbeddingProfile { Name = "p-" + Guid.NewGuid().ToString("N"), GatewayModel = "bge-m3", Dimensions = 3, MaxInputTokens = 10, MaxResidency = Residency.Eu, UpdatedAt = now };
        db.AddRange(client, profile);
        await db.SaveChangesAsync(Ct);
        Collection NewCollection(DateTimeOffset? published) => new()
        {
            Slug = "c-" + Guid.NewGuid().ToString("N"), Name = "C", DepartmentId = Guid.NewGuid(), Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, PublishedAt = published, UpdatedAt = now,
        };
        var collection = NewCollection(now);
        var unpublished = NewCollection(null);
        var pipeline = new Pipeline { Name = "p-" + Guid.NewGuid().ToString("N"), TemplateId = "url-list", UpdatedAt = now };
        db.AddRange(collection, unpublished, pipeline);
        await db.SaveChangesAsync(Ct);
        var source = new Source { CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "S", Classification = Classification.Public, PipelineId = pipeline.Id, UpdatedAt = now };
        var assistant = new Assistant { Slug = "a-" + Guid.NewGuid().ToString("N"), Name = "A", DepartmentId = collection.DepartmentId, Classification = Classification.Internal, UpdatedAt = now };
        db.AddRange(source, assistant);
        await db.SaveChangesAsync(Ct);

        var v1 = new AssistantVersion { AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", CollectionIds = [collection.Id], CreatedAt = now, UpdatedAt = now };
        db.AssistantVersions.Add(v1);
        await db.SaveChangesAsync(Ct);
        v1.State = AssistantVersionStates.Published;
        v1.PublishedAt = now;
        v1.PublishedBy = "test";
        assistant.PublishedVersionId = v1.Id;
        await db.SaveChangesAsync(Ct);
        var v2 = new AssistantVersion { AssistantId = assistant.Id, Number = 2, GatewayModel = "ume/chat-eu", CollectionIds = [unpublished.Id], CreatedAt = now, UpdatedAt = now };
        db.AssistantVersions.Add(v2);
        db.AssistantAudiences.Add(new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id });
        await db.SaveChangesAsync(Ct);
        assistant.DraftVersionId = v2.Id;
        await db.SaveChangesAsync(Ct);
        return new Seeded(client.Id, assistant.Id, collection.Id, unpublished.Id, source.Id, profile.Id);
    }
}
