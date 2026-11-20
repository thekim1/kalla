using Kalla.Domain;

namespace Kalla.FakeGateway;

/// <summary>
/// The fake organisation, listed by <c>/v1/fors/context</c> (<see cref="FakeContext"/>), and the models' prices on
/// <c>/v1/models</c>. Same departments and teams as the gateway's dev seed, with fixed ids so the admin's org sync is
/// repeatable.
/// </summary>
public static class FakeCatalog
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly Guid KommunledningskontoretId = new("019a0000-0000-7000-8000-000000001000");
    public static readonly Guid IndividOchFamiljeomsorgenId = new("019a0000-0000-7000-8000-000000003000");
    public static readonly Guid FritidsforvaltningenId = new("019a0000-0000-7000-8000-000000005000");

    /// <summary>The pilot team (Digitalisering &amp; AI): the integration's owner team and the per-level dev keys' team.</summary>
    public static readonly Guid DigitaliseringTeamId = new("019a0000-0000-7000-8000-000000001001");

    /// <summary>A department wound up, with its team: listed inactive in the context under organisation.read.</summary>
    public static readonly Guid OverformyndarId = new("019a0000-0000-7000-8000-000000007000");

    public static IReadOnlyList<CatalogDepartment> Departments { get; } =
    [
        new(FritidsforvaltningenId, "Fritidsförvaltningen", "5000", true, Created, []),
        new(IndividOchFamiljeomsorgenId, "Individ- och familjeomsorgen", "3000", true, Created, ["Byggnadsnämnden"]),
        new(KommunledningskontoretId, "Kommunledningskontoret", "1000", true, Created, []),
    ];

    public static IReadOnlyList<CatalogTeam> Teams { get; } =
    [
        new(new Guid("019a0000-0000-7000-8000-000000005001"), FritidsforvaltningenId, "Bokning & anläggningar", true, Created, null),
        new(DigitaliseringTeamId, KommunledningskontoretId, "Digitalisering & AI", true, Created, "digitalisering-ai"),
        new(new Guid("019a0000-0000-7000-8000-000000001002"), KommunledningskontoretId, "Kontaktcenter", true, Created, null),
        new(new Guid("019a0000-0000-7000-8000-000000003001"), IndividOchFamiljeomsorgenId, "Verksamhetsutveckling", true, Created, null),
    ];

    /// <summary>
    /// The whole organisation as the gateway's context lists it under <c>organisation.read</c>: the active departments and
    /// teams above and one inactive department with its team.
    /// </summary>
    public static IReadOnlyList<CatalogDepartment> OrganisationDepartments { get; } =
    [
        .. Departments,
        new(OverformyndarId, "Överförmyndarförvaltningen", "7000", false, Created, []),
    ];

    public static IReadOnlyList<CatalogTeam> OrganisationTeams { get; } =
    [
        .. Teams,
        new(new Guid("019a0000-0000-7000-8000-000000007001"), OverformyndarId, "Handläggning", false, Created, null),
    ];

    /// <summary>A model's price by its kind (a route alias: its first target's, which has the alias's kind).</summary>
    /// <remarks>
    /// Per million tokens: embeddings 0.02, chat 0.15 in and 0.60 out, rerank 0.10 in (billed per input token, gateway
    /// design §7.4).
    /// </remarks>
    public static FakePrice PriceOf(FakeModel model) => model.Kind switch
    {
        FakeModel.Embedding => new FakePrice(0.02m, null, 0m),
        FakeModel.Rerank => new FakePrice(0.10m, null, 0m),
        _ => new FakePrice(0.15m, 0.075m, 0.60m),
    };

    /// <summary>
    /// The chat model that answers without a cost: no <c>x-fors-cost-usd</c> and no stream comment, as a gateway without
    /// the feature (chat design §18.2); it is priced like every chat model, so a caller's estimate has prices to use.
    /// </summary>
    public const string NoCostModel = "fake-no-cost";

    /// <summary>
    /// A call's cost in USD at <paramref name="model"/>'s price, as the gateway's <c>CostCalculator.Calculate</c>: cached input
    /// at the cached price (the input price when there is none), the rest of the input at the input price, output at the
    /// output price, per million tokens, rounded to eight decimals.
    /// </summary>
    public static decimal Cost(FakeModel model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var price = PriceOf(model);
        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(inputTokens, 0));
        var usd = (Math.Max(inputTokens - cached, 0) * price.InputPerMillionUsd + cached * (price.CachedInputPerMillionUsd ?? price.InputPerMillionUsd)
                   + Math.Max(outputTokens, 0) * price.OutputPerMillionUsd) / 1_000_000m;
        return Math.Round(usd, 8, MidpointRounding.AwayFromZero);
    }

    /// <summary>The gateway's <c>ModelKind</c> name of a fake model kind (<c>Chat</c>, <c>Embedding</c>, <c>Rerank</c>).</summary>
    public static string KindName(string kind) => kind switch
    {
        FakeModel.Embedding => "Embedding",
        FakeModel.Rerank => "Rerank",
        _ => "Chat",
    };

    /// <summary>The provider account a deployment at <paramref name="residency"/> runs on: the <c>owned_by</c> of <c>/v1/models</c>.</summary>
    public static string ProviderName(Residency residency) => residency switch
    {
        Residency.OnPrem => "fake-onprem",
        Residency.Eu => "fake-eu",
        _ => "fake-external",
    };
}

public sealed record CatalogDepartment(Guid Id, string Name, string CostCenterCode, bool IsActive, DateTimeOffset CreatedAt, string[] MemberGroups);

public sealed record CatalogTeam(Guid Id, Guid DepartmentId, string Name, bool IsActive, DateTimeOffset CreatedAt, string? Code);

/// <summary>A model's price per million tokens in USD; no cached-input price means the input price.</summary>
public sealed record FakePrice(decimal InputPerMillionUsd, decimal? CachedInputPerMillionUsd, decimal OutputPerMillionUsd);
