using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Explain access (identity design §4.9, admin design §4.5, P1b item 9): runs the pure <see cref="IAccessPolicy"/> steps
/// of identity §4.3 for a client, a subject and an assistant against the configuration at its current version, and answers
/// the first failing step's stable id and deny code. Nothing is accessed: no gateway call, no content, no security event.
/// Platform admins ask about anything; department admins (<c>Manage</c>) about their own departments' assistants.
/// </summary>
public static class ExplainAccessEndpoints
{
    /// <summary>The scopes a user's delegated token normally carries (identity §4.2): chat, knowledge and MCP.</summary>
    public static IReadOnlyList<string> DelegatedScopes { get; } = ["Chat.Use", "Knowledge.Read", "Mcp.Use"];

    public static RouteGroupBuilder MapExplainAccess(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/access/explain", async (ExplainAccessRequest request, AdminContext admin, PolicySnapshotStore snapshots, IAccessPolicy policy,
                CancellationToken ct) =>
            {
                var version = await admin.Db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct);
                var snapshot = await snapshots.LoadAsync(version, ct);
                if (!snapshot.Assistants.TryGetValue(request.AssistantId, out var assistant) ||
                    (!admin.IsPlatformAdmin && !await admin.Departments().AnyAsync(d => d.Id == assistant.DepartmentId, ct)))
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                if (!snapshot.Clients.TryGetValue(request.ClientId, out var client))
                {
                    // The data plane refuses an unknown client before the policy runs (KnowledgeCallers); say so the same way.
                    return new AccessExplanation(false, "1", DenyReasons.ClientUnknown, 403, null, null, snapshot.Version);
                }

                var who = await PrincipalAsync(admin, request.Subject!, client, snapshot.Version, ct);
                var decision = policy.Decide(who, ResourceRef.Assistant(assistant.Id), request.Action, snapshot, confirmed: true);
                return new AccessExplanation(decision.Allowed, decision.Step, decision.Reason, decision.HttpStatus, decision.EffectiveLevel,
                    decision.MaxResidency is { } residency ? ResidencyCeiling.ToHeader(residency) : null, decision.ConfigVersion);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithTags("Access").WithName("ExplainAccess")
            .WithSummary("Why a client and subject may or may not use an assistant (the first failing policy step)");

        return api;
    }

    /// <summary>
    /// The principal the data plane would build (identity §4.9): the client kind's normal credential, the key's scopes for
    /// a key subject, no permissions, and the membership as given with <c>Known = true</c>.
    /// </summary>
    private static async Task<RagPrincipal> PrincipalAsync(AdminContext admin, ExplainSubject subject, ClientEntry client, long version, CancellationToken ct)
    {
        var membership = new MembershipFacts(
            new HashSet<Guid>(subject.Departments ?? []), new HashSet<string>(subject.Groups ?? [], StringComparer.Ordinal), Known: true);
        switch (subject.Kind)
        {
            case ExplainSubjectKinds.Key:
                if (!Guid.TryParse(subject.Id, out var keyId))
                {
                    throw AdminFaults.Field("subject.id", AdminErrorCodes.InvalidValue);
                }

                var key = await admin.Db.Keys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId, ct)
                    ?? throw AdminFaults.Field("subject.id", AdminErrorCodes.NotFound);
                var credential = key.Kind == KeyKinds.Public ? CredentialKind.PublicKey
                    : client.Kind == ClientKinds.Internal ? CredentialKind.ServiceKey
                    : CredentialKind.SecretKey;
                return new RagPrincipal(SubjectKind.Application, key.Id.ToString("D"), null, null, client, credential,
                    new KeyEntry(key.Id, key.ClientId, key.Kind, key.TeamId, key.Scopes.AssistantIds, key.Scopes.CollectionIds),
                    new HashSet<string>(), MembershipFacts.None, version);
            case ExplainSubjectKinds.OpenWebUiUser:
                // An Open WebUI user arrives on the client's connection key (chat design §4.3): the client's normal credential.
                return new RagPrincipal(SubjectKind.OwuiUser, "owui:" + subject.Id, null, null, client, CredentialKind.SecretKey, null,
                    new HashSet<string>(), MembershipFacts.None, version);
            case ExplainSubjectKinds.EntraUser:
                // A user's token carries the delegated scopes (identity §4.2); given ones explain a missing scope.
                return new RagPrincipal(SubjectKind.EntraUser, subject.Id, null, null, client, CredentialKind.AccessToken, null,
                    new HashSet<string>(subject.Permissions ?? DelegatedScopes, StringComparer.Ordinal), membership, version);
            default:
                throw AdminFaults.Field("subject.kind", AdminErrorCodes.InvalidValue);
        }
    }
}

/// <summary>The subject kinds explain access takes (identity §4.9).</summary>
public static class ExplainSubjectKinds
{
    /// <summary>An application with a key (<c>id</c> = the key id): its scopes apply.</summary>
    public const string Key = "key";

    /// <summary>An Open WebUI user (<c>id</c> = the JWT <c>sub</c>) on the client's connection key.</summary>
    public const string OpenWebUiUser = "owui-user";

    /// <summary>An Entra user (<c>id</c> = the <c>oid</c>) with the departments and groups given.</summary>
    public const string EntraUser = "entra-user";
}

public sealed record ExplainSubject : AdminRequest
{
    [Required]
    public string Kind { get; init; } = "";

    [Required, StringLength(200, MinimumLength = 1)]
    public string Id { get; init; } = "";

    [MaxLength(200)]
    public IReadOnlyList<Guid>? Departments { get; init; }

    [MaxLength(200)]
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>An Entra user's token scopes; omitted, the delegated ones (<see cref="ExplainAccessEndpoints.DelegatedScopes"/>).</summary>
    [MaxLength(20)]
    public IReadOnlyList<string>? Permissions { get; init; }
}

public sealed record ExplainAccessRequest : AdminRequest
{
    public Guid ClientId { get; init; }

    public Guid AssistantId { get; init; }

    [Required]
    public ExplainSubject? Subject { get; init; }

    /// <summary>What to explain; the default is a chat turn.</summary>
    public RagAction Action { get; init; } = RagAction.Chat;
}

/// <summary>
/// The decision (identity §4.9): <see cref="Step"/> the stable id of the first failing §4.3 step, <see cref="Reason"/> its
/// stable deny code (<c>ok</c> when allowed), the HTTP status the data plane would answer, the effective level and ceiling.
/// </summary>
public sealed record AccessExplanation(bool Allowed, string Step, string Reason, int Status, string? EffectiveLevel, string? MaxResidency, long ConfigVersion);
