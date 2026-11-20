using Npgsql;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.SiteProfiles;
using Kalla.Worker.Connectors.Crawl;

namespace Kalla.Worker.Connectors.Persistence;

/// <summary><see cref="ISiteProfileReader"/> over <c>content."SiteProfiles"</c> and the applied <c>SiteProfileVersions</c> row.</summary>
public sealed class PostgresSiteProfileReader(JobDatabase database) : ISiteProfileReader
{
    private const string AppliedSql = """
        SELECT p."Id", v."Number", p."PresetId", v."Profile"::text
        FROM content."SiteProfiles" p
        JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId"
        WHERE p."Classification" = $1 AND p."Host" = $2
        """;

    public async Task<AppliedSiteProfile?> GetAppliedAsync(string classification, string host, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(AppliedSql, connection) { Parameters = { Db.P(classification), Db.P(host) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var json = System.Text.Json.JsonDocument.Parse(reader.GetString(3));
        var profile = ContractJson.Deserialize<SiteProfile>(json.RootElement);
        return new AppliedSiteProfile(reader.GetGuid(0), reader.GetInt32(1), Db.String(reader, 2), profile);
    }
}
