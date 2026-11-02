using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering;
using Ume.Rag.Retrieval;

namespace Ume.Rag.Api.Tests;

/// <summary>#333 fix A: the api reads the chunk counts and stop-list statistics at start; readiness waits for that one attempt, at most a minute.</summary>
public sealed class RetrievalWarmupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readiness_waits_for_the_attempt_whether_it_reads_or_fails()
    {
        foreach (var fails in new[] { false, true })
        {
            var source = new HeldSource { Fails = fails };
            using var sizes = new ScopeSizeCache(source, new FakeTimeProvider());
            using var warmup = new RetrievalWarmup(sizes, NullLogger<RetrievalWarmup>.Instance, new FakeTimeProvider());
            await warmup.StartAsync(Ct);

            (await warmup.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);
            source.Release.SetResult();
            await WaitAsync(() => warmup.Done);
            (await warmup.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
            sizes.HasKeywordStatistics.ShouldBe(!fails);
            await warmup.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task A_read_that_never_ends_holds_readiness_for_at_most_a_minute()
    {
        var source = new HeldSource();
        var time = new FakeTimeProvider();
        using var sizes = new ScopeSizeCache(source, time);
        using var warmup = new RetrievalWarmup(sizes, NullLogger<RetrievalWarmup>.Instance, time);
        await warmup.StartAsync(Ct);

        (await warmup.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);
        time.Advance(RetrievalWarmup.MaxWait);
        (await warmup.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
        source.Release.SetResult();
        await warmup.StopAsync(Ct);
    }

    [Fact]
    public async Task Stop_and_dispose_are_safe_in_either_order()
    {
        // #338 CI: WebApplicationFactory stopped the host after its services were disposed (ObjectDisposedException).
        foreach (var disposeFirst in new[] { true, false })
        {
            var source = new HeldSource();
            using var sizes = new ScopeSizeCache(source, new FakeTimeProvider());
            var warmup = new RetrievalWarmup(sizes, NullLogger<RetrievalWarmup>.Instance, new FakeTimeProvider());
            await warmup.StartAsync(Ct);
            if (disposeFirst)
            {
                warmup.Dispose();
                await warmup.StopAsync(Ct);
            }
            else
            {
                await warmup.StopAsync(Ct);
                warmup.Dispose();
            }

            warmup.Dispose(); // twice is fine
            source.Release.SetResult();
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    /// <summary>One collection of 10,000 chunks; the lexeme read waits for <see cref="Release"/>, then answers or throws.</summary>
    private sealed class HeldSource : IScopeSizeSource
    {
        private static readonly Guid Profile = Guid.NewGuid();
        private static readonly Guid Collection = Guid.NewGuid();

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fails { get; init; }

        public Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CollectionChunkCount>>([new(Profile, Collection, 10_000)]);

        public async Task<IReadOnlyList<FrequentLexeme>> FrequentLexemesAsync(long minChunks, double minShare, CancellationToken cancellationToken)
        {
            await Release.Task;
            return Fails ? throw new TimeoutException("ts_stat") : [new FrequentLexeme(Profile, Collection, "umeå", 9_000)];
        }
    }
}
