using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Pipelines.Definitions;

/// <summary>The P1 stage names (engine design §2.1, §6), in pipeline order.</summary>
public static class StageNames
{
    public const string Fetch = "fetch";
    public const string Scan = "scan";
    public const string Detect = "detect";
    public const string Parse = "parse";
    public const string Clean = "clean";
    public const string Chunk = "chunk";
    public const string PersonalData = "personal-data";
    public const string Embed = "embed";
    public const string Index = "index";

    /// <summary>The fixed linear order; a template lists a subset of these.</summary>
    public static IReadOnlyList<string> Order { get; } = [Fetch, Scan, Detect, Parse, Clean, Chunk, PersonalData, Embed, Index];

    /// <summary>Stages every template has (personal-data detection has no switch, engine design §3.2).</summary>
    public static IReadOnlyList<string> Required { get; } = [Fetch, Detect, Parse, Clean, Chunk, PersonalData, Embed, Index];
}

/// <summary>The routes inside <c>parse</c> (engine design §3.2 <c>appliesTo</c>, §7.2).</summary>
public static class ParseRoutes
{
    public const string Html = "html";
    public const string Parser = "parser";
    public const string Text = "text";
    public const string Csv = "csv";

    public static IReadOnlyList<string> All { get; } = [Html, Parser, Text, Csv];
}

/// <summary>Output cache classes (engine design §8).</summary>
public static class CacheClasses
{
    public const string Durable = "durable";
    public const string Intermediate = "intermediate";
    public const string None = "none";
}

/// <summary>
/// A stage's engine facts (engine design §6, §7.4, §8). <see cref="HostClasses"/> are the job classes whose jobs run
/// the stage: the fixed-class stages one, the inline stages each class whose segment can reach them (a web page runs
/// <c>parse</c> to <c>personal-data</c> inside its <c>fetch</c> job, a PDF inside its <c>parse</c> job). Publishing
/// needs a live instance per host class (§4 rule 4).
/// </summary>
public sealed record StageDescriptor(string Name, string CacheClass, IReadOnlyList<string> HostClasses);

public static class Stages
{
    public static IReadOnlyList<StageDescriptor> All { get; } =
    [
        new(StageNames.Fetch, CacheClasses.None, [JobClasses.Fetch]),
        new(StageNames.Scan, CacheClasses.None, [JobClasses.Fetch]),
        new(StageNames.Detect, CacheClasses.Intermediate, [JobClasses.Fetch]),
        new(StageNames.Parse, CacheClasses.Durable, [JobClasses.Fetch, JobClasses.Parse]),
        new(StageNames.Clean, CacheClasses.Intermediate, [JobClasses.Fetch, JobClasses.Parse]),
        new(StageNames.Chunk, CacheClasses.Intermediate, [JobClasses.Fetch, JobClasses.Parse]),
        new(StageNames.PersonalData, CacheClasses.Intermediate, [JobClasses.Fetch, JobClasses.Parse]),
        new(StageNames.Embed, CacheClasses.None, [JobClasses.Embed]),
        new(StageNames.Index, CacheClasses.None, [JobClasses.Embed]),
    ];

    public static StageDescriptor? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}
