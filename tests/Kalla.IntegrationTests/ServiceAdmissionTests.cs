using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Persistence;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// The service fences of data-model §10 (<see cref="ServiceAdmission"/>): a migration marked <see cref="StopsServicesAttribute"/>
/// holds its roles out of the schemas on an upgrade until a service with a build that contains it is admitted. Each test
/// builds its own database (init.sql, then the migrations up to a chosen one), so the upgrade starts from a real older schema.
/// </summary>
public sealed class ServiceAdmissionTests
{
    /// <summary>
    /// The migration before <c>RemoveWorkerConcurrency</c>, which stops the admin (0.6.0). The last migration after it that
    /// stops a role is <c>DropMessageParentId</c> (the api and the admin), so that is the fence of both.
    /// </summary>
    private const string BeforeFence = "20261114021122_ClientManagedByDeployment";
    private const string Fence = "20261116231057_DropMessageParentId";
    private const string ReadSettings = """SELECT count(*) FROM config."Settings" """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_install_sets_no_fence_and_every_role_works_without_admission()
    {
        await using var database = new RagDatabase();
        await database.StartWithoutMigrationsAsync();
        await using (var db = TestPostgres.Context(database.Connection(Migrator)))
        {
            (await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, Ct)).ShouldBeEmpty();
        }

        (await Sql.ScalarAsync<long>(database.Connection(Migrator), "SELECT count(*) FROM public.ume_rag_service_fences")).ShouldBe(0);
        foreach (var role in new[] { Api, Admin, Worker })
        {
            await Sql.ExecuteAsync(database.Connection(role), ReadSettings);
        }

