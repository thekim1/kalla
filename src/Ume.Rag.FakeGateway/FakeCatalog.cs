using System.Text.Json;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// The fake organisation, in the Data API's catalogue shape (gateway <c>DataEndpoints.cs</c>: <c>{ items: [...] }</c>,
/// camelCase) plus the release 2 fields <c>memberGroups</c> and <c>code</c>. Same departments and teams as the gateway's
/// dev seed, with fixed ids so the admin's org sync (P0 #11) is repeatable.
/// </summary>
public static class FakeCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset Created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly Guid KommunledningskontoretId = new("019a0000-0000-7000-8000-000000001000");
    public static readonly Guid IndividOchFamiljeomsorgenId = new("019a0000-0000-7000-8000-000000003000");
    public static readonly Guid FritidsforvaltningenId = new("019a0000-0000-7000-8000-000000005000");

    public static IReadOnlyList<CatalogDepartment> Departments { get; } =
    [
        new(FritidsforvaltningenId, "Fritidsförvaltningen", "5000", true, Created, []),
        new(IndividOchFamiljeomsorgenId, "Individ- och familjeomsorgen", "3000", true, Created, ["Byggnadsnämnden"]),
        new(KommunledningskontoretId, "Kommunledningskontoret", "1000", true, Created, []),
    ];

    public static IReadOnlyList<CatalogTeam> Teams { get; } =
    [
        new(new Guid("019a0000-0000-7000-8000-000000005001"), FritidsforvaltningenId, "Bokning & anläggningar", true, Created, null),
        new(new Guid("019a0000-0000-7000-8000-000000001001"), KommunledningskontoretId, "Digitalisering & AI", true, Created, "digitalisering-ai"),
        new(new Guid("019a0000-0000-7000-8000-000000001002"), KommunledningskontoretId, "Kontaktcenter", true, Created, null),
        new(new Guid("019a0000-0000-7000-8000-000000003001"), IndividOchFamiljeomsorgenId, "Verksamhetsutveckling", true, Created, null),
    ];
}

public sealed record CatalogDepartment(Guid Id, string Name, string CostCenterCode, bool IsActive, DateTimeOffset CreatedAt, string[] MemberGroups);

public sealed record CatalogTeam(Guid Id, Guid DepartmentId, string Name, bool IsActive, DateTimeOffset CreatedAt, string? Code);
