using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The cost report (admin design §8.6, §11.2; B-40) on seeded usage rows: sums by day and by month with the gateway's cost
/// and the estimate apart; scope by the row's department (a department admin gets 404 for another department's assistant
/// and no foreign rows from <c>/api/costs</c>, a platform admin sees all, a department a knowledge base is shared with sees
/// no cost); deleted objects; the CSV's headers; <c>period_too_long</c>; an assistant's turns and cost by outcome
/// (<c>AssistantDailyOutcomes</c>, Decisions "Cost per outcome") in the same scope.
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class CostEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Range = "from=2025-03-01&to=2025-04-30";

    private sealed record Seeded(Department A, Department B, Assistant OwnA, Assistant OwnB, Collection SharedA, Client Client, Guid DeletedAssistant);

    private async Task<Seeded> SeedAsync(RagDbContext db)
    {
        var code = Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var a = new Department { Id = Guid.CreateVersion7(), Name = "Kost A " + code, CostCenterCode = "CA" + code, SyncedAt = DateTimeOffset.UtcNow };
        var b = new Department { Id = Guid.CreateVersion7(), Name = "Kost B " + code, CostCenterCode = "CB" + code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin)).AddEmbeddingProfileAsync(8, Ct);
        var ownA = new Assistant { Slug = "cost-a-" + code, Name = "Kostguide A " + code, DepartmentId = a.Id, Classification = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        var ownB = new Assistant { Slug = "cost-b-" + code, Name = "Kostguide B " + code, DepartmentId = b.Id, Classification = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        var shared = new Collection
        {
            Slug = "cost-" + code, Name = "Kostbas " + code, DepartmentId = a.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            SharedWith = CollectionSharingLevels.Municipality, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var client = new Client { Name = "Cost client " + code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" };
        db.AddRange(a, b, ownA, ownB, shared, client);
        await db.SaveChangesAsync(Ct);
        var deleted = Guid.CreateVersion7(); // an assistant whose row is gone

        AssistantDailyUsage Chat(Assistant assistant, Guid department, string day, string step, int calls, long input, long output, decimal cost, decimal estimated = 0m,
            int estimatedCalls = 0, long cached = 0) => new()
            {
                Day = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), AssistantId = assistant.Id, ClientId = client.Id,
                Classification = Classification.Internal, Step = step, DepartmentId = department, Calls = calls, InputTokens = input, CachedInputTokens = cached,
                OutputTokens = output, CostUsd = cost, EstimatedCalls = estimatedCalls, EstimatedCostUsd = estimated,
            };
        db.AssistantDailyUsage.AddRange(
            Chat(ownA, a.Id, "2025-03-10", "chat-answer", 2, 100, 50, 0.002m, cached: 10),
            Chat(ownA, a.Id, "2025-03-11", "chat-answer", 1, 50, 20, 0m, 0.0005m, 1),
            Chat(ownA, a.Id, "2025-03-11", "query-embed", 1, 8, 0, 0.0000016m),
            Chat(ownA, a.Id, "2025-04-02", "chat-answer", 1, 40, 10, 0.001m),
            // The assistant was in B when this call was made: its cost stays with B.
            Chat(ownA, b.Id, "2025-03-12", "chat-answer", 1, 40, 10, 0.009m),
            Chat(ownB, b.Id, "2025-03-10", "chat-answer", 1, 30, 10, 0.003m),
            Chat(ownA, a.Id, "2024-12-31", "chat-answer", 1, 30, 10, 0.5m)); // outside the range
        db.AssistantDailyUsage.Add(new AssistantDailyUsage
        {
            Day = new DateOnly(2025, 3, 10), AssistantId = deleted, ClientId = client.Id, Classification = Classification.Internal, Step = "chat-answer",
            DepartmentId = a.Id, Calls = 1, InputTokens = 10, OutputTokens = 5, CostUsd = 0.0001m,
        });
        AssistantDailyOutcome Outcome(Assistant assistant, Guid department, string day, string outcome, int turns, decimal cost, decimal estimated = 0m) => new()
        {
            Day = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), AssistantId = assistant.Id, ClientId = client.Id,
            Classification = Classification.Internal, Outcome = outcome, DepartmentId = department, Turns = turns, CostUsd = cost, EstimatedCostUsd = estimated,
        };
        db.AssistantDailyOutcomes.AddRange(
            Outcome(ownA, a.Id, "2025-03-10", "answered", 2, 0.002m),
            Outcome(ownA, a.Id, "2025-03-11", "answered", 1, 0m, 0.0005m),
            Outcome(ownA, a.Id, "2025-03-11", "not-found", 1, 0.0000016m),
            Outcome(ownA, a.Id, "2025-04-02", "error", 1, 0.001m),
            // The assistant was in B when this turn was asked: it stays with B.
            Outcome(ownA, b.Id, "2025-03-12", "answered", 1, 0.009m),
            Outcome(ownB, b.Id, "2025-03-10", "answered", 1, 0.003m),
            Outcome(ownA, a.Id, "2024-12-31", "answered", 1, 0.5m)); // outside the range
        db.CollectionDailyUsage.AddRange(
            new CollectionDailyUsage
            {
                Day = new DateOnly(2025, 3, 10), CollectionId = shared.Id, Classification = Classification.Internal, Step = "embed", DepartmentId = a.Id, Calls = 5,
                InputTokens = 1000, CostUsd = 0.00002m,
            },
            new CollectionDailyUsage
            {
                Day = new DateOnly(2025, 3, 11), CollectionId = shared.Id, Classification = Classification.Internal, Step = "eval-judge", DepartmentId = a.Id, Calls = 1,
                InputTokens = 300, OutputTokens = 40, EstimatedCalls = 1, EstimatedCostUsd = 0.0001m,
            });
        await db.SaveChangesAsync(Ct);
        return new Seeded(a, b, ownA, ownB, shared, client, deleted);
    }

    private static async Task<JsonElement> OkAsync(HttpClient user, string path)
    {
        using var response = await user.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static (string Period, string Step, long Calls, decimal Cost, decimal Estimated, long EstimatedCalls) Row(JsonElement r) =>
        (r.GetProperty("period").GetString()!, r.GetProperty("step").GetString()!, r.GetProperty("calls").GetInt64(), r.GetProperty("costUsd").GetDecimal(),
            r.GetProperty("estimatedCostUsd").GetDecimal(), r.GetProperty("estimatedCalls").GetInt64());

    [Fact]
    public async Task An_assistant_and_a_knowledge_base_sum_by_day_and_by_month_with_the_estimate_apart()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        var factory = host.Factory;
        using var adminA = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.A.CostCenterCode);
        using var viewerA = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, s.A.CostCenterCode);

        // By day: the row of department B (the assistant was there then) and the day outside the range are left out.
        var byDay = await OkAsync(viewerA, $"/api/assistants/{s.OwnA.Id}/costs?{Range}");
        byDay.GetProperty("granularity").GetString().ShouldBe("day");
        byDay.GetProperty("rows").EnumerateArray().Select(Row).ShouldBe(
        [
            ("2025-03-10", "chat-answer", 2, 0.002m, 0m, 0), ("2025-03-11", "chat-answer", 1, 0m, 0.0005m, 1), ("2025-03-11", "query-embed", 1, 0.0000016m, 0m, 0),
            ("2025-04-02", "chat-answer", 1, 0.001m, 0m, 0),
        ]);
        var first = byDay.GetProperty("rows")[0];
        (first.GetProperty("clientId").GetGuid(), first.GetProperty("clientName").GetString(), first.GetProperty("cachedInputTokens").GetInt64())
            .ShouldBe((s.Client.Id, s.Client.Name, 10L));
        var totals = byDay.GetProperty("totals");
        (totals.GetProperty("calls").GetInt64(), totals.GetProperty("costUsd").GetDecimal(), totals.GetProperty("estimatedCostUsd").GetDecimal(),
            totals.GetProperty("estimatedCalls").GetInt64(), totals.GetProperty("inputTokens").GetInt64()).ShouldBe((5L, 0.0030016m, 0.0005m, 1L, 198L));

        // By month: a month's period is its first day.
        var byMonth = await OkAsync(viewerA, $"/api/assistants/{s.OwnA.Id}/costs?{Range}&granularity=month");
        byMonth.GetProperty("rows").EnumerateArray().Select(Row).ShouldBe(
        [
            ("2025-03-01", "chat-answer", 3, 0.002m, 0.0005m, 1), ("2025-03-01", "query-embed", 1, 0.0000016m, 0m, 0), ("2025-04-01", "chat-answer", 1, 0.001m, 0m, 0),
        ]);

        // The knowledge base: per period and step, no client.
        var collection = await OkAsync(viewerA, $"/api/collections/{s.SharedA.Id}/costs?{Range}");
        collection.GetProperty("rows").EnumerateArray().Select(Row).ShouldBe([("2025-03-10", "embed", 5, 0.00002m, 0m, 0), ("2025-03-11", "eval-judge", 1, 0m, 0.0001m, 1)]);
        collection.GetProperty("rows")[0].GetProperty("clientId").ValueKind.ShouldBe(JsonValueKind.Null);

        // Scope: another department's assistant is 404; so is a knowledge base shared with the caller's department.
        (await adminA.GetAsync($"/api/assistants/{s.OwnB.Id}/costs?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var adminB = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.B.CostCenterCode);
        (await adminB.GetAsync($"/api/collections/{s.SharedA.Id}/costs?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetAsync($"/api/assistants/{s.OwnA.Id}/costs?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static (string Period, string Outcome, long Turns, decimal Cost, decimal Estimated) OutcomeRow(JsonElement r) =>
        (r.GetProperty("period").GetString()!, r.GetProperty("outcome").GetString()!, r.GetProperty("turns").GetInt64(), r.GetProperty("costUsd").GetDecimal(),
            r.GetProperty("estimatedCostUsd").GetDecimal());

    [Fact]
    public async Task An_assistants_outcomes_sum_by_period_outcome_and_client_within_the_callers_departments()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        var factory = host.Factory;
        using var viewerA = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, s.A.CostCenterCode);

        // By day, the gateway's cost and the estimate apart; the turn asked while the assistant was in B and the day outside the range left out.
        var byDay = await OkAsync(viewerA, $"/api/assistants/{s.OwnA.Id}/costs?{Range}");
        byDay.GetProperty("outcomes").EnumerateArray().Select(OutcomeRow).ShouldBe(
        [
            ("2025-03-10", "answered", 2, 0.002m, 0m), ("2025-03-11", "answered", 1, 0m, 0.0005m), ("2025-03-11", "not-found", 1, 0.0000016m, 0m),
            ("2025-04-02", "error", 1, 0.001m, 0m),
        ]);
        var first = byDay.GetProperty("outcomes")[0];
        (first.GetProperty("clientId").GetGuid(), first.GetProperty("clientName").GetString()).ShouldBe((s.Client.Id, s.Client.Name));

        // By month: a month's period is its first day.
        (await OkAsync(viewerA, $"/api/assistants/{s.OwnA.Id}/costs?{Range}&granularity=month")).GetProperty("outcomes").EnumerateArray().Select(OutcomeRow).ShouldBe(
        [
            ("2025-03-01", "answered", 3, 0.002m, 0.0005m), ("2025-03-01", "not-found", 1, 0.0000016m, 0m), ("2025-04-01", "error", 1, 0.001m, 0m),
        ]);

        // Scope: another department's assistant is 404; B's own assistant shows B's rows only.
        using var adminB = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.B.CostCenterCode);
        (await adminB.GetAsync($"/api/assistants/{s.OwnA.Id}/costs?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OkAsync(adminB, $"/api/assistants/{s.OwnB.Id}/costs?{Range}")).GetProperty("outcomes").EnumerateArray().Select(OutcomeRow)
            .ShouldBe([("2025-03-10", "answered", 1, 0.003m, 0m)]);

        // A platform admin's departments include B, so the turn of the assistant's time in B is counted too.
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        (await OkAsync(platform, $"/api/assistants/{s.OwnA.Id}/costs?{Range}")).GetProperty("outcomes").EnumerateArray().Select(OutcomeRow)
            .ShouldContain(("2025-03-12", "answered", 1, 0.009m, 0m));

        // A knowledge base's cost has no outcomes.
        (await OkAsync(viewerA, $"/api/collections/{s.SharedA.Id}/costs?{Range}")).TryGetProperty("outcomes", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_department_report_shows_the_callers_departments_only_and_a_platform_admin_all()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        var factory = host.Factory;
        using var adminA = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.A.CostCenterCode);
        using var adminB = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.B.CostCenterCode);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var a = await OkAsync(adminA, $"/api/costs?{Range}");
        var rows = a.GetProperty("rows").EnumerateArray().ToList();
        rows.ShouldAllBe(r => r.GetProperty("departmentId").GetGuid() == s.A.Id);
        rows.Select(r => (r.GetProperty("kind").GetString(), r.GetProperty("objectId").GetGuid())).Distinct().Order().ShouldBe(
            new (string?, Guid)[] { ("assistant", s.OwnA.Id), ("assistant", s.DeletedAssistant), ("collection", s.SharedA.Id) }.Order());
        var gone = rows.Single(r => r.GetProperty("objectId").GetGuid() == s.DeletedAssistant);
        (gone.GetProperty("deleted").GetBoolean(), gone.GetProperty("objectName").ValueKind).ShouldBe((true, JsonValueKind.Null));
        rows.First(r => r.GetProperty("objectId").GetGuid() == s.OwnA.Id).GetProperty("objectName").GetString().ShouldBe(s.OwnA.Name);
        a.GetProperty("totals").GetProperty("costUsd").GetDecimal().ShouldBe(0.0030016m + 0.0001m + 0.00002m);
        a.GetProperty("totals").GetProperty("estimatedCostUsd").GetDecimal().ShouldBe(0.0005m + 0.0001m);

        // kind narrows; another department's filter is not the caller's.
        (await OkAsync(adminA, $"/api/costs?{Range}&kind=collection")).GetProperty("rows").EnumerateArray()
            .ShouldAllBe(r => r.GetProperty("kind").GetString() == "collection");
        (await adminA.GetAsync($"/api/costs?{Range}&departmentId={s.B.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.GetAsync($"/api/costs?{Range}&kind=team", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // B: its own assistant and the moved assistant's earlier call, nothing of A's shared knowledge base.
        var b = (await OkAsync(adminB, $"/api/costs?{Range}")).GetProperty("rows").EnumerateArray().ToList();
        b.ShouldAllBe(r => r.GetProperty("departmentId").GetGuid() == s.B.Id);
        b.Select(r => r.GetProperty("objectId").GetGuid()).Order().ShouldBe(new[] { s.OwnA.Id, s.OwnB.Id }.Order());

        // A platform admin sees every department, here filtered to the two seeded ones.
        var all = (await OkAsync(platform, $"/api/costs?{Range}")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("departmentId").GetGuid()).ToHashSet();
        all.ShouldContain(s.A.Id);
        all.ShouldContain(s.B.Id);
        (await OkAsync(platform, $"/api/costs?{Range}&departmentId={s.B.Id}")).GetProperty("rows").GetArrayLength().ShouldBe(2);

        // The department report needs Manage.
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, s.A.CostCenterCode);
        (await viewer.GetAsync($"/api/costs?{Range}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_csv_has_the_documented_headers_and_one_row_each()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var s = await SeedAsync(db);
        var factory = host.Factory;
        using var adminA = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, s.A.CostCenterCode);

        using var response = await adminA.GetAsync($"/api/costs?{Range}&granularity=month&format=csv", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble()); // a byte order mark, as the gateway's export, for Excel
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("period;department;kind;object;step;client;calls;input_tokens;cached_input_tokens;output_tokens;cost_usd;estimated_cost_usd;estimated_calls");
        lines.Length.ShouldBe(1 + 6); // A by month: the assistant's 3, the deleted one's 1, the knowledge base's 2
        lines.ShouldContain($"2025-03;{s.A.Name};assistant;{s.OwnA.Name};chat-answer;{s.Client.Name};3;150;10;70;0,00200000;0,00050000;1");
        lines.ShouldContain($"2025-03;{s.A.Name};assistant;{s.DeletedAssistant} (deleted);chat-answer;{s.Client.Name};1;10;0;5;0,00010000;0,00000000;0");
        lines.ShouldContain($"2025-03;{s.A.Name};collection;{s.SharedA.Name};embed;;5;1000;0;0;0,00002000;0,00000000;0");
    }

    [Fact]
    public async Task A_period_over_the_limit_is_period_too_long()
    {
        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        foreach (var path in new[]
                 {
                     "/api/costs?from=2024-01-01&to=2025-02-04", // 401 days by day
                     "/api/costs?from=2022-01-01&to=2025-01-31&granularity=month", // 37 months
                 })
        {
            using var response = await platform.GetAsync(path, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, path);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("period_too_long");
        }

        // The limits themselves pass.
        (await platform.GetAsync("/api/costs?from=2024-01-01&to=2025-02-03", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.GetAsync("/api/costs?from=2022-02-01&to=2025-01-31&granularity=month", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.GetAsync("/api/costs?from=2025-02-01&to=2025-01-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.GetAsync("/api/costs?granularity=week", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // The earliest date without a start: a short period from the first day, not a 500 (#627 review).
        (await platform.GetAsync("/api/costs?to=0001-01-01", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
