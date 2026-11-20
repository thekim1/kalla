using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Config;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// B-51 (#878; Decisions "Deleting knowledge bases, sources and assistants"): a deleted assistant is in neither the api's
/// snapshot nor the published-versions view, so no client lists or reaches it (identity §4.3 step 5); its name is free for a
/// new one; its conversations stay; and <c>config.rag_retention_config_step</c> purges it only 30 days after the delete and
/// once no conversation names it.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class AssistantDeletionTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PolicySnapshotStore Store(NpgsqlDataSource dataSource) => new(dataSource,
        new ConfigVersionState(Options.Create(new ConfigOptions()), TimeProvider.System, new HostingEnvironment { EnvironmentName = "Development" },
            NullLoggerFactory.Instance),
        NullLogger<PolicySnapshotStore>.Instance);

    [Fact]
    public async Task A_deleted_assistant_is_served_to_no_client_and_its_name_is_free()
    {
        var (client, assistant, slug) = await SeedAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.Connection(Api));
        var who = (PolicySnapshot s) => new RagPrincipal(SubjectKind.OwuiUser, "u", null, null, s.Clients[client], CredentialKind.SecretKey, null,
            new HashSet<string>(), MembershipFacts.None, s.Version);
        var policy = new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds);

        var before = await Store(dataSource).LoadAsync(1, Ct);
        policy.Decide(who(before), ResourceRef.Assistant(assistant), RagAction.Chat, before, true).Allowed.ShouldBeTrue();

        await DeleteAsync(assistant, DateTimeOffset.UtcNow);
        var after = await Store(dataSource).LoadAsync(2, Ct);
        after.Assistants.ContainsKey(assistant).ShouldBeFalse();
        policy.Decide(who(after), ResourceRef.Assistant(assistant), RagAction.Chat, after, true).Allowed.ShouldBeFalse();
        policy.Filter(who(after), [ResourceRef.Assistant(assistant)], RagAction.ListModels, after, true).ShouldBeEmpty();
        (await ScalarAsync(database.Connection(Api), """SELECT count(*) FROM config."PublishedAssistantVersions" WHERE "AssistantId" = $1""",
            assistant)).ShouldBe(0);

        // The name is free at once: a new assistant takes the slug (the unique index covers assistants that are not deleted).
        var (_, again, _) = await SeedAsync(slug);
        again.ShouldNotBe(assistant);
        (await Store(dataSource).LoadAsync(3, Ct)).Assistants[again].Slug.ShouldBe(slug);
    }

    [Fact]
    public async Task The_retention_step_purges_a_deleted_assistant_after_30_days_once_no_conversation_names_it()
    {
        var (client, recent, _) = await SeedAsync();
        var (_, old, _) = await SeedAsync();
        var (_, talked, _) = await SeedAsync();
        await DeleteAsync(recent, DateTimeOffset.UtcNow.AddDays(-10));
        await DeleteAsync(old, DateTimeOffset.UtcNow.AddDays(-31));
        await DeleteAsync(talked, DateTimeOffset.UtcNow.AddDays(-31));
        await ExecuteAsync(database.Connection(null), """
            INSERT INTO chat."Conversations" ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "HistoryMode", "RetentionDays",
                                             "CreatedAt", "LastMessageAt", "ExpiresAt")
            VALUES ($1, 'public', $2, $3, 'owui-user', 'u', 'none', 30, now(), now(), now() + interval '30 days')
            """, Guid.CreateVersion7(), talked, client);

        await using (var connection = new NpgsqlConnection(database.Connection(Admin)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT config.rag_retention_config_step(1000)", connection);
            await command.ExecuteScalarAsync(Ct);
        }

        async Task<long> Rows(Guid id) => await ScalarAsync(database.Connection(null), """SELECT count(*) FROM config."Assistants" WHERE "Id" = $1""", id);
        (await Rows(old)).ShouldBe(0);
        (await ScalarAsync(database.Connection(null), """SELECT count(*) FROM config."AssistantVersions" WHERE "AssistantId" = $1""", old)).ShouldBe(0);
        (await Rows(recent)).ShouldBe(1); // within its 30 days
        (await Rows(talked)).ShouldBe(1); // a conversation still names it
        (await ScalarAsync(database.Connection(null), """SELECT count(*) FROM chat."Conversations" WHERE "AssistantId" = $1""", talked)).ShouldBe(1);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private Task DeleteAsync(Guid assistant, DateTimeOffset at) =>
        ExecuteAsync(database.Connection(null), """UPDATE config."Assistants" SET "DeletedAt" = $2 WHERE "Id" = $1""", assistant, at);

    /// <summary>A public assistant with a published v1, offered to a new Open WebUI client; returns the client, the assistant and its slug.</summary>
    private async Task<(Guid Client, Guid Assistant, string Slug)> SeedAsync(string? slug = null)
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var now = DateTimeOffset.UtcNow;
        var code = Guid.NewGuid().ToString("N")[..10];
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Del " + code, CostCenterCode = code, SyncedAt = now };
        var client = new Client { Name = "Delete test " + code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
        db.AddRange(department, client);
        var assistant = new Assistant
        {
            Slug = slug ?? "del-" + code, Name = "Del " + code, DepartmentId = department.Id, Classification = Classification.Public, UpdatedAt = now,
        };
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var v1 = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", State = AssistantVersionStates.Published, CreatedAt = now, UpdatedAt = now,
            PublishedAt = now, PublishedBy = "test",
        };
        db.AssistantVersions.Add(v1);
        await db.SaveChangesAsync(Ct);
        assistant.PublishedVersionId = v1.Id;
        db.AssistantAudiences.Add(new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id });
        await db.SaveChangesAsync(Ct);
        return (client.Id, assistant.Id, assistant.Slug);
    }
}
