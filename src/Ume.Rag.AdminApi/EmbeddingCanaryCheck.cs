using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// One profile's canary check. <c>Error</c> is null when the check ran, else <c>gateway_not_configured</c>,
/// <c>gateway_key_missing</c>, <c>gateway_refused</c>, <c>gateway_unavailable</c>, <c>residency_refused</c>,
/// <c>bad_vector</c> or <c>error</c>, with <c>Detail</c> from <see cref="GatewayModelSync.ErrorText"/>; nothing is stored then.
/// </summary>
public sealed record EmbeddingCanaryCheckResult(Guid ProfileId, string Profile, double? Similarity, bool Passed, DateTimeOffset? CheckedAt,
    string? Error = null, string? Detail = null);

/// <summary>A profile's stored last check; <c>Passed</c> is null when it was never checked.</summary>
public sealed record EmbeddingCanaryLastResult(Guid ProfileId, string Profile, bool Recorded, double? Similarity, bool? Passed, DateTimeOffset? CheckedAt);

/// <summary>
/// The embedding canary on demand (admin design §4.5 <c>POST /api/gateway/canary-check</c>, processing design §11): for each
/// active profile with a recorded canary, one embedding of its <c>CanaryText</c> with its model at its ceiling, compared with
/// <c>CanaryEmbedding</c>. The result is stored on the profile (<c>CanaryCheckedAt</c>, <c>CanarySimilarity</c>), which the
/// worker reads to refuse a profile whose check failed; each stored result is audited <c>check-canary</c>. Only a platform
/// admin's request runs it.
/// </summary>
public sealed partial class EmbeddingCanaryCheck(GatewayApi gateway, IOptions<GatewayOptions> options, IMeterFactory meters,
    ILogger<EmbeddingCanaryCheck> logger)
{
    private readonly Gauge<double> _similarity = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateGauge<double>("rag.embed.canary_similarity", description: "Cosine of the last embedding canary check per profile");

    /// <summary>The last stored check of every active profile, from the database only: no gateway call.</summary>
    public static async Task<IReadOnlyList<EmbeddingCanaryLastResult>> LastAsync(RagDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var recorded = (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM config."EmbeddingProfiles" WHERE "CanaryEmbedding" IS NOT NULL
            """).ToListAsync(cancellationToken)).ToHashSet();
        var profiles = await db.EmbeddingProfiles.AsNoTracking().Where(p => p.State == EmbeddingProfileStates.Active)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(cancellationToken);
        return [.. profiles.Select(p => new EmbeddingCanaryLastResult(p.Id, p.Name, recorded.Contains(p.Id), p.CanarySimilarity,
            p.CanarySimilarity is { } similarity ? similarity >= EmbeddingCanaryThreshold.MinCosine : null, p.CanaryCheckedAt))];
    }

    public async Task<IReadOnlyList<EmbeddingCanaryCheckResult>> RunAsync(AdminContext admin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var db = admin.Db;
        var recorded = await db.Database.SqlQuery<CanaryRow>($"""
            SELECT "Id", "CanaryEmbedding"::real[] AS "Embedding" FROM config."EmbeddingProfiles"
            WHERE "State" = 'active' AND "CanaryText" IS NOT NULL AND "CanaryEmbedding" IS NOT NULL
            """).ToDictionaryAsync(r => r.Id, r => r.Embedding, cancellationToken);
        var profiles = await db.EmbeddingProfiles.AsNoTracking().Where(p => recorded.Keys.Contains(p.Id))
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(cancellationToken);
        var configured = !string.IsNullOrEmpty(options.Value.BaseUrl);
        var results = new List<EmbeddingCanaryCheckResult>(profiles.Count);
        foreach (var profile in profiles)
        {
            results.Add(configured
                ? await CheckAsync(admin, profile, recorded[profile.Id], cancellationToken)
                : new EmbeddingCanaryCheckResult(profile.Id, profile.Name, null, false, null, "gateway_not_configured"));
        }

        return results;
    }

    private async Task<EmbeddingCanaryCheckResult> CheckAsync(AdminContext admin, EmbeddingProfile profile, float[] canary, CancellationToken cancellationToken)
    {
        float[] vector;
        try
        {
            var answer = await gateway.EmbedAsync(profile.GatewayModel, [profile.CanaryText!], profile.MaxResidency, cancellationToken,
                profile.RequestDimensions ? profile.Dimensions : null);
            vector = answer.Vectors[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var error = ex switch
            {
                GatewayKeyMissingException => "gateway_key_missing",
                GatewayRefusedException => "gateway_refused",
                GatewayUnavailableException => "gateway_unavailable",
                GatewayResidencyException => "residency_refused",
                _ => "error",
            };
            LogCheckFailed(logger, profile.Name, GatewayModelSync.ErrorText(ex));
            return new EmbeddingCanaryCheckResult(profile.Id, profile.Name, null, false, null, error, GatewayModelSync.ErrorText(ex));
        }

        if (vector.Length != canary.Length)
        {
            return new EmbeddingCanaryCheckResult(profile.Id, profile.Name, null, false, null, "bad_vector",
                $"{vector.Length} dimensions, the recorded canary {canary.Length}");
        }

        // Stored as real: the worker compares the stored value, so the verdict is taken from it too.
        var stored = (float)Cosine(vector, canary);
        var passed = stored >= EmbeddingCanaryThreshold.MinCosine;
        var checkedAt = admin.Now;
        await admin.WriteAsync(async ct =>
        {
            var row = await admin.Db.EmbeddingProfiles.SingleAsync(p => p.Id == profile.Id, ct);
            var before = new { row.CanaryCheckedAt, row.CanarySimilarity };
            row.CanaryCheckedAt = checkedAt;
            row.CanarySimilarity = stored;
            return new AdminChange<bool>(true, "check-canary", "embedding-profile", profile.Id, before,
                new { CanaryCheckedAt = checkedAt, CanarySimilarity = stored, Passed = passed, profile.GatewayModel });
        }, cancellationToken);
        _similarity.Record(stored, new KeyValuePair<string, object?>("profile", profile.Name));
        if (!passed)
        {
            LogMismatch(logger, profile.Name, stored);
        }

        return new EmbeddingCanaryCheckResult(profile.Id, profile.Name, stored, passed, checkedAt);
    }

    internal static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private sealed class CanaryRow
    {
        public Guid Id { get; init; }
        public required float[] Embedding { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Embedding canary of profile {Profile} has cosine {Similarity:F5} < 0.999: the gateway serves another model; the worker refuses the profile")]
    private static partial void LogMismatch(ILogger logger, string profile, double similarity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding canary check of profile {Profile} failed: {Error}; nothing was stored")]
    private static partial void LogCheckFailed(ILogger logger, string profile, string error);
}
