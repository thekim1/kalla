using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;

namespace Kalla.ContractTests;

/// <summary>
/// The worker contracts of P1a item 4 (hand-off J3): every file under <c>contracts/{sources,pipeline,jobs,site-profiles}/</c>
/// is the export of a record in <c>Kalla.Pipelines</c> (<see cref="ContractSchemas.All"/>). The records are the source,
/// because the worker binds them and the admin validates against the same schemas: a hand edit of a file, or a record
/// change without a new export, fails here. With <c>KALLA_UPDATE_CONTRACTS=1</c> the test writes the files instead of
/// comparing; commit the result.
/// </summary>
public sealed class WorkerContractTests
{
    private static readonly string[] ContractDirectories = ["sources", "pipeline", "jobs", "site-profiles"];

    // Relaxed escaping keeps <, &, ' and § readable; the files are never embedded in HTML.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEnumerable<TheoryDataRow<string>> Paths => ContractSchemas.All.Select(c => new TheoryDataRow<string>(c.Path));

    [Theory]
    [MemberData(nameof(Paths))]
    public void Committed_schema_matches_its_record(string path)
    {
        var contract = ContractSchemas.All.Single(c => c.Path == path);
        var file = Path.Combine(ContractFiles.RepoRoot, "contracts", path);
        var exported = contract.Export().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("KALLA_UPDATE_CONTRACTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, exported);
        }

        File.Exists(file).ShouldBeTrue($"{path} is missing. Run with KALLA_UPDATE_CONTRACTS=1 to export it.");
        File.ReadAllText(file).ReplaceLineEndings("\n").ShouldBe(exported, $"{path} differs from {contract.Type.Name}. Run with KALLA_UPDATE_CONTRACTS=1 to regenerate.");
    }

    [Fact]
    public void Every_committed_schema_has_a_record()
    {
        var known = ContractSchemas.All.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var committed = ContractDirectories
            .Select(d => Path.Combine(ContractFiles.RepoRoot, "contracts", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.schema.json", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(Path.Combine(ContractFiles.RepoRoot, "contracts"), f).Replace('\\', '/'));

        committed.Where(p => !known.Contains(p)).ShouldBeEmpty("Schema files without a record in ContractSchemas.All.");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Committed_schema_compiles_and_carries_its_id(string path)
    {
        var file = Path.Combine(ContractFiles.RepoRoot, "contracts", path);
        var schema = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        schema["$schema"]!.GetValue<string>().ShouldBe(ContractSchemaExporter.Dialect);
        schema["$id"]!.GetValue<string>().ShouldBe($"https://kalla.local/contracts/{path}");
        Should.NotThrow(() => SchemaValidator.Compile(schema));
    }

    /// <summary>The P0 <c>cancel-run</c> command as the admin enqueues it (<c>CommandJobs.CancelRunJob</c>) and as the worker answers.</summary>
    [Fact]
    public void P0_cancel_run_payload_and_result_match_the_contract()
    {
        var payload = Validator("jobs/v1/command.cancel-run.payload.schema.json");
        payload.Validate(ContractFiles.ParseElement("""{"kind":"cancel-run","runId":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a"}""")).ShouldBeEmpty();
        payload.Validate(ContractFiles.ParseElement("""{"kind":"cancel-run"}""")).ShouldNotBeEmpty();
        Validator("jobs/v1/command.cancel-run.result.schema.json")
            .Validate(ContractFiles.ParseElement("""{"cancelledJobs":3}""")).ShouldBeEmpty();
    }

    private static SchemaValidator Validator(string path) =>
        SchemaValidator.Compile(JsonNode.Parse(File.ReadAllText(Path.Combine(ContractFiles.RepoRoot, "contracts", path)))!);
}
