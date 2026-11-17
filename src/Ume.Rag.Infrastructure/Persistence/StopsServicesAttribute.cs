namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>
/// Marks a migration the previous release's services of <see cref="Roles"/> (<see cref="ServiceRoles"/>) cannot run beside,
/// a release with "Rolling update: no" (data-model §10). On an upgrade the migration service revokes those roles' schema
/// access before it applies the migration, so their old containers fail every statement; a service of the role gets the
/// access back by asking for admission with a build that contains the migration (<see cref="ServiceAdmission"/>,
/// <see cref="SchemaAdmissionService"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StopsServicesAttribute(params string[] roles) : Attribute
{
    public IReadOnlyList<string> Roles { get; } = roles;
}
