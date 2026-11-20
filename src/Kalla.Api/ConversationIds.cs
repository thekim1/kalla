using System.Security.Cryptography;
using System.Text;
using Kalla.Answering.Chat;

namespace Kalla.Api;

/// <summary>
/// Issued conversation ids (chat design §11.1): <c>c1.{id:N}.{mac}</c>, <c>mac</c> = the first 16 bytes of
/// HMAC-SHA256(<c>Security:ConversationIdKeys:&lt;kid&gt;</c>, <c>"c1." + clientId + "." + assistantId + "." + subjectId + "." + id</c>),
/// base64url. Verified without SQL; the id embeds no data and cannot be replayed by another subject, client or assistant (an
/// assistant of another level would otherwise append to the conversation, #266). Rotation as the
/// download grants: the newest key (the greatest kid) signs, every key verifies.
/// </summary>
public sealed class ConversationIds
{
    public const string HeaderName = "x-rag-conversation-id";
    public const string Prefix = "c1.";

    private readonly byte[] _signing;
    private readonly IReadOnlyList<byte[]> _keys;

    public ConversationIds(IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || keys.Values.Any(k => k.Length < 32))
        {
            throw new InvalidOperationException("Security:ConversationIdKeys needs at least one key of at least 32 bytes.");
        }

        _signing = keys.OrderBy(k => k.Key, StringComparer.Ordinal).Last().Value;
        _keys = [.. keys.Values];
    }

    /// <summary>A new conversation id for (client, subject) and its header value.</summary>
    public (Guid Id, string Header) Issue(Guid clientId, Guid assistantId, string subjectId)
    {
        var id = Guid.CreateVersion7();
        return (id, HeaderFor(id, clientId, assistantId, subjectId));
    }

    /// <summary>The header value of an existing conversation for its (client, subject), signed with the newest key.</summary>
    public string HeaderFor(Guid id, Guid clientId, Guid assistantId, string subjectId) =>
        Prefix + id.ToString("N") + "." + Mac(_signing, clientId, assistantId, subjectId, id);

    /// <summary>The conversation id of a well-formed header, unverified (the endpoints read the row's assistant, then verify).</summary>
    public static Guid? Parse(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var parts = header.Split('.');
        return parts.Length == 3 && parts[0] + "." == Prefix && Guid.TryParseExact(parts[1], "N", out var id) ? id : null;
    }

    /// <summary>The id of a header issued to this (client, subject); otherwise 404 <c>conversation_not_found</c>.</summary>
    public Guid Verify(string header, Guid clientId, Guid assistantId, string subjectId)
    {
        ArgumentNullException.ThrowIfNull(header);
        var parts = header.Split('.');
        if (parts.Length == 3 && parts[0] + "." == Prefix && Guid.TryParseExact(parts[1], "N", out var id) &&
            _keys.Any(k => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(k, clientId, assistantId, subjectId, id)), Encoding.ASCII.GetBytes(parts[2]))))
        {
            return id;
        }

        throw new ChatException(404, "conversation_not_found", "The conversation does not exist.", HeaderName) { };
    }

    private static string Mac(byte[] key, Guid clientId, Guid assistantId, string subjectId, Guid id)
    {
        // Fixed-length ids around the subject keep the input unambiguous.
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Prefix + clientId.ToString("D") + "." + assistantId.ToString("D") + "." + subjectId + "." + id.ToString("D")));
        return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
