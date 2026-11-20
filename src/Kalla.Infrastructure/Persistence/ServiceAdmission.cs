using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Kalla.Infrastructure.Persistence;

/// <summary>A service role the migration service held out of the schemas, and the migration it waits for.</summary>
public sealed record ServiceFence(string Role, string Migration);

/// <summary>
/// The database-side stop of a release that cannot run beside the previous one (data-model §10): a migration marked
/// <see cref="StopsServicesAttribute"/> fences its roles. On an upgrade the migration service records the fence in
/// <c>public.kalla_service_fences</c> and revokes the role's schema <c>USAGE</c> before it migrates, and again after
/// <c>app-roles.sql</c> has granted it, so the role's old containers fail every statement until they are replaced. A
/// service of the role gets its schemas back through <c>public.kalla_admit(build)</c> (<see cref="SchemaAdmissionService"/>)
/// with the last migration compiled into it: a build older than the fence is refused (<see cref="RefusedSqlState"/>), a
/// newer one is admitted and keeps its access through later migration runs. Only with the production roles (a roles
/// path); the development superuser has none. The table and function are kept out of the EF model and created on every
/// run (<see cref="Sql"/>); the service roles cannot read or write the table.
/// </summary>
public static class ServiceAdmission
{
    /// <summary>The SQLSTATE of a refused admission (class <c>UR</c>, Källa's own).</summary>
    public const string RefusedSqlState = "UR001";

