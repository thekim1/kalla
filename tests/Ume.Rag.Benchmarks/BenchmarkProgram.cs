using BenchmarkDotNet.Running;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// <c>dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter *</c> runs every benchmark (see README.md and
/// BenchmarkDotNet's <c>--help</c>). The <c>Postgres*</c> classes need Docker for the pgvector container.
/// </summary>
public static class BenchmarkProgram
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
}
