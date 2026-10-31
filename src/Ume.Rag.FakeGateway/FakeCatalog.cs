using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// The fake organisation, in the Data API's catalogue shape (gateway <c>DataEndpoints.cs</c>: <c>{ items: [...] }</c>,
/// camelCase) plus the release 2 fields <c>memberGroups</c> and <c>code</c>. Same departments and teams as the gateway's
/// dev seed, with fixed ids so the admin's org sync (P0 #11) is repeatable.
/// </summary>
public static class FakeCatalog
{
    /// <summary>camelCase with enums as strings, as the gateway's <c>JsonStringEnumConverter</c>.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

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

    /// <summary>One provider account per residency (gateway <c>/v1/catalog/providers</c>).</summary>
    public static IReadOnlyList<CatalogProvider> Providers { get; } =
    [
        new(ProviderId(Residency.OnPrem), "fake-onprem", "Fake on-prem", "OpenAICompatible", Residency.OnPrem, true),
        new(ProviderId(Residency.Eu), "fake-eu", "Fake EU", "AzureOpenAI", Residency.Eu, true),
        new(ProviderId(Residency.External), "fake-external", "Fake external", "OpenAI", Residency.External, true),
    ];

    /// <summary>The deployments (gateway <c>/v1/catalog/models</c>): single-target chat and embedding models.</summary>
    public static IReadOnlyList<CatalogModel> Models(IEnumerable<FakeModel> models) =>
    [
        .. Deployable(models).Select(m => new CatalogModel(ModelId(m.Name), m.Name, m.Name,
            m.Kind == "embedding" ? "Embedding" : "Chat", ProviderId(m.Targets[0].Residency), true)),
    ];

    /// <summary>One price per deployment (gateway <c>/v1/catalog/prices</c>): embeddings 0.02, chat 0.15 in and 0.60 out per million.</summary>
    public static IReadOnlyList<CatalogPrice> Prices(IEnumerable<FakeModel> models) =>
    [
        .. Deployable(models).Select(m => m.Kind == "embedding"
            ? new CatalogPrice(ModelId(m.Name), Created, 0.02m, null, 0m)
            : new CatalogPrice(ModelId(m.Name), Created, 0.15m, 0.075m, 0.60m)),
    ];

    private static IEnumerable<FakeModel> Deployable(IEnumerable<FakeModel> models) =>
        models.Where(m => m.Targets.Count == 1 && m.Kind is "chat" or "embedding").OrderBy(m => m.Name, StringComparer.Ordinal);

    private static Guid ProviderId(Residency residency) => new($"019a0000-0000-7000-8000-0000000090{(int)residency:D2}");

    /// <summary>A stable id per model name, so repeated syncs see the same deployment.</summary>
    private static Guid ModelId(string name) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
}

public sealed record CatalogDepartment(Guid Id, string Name, string CostCenterCode, bool IsActive, DateTimeOffset CreatedAt, string[] MemberGroups);

public sealed record CatalogTeam(Guid Id, Guid DepartmentId, string Name, bool IsActive, DateTimeOffset CreatedAt, string? Code);

public sealed record CatalogProvider(Guid Id, string Name, string DisplayName, string Type, Residency Residency, bool IsEnabled);

public sealed record CatalogModel(Guid Id, string Name, string UpstreamModel, string Kind, Guid ProviderId, bool IsEnabled);

public sealed record CatalogPrice(Guid ModelId, DateTimeOffset EffectiveFrom, decimal InputPerMillionUsd, decimal? CachedInputPerMillionUsd,
    decimal OutputPerMillionUsd);
