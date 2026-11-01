using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>edit-parse-rule</c> and <c>disable-parse-rule</c> (P1b item 8; processing design §3.5): the first edit creates a
/// <c>manual</c> rule, each edit that changes it is a new revision, a disable stops it, each change is audited with its
/// revisions, and each enqueues a <c>reprocess</c> sync only for the active sources with parser-route documents in the old or
/// the new scope. An edit or disable that changes nothing writes nothing; refused commands write nothing.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class ParseRuleCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Admin = "admin-oid";

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task The_first_edit_creates_a_manual_rule_and_a_changing_edit_makes_a_new_revision()
    {
        var profileId = Guid.CreateVersion7();
        var host = Host();
        var first = await _runner.RunAsync(Edit(profileId, host, ocr: "force"), Guid.NewGuid());

        first.Result.ShouldBeOfType<JobResult.Committed>();
        var created = await CommandSeeds.ResultAsync(database, first.Job.Id);
        (created.GetProperty("state").GetString(), created.GetProperty("changed").GetBoolean()).ShouldBe(("active", true));
        var revision1 = created.GetProperty("revision").GetString()!;
        (await RowAsync(profileId)).ShouldBe("internal|manual|active|force");
        revision1.ShouldBe(await CatalogRevisionAsync(profileId, host), "the result's revision is the one the catalog computes");

        var second = await _runner.RunAsync(Edit(profileId, host, ocr: "off"), Guid.NewGuid());
        var revision2 = (await CommandSeeds.ResultAsync(database, second.Job.Id)).GetProperty("revision").GetString()!;
        revision2.ShouldNotBe(revision1);
        revision2.ShouldBe(await CatalogRevisionAsync(profileId, host));
        (await RowAsync(profileId)).ShouldBe("internal|manual|active|off");

        var same = await _runner.RunAsync(Edit(profileId, host, ocr: "off") with { DecidedBy = "someone-else" }, Guid.NewGuid());
        var unchanged = await CommandSeeds.ResultAsync(database, same.Job.Id);
        (unchanged.GetProperty("changed").GetBoolean(), unchanged.GetProperty("revision").GetString()).ShouldBe((false, revision2));

        (await AuditAsync(profileId)).ShouldBe(
        [
            $"{Admin}|create|-|{revision1}|rag-worker|internal",
            $"{Admin}|update|{revision1}|{revision2}|rag-worker|internal",
        ], "one entry per revision, none for the edit that changed nothing");
    }

    [Fact]
    public async Task A_disable_stops_the_rule_and_an_edit_enables_it_again()
    {
        var profileId = Guid.CreateVersion7();
        var host = Host();
        (await _runner.RunAsync(Edit(profileId, host), Guid.NewGuid())).Result.ShouldBeOfType<JobResult.Committed>();

        var disable = await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = profileId }, Guid.NewGuid());

        disable.Result.ShouldBeOfType<JobResult.Committed>();
        var result = await CommandSeeds.ResultAsync(database, disable.Job.Id);
        (result.GetProperty("state").GetString(), result.GetProperty("changed").GetBoolean()).ShouldBe(("disabled", true));
        (await RowAsync(profileId)).ShouldStartWith("internal|manual|disabled|");
        (await CatalogRevisionAsync(profileId, host)).ShouldBeNull("the catalog serves active rules only");
        var again = await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = profileId, DecidedBy = Admin }, Guid.NewGuid());
        (await CommandSeeds.ResultAsync(database, again.Job.Id)).GetProperty("changed").GetBoolean().ShouldBeFalse();

        (await _runner.RunAsync(Edit(profileId, host), Guid.NewGuid())).Result.ShouldBeOfType<JobResult.Committed>();
        (await RowAsync(profileId)).ShouldStartWith("internal|manual|active|");
        (await CatalogRevisionAsync(profileId, host)).ShouldNotBeNull();
        (await AuditAsync(profileId)).Select(a => string.Join('|', a.Split('|')[..2]))
            .ShouldBe([$"{Admin}|create", "system:worker|disable", $"{Admin}|update"]);
    }

    [Fact]
    public async Task Refused_commands_write_nothing()
    {
        var builtin = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."ParseProfiles" ("Id", "Classification", "Match", "Strategy", "Origin", "State")
            VALUES ($1, 'internal', '{"textPattern":"protokoll"}', '{}', 'builtin', 'active')
            """, builtin);
        (await _runner.RunAsync(Edit(other, Host()), Guid.NewGuid())).Result.ShouldBeOfType<JobResult.Committed>();

        (await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = Guid.CreateVersion7() }, Guid.NewGuid())).Result
            .ShouldBe(new JobResult.FatalError(ParseRuleRows.UnknownCode));
        (await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = builtin }, Guid.NewGuid())).Result
            .ShouldBe(new JobResult.FatalError(ParseRuleRows.NotManualCode));
        (await _runner.RunAsync(Edit(builtin, Host()), Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError(ParseRuleRows.NotManualCode));
        (await _runner.RunAsync(Edit(other, Host()), Guid.NewGuid(), Classification.Public)).Result
            .ShouldBe(new JobResult.FatalError(ParseRuleRows.LevelMismatchCode));
        (await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = other }, Guid.NewGuid(), Classification.Public)).Result
            .ShouldBe(new JobResult.FatalError(ParseRuleRows.LevelMismatchCode));
        var lookbehind = Edit(Guid.CreateVersion7(), Host()) with { Match = new ParseRuleMatch { TextPattern = "(?<=Justerare )\\w+" } };
        (await _runner.RunAsync(lookbehind, Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError("parse_rule.pattern_invalid"));
        var invalid = new { kind = CommandKinds.EditParseRule, profileId = Guid.CreateVersion7(), match = new { } };
        (await _runner.RunAsync(invalid, Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));

        (await RowAsync(builtin)).ShouldBe("internal|builtin|active|");
        (await RowAsync(other)).ShouldStartWith("internal|manual|active|");
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ParseProfiles" WHERE "Id" = $1""", lookbehind.ProfileId)).ShouldBe(0);
        (await AuditAsync(builtin)).ShouldBeEmpty();
        (await AuditAsync(other)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_edit_whose_lease_was_lost_writes_nothing()
    {
        var profileId = Guid.CreateVersion7();
        var job = await _runner.LeaseAsync(Edit(profileId, Host()), Guid.NewGuid(), Classification.Internal);
        await _runner.StealLeaseAsync(job.Id);

        (await _runner.RunAsync(job)).ShouldBeOfType<JobResult.Success>();

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."ParseProfiles" WHERE "Id" = $1""", profileId)).ShouldBe(0);
        (await AuditAsync(profileId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rule_change_reprocesses_only_the_sources_with_parser_documents_in_the_old_or_new_scope()
    {
        // A: PDFs on host A; B: a PDF on host B; C: only HTML on host A; D: a PDF on host A at another level; E: host A's PDF,
        // soft-deleted; F: a PDF on host A outside the path glob.
        var hostA = Host();
        var hostB = Host();
        var a = await EngineKit.SeedUrlListAsync(database, []);
        var b = await EngineKit.SeedUrlListAsync(database, []);
        var c = await EngineKit.SeedUrlListAsync(database, []);
        var d = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var e = await EngineKit.SeedUrlListAsync(database, []);
        var f = await EngineKit.SeedUrlListAsync(database, []);
        var seeds = new[] { a, b, c, d, e, f };
        try
        {
            await CommandSeeds.DocumentAsync(database, a, $"https://{hostA}/protokoll/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, a, $"https://{hostA}/protokoll/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, b, $"https://{hostB}/protokoll/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, c, $"https://{hostA}/protokoll/{Guid.NewGuid():N}");
            await CommandSeeds.DocumentAsync(database, d, $"https://{hostA}/protokoll/{Guid.NewGuid():N}.pdf", "application/pdf");
            var deleted = await CommandSeeds.DocumentAsync(database, e, $"https://{hostA}/protokoll/{Guid.NewGuid():N}.pdf", "application/pdf");
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "DeletedAt" = now() WHERE "Id" = $1""", deleted);
            await CommandSeeds.DocumentAsync(database, f, $"https://{hostA}/nyheter/{Guid.NewGuid():N}.pdf", "application/pdf");
            var profileId = Guid.CreateVersion7();

            // A text pattern does not narrow it: the rule is a candidate of every document in its scope.
            var onA = Edit(profileId, hostA) with { Match = new ParseRuleMatch { Domain = hostA, PathGlob = "/protokoll/*", TextPattern = "kallelse" } };
            var edit = await _runner.RunAsync(onA, a.DepartmentId);

            edit.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, edit.Job.Id)).GetProperty("reprocessSyncs").GetInt32().ShouldBe(1);
            (await SyncsAsync(seeds)).ShouldBe([a.SourceId]);
            await AssertSyncJobAsync(a);

            await DeleteSyncsAsync();
            var onB = onA with { Match = onA.Match with { Domain = hostB } };
            (await _runner.RunAsync(onB, a.DepartmentId)).Result.ShouldBeOfType<JobResult.Committed>();
            (await SyncsAsync(seeds)).ShouldBe([a.SourceId, b.SourceId], ignoreOrder: true, "the old scope's source and the new one's");

            await DeleteSyncsAsync();
            (await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = profileId }, a.DepartmentId)).Result.ShouldBeOfType<JobResult.Committed>();
            (await SyncsAsync(seeds)).ShouldBe([b.SourceId]);

            await DeleteSyncsAsync();
            (await _runner.RunAsync(new DisableParseRuleCommand { ProfileId = profileId }, a.DepartmentId)).Result.ShouldBeOfType<JobResult.Committed>();
            (await SyncsAsync(seeds)).ShouldBeEmpty("a disabled rule disabled again changes nothing");
        }
        finally
        {
            foreach (var seed in seeds)
            {
                await EngineKit.PauseAsync(database, seed);
            }
        }
    }

    [Fact]
    public async Task A_department_scoped_rule_reprocesses_only_its_departments_sources()
    {
        var host = Host();
        var a = await EngineKit.SeedUrlListAsync(database, []);
        var b = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await CommandSeeds.DocumentAsync(database, a, $"https://{host}/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, b, $"https://{host}/{Guid.NewGuid():N}.pdf", "application/pdf");

            (await _runner.RunAsync(Edit(Guid.CreateVersion7(), host) with { DepartmentId = b.DepartmentId }, b.DepartmentId)).Result
                .ShouldBeOfType<JobResult.Committed>();

            (await SyncsAsync([a, b])).ShouldBe([b.SourceId]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, a);
            await EngineKit.PauseAsync(database, b);
        }
    }

    private static EditParseRuleCommand Edit(Guid profileId, string host, string? ocr = "force") => new()
    {
        ProfileId = profileId,
        Match = new ParseRuleMatch { Domain = host, MediaType = "application/pdf" },
        Strategy = new ParseRuleStrategy { Ocr = ocr },
        CleanupRules = new ParseRuleCleanup { MaxLevel = 3, Furniture = ["^Justerare"] },
        DecidedBy = Admin,
    };

    private static string Host() => $"r{Guid.NewGuid():N}.example.se";

    /// <summary><c>Classification|Origin|State|ocr</c> of the row.</summary>
    private Task<string> RowAsync(Guid profileId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "Classification" || '|' || "Origin" || '|' || "State" || '|' || coalesce("Strategy" ->> 'ocr', '') FROM content."ParseProfiles" WHERE "Id" = $1
            """, profileId);

    /// <summary>The rule's revision as a fresh catalog serves it to a PDF on the rule's host, or null when it serves none.</summary>
    private async Task<string?> CatalogRevisionAsync(Guid profileId, string host)
    {
        var config = new ConfigVersionState(Options.Create(new ConfigOptions()), TimeProvider.System, new HostingEnvironment { ApplicationName = "test" },
            NullLoggerFactory.Instance);
        var catalog = new ParseRuleCatalog(database.Database(RagDatabase.Worker), config, TimeProvider.System, NullLogger<ParseRuleCatalog>.Instance);
        var candidates = await catalog.CandidatesForDepartmentAsync(Classification.Internal, Guid.NewGuid(), $"https://{host}/a.pdf", "application/pdf",
            TestContext.Current.CancellationToken);
        return candidates.SingleOrDefault(r => r.Name == profileId.ToString("D"))?.Revision;
    }

    /// <summary>The rule's audit entries in order: <c>Actor|Action|before revision|after revision|Service|Classification</c>.</summary>
    private async Task<List<string>> AuditAsync(Guid profileId)
    {
        var entries = new List<string>();
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Actor" || '|' || "Action" || '|' || coalesce("Details" -> 'before' ->> 'revision', '-') || '|' || ("Details" -> 'after' ->> 'revision')
                   || '|' || "Service" || '|' || "Classification"
            FROM audit."AuditLog" WHERE "EntityType" = 'parserule' AND "EntityId" = $1 ORDER BY "Id"
            """, connection) { Parameters = { new() { Value = profileId.ToString("D") } } };
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(reader.GetString(0));
        }

        return entries;
    }

    /// <summary>The seeds' sources with a queued sync.</summary>
    private async Task<List<Guid>> SyncsAsync(IEnumerable<SourceSeed> seeds)
    {
        var queued = new List<Guid>();
        foreach (var seed in seeds)
        {
            if (await CommandSeeds.SyncJobsAsync(database, seed.SourceId) > 0)
            {
                queued.Add(seed.SourceId);
            }
        }

        return queued;
    }

    /// <summary>The queued sync of the seed: a <c>manual</c> <c>reprocess</c>, its dedup key, level and department, held back for the catalog's max age.</summary>
    private async Task AssertSyncJobAsync(SourceSeed seed)
    {
        var row = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "Payload"::text || '|' || "DedupKey" || '|' || "Classification" || '|' || "DepartmentId"::text || '|'
                   || ("AvailableAt" > now() + interval '50 seconds')::text
            FROM content."Jobs" WHERE "Class" = 'sync' AND "Payload" ->> 'sourceId' = $1
            """, seed.SourceId.ToString());
        var parts = row.Split('|');
        using var payload = JsonDocument.Parse(parts[0]);
        var sync = ContractJson.Deserialize<SyncPayload>(payload.RootElement);
        (sync.Kind, sync.Trigger, sync.RunId).ShouldBe((SyncKind.Reprocess, RunTrigger.Manual, (Guid?)null));
        parts[1..].ShouldBe([JobDedupKeys.Sync(seed.SourceId), seed.Classification, seed.DepartmentId.ToString(), "true"]);
    }

    private Task DeleteSyncsAsync() => Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "Class" = 'sync'""");
}
