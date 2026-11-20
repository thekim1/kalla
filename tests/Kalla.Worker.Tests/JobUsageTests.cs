using Microsoft.Extensions.Time.Testing;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using Kalla.Worker.Jobs;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests;

/// <summary>
/// The estimate of a call the gateway gave no cost for (engine design §10.6): the mirror's prices, read once for a while, and a
/// charged call recorded whatever becomes of the job.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class JobUsageTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_charged_call_is_recorded_with_its_estimate_from_a_price_read_the_job_cannot_cancel()
    {
        var prices = new RecordingPrices(new ModelPrices(1m, null, 2m));
        var sink = TestUsage.Sink(prices);

        // The gateway has charged the call, so a cancelled job must not lose it: the price read takes no cancellation.
        await sink.RecordAsync(new GatewayCallUsage("chat", "priced", 1_000_000, 0, 1_000_000, CostUsd: null));

        prices.Tokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse();
        sink.Usage.HasPending.ShouldBeTrue();
        sink.Usage.PendingCostUsd.ShouldBe(3m);
    }

    [Fact]
    public async Task The_mirror_prices_are_read_once_for_a_minute_and_a_model_it_does_not_list_has_none()
    {
        var model = $"price-test-{Guid.NewGuid():N}";
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "InputPerMillionUsd",
                                                "CachedInputPerMillionUsd", "OutputPerMillionUsd")
            VALUES ($1, 'model', ARRAY['OnPrem'], 'manual', false, 'manual', 0.15, NULL, 0.60)
            """, model);
        try
        {
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var prices = new PostgresGatewayModelPrices(database.Database(RagDatabase.Worker), clock);

            (await prices.GetAsync(model, Ct)).ShouldBe(new ModelPrices(0.15m, null, 0.60m));
            (await prices.GetAsync("not-mirrored", Ct)).ShouldBeNull();

            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."GatewayModels" SET "InputPerMillionUsd" = 0.30 WHERE "Name" = $1""", model);
            clock.Advance(PostgresGatewayModelPrices.CacheFor - TimeSpan.FromSeconds(1));
            (await prices.GetAsync(model, Ct)).ShouldBe(new ModelPrices(0.15m, null, 0.60m), "within a minute of the read");

            clock.Advance(TimeSpan.FromSeconds(1));
            (await prices.GetAsync(model, Ct)).ShouldBe(new ModelPrices(0.30m, null, 0.60m), "read again after a minute");
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."GatewayModels" WHERE "Name" = $1""", model);
        }
    }

    /// <summary>Fixed prices that keep the token of each read.</summary>
    private sealed class RecordingPrices(ModelPrices prices) : IGatewayModelPrices
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task<ModelPrices?> GetAsync(string model, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult<ModelPrices?>(prices);
        }
    }
}
