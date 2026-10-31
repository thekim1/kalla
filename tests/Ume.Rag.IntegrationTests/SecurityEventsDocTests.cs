using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// <c>docs/security-events.md</c> lists every security event the code emits, with its id, name and level, so the SIEM
/// team's reference cannot fall behind the code (identity design §10).
/// </summary>
public sealed partial class SecurityEventsDocTests
{
    private static readonly string Doc = File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("docs", "security-events.md")));

    /// <summary>Every <c>[LoggerMessage]</c> of <see cref="SecurityEvents"/>.</summary>
    private static IEnumerable<LoggerMessageAttribute> Emitted() =>
        typeof(SecurityEvents).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<LoggerMessageAttribute>()).OfType<LoggerMessageAttribute>().OrderBy(a => a.EventId);

    public static TheoryData<int, string, string> Events()
    {
        var data = new TheoryData<int, string, string>();
        foreach (var attribute in Emitted())
        {
            data.Add(attribute.EventId, attribute.EventName!, attribute.Level.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Every_event_is_in_the_table_with_its_name_and_level(int id, string name, string level)
    {
        var row = Row().Matches(Doc).FirstOrDefault(m => m.Groups["id"].Value == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        row.ShouldNotBeNull($"{id} {name} is missing from docs/security-events.md");
        row.Groups["name"].Value.ShouldBe(name);
        row.Groups["level"].Value.ShouldBe(level);
        row.Groups["status"].Value.Trim()[..2].ShouldBeOneOf(["P0", "P1"], $"{id} is defined in code, so its row says P0 or P1");
    }

    [Fact]
    public void Every_event_uses_the_security_category_and_a_rag_name()
    {
        foreach (var attribute in Emitted())
        {
            attribute.EventName.ShouldStartWith("rag.");
        }

        SecurityEvents.Category.ShouldBe("Ume.Rag.Security");
        Doc.ShouldContain("**`Ume.Rag.Security`**");
    }

    // | 6001 | `rag.admin.change` | Information | … | P0 |
    [GeneratedRegex(@"^\| (?<id>\d{4}) \| `(?<name>[a-z._]+)` \| (?<level>\w+) \|.*\| (?<status>[^|]+) \|$", RegexOptions.Multiline)]
    private static partial Regex Row();
}
