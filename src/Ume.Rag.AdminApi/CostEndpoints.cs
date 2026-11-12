using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The cost report (admin design §8.6; cost-attribution §5 C4): what assistants and knowledge bases cost, read from
/// <c>chat."AssistantDailyUsage"</c> and <c>content."CollectionDailyUsage"</c> per Europe/Stockholm day or calendar month,
/// step and client, the gateway's cost always apart from the estimate. Scope is the row's <c>DepartmentId</c> (the object's
/// department when the call was made) among the caller's departments, as the feedback totals (§8.1); names are read from
/// the config tables by id, never joined to the usage tables (§4.8). Reporting only, and not audited: no content.
/// </summary>
public static class CostEndpoints
{
    /// <summary>The longest period by day (admin §8.6).</summary>
    public const int MaxDays = 400;

    /// <summary>The longest period by month (admin §8.6; the tables keep 3 years, data-model §8).</summary>
    public const int MaxMonths = 36;

    /// <summary>The default period: the last 30 days, today included.</summary>
    public const int DefaultDays = 30;

    public const string Day = "day";
    public const string Month = "month";
    public const string AssistantKind = "assistant";
    public const string CollectionKind = "collection";

    /// <summary>The CSV headers of admin §8.6, in order.</summary>
    public static IReadOnlyList<string> CsvHeaders { get; } =
    [
        "period", "department", "kind", "object", "step", "client", "calls", "input_tokens", "cached_input_tokens", "output_tokens", "cost_usd",
        "estimated_cost_usd", "estimated_calls",
    ];

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    public static RouteGroupBuilder MapCosts(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/assistants/{id:guid}/costs", async (Guid id, DateOnly? from, DateOnly? to, string? granularity, AdminContext admin, CancellationToken ct) =>
            {
                var period = CostPeriod.Of(from, to, granularity, Today(admin));
                var departments = await admin.Departments().Select(d => d.Id).ToArrayAsync(ct);
                _ = await admin.Db.Assistants.AsNoTracking().Where(a => a.Id == id && departments.Contains(a.DepartmentId)).Select(a => (Guid?)a.Id)
                    .SingleOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var rows = await AssistantUsageAsync(admin, period, departments, [id], ct);
                var clients = await ClientNamesAsync(admin, rows, ct);
                return ObjectCosts.Of(period, [.. rows.OrderBy(r => r.Period).ThenBy(r => r.Step, StringComparer.Ordinal).ThenBy(r => r.ClientId)
                    .Select(r => CostRow.Of(r, r.ClientId, r.ClientId is { } c ? clients.GetValueOrDefault(c) : null))]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Costs").WithName("GetAssistantCosts")
            .WithSummary("An assistant's cost per period, step and client: tokens, the gateway's cost and the estimate apart, with totals");

        api.MapGet("/collections/{id:guid}/costs", async (Guid id, DateOnly? from, DateOnly? to, string? granularity, AdminContext admin, CancellationToken ct) =>
            {
                var period = CostPeriod.Of(from, to, granularity, Today(admin));
                var departments = await admin.Departments().Select(d => d.Id).ToArrayAsync(ct);
                // The owning department only: a department the knowledge base is shared with sees no cost (§3.2, §8.6).
                _ = await CollectionEndpoints.Scoped(admin).AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var rows = await CollectionUsageAsync(admin, period, departments, [id], ct);
                return ObjectCosts.Of(period, [.. rows.OrderBy(r => r.Period).ThenBy(r => r.Step, StringComparer.Ordinal).Select(r => CostRow.Of(r, null, null))]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Costs").WithName("GetCollectionCosts")
            .WithSummary("A knowledge base's cost per period and step (its own department only), the gateway's cost and the estimate apart");

        api.MapGet("/costs", async (DateOnly? from, DateOnly? to, string? granularity, Guid? departmentId, string? kind, string? format, AdminContext admin,
                CancellationToken ct) =>
            {
                var period = CostPeriod.Of(from, to, granularity, Today(admin));
                if (kind is not (null or AssistantKind or CollectionKind))
                {
                    throw AdminFaults.Field("kind", AdminErrorCodes.InvalidValue);
                }

                var scope = await admin.Departments().AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct);
                if (departmentId is { } only && scope.All(d => d.Id != only))
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                Guid[] departments = [.. scope.Select(d => d.Id).Where(d => departmentId is null || d == departmentId)];
                var departmentNames = scope.ToDictionary(d => d.Id, d => d.Name);
                var rows = new List<DepartmentCostRow>();
                if (kind is null or AssistantKind)
                {
                    var usage = await AssistantUsageAsync(admin, period, departments, null, ct);
                    Guid[] ids = [.. usage.Select(u => u.ObjectId).Distinct()];
                    var names = await admin.Db.Assistants.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
                    var clients = await ClientNamesAsync(admin, usage, ct);
                    rows.AddRange(usage.Select(u => DepartmentCostRow.Of(u, AssistantKind, departmentNames.GetValueOrDefault(u.DepartmentId), names.GetValueOrDefault(u.ObjectId),
                        u.ClientId is { } c ? clients.GetValueOrDefault(c) : null)));
                }

                if (kind is null or CollectionKind)
                {
                    var usage = await CollectionUsageAsync(admin, period, departments, null, ct);
                    Guid[] ids = [.. usage.Select(u => u.ObjectId).Distinct()];
                    // A deleted knowledge base (DeletedAt set) shows as deleted, as one whose row is gone.
                    var names = await admin.Db.Collections.AsNoTracking().Where(c => ids.Contains(c.Id) && c.DeletedAt == null).Select(c => new { c.Id, c.Name })
                        .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                    rows.AddRange(usage.Select(u => DepartmentCostRow.Of(u, CollectionKind, departmentNames.GetValueOrDefault(u.DepartmentId), names.GetValueOrDefault(u.ObjectId),
                        null)));
                }

                List<DepartmentCostRow> ordered = [.. rows.OrderBy(r => r.Period).ThenBy(r => r.DepartmentName, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal)
                    .ThenBy(r => r.ObjectName ?? r.ObjectId.ToString(), StringComparer.Ordinal).ThenBy(r => r.ObjectId).ThenBy(r => r.Step, StringComparer.Ordinal)
                    .ThenBy(r => r.ClientName, StringComparer.Ordinal)];
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csv = Csv.Of(CsvHeaders, ordered.Select(r => CsvRow(r, period.Granularity)), ';');
                    return Results.File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)], "text/csv; charset=utf-8",
                        $"costs-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.csv");
                }

                return Results.Ok(new CostReport(period.From, period.To, period.Granularity, ordered, CostTotals.Of(ordered.Select(r => r.Sums))));
            })
            .RequireAuthorization(AdminPolicies.Manage).WithTags("Costs").WithName("GetCosts")
            .WithSummary("The cost per period, department, assistant or knowledge base and step of the caller's departments (format=csv: one row each)");

        return api;
    }

