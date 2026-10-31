using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// Range progress in the parser route (engine design §9.5, §8): with a used-up slice the route yields at a range boundary
/// after storing what it converted, a resumed parse asks only for the ranges it has no result for, and the joined document
/// equals the one parsed in one go. Failed ranges are not stored, so they are asked again.
/// </summary>
public sealed class ParseSlicingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ParseOptions OnePagePerRange = new() { RangePages = { [ParseStrategies.TextLayer] = 1 } };

    [Fact]
    public async Task A_used_up_slice_yields_after_each_range_and_the_resumed_parse_equals_the_unsliced_one()
    {
        var store = new MemoryRanges();
        var converter = new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To));
        var clock = new FakeTimeProvider();
        var slices = 0;
        ParsedDocument? sliced = null;
        while (sliced is null)
        {
            slices.ShouldBeLessThan(10);
            var progress = new ParseProgress(store, new ParseSliceClock(clock, TimeSpan.Zero));
            try
            {
                sliced = await Route(converter).ParseAsync(new byte[1], Pdf(4), new ParseParams(), "doc", Ct, progress);
            }
            catch (ParseYieldException)
            {
                slices++;
            }
        }

        slices.ShouldBe(3, "four ranges, one per slice");
        converter.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(["1-1", "2-2", "3-3", "4-4"]);
        var whole = await Route(new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To))).ParseAsync(new byte[1], Pdf(4), new ParseParams(), "doc", Ct);
        Json(sliced).ShouldBe(Json(whole));
    }

    [Fact]
    public async Task A_parse_that_died_after_range_k_resumes_at_range_k_plus_one()
    {
        var store = new MemoryRanges();
        var crash = new FakeConverter(o => o.Pages!.From == 3 ? throw new OperationCanceledException("killed") : FakeConverter.Pages(o.Pages.From, o.Pages.To));
        await Should.ThrowAsync<OperationCanceledException>(() => Route(crash).ParseAsync(new byte[1], Pdf(5), new ParseParams(), "doc", Ct, new ParseProgress(store)));

        var resumed = new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To));
        var parsed = await Route(resumed).ParseAsync(new byte[1], Pdf(5), new ParseParams(), "doc", Ct, new ParseProgress(store));

        resumed.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(["3-3", "4-4", "5-5"]);
        parsed.Pages.Select(p => p.Page).ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task A_failed_range_is_not_stored_and_is_asked_again_in_the_next_slice()
    {
        var store = new MemoryRanges();
        var converter = new FakeConverter(o => o.Pages!.From == 1 ? throw FakeConverter.Error("timeout") : FakeConverter.Pages(o.Pages.From, o.Pages.To));
        var clock = new FakeTimeProvider();
        await Should.ThrowAsync<ParseYieldException>(() =>
            Route(converter).ParseAsync(new byte[1], Pdf(11), new ParseParams(), "doc", Ct, new ParseProgress(store, new ParseSliceClock(clock, TimeSpan.Zero))));
        store.Keys.ShouldBe(["text-layer:2-2"], "the failed page is not stored; the yield came after the first stored range");

        var parsed = await Route(converter).ParseAsync(new byte[1], Pdf(11), new ParseParams(), "doc", Ct, new ParseProgress(store));
        parsed.MissingPages.ShouldBe([1]);
        converter.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(
            ["1-1", "2-2", "1-1", .. Enumerable.Range(3, 9).Select(p => $"{p}-{p}")]);
    }

    private static ParserRoute Route(FakeConverter converter) => new(converter, new ParsePlanner(), Options.Create(OnePagePerRange));

    private static DetectResult Pdf(int pages) =>
        new(ProcessingMediaTypes.Pdf, ParseRoute.Parser)
        {
            FileExtension = ".pdf",
            Inspect = new InspectFacts("pdf", pages, true, [.. Enumerable.Range(1, pages).Select(p => new InspectPageSignals(p) { TextChars = 1000, ImageCoverage = 0 })]),
        };

    private static string Json(ParsedDocument document) => JsonSerializer.Serialize(document, ProcessingJson.Options);

    /// <summary>Range results in memory, by strategy and pages.</summary>
    private sealed class MemoryRanges : IParseRangeStore
    {
        private readonly ConcurrentDictionary<string, ParserConvertResult> _results = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys => [.. _results.Keys.Order(StringComparer.Ordinal)];

        public Task<ParserConvertResult?> GetAsync(ParseRange range, CancellationToken cancellationToken) =>
            Task.FromResult(_results.GetValueOrDefault(Key(range)));

        public Task PutAsync(ParseRange range, ParserConvertResult result, CancellationToken cancellationToken)
        {
            _results.TryAdd(Key(range), result);
            return Task.CompletedTask;
        }

        private static string Key(ParseRange range) => $"{range.Strategy}:{range.From}-{range.To}";
    }
}
