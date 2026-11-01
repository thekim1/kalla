using System.Security.Cryptography;
using System.Text;
using Ume.Rag.Answering.Chat;

namespace Ume.Rag.Api;

/// <summary>
/// Issued conversation ids (chat design §11.1): <c>c1.{id:N}.{mac}</c>, <c>mac</c> = the first 16 bytes of
/// HMAC-SHA256(<c>Security:ConversationIdKeys:&lt;kid&gt;</c>, <c>"c1." + clientId + "." + subjectId + "." + id</c>), base64url.
/// Verified without SQL; the id embeds no data and cannot be replayed by another subject or client. Rotation as the
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
    public (Guid Id, string Header) Issue(Guid clientId, string subjectId)
    {
        var id = Guid.CreateVersion7();
        return (id, Prefix + id.ToString("N") + "." + Mac(_signing, clientId, subjectId, id));
    }

    /// <summary>The id of a header issued to this (client, subject); otherwise 404 <c>conversation_not_found</c>.</summary>
    public Guid Verify(string header, Guid clientId, string subjectId)
    {
        ArgumentNullException.ThrowIfNull(header);
        var parts = header.Split('.');
        if (parts.Length == 3 && parts[0] + "." == Prefix && Guid.TryParseExact(parts[1], "N", out var id) &&
            _keys.Any(k => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(k, clientId, subjectId, id)), Encoding.ASCII.GetBytes(parts[2]))))
        {
            return id;
        }

        throw new ChatException(404, "conversation_not_found", "The conversation does not exist.", HeaderName) { };
    }

    private static string Mac(byte[] key, Guid clientId, string subjectId, Guid id)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Prefix + clientId.ToString("D") + "." + subjectId + "." + id.ToString("D")));
        return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
