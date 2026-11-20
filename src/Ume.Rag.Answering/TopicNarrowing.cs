namespace Ume.Rag.Answering;

/// <summary>
/// A chat request's <c>ume.topics</c> applied to an assistant's links (Decisions "Topics per request"; chat design §3.8,
/// knowledge design §4.2). A request narrows and never widens: a link with topics keeps the ones also asked for and is left out
/// when none is; a link without topics is searched for the asked topics only.
/// </summary>
public static class TopicNarrowing
{
    /// <summary>
    /// The topics a link searches under <paramref name="asked"/>: the link's own when nothing is asked, the common ones (in the
    /// link's order) for a link with topics, the asked ones for a link without; null when the link is left out of the turn.
    /// </summary>
    public static IReadOnlyList<string>? Narrow(IReadOnlyList<string> linkTopics, IReadOnlyList<string>? asked)
    {
        ArgumentNullException.ThrowIfNull(linkTopics);
        if (asked is not { Count: > 0 })
        {
            return linkTopics;
        }

        if (linkTopics.Count == 0)
        {
            return asked;
        }

        var common = linkTopics.Where(asked.Contains).ToList();
        return common.Count > 0 ? common : null;
    }

    /// <summary>The asked topics that at least one of <paramref name="links"/> searches, in the request's order (§3.8, the answer's <c>ume.topics</c>).</summary>
    public static IReadOnlyList<string> Searched(IEnumerable<IReadOnlyList<string>> links, IReadOnlyList<string> asked)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(asked);
        var searched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (Narrow(link, asked) is { } topics)
            {
                searched.UnionWith(topics);
            }
        }

        return [.. asked.Where(searched.Contains)];
    }
}
