using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// <c>GET /api/data-protection</c>, <c>GET/PUT /api/data-protection/{level}</c> (admin design §4.5, V29; read for every
/// role, change for platform admins): one policy per level (plan §8a). No row means the defaults in code
/// (<see cref="Defaults"/>); the first save creates the row. Until A1 allows stored chat content (weakness review §4) the
/// only history mode is <c>none</c>, and a level above <c>public</c> keeps its blob master key: it may be renamed, never
/// removed (data-model §7).
/// </summary>
public static partial class DataProtectionEndpoints
{
    public static RouteGroupBuilder MapDataProtection(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var policies = api.MapGroup("/data-protection").WithTags("Data protection");

        policies.MapGet("", async (AdminContext admin, CancellationToken ct) =>
            {
                var rows = await admin.Db.DataProtectionPolicies.AsNoTracking().ToDictionaryAsync(p => p.Classification, StringComparer.Ordinal, ct);
                return Classification.All.Select(level => DataProtectionDto.Of(rows.GetValueOrDefault(level) ?? Defaults(level))).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListDataProtection").WithSummary("The data protection policy of every level");

        policies.MapGet("/{level}", async (string level, AdminContext admin, CancellationToken ct) =>
            {
                var known = Known(level);
                return DataProtectionDto.Of(await admin.Db.DataProtectionPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Classification == known, ct)
                    ?? Defaults(known));
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetDataProtection").WithSummary("One level's data protection policy");

        policies.MapPut("/{level}", async (string level, DataProtectionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                Known(level);
                Rules(level, request);
                return DataProtectionDto.Of(await admin.WriteAsync(async attempt =>
                {
                    var row = await admin.Db.DataProtectionPolicies.SingleOrDefaultAsync(p => p.Classification == level, attempt);
                    object? before = null;
                    if (row is null)
                    {
                        if (!string.IsNullOrEmpty(request.Version) && request.Version != "0")
                        {
                            throw AdminFaults.Of(AdminErrorCodes.ConcurrentChange);
                        }

                        row = Defaults(level);
                        admin.Db.DataProtectionPolicies.Add(row);
                    }
                    else
                    {
                        admin.ExpectVersion(row, request.Version);
                        before = AuditJson.WithoutVersion(DataProtectionDto.Of(row));
                    }

                    request.ApplyTo(row);
                    row.UpdatedAt = admin.Now;
                    row.UpdatedBy = admin.Actor;
                    return new AdminChange<DataProtectionPolicy>(row, "update", AuditEntityTypes.DataProtection, level, before,
                        AuditJson.WithoutVersion(DataProtectionDto.Of(row)))
                    {
                        Classification = level,
                    };
                }, ct));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateDataProtection").WithSummary("Change one level's data protection policy");

        return api;
    }

    /// <summary>
    /// The policy of a level without a row (plan §8a, data-model §8): retention 90 days (<c>sensitive</c> 30), chat encryption
    /// above <c>public</c>, <c>rag-blob-&lt;level&gt;</c> blob keys above <c>public</c>, a second approver and history-free clients
    /// only for <c>sensitive</c>.
    /// </summary>
    public static DataProtectionPolicy Defaults(string level) => DataProtectionPolicy.DefaultFor(level);

    private static string Known(string level) =>
        Classification.All.Contains(level) ? level : throw AdminFaults.Of(AdminErrorCodes.NotFound);

    /// <summary>V29, as named rules (the table's CHECKs are the backstop and would answer <c>platform_rule</c>).</summary>
    private static void Rules(string level, DataProtectionRequest request)
    {
        string? rule = null;
        if (request.AllowedHistoryModes.Any(m => !HistoryModes.All.Contains(m)) || !HistoryModes.All.Contains(request.DefaultHistoryMode))
        {
            rule = "only the history mode 'none' is available until stored chat content is built (A1)";
        }
        else if (!request.AllowedHistoryModes.Contains(request.DefaultHistoryMode))
        {
            rule = "the default history mode must be one of the allowed modes";
        }
        else if (level != Classification.Public && !request.EncryptionRequired)
        {
            rule = "chat encryption is required above public";
        }
        else if (request.RetentionDefaultDays > request.RetentionMaxDays || request.RetentionMaxDays > 90)
        {
            rule = "the default retention must not exceed the maximum, and the maximum must not exceed 90 days";
        }
        else if (level == Classification.Public ? request.BlobMasterKeyName is not null : request.BlobMasterKeyName is null || !KeyName().IsMatch(request.BlobMasterKeyName))
        {
            rule = level == Classification.Public
                ? "public blobs are not envelope-encrypted, so public has no blob key"
                : "a blob key name like rag-blob-internal is required above public";
        }

        if (rule is not null)
        {
            throw AdminFaults.Of(AdminErrorCodes.PolicyRule, new Dictionary<string, string> { ["rule"] = rule });
        }
    }

    [GeneratedRegex("^rag-blob-[a-z0-9-]{1,40}$")]
    private static partial Regex KeyName();
}

public sealed record DataProtectionDto(
    string Classification, IReadOnlyList<string> AllowedHistoryModes, string DefaultHistoryMode, int RetentionDefaultDays, int RetentionMaxDays,
    bool EncryptionRequired, bool ReviewSecondApprover, bool ClientsWithoutHistoryOnly, string? BlobMasterKeyName, DateTimeOffset? UpdatedAt, string? UpdatedBy,
    string Version)
{
    public static DataProtectionDto Of(DataProtectionPolicy p) => new(p.Classification, p.AllowedHistoryModes, p.DefaultHistoryMode, p.RetentionDefaultDays,
        p.RetentionMaxDays, p.EncryptionRequired, p.ReviewSecondApprover, p.ClientsWithoutHistoryOnly, p.BlobMasterKeyName,
        p.Version == 0 ? null : p.UpdatedAt, p.UpdatedBy, p.Version.ToString(CultureInfo.InvariantCulture));
}

public sealed record DataProtectionRequest : AdminRequest
{
    [Required, MinLength(1), MaxLength(3)]
    public IReadOnlyList<string> AllowedHistoryModes { get; init; } = [HistoryModes.None];

    [Required, MaxLength(20)]
    public string DefaultHistoryMode { get; init; } = HistoryModes.None;

    [Range(1, 90)]
    public int RetentionDefaultDays { get; init; } = 90;

    [Range(1, 90)]
    public int RetentionMaxDays { get; init; } = 90;

    public bool EncryptionRequired { get; init; }
    public bool ReviewSecondApprover { get; init; }
    public bool ClientsWithoutHistoryOnly { get; init; }

    [MaxLength(60)]
    public string? BlobMasterKeyName { get; init; }

    /// <summary>The row's <c>version</c>; null or <c>0</c> while the level has no row.</summary>
    public string? Version { get; init; }

    public void ApplyTo(DataProtectionPolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.AllowedHistoryModes = [.. AllowedHistoryModes];
        p.DefaultHistoryMode = DefaultHistoryMode;
        p.RetentionDefaultDays = RetentionDefaultDays;
        p.RetentionMaxDays = RetentionMaxDays;
        p.EncryptionRequired = EncryptionRequired;
        p.ReviewSecondApprover = ReviewSecondApprover;
        p.ClientsWithoutHistoryOnly = ClientsWithoutHistoryOnly;
        p.BlobMasterKeyName = BlobMasterKeyName;
    }
}
