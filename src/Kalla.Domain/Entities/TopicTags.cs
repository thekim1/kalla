using System.Text;
using System.Text.RegularExpressions;

namespace Kalla.Domain.Entities;

/// <summary>
/// Chunk tags (processing design §7): lower case, NFC, <c>[\p{L}\p{N}-]{1,50}</c>. An assistant's link topics (at most 20 per
/// link, V11) and a chat request's <c>ume.topics</c> (chat design §3.8) are in this format.
/// </summary>
public static partial class TopicTags
{
    public static bool IsValid(string tag) =>
        tag is not null && Pattern().IsMatch(tag) && tag.IsNormalized(NormalizationForm.FormC) && string.Equals(tag, tag.ToLowerInvariant(), StringComparison.Ordinal);

    [GeneratedRegex(@"^[\p{L}\p{N}-]{1,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
