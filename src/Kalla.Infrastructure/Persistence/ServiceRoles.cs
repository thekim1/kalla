namespace Kalla.Infrastructure.Persistence;

/// <summary>The services' Postgres login roles (<c>deploy/postgres/init.sql</c>, data-model §1.2).</summary>
public static class ServiceRoles
{
    public const string Api = "ume_rag_api";
    public const string Admin = "ume_rag_admin";
    public const string Worker = "ume_rag_worker";

    public static IReadOnlyList<string> All { get; } = [Api, Admin, Worker];
}