    private static DateOnly Today(AdminContext admin) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(admin.Now, Stockholm).DateTime);

    /// <summary>
    /// <c>AssistantDailyUsage</c> summed per period, assistant, department, step and client within <paramref name="departments"/>
    /// (the row's own department), for <paramref name="objects"/> or every assistant when null.
    /// </summary>
    private static Task<List<UsageAggregate>> AssistantUsageAsync(AdminContext admin, CostPeriod period, Guid[] departments, Guid[]? objects, CancellationToken ct) =>
        admin.Db.Database.SqlQuery<UsageAggregate>($"""
            SELECT date_trunc({period.Granularity}, u."Day")::date AS "Period", u."AssistantId" AS "ObjectId", u."DepartmentId", u."Step", u."ClientId",
                sum(u."Calls")::bigint AS "Calls", sum(u."InputTokens")::bigint AS "InputTokens", sum(u."CachedInputTokens")::bigint AS "CachedInputTokens",
                sum(u."OutputTokens")::bigint AS "OutputTokens", sum(u."CostUsd") AS "CostUsd", sum(u."EstimatedCostUsd") AS "EstimatedCostUsd",
                sum(u."EstimatedCalls")::bigint AS "EstimatedCalls"
            FROM chat."AssistantDailyUsage" u
            WHERE u."Day" BETWEEN {period.From} AND {period.To} AND u."DepartmentId" = ANY({departments})
                AND ({objects is null} OR u."AssistantId" = ANY({objects ?? []}))
            GROUP BY 1, 2, 3, 4, 5
            """).ToListAsync(ct);

    /// <summary><c>CollectionDailyUsage</c> summed as <see cref="AssistantUsageAsync"/>, without a client.</summary>
    private static Task<List<UsageAggregate>> CollectionUsageAsync(AdminContext admin, CostPeriod period, Guid[] departments, Guid[]? objects, CancellationToken ct) =>
        admin.Db.Database.SqlQuery<UsageAggregate>($"""
            SELECT date_trunc({period.Granularity}, u."Day")::date AS "Period", u."CollectionId" AS "ObjectId", u."DepartmentId", u."Step", NULL::uuid AS "ClientId",
                sum(u."Calls")::bigint AS "Calls", sum(u."InputTokens")::bigint AS "InputTokens", sum(u."CachedInputTokens")::bigint AS "CachedInputTokens",
                sum(u."OutputTokens")::bigint AS "OutputTokens", sum(u."CostUsd") AS "CostUsd", sum(u."EstimatedCostUsd") AS "EstimatedCostUsd",
                sum(u."EstimatedCalls")::bigint AS "EstimatedCalls"
            FROM content."CollectionDailyUsage" u
            WHERE u."Day" BETWEEN {period.From} AND {period.To} AND u."DepartmentId" = ANY({departments})
                AND ({objects is null} OR u."CollectionId" = ANY({objects ?? []}))
            GROUP BY 1, 2, 3, 4, 5
            """).ToListAsync(ct);

    /// <summary>The names of the clients in <paramref name="rows"/> (<c>admin-console</c> is the test panel's, §4.9).</summary>
    private static async Task<Dictionary<Guid, string>> ClientNamesAsync(AdminContext admin, IEnumerable<UsageAggregate> rows, CancellationToken ct)
    {
        Guid[] ids = [.. rows.Select(r => r.ClientId).OfType<Guid>().Distinct()];
        return ids.Length == 0
            ? []
            : await admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    /// <summary>A CSV row of §8.6: the month as <c>yyyy-MM</c>, a deleted object as its id and "(deleted)", costs in Swedish decimals as the gateway's export.</summary>
    private static string?[] CsvRow(DepartmentCostRow r, string granularity) =>
    [
        granularity == Month ? r.Period.ToString("yyyy-MM", CultureInfo.InvariantCulture) : r.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        r.DepartmentName ?? r.DepartmentId.ToString(), r.Kind, r.ObjectName ?? $"{r.ObjectId} (deleted)", r.Step, r.ClientName ?? "",
        Invariant(r.Calls), Invariant(r.InputTokens), Invariant(r.CachedInputTokens), Invariant(r.OutputTokens), r.CostUsd.ToString(Swedish),
        r.EstimatedCostUsd.ToString(Swedish), Invariant(r.EstimatedCalls),
    ];

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A report's period (admin §8.6): inclusive dates, <c>day</c> or <c>month</c>, within the limits.</summary>
public sealed record CostPeriod(DateOnly From, DateOnly To, string Granularity)
{
    /// <summary>The period of a query: default the last 30 days by day; <c>from</c> after <c>to</c> is invalid; too long is <c>period_too_long</c>.</summary>
    public static CostPeriod Of(DateOnly? from, DateOnly? to, string? granularity, DateOnly today)
    {
        var unit = granularity ?? CostEndpoints.Day;
        if (unit is not (CostEndpoints.Day or CostEndpoints.Month))
        {
            throw AdminFaults.Field("granularity", AdminErrorCodes.InvalidValue);
        }

        var end = to ?? today;
        var start = from ?? end.AddDays(1 - CostEndpoints.DefaultDays);
        if (start > end)
        {
            throw AdminFaults.Field("from", AdminErrorCodes.InvalidValue);
        }

        var tooLong = unit == CostEndpoints.Day
            ? end.DayNumber - start.DayNumber + 1 > CostEndpoints.MaxDays
            : (end.Year * 12 + end.Month) - (start.Year * 12 + start.Month) + 1 > CostEndpoints.MaxMonths;
        return tooLong ? throw AdminFaults.Of(AdminErrorCodes.PeriodTooLong) : new CostPeriod(start, end, unit);
    }
}

/// <summary>One summed group of a usage table (the SQL's columns).</summary>
public sealed class UsageAggregate
{
    public DateOnly Period { get; init; }
    public Guid ObjectId { get; init; }
    public Guid DepartmentId { get; init; }
    public string Step { get; init; } = "";
    public Guid? ClientId { get; init; }
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public decimal EstimatedCostUsd { get; init; }
    public long EstimatedCalls { get; init; }
}

/// <summary>The summed fields of §8.6: the gateway's cost (<see cref="CostUsd"/>) apart from the estimate of the calls without one.</summary>
public sealed record CostTotals(long Calls, long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, decimal EstimatedCostUsd, long EstimatedCalls)
{
    public static CostTotals Of(UsageAggregate u) => new(u.Calls, u.InputTokens, u.CachedInputTokens, u.OutputTokens, u.CostUsd, u.EstimatedCostUsd, u.EstimatedCalls);

    public static CostTotals Of(IEnumerable<CostTotals> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        long calls = 0, input = 0, cached = 0, output = 0, estimatedCalls = 0;
        decimal cost = 0, estimated = 0;
        foreach (var p in parts)
        {
            calls += p.Calls;
            input += p.InputTokens;
            cached += p.CachedInputTokens;
            output += p.OutputTokens;
            cost += p.CostUsd;
            estimated += p.EstimatedCostUsd;
            estimatedCalls += p.EstimatedCalls;
        }

        return new CostTotals(calls, input, cached, output, cost, estimated, estimatedCalls);
    }
}

/// <summary>An object's cost per period, step and (assistants) client; <see cref="ClientName"/> is <c>admin-console</c> for the test panel.</summary>
public sealed record CostRow(
    DateOnly Period, string Step, Guid? ClientId, string? ClientName, long Calls, long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd,
    decimal EstimatedCostUsd, long EstimatedCalls)
{
    public static CostRow Of(UsageAggregate u, Guid? clientId, string? clientName) => new(u.Period, u.Step, clientId, clientName, u.Calls, u.InputTokens,
        u.CachedInputTokens, u.OutputTokens, u.CostUsd, u.EstimatedCostUsd, u.EstimatedCalls);
}

/// <summary>An assistant's or knowledge base's cost report: the period (a month's <c>period</c> is its first day), the rows and their totals.</summary>
public sealed record ObjectCosts(DateOnly From, DateOnly To, string Granularity, IReadOnlyList<CostRow> Rows, CostTotals Totals)
{
    public static ObjectCosts Of(CostPeriod period, IReadOnlyList<CostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(rows);
        return new(period.From, period.To, period.Granularity, rows,
            CostTotals.Of(rows.Select(r => new CostTotals(r.Calls, r.InputTokens, r.CachedInputTokens, r.OutputTokens, r.CostUsd, r.EstimatedCostUsd, r.EstimatedCalls))));
    }
}

/// <summary>
/// A row of the department report: the period, the row's department, <c>assistant</c> or <c>collection</c>, the object (its
/// name null and <see cref="Deleted"/> true when it is gone or deleted), the step and the client (assistants only).
/// </summary>
public sealed record DepartmentCostRow(
    DateOnly Period, Guid DepartmentId, string? DepartmentName, string Kind, Guid ObjectId, string? ObjectName, bool Deleted, string Step, Guid? ClientId,
    string? ClientName, long Calls, long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, decimal EstimatedCostUsd, long EstimatedCalls)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public CostTotals Sums => new(Calls, InputTokens, CachedInputTokens, OutputTokens, CostUsd, EstimatedCostUsd, EstimatedCalls);

    public static DepartmentCostRow Of(UsageAggregate u, string kind, string? departmentName, string? objectName, string? clientName) => new(u.Period,
        u.DepartmentId, departmentName, kind, u.ObjectId, objectName, objectName is null, u.Step, u.ClientId, clientName, u.Calls, u.InputTokens,
        u.CachedInputTokens, u.OutputTokens, u.CostUsd, u.EstimatedCostUsd, u.EstimatedCalls);
}

/// <summary>The department report (admin §8.6): the period, one row per period, department, object, step and client, and the totals.</summary>
public sealed record CostReport(DateOnly From, DateOnly To, string Granularity, IReadOnlyList<DepartmentCostRow> Rows, CostTotals Totals);
