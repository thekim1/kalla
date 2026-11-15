using BenchmarkDotNet.Running;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// <c>dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter *</c> runs every benchmark (see README.md and
/// BenchmarkDotNet's <c>--help</c>). The <c>Postgres*</c> classes need Docker for the pgvector container.
/// </summary>
public static class BenchmarkProgram
{
    public static async Task<int> Main(string[] args)
    {
        // "measure <name>": the Stopwatch measurements of Measurements/HotPathMeasurements.cs, not BenchmarkDotNet.
        if (args is ["measure", .. var rest])
        {
            return await Measurements.HotPathMeasurements.RunAsync(rest);
        }

        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
        return 0;
    }
}
