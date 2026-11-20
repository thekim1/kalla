using System.Net;
using System.Text.Json;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Api.Tests;

/// <summary>
/// The shared usage reading and estimate (chat design §8.3; gateway-changes §5.4; cost-attribution §5): tokens from the
/// answer's <c>usage</c> (cached from <c>prompt_tokens_details.cached_tokens</c>), the cost from <c>x-fors-cost-usd</c>, else
/// from the stream comment, which the reader never passes on, else none and an estimate from the mirror's prices, kept
/// apart from the gateway's cost.
/// </summary>
public sealed class GatewayUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The FakeGateway's chat prices: 0.15 in, 0.075 cached, 0.60 out per million tokens.</summary>
    private static readonly ModelPrices ChatPrices = new(0.15m, 0.075m, 0.60m);

    private static HttpResponseMessage Answer(string? cost)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (cost is not null)
        {
            response.Headers.TryAddWithoutValidation("x-fors-cost-usd", cost);
        }

        return response;
    }

    [Fact]
    public void A_json_answers_cost_is_the_header_and_its_tokens_the_usage()
    {
        using var body = JsonDocument.Parse("""
            {"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":80,"total_tokens":1280,"prompt_tokens_details":{"cached_tokens":1024}}}
            """);
        using var response = Answer("0.00012345");

        var call = GatewayUsageReader.Read(response, body.RootElement, "condense", "fors/chat-eu");

        call.ShouldBe(new GatewayCallUsage("condense", "fors/chat-eu", 1200, 1024, 80, 0.00012345m));
        var record = UsageRecord.From(call, ChatPrices);
        record.ShouldBe(new UsageRecord("condense", "fors/chat-eu", 1200, 1024, 80, 0.00012345m, 0m, Estimated: false));
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":12,"completion_tokens":3}}""", 12, 0, 3)]
    [InlineData("""{"usage":{"prompt_tokens":12,"prompt_tokens_details":{"cached_tokens":8}}}""", 12, 8, 0)]
    [InlineData("""{"usage":{"prompt_tokens":12,"prompt_tokens_details":null}}""", 12, 0, 0)]
    [InlineData("""{"usage":{"prompt_tokens":12,"total_tokens":12}}""", 12, 0, 0)]
    [InlineData("""{"usage":{"total_tokens":15}}""", 15, 0, 0)]
    [InlineData("""{"meta":{"billed_units":{"input_tokens":40}}}""", 40, 0, 0)]
    public void Tokens_are_read_from_the_usage(string json, int input, int cached, int output)
    {
        using var body = JsonDocument.Parse(json);
        GatewayUsageReader.Tokens(body.RootElement).ShouldBe(new GatewayTokens(input, cached, output));
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"x"}}],"usage":null}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""[]""")]
    public void A_body_without_usage_has_no_tokens(string json)
    {
        using var body = JsonDocument.Parse(json);
        GatewayUsageReader.Tokens(body.RootElement).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1e-5")]
    public void A_missing_or_unreadable_cost_header_is_no_cost(string? header)
    {
        using var response = Answer(header);
        GatewayUsageReader.CostHeader(response).ShouldBeNull();
    }

    [Fact]
    public void A_repeated_cost_header_is_no_cost()
    {
        using var response = Answer("0.00000100");
        response.Headers.TryAddWithoutValidation("x-fors-cost-usd", "0.00000200");
        GatewayUsageReader.CostHeader(response).ShouldBeNull();
    }

    [Fact]
    public async Task A_streams_cost_is_the_comment_and_the_comment_is_not_passed_on()
    {
        // As the gateway writes a stream asked with x-fors-stream-cost: 1 (GatewayRequestHandler.WriteCostComment): the
        // comment just before data: [DONE], six decimals.
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"content":"Hej"},"finish_reason":null}],"usage":null}

            data: {"choices":[],"usage":{"prompt_tokens":900,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":512}}}

            : fors-cost-usd=0.000123

            data: [DONE]

            """;
        var reader = new SseReader(new StringReader(stream));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            lines.Add(line);
        }

        reader.CostUsd.ShouldBe(0.000123m);
        lines.ShouldNotContain(l => l.Contains("fors-cost-usd", StringComparison.Ordinal));
        lines.Where(l => l.Length > 0).ShouldBe([
            """data: {"choices":[{"index":0,"delta":{"content":"Hej"},"finish_reason":null}],"usage":null}""",
            """data: {"choices":[],"usage":{"prompt_tokens":900,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":512}}}""",
            "data: [DONE]",
        ]);

        // The stream's usage is the last chunk's; with the comment's cost the call is not estimated.
        using var chunk = JsonDocument.Parse(lines[2]["data: ".Length..]);
        var call = new GatewayCallUsage("chat-answer", "fors/chat-eu", GatewayUsageReader.Tokens(chunk.RootElement)!.Value, reader.CostUsd);
        UsageRecord.From(call, ChatPrices).ShouldBe(new UsageRecord("chat-answer", "fors/chat-eu", 900, 512, 40, 0.000123m, 0m, Estimated: false));
    }

    [Fact]
    public async Task A_stream_without_the_comment_has_no_cost_and_other_comments_pass()
    {
        var reader = new SseReader(new StringReader(": keep-alive\n\ndata: [DONE]\n\n"));
        (await reader.ReadLineAsync(Ct)).ShouldBe(": keep-alive");
        (await reader.ReadLineAsync(Ct)).ShouldBe("");
        (await reader.ReadLineAsync(Ct)).ShouldBe("data: [DONE]");
        (await reader.ReadLineAsync(Ct)).ShouldBe("");
        (await reader.ReadLineAsync(Ct)).ShouldBeNull();
        reader.CostUsd.ShouldBeNull();
    }

    [Theory]
    [InlineData(": fors-cost-usd=0.000123", true, "0.000123")]
    [InlineData(": fors-cost-usd=0", true, "0")]
    [InlineData(": fors-cost-usd=x", true, null)]
    [InlineData(": keep-alive", false, null)]
    [InlineData("data: {\"x\":\": fors-cost-usd=1\"}", false, null)]
    public void Only_the_gateways_comment_is_the_cost_comment(string line, bool isComment, string? cost)
    {
        GatewayUsageReader.IsCostComment(line, out var usd).ShouldBe(isComment);
        usd.ShouldBe(cost is null ? null : decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Without_a_gateway_cost_the_call_is_estimated_from_the_prices_with_cached_input_at_its_own_price()
    {
        // (1,200 − 1,024) × 0.15 + 1,024 × 0.075 + 80 × 0.60 per million = 0.0000264 + 0.0000768 + 0.000048.
        var record = UsageRecord.From(new GatewayCallUsage("chat-answer", "fake-no-cost", 1200, 1024, 80, null), ChatPrices);

        record.ShouldBe(new UsageRecord("chat-answer", "fake-no-cost", 1200, 1024, 80, 0m, 0.0001512m, Estimated: true));
    }

    [Fact]
    public void Without_a_cached_price_cached_input_costs_the_input_price_and_without_prices_nothing()
    {
        new ModelPrices(0.15m, null, 0.60m).Estimate(1200, 1024, 80).ShouldBe(0.000228m);
        new ModelPrices(null, null, null).Estimate(1200, 1024, 80).ShouldBe(0m);
        UsageRecord.From(new GatewayCallUsage("rerank", "unpriced", 100, 0, 0, null), null).ShouldBe(new UsageRecord("rerank", "unpriced", 100, 0, 0, 0m, 0m, Estimated: true));
    }

    [Fact]
    public void Cached_input_counts_at_most_the_input_and_counts_never_go_below_zero()
    {
        UsageRecord.From(new GatewayCallUsage("condense", "m", 100, 150, -5, null), ChatPrices)
            .ShouldBe(new UsageRecord("condense", "m", 100, 100, 0, 0m, 0.0000075m, Estimated: true));
    }

    [Fact]
    public void The_estimate_uses_the_mirrored_prices_of_a_gateway_model()
    {
        var model = new GatewayModel { Name = "fors/chat-eu", InputPerMillionUsd = 2m, CachedInputPerMillionUsd = 0.5m, OutputPerMillionUsd = 8m };
        ModelPrices.Of(model).ShouldBe(new ModelPrices(2m, 0.5m, 8m));
        ModelPrices.Of(model).Estimate(1_000_000, 500_000, 100_000).ShouldBe(1m + 0.25m + 0.8m);
    }
}
