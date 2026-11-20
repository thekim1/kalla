using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Step 7 of the write path (admin design §4.4, §4.7, §11.2 "Jobs"; H4, #28): a change's <c>command</c> jobs are enqueued in
/// its transaction, notify <c>rag_jobs</c> only on commit, and roll back with it.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class CommandEnqueueTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> DepartmentAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Jobs", CostCenterCode = "J" + Random.Shared.Next(100_000, 999_999), SyncedAt = DateTimeOffset.UtcNow };
        db.Departments.Add(department);
        await db.SaveChangesAsync(Ct);
        return department.Id;
    }

    private static AdminContext Admin(AsyncServiceScope scope)
    {
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "o-jobs"), new Claim("roles", RagRoles.PlatformAdmin)], "test", "name", "roles")),
        };
        return scope.ServiceProvider.GetRequiredService<AdminContext>();
    }

    private static Task<AdminChange<Guid>> CreateClient(AdminContext admin, Guid id, params Domain.Ports.JobRequest[] jobs)
    {
        admin.Db.Clients.Add(new Client { Id = id, Name = "Jobs " + id, Kind = ClientKinds.Integration, ClassificationCeiling = "internal" });
        return Task.FromResult(new AdminChange<Guid>(id, "create", AuditEntityTypes.Client, id, null, null) { Jobs = jobs });
    }

    private async Task<NpgsqlConnection> ListenAsync(List<string> payloads)
    {
        var listener = new NpgsqlConnection(database.Connection(RagDatabase.Admin, pooling: false));
        await listener.OpenAsync(Ct);
        listener.Notification += (_, e) => { lock (payloads) payloads.Add(e.Payload); };
        await using var listen = new NpgsqlCommand("LISTEN rag_jobs", listener);
        await listen.ExecuteNonQueryAsync(Ct);
        return listener;
    }

    [Fact]
    public async Task A_change_with_a_command_job_commits_both_and_notifies_on_commit()
    {
        var departmentId = await DepartmentAsync();
        var runId = Guid.CreateVersion7();
        var notified = new List<string>();
        await using var listener = await ListenAsync(notified);
        var factory = host.Factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = Admin(scope);
        var id = Guid.CreateVersion7();

        await admin.WriteAsync(_ => CreateClient(admin, id, CommandJobs.CancelRunJob(runId, departmentId, "internal")), Ct);
        await listener.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Clients.AnyAsync(c => c.Id == id, Ct)).ShouldBeTrue();
        (await db.AuditLog.CountAsync(a => a.EntityId == id.ToString(), Ct)).ShouldBe(1);
        var job = await db.Database.SqlQuery<JobRow>(
            $"""SELECT "Class", "DedupKey", "State", "Payload"::text AS "Payload" FROM content."Jobs" WHERE "RunId" = {runId}""").SingleAsync(Ct);
        job.Class.ShouldBe(JobClasses.Command);
        job.DedupKey.ShouldBe($"cancel:{runId}");
        job.State.ShouldBe("queued");
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            payload.RootElement.GetProperty("kind").GetString().ShouldBe(CommandJobs.CancelRun);
            payload.RootElement.GetProperty("runId").GetGuid().ShouldBe(runId);
        }

        lock (notified)
        {
            notified.ShouldContain(JobClasses.Command);
        }
    }

    [Fact]
    public async Task A_job_that_cannot_be_enqueued_rolls_back_the_change_and_notifies_nothing()
    {
        var departmentId = await DepartmentAsync();
        var notified = new List<string>();
        await using var listener = await ListenAsync(notified);
        var factory = host.Factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = Admin(scope);
        var id = Guid.CreateVersion7();
        // The queue refuses an unknown class after the change and its audit entry are saved in the transaction.
        var bad = new Domain.Ports.JobRequest("no-such-class", departmentId, "internal", "{}");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => admin.WriteAsync(_ => CreateClient(admin, id, bad), Ct));
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            if (!await listener.WaitAsync(TimeSpan.FromSeconds(1), Ct))
            {
                throw new TimeoutException();
            }
        });

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Clients.AnyAsync(c => c.Id == id, Ct)).ShouldBeFalse();
        (await db.AuditLog.AnyAsync(a => a.EntityId == id.ToString(), Ct)).ShouldBeFalse();
        lock (notified)
        {
            notified.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_second_cancel_of_the_same_run_while_one_is_queued_is_skipped()
    {
        var departmentId = await DepartmentAsync();
        var runId = Guid.CreateVersion7();
        var factory = host.Factory;
        for (var i = 0; i < 2; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var admin = Admin(scope);
            await admin.WriteAsync(_ => CreateClient(admin, Guid.CreateVersion7(), CommandJobs.CancelRunJob(runId, departmentId, "internal")), Ct);
        }

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        (await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM content."Jobs" WHERE "RunId" = {runId}""").SingleAsync(Ct)).ShouldBe(1);
    }

    private sealed record JobRow(string Class, string? DedupKey, string State, string Payload);
}
