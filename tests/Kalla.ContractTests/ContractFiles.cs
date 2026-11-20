using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kalla.ContractTests;

/// <summary>The contract files in the repository: schemas, fixtures, cases and goldens.</summary>
public static class ContractFiles
{
    public static readonly string RepoRoot = FindRepoRoot();

    public static string Schema(string name) => Path.Combine(RepoRoot, "contracts", "parser", "v1", $"{name}.schema.json");

    public static string Fixture(string name) => Path.Combine(RepoRoot, "tests", "fixtures", "parser", name);

    public static string Golden(string name) => Fixture(Path.Combine("golden", $"{name}.json"));

    /// <summary>The request id a case is sent with; scripts/update_goldens.py uses the same.</summary>
    public static string RequestId(string caseName) => $"case-{caseName}";

    /// <summary>tests/fixtures/parser/cases.json: <c>conversions</c> and <c>errors</c>, shared with update_goldens.py.</summary>
    public static IEnumerable<TheoryDataRow<string>> CaseNames(string group) =>
        Cases(group).Select(c => new TheoryDataRow<string>(c["name"]!.GetValue<string>()));

    public static JsonObject Case(string group, string name) =>
        Cases(group).Single(c => string.Equals(c["name"]!.GetValue<string>(), name, StringComparison.Ordinal));

    private static IEnumerable<JsonObject> Cases(string group) =>
        JsonNode.Parse(File.ReadAllText(Fixture("cases.json")))![group]!.AsArray().Select(c => c!.AsObject());

    public static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kalla.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Kalla.slnx not found above the test output directory.");
    }
}
