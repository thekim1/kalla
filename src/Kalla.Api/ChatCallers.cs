using Microsoft.Extensions.Options;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;

namespace Kalla.Api;

/// <summary>
/// Who calls a chat route (identity design §5, §6): the key as for the knowledge routes (without their client-kind gate),
/// then for an <c>open-webui</c> client the Open WebUI user from <c>X-OpenWebUI-User-Jwt</c> (§5.2). The JWT is checked only
/// for the deployment's own Open WebUI client (<see cref="ClientEntry.IsManagedByDeployment"/>); for any other, and when the
/// deployment has none (#95), no Open WebUI user is trusted. Without a
/// JWT, <c>GET /v1/models</c> answers the client-level list (§5.4); a chat needs the user. Refusals are 401 and counted in
/// the 5001 buckets.
/// </summary>
public sealed class ChatCallers(KnowledgeCallers keys, IOptions<OpenWebUiOptions> options, KnowledgeSecurity security, TimeProvider time)
{
    public async Task<RagPrincipal> AuthenticateAsync(HttpRequest request, bool userRequired, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var who = await keys.AuthenticateAsync(request, ct, knowledgeRoute: false);
        if (who.Client.Kind != ClientKinds.OpenWebUi)
        {
            return who;
        }

        var hasJwt = request.Headers.ContainsKey(OpenWebUiUserJwt.Header);
        if (!hasJwt && !userRequired && !request.Headers.Keys.Any(h => h.StartsWith("X-OpenWebUI-User-", StringComparison.OrdinalIgnoreCase)))
        {
            return who; // the client-level model list (§5.4)
        }

        var configured = who.Client.IsManagedByDeployment;
        var (user, refusal) = configured ? await OpenWebUiUserJwt.ValidateAsync(request, options.Value, time.GetUtcNow()) : (null, hasJwt ? "user_jwt_invalid" : "user_jwt_missing");
        if (user is null)
        {
            security.AuthFailed(request.HttpContext, refusal!, "owui-jwt", who.Key?.Id, null, who.Client.Id);
            throw new KnowledgeFaultException(401, refusal!, refusal switch
            {
                "user_jwt_missing" => "The Open WebUI user is missing (X-OpenWebUI-User-Jwt).",
                "user_jwt_expired" => "The Open WebUI user token has expired.",
                "user_jwt_role" => "The Open WebUI user's role may not chat.",
                _ => "The Open WebUI user token is not valid.",
            });
        }

        var principal = who with { Kind = SubjectKind.OwuiUser, SubjectId = "owui:" + user.Subject, DisplayName = user.Name, Email = user.Email };
        request.HttpContext.Items[KnowledgeCallers.PrincipalItem] = principal;
        return principal;
    }
}
