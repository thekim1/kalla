using Kalla.Domain.Entities;

namespace Kalla.Domain.Access;

/// <summary>The access policy (identity design §4.2): pure, no I/O, so the matrix is a table-driven test.</summary>
public interface IAccessPolicy
{
    /// <summary>The first failing step of identity §4.3 decides; <paramref name="confirmed"/> is step 0's input.</summary>
    AccessDecision Decide(RagPrincipal who, ResourceRef what, RagAction action, PolicySnapshot config, bool confirmed);

    /// <summary>The candidates <see cref="Decide"/> allows, silently (no events; <c>/v1/models</c>, <c>/v1/collections</c>).</summary>
    IReadOnlyList<ResourceRef> Filter(RagPrincipal who, IEnumerable<ResourceRef> candidates, RagAction action, PolicySnapshot config, bool confirmed);
}

/// <summary>
/// Identity design §4.3 steps 0–11 for P1 (step 12, tools, is P2). <paramref name="knowledgeOpenToClientKinds"/> is
/// <c>Knowledge:OpenToClientKinds</c> (step 1a, knowledge routes: <see cref="RagAction.Search"/> and
/// <see cref="RagAction.ReadDocument"/>; P1 <c>internal</c> only). Download grants are re-checked by their own rule
/// (knowledge design §4.6), not by step 1a.
/// </summary>
public sealed class AccessPolicy(IReadOnlySet<string> knowledgeOpenToClientKinds) : IAccessPolicy
{
    /// <summary>The P1 default of <c>Knowledge:OpenToClientKinds</c>.</summary>
    public static IReadOnlySet<string> DefaultKnowledgeClientKinds { get; } = new HashSet<string>(StringComparer.Ordinal) { ClientKinds.Internal };

    public AccessDecision Decide(RagPrincipal who, ResourceRef what, RagAction action, PolicySnapshot config, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(config);
        var v = config.Version;

        // 0: the snapshot is confirmed within the grace window.
        if (!confirmed)
        {
            return AccessDecision.Deny("0", DenyReasons.ConfigUnconfirmed, 503, v);
        }

        // 1: the client exists, is enabled, parses (fail closed, §4.5 rule 3), and is the key's client.
        if (!config.Clients.TryGetValue(who.Client.Id, out var client) || (who.Key is { } key && key.ClientId != client.Id))
        {
            return AccessDecision.Deny("1", DenyReasons.ClientUnknown, 403, v);
        }

        if (!client.IsEnabled || Levels.Rank(client.Ceiling) is null)
        {
            return AccessDecision.Deny("1", DenyReasons.ClientDisabled, 403, v);
        }

        // 1a: knowledge routes are open to the configured client kinds only.
        if (action is RagAction.Search or RagAction.ReadDocument && who.Credential != CredentialKind.DownloadGrant &&
            !knowledgeOpenToClientKinds.Contains(client.Kind))
        {
            return AccessDecision.Deny("1a", DenyReasons.ClientNotAllowed, 403, v);
        }

        // 2: the credential kind is one the client kind uses.
        if (!CredentialAllowed(client.Kind, who.Credential))
        {
            return AccessDecision.Deny("2", DenyReasons.CredentialNotAllowed, 403, v);
        }

        // 3: anonymous subjects only through a widget, and a widget only anonymous.
        if ((who.Kind == SubjectKind.Anonymous) != (client.Kind == ClientKinds.Widget))
        {
            return AccessDecision.Deny("3", DenyReasons.SubjectNotAllowed, 403, v);
        }

        // 4: permission for the action.
        if (!PermissionAllowed(who, action))
        {
            return AccessDecision.Deny("4", DenyReasons.PermissionMissing, 403, v);
        }

        // 5–11 per resource kind.
        return what.Kind == ResourceKind.Assistant
            ? DecideAssistant(who, client, what.Id, config)
            : DecideCollection(who, client, what.Id, config);
    }

