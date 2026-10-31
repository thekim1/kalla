using Microsoft.Extensions.Options;
using Ume.Rag.Answering;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Keys;

namespace Ume.Rag.Api;

/// <summary>A refusal of a knowledge route before or outside the knowledge service: status, <c>code</c> and detail (§3.6).</summary>
public sealed class KnowledgeFaultException(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// <summary>Field errors of a 400 <c>invalid_request</c> (§3.1).</summary>
    public IDictionary<string, string[]>? Errors { get; init; }
}

/// <summary>
/// Who calls a knowledge route (knowledge design §2.3, §4.1 steps 0–2; identity design §6.3): a Ume.Rag key resolved
/// through the <see cref="KeyStore"/>, its status, a confirmed snapshot, the client's registry entry and the gate
/// <c>Knowledge:OpenToClientKinds</c>. P1 has keys only; tokens and Open WebUI user JWTs come with the chat role (P2 for
/// knowledge routes). Download grants are read by the content routes themselves.
/// </summary>
public sealed class KnowledgeCallers(KeyStore keys, IPolicySnapshotSource snapshots, IOptions<KnowledgeOptions> options, TimeProvider time)
{
    public async Task<RagPrincipal> AuthenticateAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var presented = KeyStore.ExtractKey(request);
        if (presented is null)
        {
            throw new KnowledgeFaultException(401, "missing_key", "An API key is required.");
        }

        var key = await keys.FindAsync(presented, ct) ?? throw new KnowledgeFaultException(401, KeyChecks.InvalidKey, "The API key is not valid.");
        if (KeyChecks.Refusal(key, presented, time.GetUtcNow()) is { } refusal)
        {
            throw refusal == KeyChecks.OwnerInactive
                ? new KnowledgeFaultException(403, refusal, "The key's client is disabled.")
                : new KnowledgeFaultException(401, refusal, "The API key cannot be used.");
        }

        if (!snapshots.IsConfirmed)
        {
            throw new KnowledgeFaultException(503, DenyReasons.ConfigUnconfirmed, "The configuration could not be confirmed. Try again shortly.");
        }

        var snapshot = snapshots.Current;
        if (!snapshot.Clients.TryGetValue(key.ClientId, out var client))
        {
            throw new KnowledgeFaultException(403, DenyReasons.ClientUnknown, "The key's client is not registered.");
        }

        if (!options.Value.OpenToClientKinds.Contains(client.Kind, StringComparer.Ordinal))
        {
            throw new KnowledgeFaultException(403, DenyReasons.ClientNotAllowed, "This client may not use the knowledge routes.");
        }

        var credential = key.Kind == KeyKinds.Public ? CredentialKind.PublicKey
            : client.Kind == ClientKinds.Internal ? CredentialKind.ServiceKey
            : CredentialKind.SecretKey;
        return new RagPrincipal(SubjectKind.Application, key.Id.ToString("D"), null, null, client, credential,
            new KeyEntry(key.Id, key.ClientId, key.Kind, key.TeamId, key.Scopes.AssistantIds, key.Scopes.CollectionIds),
            new HashSet<string>(), MembershipFacts.None, snapshot.Version);
    }
}

/// <summary>
/// Turns <see cref="KnowledgeFaultException"/> and <see cref="RetrievalDeniedException"/> into problem details with the
/// <c>code</c> extension (§3.6): <c>WWW-Authenticate: Bearer</c> on 401, <c>Retry-After: 1</c> on
/// <c>search_unavailable</c>, and the echoed slug for <c>collection_not_found</c>.
/// </summary>
public sealed class KnowledgeProblems : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            return await next(context);
        }
        catch (KnowledgeFaultException fault)
        {
            return Problem(context.HttpContext, fault.Status, fault.Code, fault.Message, null, fault.Errors);
        }
        catch (RetrievalDeniedException denied)
        {
            return Problem(context.HttpContext, denied.Status, denied.Code, Detail(denied.Code), denied.Subject);
        }
    }

    public static IResult Problem(HttpContext http, int status, string code, string detail, string? subject, IDictionary<string, string[]>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (status == 401)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
        }

        if (code is KnowledgeCodes.SearchUnavailable or KnowledgeCodes.EmbeddingUnavailable)
        {
            http.Response.Headers.RetryAfter = "1";
        }

        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (subject is not null && code == KnowledgeCodes.CollectionNotFound)
        {
            extensions["collection"] = subject;
        }

        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, title: "The request could not be completed", detail: detail, extensions: extensions);
    }

    private static string Detail(string code) => code switch
    {
        KnowledgeCodes.InvalidRequest => "The request is not valid.",
        KnowledgeCodes.AssistantIdNotAllowed => "assistant_id is only accepted from internal clients.",
        KnowledgeCodes.CollectionNotFound => "The collection does not exist or is not visible to this client.",
        KnowledgeCodes.NotFound => "Not found.",
        KnowledgeCodes.ContentNotServed => "The document's content is not served; follow its url.",
        KnowledgeCodes.ContentUnavailable => "The document's content cannot be served right now.",
        KnowledgeCodes.EmbeddingUnavailable => "Vector search is unavailable. Try again, or use mode keyword.",
        KnowledgeCodes.SearchUnavailable => "Search is busy. Try again.",
        DenyReasons.ConfigUnconfirmed => "The configuration could not be confirmed. Try again shortly.",
        DenyReasons.MembershipUnavailable => "Group membership could not be determined. Try again shortly.",
        _ => "The request was refused.",
    };
}
