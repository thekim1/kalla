using Microsoft.Extensions.Options;
using Kalla.Domain;

namespace Kalla.FakeGateway;

/// <summary>
/// The gateway's Integration API (gateway phase 3; gateway-changes §5.6; repo-and-deployment §4.3), read-only:
/// <c>GET /integration/v1/usage</c> from the requests this fake served (<see cref="FakeUsage"/>: the key each ran as, its
/// <c>x-ume-step</c> and <c>x-ume-run-id</c>), and <c>GET /integration/v1/budgets</c> from
/// <c>FakeGateway:AssignedKeys:&lt;n&gt;:Budgets</c>. <c>Authorization: Bearer</c> with a credential key holding the scope
/// <c>usage.read</c>: any other key or none is <c>401</c>, a credential key without the scope <c>403</c>. Errors are problem
/// details, as the gateway's Admin API.
/// </summary>
public static class FakeIntegrationApi
{
    public const string UsageReadScope = "usage.read";
    public const int MaxDays = 366;
    private static readonly string[] Dimensions = ["key", "model", "step"];

    public static void MapFakeIntegration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/integration/v1/usage", (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options, FakeUsage served) =>
        {
            var o = options.Value;
            if (Authorize(ctx, o) is { } refused)
            {
                return refused;
            }

            var q = ctx.Request.Query;
            if (!DateOnly.TryParse(q["from"], System.Globalization.CultureInfo.InvariantCulture, out var from)
                || !DateOnly.TryParse(q["to"], System.Globalization.CultureInfo.InvariantCulture, out var to) || from > to)
            {
                return Problem(400, "from and to are required dates, from not after to.");
            }

            if (to.DayNumber - from.DayNumber + 1 > MaxDays)
            {
                return Problem(400, $"The period is longer than {MaxDays} days.");
            }

            var granularity = q["granularity"].ToString() is { Length: > 0 } g ? g : "day";
            if (granularity is not ("day" or "month"))
            {
                return Problem(400, "granularity is day or month.");
            }

            string[] groupBy = q.ContainsKey("groupBy")
                ? [.. q["groupBy"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : ["key"];
            if (groupBy.Any(d => !Dimensions.Contains(d)) || groupBy.Distinct().Count() != groupBy.Length)
            {
                return Problem(400, "groupBy is any of key, model and step, comma-separated.");
            }

            Guid? keyId = null;
            if (q["keyId"].ToString() is { Length: > 0 } k)
            {
                if (!Guid.TryParse(k, out var id) || !IntegrationKeys(o).ContainsKey(id))
                {
                    return Problem(404, "The key is not one of the integration's keys.");
                }

                keyId = id;
            }

            if (granularity == "month")
            {
                from = new DateOnly(from.Year, from.Month, 1); // a month widens the period to whole months
                to = new DateOnly(to.Year, to.Month, 1).AddMonths(1).AddDays(-1);
            }

            var runId = q["runId"].ToString() is { Length: > 0 } run ? run : null;
            var names = IntegrationKeys(o);
            var byKey = groupBy.Contains("key");
            var byModel = groupBy.Contains("model");
            var byStep = groupBy.Contains("step");
            var items = served.Records
                .Select(r => (Record: r, Day: StockholmTime.DayOf(r.At)))
                .Where(x => x.Day >= from && x.Day <= to && (keyId is null || x.Record.KeyId == keyId) && (runId is null || x.Record.RunId == runId))
                .GroupBy(x => (Period: granularity == "month" ? new DateOnly(x.Day.Year, x.Day.Month, 1) : x.Day, Key: byKey ? x.Record.KeyId : null,
                    Model: byModel ? x.Record.Model : null, Step: byStep ? x.Record.Step : null))
                .OrderBy(g => g.Key.Period).ThenBy(g => g.Key.Key).ThenBy(g => g.Key.Model, StringComparer.Ordinal).ThenBy(g => g.Key.Step, StringComparer.Ordinal)
                .Select(g => new
                {
                    period = g.Key.Period,
                    keyId = g.Key.Key,
                    keyName = g.Key.Key is { } id && names.TryGetValue(id, out var name) ? name : null,
                    model = g.Key.Model,
                    step = g.Key.Step,
                    requests = g.LongCount(),
                    successfulRequests = g.LongCount(),
                    inputTokens = g.Sum(x => x.Record.InputTokens),
                    cachedInputTokens = 0L,
                    outputTokens = g.Sum(x => x.Record.OutputTokens),
                    costUsd = g.Sum(x => x.Record.CostUsd),
                })
                .ToList();
            return Results.Json(new { from, to, granularity, groupBy, items });
        });

        app.MapGet("/integration/v1/budgets", (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
        {
            var o = options.Value;
            if (Authorize(ctx, o) is { } refused)
            {
                return refused;
            }

            if (!Guid.TryParse(ctx.Request.Query["keyId"], out var keyId) || o.AssignedKeys.FirstOrDefault(a => a.Id == keyId) is not { } key)
            {
                return Problem(404, "The key is not one of the integration's keys.");
            }

            var team = FakeCatalog.OrganisationTeams.FirstOrDefault(t => t.Id == key.TeamId);
            var now = DateTimeOffset.UtcNow;
            var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var items = key.Budgets.Select(b => new
            {
                id = b.Id,
                scope = b.Scope,
                scopeId = b.ScopeId ?? b.Scope switch
                {
                    "Team" => key.TeamId,
                    "Department" => team?.DepartmentId ?? Guid.Empty,
                    _ => key.Id,
                },
                unit = b.Unit,
                limit = b.Limit,
                residency = b.Residency,
                period = b.Period,
                periodStart = start,
                periodEnd = start.AddMonths(1),
                spent = b.Spent,
                percentUsed = b.Limit == 0 ? 100m : Math.Round(b.Spent / b.Limit * 100m, 2),
                exhausted = b.Spent >= b.Limit,
            }).ToList();
            return Results.Json(new { keyId, items });
        });
    }

    /// <summary>401 for no key or one that is not a credential key; 403 for a credential key without <c>usage.read</c>; null when allowed.</summary>
    private static IResult? Authorize(HttpContext ctx, FakeGatewayOptions o)
    {
        var presented = FakeAccess.BearerOrApiKey(ctx.Request);
        var credential = presented is null ? null : o.CredentialKeys.Values.FirstOrDefault(c => c.Key == presented);
        return credential is null
            ? Problem(401, "The key is missing, or is not an integration's credential key.")
            : !credential.Scopes.Contains(UsageReadScope, StringComparer.Ordinal)
                ? Problem(403, $"The credential key lacks the scope {UsageReadScope}.")
                : null;
    }

    /// <summary>The integration's keys by id, with their names: the assigned keys and the credential keys.</summary>
    private static Dictionary<Guid, string> IntegrationKeys(FakeGatewayOptions o)
    {
        var keys = o.AssignedKeys.ToDictionary(k => k.Id, k => k.Name);
        foreach (var c in o.CredentialKeys.Values)
        {
            keys.TryAdd(FakeAccess.CredentialId(c), c.Name ?? "credential");
        }

        return keys;
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}