    /// <summary>Idempotent: the fence table (no grants to the service roles) and the admission function.</summary>
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS public.kalla_service_fences (
            role text PRIMARY KEY,
            fence text NOT NULL,
            admitted text NULL,
            schemas text[] NOT NULL DEFAULT '{}'
        );
        CREATE OR REPLACE FUNCTION public.kalla_admit(build text) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
        DECLARE
            fenced public.kalla_service_fences;
            schema_name text;
        BEGIN
            SELECT * INTO fenced FROM public.kalla_service_fences WHERE role = session_user FOR UPDATE;
            IF NOT FOUND THEN
                RETURN;
            END IF;
            IF build IS NULL OR build COLLATE "C" < fenced.fence COLLATE "C" THEN
                RAISE EXCEPTION 'build % of % is older than its schema fence %', coalesce(build, 'null'), session_user, fenced.fence
                    USING ERRCODE = 'UR001';
            END IF;
            UPDATE public.kalla_service_fences SET admitted = greatest(admitted COLLATE "C", build COLLATE "C") WHERE role = session_user;
            -- Only a role still held out gets its schemas back: once admitted, app-roles.sql alone decides its grants.
            IF fenced.admitted IS NULL OR fenced.admitted COLLATE "C" < fenced.fence COLLATE "C" THEN
                FOREACH schema_name IN ARRAY fenced.schemas LOOP
                    IF to_regnamespace(schema_name) IS NOT NULL THEN
                        EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', schema_name, session_user);
                    END IF;
                END LOOP;
            END IF;
        END
        $$;
        REVOKE ALL ON FUNCTION public.kalla_admit(text) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.kalla_admit(text) TO kalla_api, kalla_admin, kalla_worker;
        """;

    /// <summary>Records the fences, keeping a greater one; <c>admitted</c> stays as it is.</summary>
    private const string UpsertFencesSql = """
        INSERT INTO public.kalla_service_fences (role, fence)
        SELECT f.role, f.fence FROM unnest($1::text[], $2::text[]) AS f(role, fence)
        ON CONFLICT (role) DO UPDATE SET fence = greatest(kalla_service_fences.fence COLLATE "C", excluded.fence COLLATE "C")
        """;

    /// <summary>Revokes the schemas from every role not admitted past its fence.</summary>
    private const string RevokeHeldOutSql = """
        DO $$
        DECLARE
            held record;
            schema_name text;
        BEGIN
            FOR held IN SELECT role FROM public.kalla_service_fences
                        WHERE admitted IS NULL OR admitted COLLATE "C" < fence COLLATE "C" LOOP
                FOR schema_name IN SELECT nspname FROM pg_namespace WHERE nspname IN ('config', 'content', 'chat', 'audit') LOOP
                    EXECUTE format('REVOKE USAGE ON SCHEMA %I FROM %I', schema_name, held.role);
                END LOOP;
            END LOOP;
        END
        $$
        """;

    /// <summary>
    /// After <c>app-roles.sql</c>: stores, for every role not admitted past its fence, the schemas it has <c>USAGE</c> on now
    /// (what admission gives back) and revokes them.
    /// </summary>
    private const string HoldOutSql = """
        DO $$
        DECLARE
            held record;
            schema_name text;
        BEGIN
            FOR held IN SELECT role FROM public.kalla_service_fences
                        WHERE admitted IS NULL OR admitted COLLATE "C" < fence COLLATE "C" FOR UPDATE LOOP
                UPDATE public.kalla_service_fences SET schemas = ARRAY(
                    SELECT n.nspname::text FROM pg_namespace n
                    WHERE n.nspname IN ('config', 'content', 'chat', 'audit') AND has_schema_privilege(held.role::name, n.oid, 'USAGE')
                    ORDER BY n.nspname)
                WHERE role = held.role;
                FOR schema_name IN SELECT unnest(schemas) FROM public.kalla_service_fences WHERE role = held.role LOOP
                    EXECUTE format('REVOKE USAGE ON SCHEMA %I FROM %I', schema_name, held.role);
                END LOOP;
            END LOOP;
        END
        $$
        """;

    /// <summary>Creates or replaces the fence table and the admission function (<see cref="Sql"/>).</summary>
    internal static Task InstallAsync(RagDbContext db, CancellationToken cancellationToken) =>
        OnConnectionAsync(db, async connection =>
        {
            await using var command = new NpgsqlCommand(Sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// The fences of this run: on an upgrade (at least one applied migration), each role a pending migration stops, with the
    /// greatest such migration's id. A first install fences nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<ServiceFence>> PendingFencesAsync(RagDbContext db, CancellationToken cancellationToken)
    {
        if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            return [];
        }

        var migrations = db.GetService<IMigrationsAssembly>().Migrations;
        var fences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in await db.Database.GetPendingMigrationsAsync(cancellationToken))
        {
            foreach (var role in migrations[id].GetCustomAttribute<StopsServicesAttribute>()?.Roles ?? [])
            {
                if (!ServiceRoles.All.Contains(role, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Migration {id} stops '{role}', which is not a service role.");
                }

                if (!fences.TryGetValue(role, out var fence) || string.CompareOrdinal(id, fence) > 0)
                {
                    fences[role] = id;
                }
            }
        }

        return [.. fences.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new ServiceFence(f.Key, f.Value))];
    }

    /// <summary>Before the migrations, in one transaction: records <paramref name="fences"/> and revokes the roles' schemas.</summary>
    internal static Task FenceAsync(RagDbContext db, IReadOnlyList<ServiceFence> fences, CancellationToken cancellationToken) =>
        OnConnectionAsync(db, async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var upsert = new NpgsqlCommand(UpsertFencesSql, connection, transaction))
            {
                upsert.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. fences.Select(f => f.Role)] });
                upsert.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. fences.Select(f => f.Migration)] });
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var revoke = new NpgsqlCommand(RevokeHeldOutSql, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>After <c>app-roles.sql</c>: keeps every role not admitted past its fence out of the schemas it was just granted.</summary>
    internal static Task HoldOutAsync(RagDbContext db, CancellationToken cancellationToken) =>
        OnConnectionAsync(db, async connection =>
        {
            await using var command = new NpgsqlCommand(HoldOutSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> on the context's own connection with Npgsql directly: EF Core's raw SQL would read the
    /// statements' curly braces as format placeholders.
    /// </summary>
    private static async Task OnConnectionAsync(RagDbContext db, Func<NpgsqlConnection, Task> work, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await work((NpgsqlConnection)db.Database.GetDbConnection());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