        // Asking for admission without a fence does nothing, whatever the build.
        await Sql.ExecuteAsync(database.Connection(Admin), "SELECT public.ume_rag_admit('0')");
        await Sql.ExecuteAsync(database.Connection(Admin), ReadSettings);
    }

    [Fact]
    public async Task An_upgrade_across_a_fenced_migration_holds_the_role_out_until_a_build_that_contains_it_is_admitted()
    {
        await using var database = await DatabaseBeforeFenceAsync();
        await using (var db = TestPostgres.Context(database.Connection(Migrator)))
        {
            (await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, Ct)).ShouldBe([new ServiceFence(Admin, Fence), new ServiceFence(Api, Fence)]);
        }

        var build = CurrentBuild(database);
        var denied = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Admin), ReadSettings));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        denied.MessageText.ShouldBe("permission denied for schema config");
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """SELECT count(*) FROM content."Jobs" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), ReadSettings);
        await Sql.ExecuteAsync(database.Connection(Worker), ReadSettings);
        // What admission gives back: the schemas app-roles.sql granted the admin.
        (await Sql.ListAsync(database.Connection(Migrator), "SELECT unnest(schemas) FROM public.ume_rag_service_fences WHERE role = 'ume_rag_admin'"))
            .ShouldBe(["audit", "chat", "config", "content"]);

        // The previous version's build is refused and gets nothing.
        var refused = await Should.ThrowAsync<PostgresException>(() =>
            Sql.ExecuteAsync(database.Connection(Admin), $"SELECT public.ume_rag_admit('{BeforeFence}')"));
        refused.SqlState.ShouldBe(ServiceAdmission.RefusedSqlState);
        refused.MessageText.ShouldContain(Fence);
        refused.MessageText.ShouldContain(BeforeFence);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), ReadSettings);

        // A role admits only itself (session_user), and no service role reads or writes the fences.
        await Sql.ExecuteAsync(database.Connection(Api), $"SELECT public.ume_rag_admit('{build}')");
        await Sql.ExecuteAsync(database.Connection(Api), ReadSettings);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), ReadSettings);
        foreach (var role in new[] { Api, Admin, Worker })
        {
            await Sql.ShouldBeDeniedAsync(database.Connection(role), "SELECT count(*) FROM public.ume_rag_service_fences");
            await Sql.ShouldBeDeniedAsync(database.Connection(role), $"UPDATE public.ume_rag_service_fences SET admitted = '{build}'");
            await Sql.ShouldBeDeniedAsync(database.Connection(role), "INSERT INTO public.ume_rag_service_fences (role, fence) VALUES ('ume_rag_worker', '0')");
        }

        // A role never admitted stays out through another migration run, which fences nothing new.
        await using (var db = TestPostgres.Context(database.Connection(Migrator)))
        {
            (await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, Ct)).ShouldBeEmpty();
        }

        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), ReadSettings);

        // The current build is admitted and gets its schemas back, also through later migration runs.
        await Sql.ExecuteAsync(database.Connection(Admin), $"SELECT public.ume_rag_admit('{build}')");
        await Sql.ExecuteAsync(database.Connection(Admin), ReadSettings);
        (await Sql.ScalarAsync<string>(database.Connection(Migrator), "SELECT admitted FROM public.ume_rag_service_fences WHERE role = 'ume_rag_admin'"))
            .ShouldBe(build);
        await using (var db = TestPostgres.Context(database.Connection(Migrator)))
        {
            (await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, Ct)).ShouldBeEmpty();
        }

        await Sql.ExecuteAsync(database.Connection(Admin), ReadSettings);
        await Sql.ExecuteAsync(database.Connection(Admin), """SELECT count(*) FROM content."Jobs" """);
    }

    [Fact]
    public async Task The_service_is_admitted_at_start_refused_when_the_fence_is_ahead_and_skips_a_database_without_the_function()
    {
        await using var database = await DatabaseBeforeFenceAsync();
        await using (var db = TestPostgres.Context(database.Connection(Migrator)))
        {
            await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, Ct);
        }

        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), ReadSettings);
        using (var admin = ServiceHost(database.Connection(Admin), new CapturingLoggerProvider()))
        {
            await admin.StartAsync(Ct);
            await admin.StopAsync(Ct);
        }

        await Sql.ExecuteAsync(database.Connection(Admin), ReadSettings);

        // A fence beyond every migration of this build: the worker's start fails with one Critical line.
        await Sql.ExecuteAsync(database.Connection(Migrator),
            "INSERT INTO public.ume_rag_service_fences (role, fence) VALUES ('ume_rag_worker', '99991231235959_FromALaterRelease')");
        var logs = new CapturingLoggerProvider();
        using (var worker = ServiceHost(database.Connection(Worker), logs))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => worker.StartAsync(Ct));
            error.SqlState.ShouldBe(ServiceAdmission.RefusedSqlState);
        }

        var critical = logs.Entries.Where(e => e.Category == typeof(SchemaAdmissionService).FullName).ShouldHaveSingleItem();
        critical.Level.ShouldBe(LogLevel.Critical);
        critical.Message.ShouldContain("99991231235959_FromALaterRelease");
        critical.Message.ShouldContain(CurrentBuild(database));

        // Without the function (development, or a database migrated before it existed) nothing is asked.
        await Sql.ExecuteAsync(database.Connection(Migrator), "DROP FUNCTION public.ume_rag_admit(text)");
        using (var worker = ServiceHost(database.Connection(Worker), new CapturingLoggerProvider()))
        {
            await worker.StartAsync(Ct);
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public void Admission_is_the_first_hosted_service_and_only_when_asked_for()
    {
        var builder = Builder("Host=127.0.0.1;Port=1;Database=ragdb;Username=nobody");
        builder.Services.AddHostedService<EarlierService>();
        builder.AddRagDatabase(admitAtStart: true);
        using (var services = builder.Services.BuildServiceProvider())
        {
            services.GetServices<IHostedService>().First().ShouldBeOfType<SchemaAdmissionService>();
        }

        var migration = Builder("Host=127.0.0.1;Port=1;Database=ragdb;Username=nobody");
        migration.AddRagDatabase();
        using (var services = migration.Services.BuildServiceProvider())
        {
            services.GetServices<IHostedService>().OfType<SchemaAdmissionService>().ShouldBeEmpty();
        }
    }

    /// <summary>A database with the roles of init.sql and the schema of the release before <see cref="Fence"/>.</summary>
    private static async Task<RagDatabase> DatabaseBeforeFenceAsync()
    {
        var database = new RagDatabase();
        await database.StartWithoutMigrationsAsync();
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        await db.GetService<IMigrator>().MigrateAsync(BeforeFence, Ct);
        return database;
    }

    private static string CurrentBuild(RagDatabase database)
    {
        using var db = TestPostgres.Context(database.Connection(Migrator));
        return db.Database.GetMigrations().Last();
    }

    private static IHost ServiceHost(string connection, CapturingLoggerProvider logs)
    {
        var builder = Builder(connection);
        builder.Logging.AddProvider(logs);
        builder.AddRagDatabase(admitAtStart: true);
        return builder.Build();
    }

    private static HostApplicationBuilder Builder(string connection)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ragdb"] = connection });
        return builder;
    }

    private sealed class EarlierService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
