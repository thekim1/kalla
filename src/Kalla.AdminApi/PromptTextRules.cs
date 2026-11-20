using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kalla.Answering.Chat;
using Kalla.Domain.Admin;

namespace Kalla.AdminApi;

/// <summary>
/// V38 (admin design, chat design §7.3; Decisions "Chat prompt texts configurable"): <c>Settings.Chat.prompts</c> holds only the
/// known members (<see cref="PromptTexts.Members"/>, nested by their dots), each a string or null (absent or empty = the
/// default), within its length; no <c>&lt;sources</c>, <c>&lt;source</c>, <c>&lt;skill_</c> or <c>&lt;tool_result</c> tag
/// (any case, spaces allowed, as §7.2's neutralisation); no control characters but line breaks; <c>sourcesLabel</c> one line;
/// no placeholder but the member's own (<c>{assistant}</c> in <c>identity</c>, <c>{N}</c> in <c>followUps</c>).
/// </summary>
public static partial class PromptTextRules
{
    /// <summary>Throws <c>prompt_text_invalid</c> for the first member that breaks a rule.</summary>
    public static void Ensure(JsonNode? prompts)
    {
        if (Problem(prompts) is { } problem)
        {
            throw AdminFaults.Of(AdminErrorCodes.PromptTextInvalid,
                new Dictionary<string, string> { ["field"] = problem.Field, ["problem"] = problem.Reason });
        }
    }

    /// <summary>The first broken rule as (member, reason), or null when the texts pass.</summary>
    public static (string Field, string Reason)? Problem(JsonNode? prompts)
    {
        if (prompts is null)
        {
            return null;
        }

        if (prompts is not JsonObject json)
        {
            return ("prompts", "not an object");
        }

        var known = PromptTexts.Members.ToDictionary(m => m.Path, StringComparer.Ordinal);
        var groups = known.Keys.SelectMany(Prefixes).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, node) in Leaves(json, ""))
        {
            if (groups.Contains(path))
            {
                return (path, "not an object");
            }

            if (!known.TryGetValue(path, out var member))
            {
                return (path, "unknown member");
            }

            if (node is null)
            {
                continue;
            }

            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            {
                return (path, "not a text");
            }

            var text = v.GetValue<string>();
            if (text.Length > member.MaxLength)
            {
                return (path, $"longer than {member.MaxLength} characters");
            }

            if (ReservedTag().IsMatch(text))
            {
                return (path, "contains a reserved tag");
            }

            if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r')))
            {
                return (path, "contains a control character");
            }

            if (member.OneLine && text.IndexOfAny(['\n', '\r']) >= 0)
            {
                return (path, "must be one line");
            }

            if (Placeholder().Matches(text).Select(m => m.Value).FirstOrDefault(p => p != member.Placeholder) is { } unknown)
            {
                return (path, $"unknown placeholder {unknown}");
            }
        }

        return null;
    }

    /// <summary>Every member path under <paramref name="json"/>; an object at a known text's path is reported as a leaf.</summary>
    private static IEnumerable<(string Path, JsonNode? Node)> Leaves(JsonObject json, string prefix)
    {
        foreach (var (name, node) in json)
        {
            var path = prefix + name;
            if (node is JsonObject inner && PromptTexts.Members.Any(m => m.Path.StartsWith(path + ".", StringComparison.Ordinal)))
            {
                foreach (var leaf in Leaves(inner, path + "."))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return (path, node);
            }
        }
    }

    private static IEnumerable<string> Prefixes(string path)
    {
        for (var i = path.IndexOf('.', StringComparison.Ordinal); i > 0; i = path.IndexOf('.', i + 1))
        {
            yield return path[..i];
        }
    }

    [GeneratedRegex(@"<\s*/?\s*(?:sources?|skill_|tool_result)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedTag();

    [GeneratedRegex(@"\{[^{}\s]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