    public IReadOnlyList<ResourceRef> Filter(RagPrincipal who, IEnumerable<ResourceRef> candidates, RagAction action, PolicySnapshot config, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates.Where(c => Decide(who, c, action, config, confirmed).Allowed)];
    }

    private static AccessDecision DecideAssistant(RagPrincipal who, ClientEntry client, Guid id, PolicySnapshot config)
    {
        var v = config.Version;
        // 5: exists, active, with a published version.
        if (!config.Assistants.TryGetValue(id, out var assistant) || assistant.State != AssistantStates.Active || assistant.Published is null ||
            Levels.Rank(assistant.Classification) is null)
        {
            return AccessDecision.Deny("5", DenyReasons.NotFound, 404, v);
        }

        // 6: key scope. Assistant scopes name the assistant; collection scopes must reach at least one of its published
        // collections (the search is then narrowed to them, knowledge §4.2), so a collection-scoped key cannot use an
        // assistant to search outside its scope, nor see other departments' assistants in /v1/models.
        if (who.Key is { AssistantScopes.Count: > 0 } key && !key.AssistantScopes.Contains(id))
        {
            return AccessDecision.Deny("6", DenyReasons.KeyScope, 404, v);
        }

        if (who.Key is { CollectionScopes.Count: > 0 } scoped && !assistant.Published.CollectionIds.Any(scoped.CollectionScopes.Contains))
        {
            return AccessDecision.Deny("6", DenyReasons.KeyScope, 404, v);
        }

        if (CeilingAnonymousHistory(who, client, assistant.Classification, assistant.HistoryMode, config) is { } denied)
        {
            return denied;
        }

        // 10, 11: audience. Application subjects match only client rows; department and group rows need known membership.
        var needsMembership = false;
        foreach (var audience in assistant.Audiences)
        {
            switch (audience.Kind)
            {
                case AssistantAudienceKinds.Client when audience.SubjectId == client.Id:
                    return Allowed(assistant.Classification, v);
                case "department" when who.Kind != SubjectKind.Application && audience.SubjectId is { } department:
                    if (who.Membership.Known && who.Membership.Departments.Contains(department))
                    {
                        return Allowed(assistant.Classification, v);
                    }

                    needsMembership |= !who.Membership.Known;
                    break;
                case "group" when who.Kind != SubjectKind.Application && audience.GroupValue is { } group:
                    if (who.Membership.Known && who.Membership.Groups.Contains(group))
                    {
                        return Allowed(assistant.Classification, v);
                    }

                    needsMembership |= !who.Membership.Known;
                    break;
            }
        }

        return needsMembership
            ? AccessDecision.Deny("11", DenyReasons.MembershipUnavailable, 503, v)
            : AccessDecision.Deny("10", DenyReasons.AudienceMismatch, 404, v);
    }

    /// <summary>
    /// A collection directly (knowledge routes, download grants). P1: the collection must exist and not be deleted; its
    /// audience is the client itself only for <c>internal</c> clients and grants (an assistant's collections are reached
    /// through the assistant's decision); other client kinds wait for <c>CollectionAudiences</c> (P2).
    /// </summary>
    private static AccessDecision DecideCollection(RagPrincipal who, ClientEntry client, Guid id, PolicySnapshot config)
    {
        var v = config.Version;
        if (!config.Collections.TryGetValue(id, out var collection) || collection.Deleted || Levels.Rank(collection.Classification) is null)
        {
            return AccessDecision.Deny("5", DenyReasons.NotFound, 404, v);
        }

        if (who.Key is { CollectionScopes.Count: > 0 } key && !key.CollectionScopes.Contains(id))
        {
            return AccessDecision.Deny("6", DenyReasons.KeyScope, 404, v);
        }

        if (CeilingAnonymousHistory(who, client, collection.Classification, null, config) is { } denied)
        {
            return denied;
        }

        return client.Kind == ClientKinds.Internal || who.Credential == CredentialKind.DownloadGrant
            ? Allowed(collection.Classification, v)
            : AccessDecision.Deny("10", DenyReasons.AudienceMismatch, 404, v);
    }

    /// <summary>Steps 7 (ceiling), 8 (anonymous) and 9 (data handling); null when all pass.</summary>
    private static AccessDecision? CeilingAnonymousHistory(RagPrincipal who, ClientEntry client, string level, string? historyMode, PolicySnapshot config)
    {
        var v = config.Version;
        if (!Levels.AtMost(level, client.Ceiling))
        {
            return AccessDecision.Deny("7", DenyReasons.CeilingExceeded, 404, v);
        }

        if (who.Kind == SubjectKind.Anonymous && level != Classification.Public)
        {
            return AccessDecision.Deny("8", DenyReasons.AnonymousNotAllowed, 404, v);
        }

        // A level missing from the snapshot refuses (fail closed); the snapshot fills unsaved levels with the code defaults.
        if (!config.LevelPolicies.TryGetValue(level, out var policy) || (policy.ClientsWithoutHistoryOnly && client.StoresHistory) ||
            (historyMode is not null && !policy.AllowedHistoryModes.Contains(historyMode)))
        {
            return AccessDecision.Deny("9", DenyReasons.ClientStoresHistory, 404, v);
        }

        return null;
    }

    private static AccessDecision Allowed(string level, long version) =>
        new(true, DenyReasons.None, 200, "ok", level, ResidencyCeiling.ForLevel(level), version);

    /// <summary>Step 2: <c>open-webui</c> sk key (+ JWT), <c>widget</c> pk key, <c>integration</c> sk key or app token, delegated clients a token, <c>internal</c> the service key.</summary>
    private static bool CredentialAllowed(string clientKind, CredentialKind credential) => credential == CredentialKind.DownloadGrant || clientKind switch
    {
        ClientKinds.OpenWebUi => credential == CredentialKind.SecretKey,
        ClientKinds.Widget => credential == CredentialKind.PublicKey,
        ClientKinds.Integration => credential is CredentialKind.SecretKey or CredentialKind.AccessToken,
        ClientKinds.OwnClient or ClientKinds.Teams or ClientKinds.McpClient or ClientKinds.EntraApp => credential == CredentialKind.AccessToken,
        ClientKinds.Internal => credential == CredentialKind.ServiceKey,
        _ => false,
    };

    /// <summary>Step 4: keys by kind (<c>pk</c> chat-side only), tokens by scope or app role, grants only <see cref="RagAction.ReadDocument"/>.</summary>
    private static bool PermissionAllowed(RagPrincipal who, RagAction action) => who.Credential switch
    {
        CredentialKind.DownloadGrant => action == RagAction.ReadDocument,
        CredentialKind.PublicKey => action is RagAction.ListModels or RagAction.Chat or RagAction.Feedback,
        CredentialKind.SecretKey or CredentialKind.ServiceKey => true,
        CredentialKind.AccessToken => action switch
        {
            RagAction.ListModels or RagAction.Chat or RagAction.Feedback => who.Permissions.Contains("Chat.Use") || who.Permissions.Contains("Chat.Invoke.All"),
            RagAction.Search or RagAction.ReadDocument => who.Permissions.Contains("Knowledge.Read") || who.Permissions.Contains("Knowledge.Read.All"),
            RagAction.McpCall => who.Permissions.Contains("Mcp.Use"),
            _ => false,
        },
        _ => false,
    };
}
