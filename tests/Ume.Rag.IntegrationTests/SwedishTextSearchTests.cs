using Npgsql;
using Ume.Rag.Retrieval.Text;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// A compound part matches through Postgres' built-in <c>swedish</c> configuration once the text carries the parts from
/// <see cref="SwedishDecompounder"/> (data-model §3.1): the stored side (<c>SearchText</c>) and the query side.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class SwedishTextSearchTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static SwedishDecompounder Decompounder => SwedishDecompounder.Default;

    [Theory]
    [InlineData("Äldreomsorgen i Umeå har fått nya riktlinjer.", "omsorg")]
    [InlineData("Ansök om förskoleplats i e-tjänsten.", "förskola")]
    [InlineData("Avfallstaxan för 2027 är beslutad.", "taxor")]
    [InlineData("Så söker du parkeringstillstånd för rörelsehindrade.", "tillstånd")]
    public async Task A_part_of_a_stored_compound_matches(string text, string query)
    {
        (await MatchesAsync(text, query)).ShouldBeFalse("the built-in swedish configuration alone does not split compounds");
        (await MatchesAsync(Decompounder.WithParts(text), Decompounder.WithParts(query))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_compound_in_the_query_matches_its_parts_in_the_text()
    {
        const string text = "Så skickar du in en ansökan om bygglov.";
        (await MatchesAsync(Decompounder.WithParts(text), "bygglovsansökan")).ShouldBeFalse();
        (await MatchesAsync(Decompounder.WithParts(text), Decompounder.WithParts("bygglovsansökan"))).ShouldBeTrue();
    }

    private async Task<bool> MatchesAsync(string searchText, string query)
    {
        await using var connection = new NpgsqlConnection(database.Connection(Api));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(to_tsvector('swedish', $1) @@ content.rag_or_query(to_tsvector('swedish', $2)), false)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = searchText });
        command.Parameters.Add(new NpgsqlParameter { Value = query });
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }
}
