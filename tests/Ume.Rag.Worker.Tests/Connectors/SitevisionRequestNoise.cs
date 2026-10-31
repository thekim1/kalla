using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// A second fetch of a Sitevision page, offline (connectors design §12.2, PR #104 deviation 4): Sitevision draws new
/// <c>data-cid</c> UUIDs and new <c>sv_…</c> element ids on every request, and the <c>aria-labelledby</c>,
/// <c>aria-describedby</c> and <c>for</c> values that reference an id change with it. Exactly those values are redrawn;
/// every other byte stays as it was.
/// </summary>
public static partial class SitevisionRequestNoise
{
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    /// <param name="Html">The page as a second request would serve it.</param>
    /// <param name="Cids">The <c>data-cid</c> UUIDs redrawn.</param>
    /// <param name="Ids">The distinct <c>sv_…</c> ids redrawn.</param>
    public sealed record Refetch(string Html, int Cids, int Ids);

    public static Refetch Redraw(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var cids = 0;
        var redrawn = CidUuid().Replace(html, _ =>
        {
            cids++;
            return $"data-cid=\"{Guid.NewGuid():D}\"";
        });

        // One new id per old one, so an id and its references stay paired as Sitevision serves them.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        redrawn = IdAttribute().Replace(redrawn, attribute => attribute.Groups["name"].Value + "=\"" + SitevisionId().Replace(attribute.Groups["value"].Value, id =>
        {
            if (!ids.TryGetValue(id.Value, out var fresh))
            {
                fresh = "sv_" + RandomNumberGenerator.GetString(IdAlphabet, id.Value.Length - 3);
                ids.Add(id.Value, fresh);
            }

            return fresh;
        }) + "\"");
        return new Refetch(redrawn, cids, ids.Count);
    }

    [GeneratedRegex(@"data-cid=""[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}""")]
    private static partial Regex CidUuid();

    [GeneratedRegex(@"(?<=\s)(?<name>id|for|aria-labelledby|aria-describedby)=""(?<value>[^""]*)""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"(?<!\S)sv_[A-Za-z0-9_-]+")]
    private static partial Regex SitevisionId();
}
