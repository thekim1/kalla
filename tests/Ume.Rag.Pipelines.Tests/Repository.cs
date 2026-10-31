using System.Text.Json;
using Ume.Rag.Pipelines.Definitions;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>Files of the repository the tests read: the P1 templates the migrator seeds.</summary>
public static class Repository
{
    public static readonly string Root = FindRoot();

    public static string TemplatesDirectory => Path.Combine(Root, "src", "Ume.Rag.MigrationService", "Templates");

    public static IReadOnlyList<string> TemplateFiles => [.. Directory.EnumerateFiles(TemplatesDirectory, "*.json").Order(StringComparer.Ordinal)];

    public static TemplateCatalog Templates { get; } = TemplateCatalog.Load(TemplateFiles.Select(File.ReadAllText));

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UmeRag.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("UmeRag.slnx not found above the test output directory.");
    }
}
