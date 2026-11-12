using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// <c>GET /v1/ume/context</c> as the gateway builds it (<c>ContextEndpoint.Build</c>, thekim1/llmgateway#84 at
/// <c>7b5f4e3</c>; the contract's <c>UmeContext</c>, <c>contracts/gateway/integrations.json</c>): the presented key, and for
/// a credential key its integration, the assigned keys that are not revoked, and their teams and departments; with the
/// scope <c>organisation.read</c> every department and team, an inactive one included. A per-level dev key is an ordinary
/// key of the pilot team (role <c>None</c>, no integration).
/// </summary>
public static class FakeContext
{
    public const string OrganisationRead = "organisation.read";

    private static readonly Residency[] Ladder = [Residency.OnPrem, Residency.Eu, Residency.External];

    public static JsonObject Build(FakeKey key, FakeGatewayOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        var credential = key.Credential;
        var assigned = credential is null ? [] : options.AssignedKeys.Where(k => k.Status != "Revoked").ToList();
        var ownTeam = credential is null ? FakeCatalog.DigitaliseringTeamId : options.Integration.OwnerTeamId;
        var teamIds = new HashSet<Guid> { ownTeam };
        teamIds.UnionWith(assigned.Select(k => k.TeamId));

        IEnumerable<CatalogTeam> teams = FakeCatalog.OrganisationTeams.Where(t => teamIds.Contains(t.Id));
        IEnumerable<CatalogDepartment> departments = FakeCatalog.OrganisationDepartments.Where(d => teams.Any(t => t.DepartmentId == d.Id));
        if (credential is not null && credential.Scopes.Contains(OrganisationRead))
        {
            // The scope organisation.read: the whole organisation, which includes the teams above.
            departments = FakeCatalog.OrganisationDepartments;
            teams = FakeCatalog.OrganisationTeams;
        }

        var ownResidencies = Ladder.Where(r => r <= key.Level).ToList();
        return new JsonObject
        {
            ["object"] = "ume.context",
            ["gateway"] = new JsonObject
            {
                ["version"] = options.Gateway.Version,
                ["admin_url"] = string.IsNullOrEmpty(options.Gateway.AdminUrl) ? null : options.Gateway.AdminUrl,
                ["features"] = Strings(options.Gateway.Features),
            },
            ["key"] = new JsonObject
            {
                ["id"] = credential?.Id ?? StableId(credential?.Key ?? "level-" + key.Level),
                ["name"] = credential?.Name ?? (credential is null ? $"Fake {ResidencyCeiling.ToHeader(key.Level)} key" : "Ume.Rag credential key"),
                ["prefix"] = Prefix(credential?.Key ?? options.Keys.GetValueOrDefault(key.Level) ?? ""),
                ["team_id"] = ownTeam,
                ["integration_role"] = credential is null ? "None" : "Credential",
                ["integration_scopes"] = Strings(credential?.Scopes ?? []),
                ["require_max_residency"] = options.RequireMaxResidency,
                ["allowed_residencies"] = Strings(ownResidencies.Select(ResidencyCeiling.ToHeader)),
            },
            ["integration"] = credential is null
                ? null
                : new JsonObject { ["id"] = options.Integration.Id, ["name"] = options.Integration.Name, ["owner_team_id"] = options.Integration.OwnerTeamId },
            ["departments"] = new JsonArray([.. departments.OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id).Select(d => (JsonNode?)new JsonObject
            {
                ["id"] = d.Id,
                ["name"] = d.Name,
                ["cost_center_code"] = d.CostCenterCode,
                ["is_active"] = d.IsActive,
            })]),
            ["teams"] = new JsonArray([.. teams.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id).Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id,
                ["department_id"] = t.DepartmentId,
                ["name"] = t.Name,
                ["code"] = t.Code,
                ["is_active"] = t.IsActive,
            })]),
            ["assigned_keys"] = new JsonArray([.. assigned.Select(k => (JsonNode?)AssignedKey(k, ownResidencies, now))]),
        };
    }

    /// <summary>
    /// An assigned key as the gateway shows it (<c>ContextEndpoint.AssignedKey</c>): never a secret. <c>usable</c> is false
    /// when it and the presenting credential key allow no residency in common (empty = all).
    /// </summary>
    private static JsonObject AssignedKey(FakeAssignedKey k, IReadOnlyCollection<Residency> credentialResidencies, DateTimeOffset now)
    {
        var team = FakeCatalog.OrganisationTeams.FirstOrDefault(t => t.Id == k.TeamId);
        var department = team is null ? null : FakeCatalog.OrganisationDepartments.FirstOrDefault(d => d.Id == team.DepartmentId);
        var own = k.AllowedResidencies.Count == 0 ? Ladder : [.. k.AllowedResidencies];
        var status = k.Status == "Active" && k.ExpiresAt is { } expires && expires <= now ? "Expired" : k.Status;
        return new JsonObject
        {
            ["id"] = k.Id,
            ["name"] = k.Name,
            ["description"] = k.Description,
            ["team_id"] = k.TeamId,
            ["department_id"] = team?.DepartmentId,
            ["status"] = status,
            ["owner_active"] = team is { IsActive: true } && department is { IsActive: true },
            ["expires_at"] = k.ExpiresAt,
            ["allowed_models"] = Strings(k.AllowedModels),
            ["allowed_residencies"] = Strings(k.AllowedResidencies.Select(ResidencyCeiling.ToHeader)),
            ["usable"] = own.Intersect(credentialResidencies).Any(),
            ["allowed_providers"] = Strings(k.AllowedProviders),
            ["pii_policy"] = k.PiiPolicy,
            ["content_guard_policy"] = k.ContentGuardPolicy,
            ["attachment_policy"] = k.AttachmentPolicy,
            ["allow_provider_tools"] = k.AllowProviderTools,
            ["allow_provider_storage"] = k.AllowProviderStorage,
            ["requests_per_minute"] = k.RequestsPerMinute,
            ["tokens_per_minute"] = k.TokensPerMinute,
            ["max_concurrent_requests"] = k.MaxConcurrentRequests,
            ["max_output_tokens"] = k.MaxOutputTokens,
            ["has_system_prompt"] = k.HasSystemPrompt,
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>The key's prefix as the gateway shows it: its first 12 characters.</summary>
    private static string Prefix(string key) => key.Length <= 12 ? key : key[..12];

    /// <summary>A stable id per key, so repeated syncs see the same key.</summary>
    private static Guid StableId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("fake-key:" + key)).AsSpan(0, 16));
}
