using System.Globalization;
using System.Text;
using Kalla.Domain.Entities;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// #129 point 2 (processing design §13.1) through a running worker: a page with a table over
/// <see cref="ChunkOptions.LargeTableRows"/> rows and a list-heavy section syncs into live chunks whose
/// <c>ContentKind</c> is <c>table</c> for every row group (only the second and later marked <c>LargeTable</c>), <c>list</c>
/// for the list and <c>text</c> for prose; a plain page is all <c>text</c> without <c>LargeTable</c>. The chunk stage's output
/// goes through the output cache to the embed job, so the values survive its serialization.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class ChunkContentKindEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-content-kind-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Table_row_groups_lists_and_prose_get_their_content_kind_and_only_later_groups_of_a_large_table_are_marked()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(name == "taxa" ? Taxa() : EngineKit.Page($"bygglov {_marker}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/taxa"), site.Url("/p/plain")]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed), TimeSpan.FromMinutes(3))).Added.ShouldBe(2);

            // Per live chunk in order: its kind, "+" when LargeTable.
            var taxa = (await KindsAsync(seed, "/p/taxa")).Split(',');
            var tables = taxa.Where(k => k.StartsWith(ChunkContentKinds.Table, StringComparison.Ordinal)).ToList();
            tables.Count.ShouldBeGreaterThan(2, string.Join(',', taxa));
            tables[0].ShouldBe(ChunkContentKinds.Table, "the first row group of the large table stays eligible");
            tables.Skip(1).ShouldAllBe(k => k == ChunkContentKinds.Table + "+", "every later row group of a table over 2,000 rows");
            taxa.ShouldContain(ChunkContentKinds.List);
            taxa.ShouldContain(ChunkContentKinds.Text);
            taxa.Where(k => !k.StartsWith(ChunkContentKinds.Table, StringComparison.Ordinal)).ShouldAllBe(k => !k.EndsWith('+'));

            (await KindsAsync(seed, "/p/plain")).Split(',').ShouldAllBe(k => k == ChunkContentKinds.Text);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>Prose, a list-heavy section and a table of 2,001 data rows under a header row.</summary>
    private string Taxa()
    {
        var html = new StringBuilder($"<!doctype html><html lang=\"sv\"><head><title>Taxa {_marker}</title></head><body><main><h1>Taxa {_marker}</h1>");
        html.Append("<h2>Om taxan</h2><p>Umeå kommun tar ut avgifter enligt taxan som fullmäktige har beslutat. Avgifterna gäller från januari. ");
        html.Append("Den som har frågor om en faktura kan kontakta kundtjänst under kontorstid. Taxan räknas upp varje år.</p>");
        html.Append("<h2>Så ansöker du</h2><p>Gör så här.</p><ul>");
        for (var i = 1; i <= 6; i++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<li>Steg {i}: fyll i uppgifterna om fastigheten och skicka in blanketten till byggnadsnämnden.</li>");
        }

        html.Append("</ul><h2>Avgifter</h2><table><thead><tr><th>Tjänst</th><th>Belopp</th></tr></thead><tbody>");
        for (var r = 1; r <= ChunkOptions.LargeTableRows + 1; r++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>Tjänst {r}</td><td>{r * 10} kr</td></tr>");
        }

        return html.Append("</tbody></table></main></body></html>").ToString();
    }

    private Task<string> KindsAsync(SourceSeed seed, string path) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg(c."ContentKind" || CASE WHEN c."LargeTable" THEN '+' ELSE '' END, ',' ORDER BY c."Ordinal")
            FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
            WHERE d."SourceId" = $1 AND d."Url" LIKE '%' || $2 AND c."DeletedAt" IS NULL
            """, seed.SourceId, path);
}
