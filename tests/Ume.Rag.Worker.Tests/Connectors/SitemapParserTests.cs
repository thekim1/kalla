using System.IO.Compression;
using System.Text;
using Ume.Rag.Worker.Connectors.Sitemaps;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>The sitemap parser (connectors design §5.1, §18): index, gzip, XXE refused, size limits, <c>lastmod</c>.</summary>
public sealed class SitemapParserTests
{
    [Fact]
    public async Task Reads_a_urlset_with_lastmod()
    {
        var document = await ParseAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc> https://www.umea.se/a </loc><lastmod>2026-01-15</lastmod><changefreq>daily</changefreq></url>
              <url><loc>https://www.umea.se/b</loc><lastmod>2026-01-15T10:30:00+01:00</lastmod></url>
              <url><loc>https://www.umea.se/c</loc><lastmod>not a date</lastmod></url>
              <url><lastmod>2026-01-15</lastmod></url>
            </urlset>
            """);

        document.IsIndex.ShouldBeFalse();
        document.Error.ShouldBeNull();
        document.Entries.ShouldBe([
            new SitemapEntry("https://www.umea.se/a", new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            new SitemapEntry("https://www.umea.se/b", new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.FromHours(1))),
            new SitemapEntry("https://www.umea.se/c", null),
        ]);
    }

    [Fact]
    public async Task Reads_a_sitemap_index()
    {
        var document = await ParseAsync("""
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.umea.se/sitemap1.xml.gz</loc><lastmod>2026-10</lastmod></sitemap>
            </sitemapindex>
            """);

        document.IsIndex.ShouldBeTrue();
        document.Entries.ShouldBe([new SitemapEntry("https://www.umea.se/sitemap1.xml.gz", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))]);
    }

    [Fact]
    public async Task Reads_gzip_by_its_magic_bytes()
    {
        var xml = "<urlset><url><loc>https://www.umea.se/z</loc></url></urlset>"u8.ToArray();
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(xml, TestContext.Current.CancellationToken);
        }

        compressed.Position = 0;
        var document = await SitemapParser.ParseAsync(compressed, TestContext.Current.CancellationToken);

        document.Entries.Single().Location.ShouldBe("https://www.umea.se/z");
    }

    [Theory]
    // XXE: an external entity reading a local file.
    [InlineData("""<?xml version="1.0"?><!DOCTYPE urlset [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><urlset><url><loc>&xxe;</loc></url></urlset>""")]
    // Billion laughs.
    [InlineData("""<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]><urlset><url><loc>&lol2;</loc></url></urlset>""")]
    // An external DTD.
    [InlineData("""<?xml version="1.0"?><!DOCTYPE urlset SYSTEM "http://169.254.169.254/x.dtd"><urlset/>""")]
    public async Task Refuses_any_dtd(string xml)
    {
        var document = await ParseAsync(xml);

        document.Error.ShouldBe(SitemapParser.RefusedDtd);
        document.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Keeps_entries_before_malformed_xml_and_says_why()
    {
        var document = await ParseAsync("<urlset><url><loc>https://www.umea.se/a</loc></url><url><loc>broken");

        document.Error.ShouldBe(SitemapParser.RefusedSyntax);
        document.Entries.Single().Location.ShouldBe("https://www.umea.se/a");
    }

    [Fact]
    public async Task Stops_at_50000_entries()
    {
        var xml = new StringBuilder("<urlset>");
        for (var i = 0; i <= SitemapParser.MaxEntries; i++)
        {
            xml.Append("<url><loc>https://www.umea.se/p").Append(i).Append("</loc></url>");
        }

        var document = await ParseAsync(xml.Append("</urlset>").ToString());

        document.Entries.Count.ShouldBe(SitemapParser.MaxEntries);
        document.Truncated.ShouldBeTrue();
        document.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Refuses_more_than_50_MB_uncompressed_even_when_the_gzip_is_small()
    {
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync("<urlset><url><loc>https://www.umea.se/a</loc></url>"u8.ToArray(), TestContext.Current.CancellationToken);
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (var i = 0; i < 51; i++)
            {
                await gzip.WriteAsync(spaces, TestContext.Current.CancellationToken);
            }

            await gzip.WriteAsync("</urlset>"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        compressed.Length.ShouldBeLessThan(1024 * 1024);
        compressed.Position = 0;
        var document = await SitemapParser.ParseAsync(compressed, TestContext.Current.CancellationToken);

        document.Error.ShouldBe(SitemapParser.RefusedSize);
    }

    [Theory]
    [InlineData("2026", 2026, 1, 1)]
    [InlineData("2026-03", 2026, 3, 1)]
    [InlineData("2026-03-04", 2026, 3, 4)]
    public void Reads_partial_w3c_dates_as_utc(string value, int year, int month, int day) =>
        SitemapParser.ParseLastModified(value).ShouldBe(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero));

    private static async Task<SitemapDocument> ParseAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await SitemapParser.ParseAsync(stream, TestContext.Current.CancellationToken);
    }
}
