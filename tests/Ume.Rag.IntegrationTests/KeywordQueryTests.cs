using Npgsql;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// <c>content.rag_or_query</c> and its aggregate over the built-in <c>tsquery_or</c> (data-model §3.3, knowledge design
/// §6.2; to-verify #142): an OR of the query's lexemes, NULL for stop words only, and safe for every lexeme the
/// <c>swedish</c> parser emits.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class KeywordQueryTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lexemes_are_combined_with_or_so_one_matching_word_is_enough()
    {
        await using var connection = await OpenAsync();
        var query = await QueryTextAsync(connection, "ansökan om bygglov för förskoleplats");

        query.ShouldNotBeNull();
        query.ShouldContain(" | ");
        query.ShouldNotContain(" & ");
        (await MatchesAsync(connection, "bygglov", "ansökan om bygglov för förskoleplats")).ShouldBeTrue();
        (await MatchesAsync(connection, "avfallstaxa", "ansökan om bygglov")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_query_of_stop_words_only_gives_null()
    {
        await using var connection = await OpenAsync();
        (await QueryTextAsync(connection, "och det är som")).ShouldBeNull();
        (await QueryTextAsync(connection, "")).ShouldBeNull();
    }

    [Theory]
    [InlineData("https://www.umea.se/bygga-bo-och-miljo/bygglov?lang=sv&x=1#ansokan")]
    [InlineData("www.umea.se/kommun-och-politik umea.se kontakt@umea.se 090-16 10 00")]
    [InlineData("/var/data/fil.pdf C:\\temp\\bilaga 1.docx a\\b\\c")]
    [InlineData("e-tjänst bygg-lov self-service o'brien 'citat' \"dubbel\" l'été")]
    [InlineData("(a | b) & !c <-> d:* 'x':A \\' \\\\ ::: & | ! ( )")]
    [InlineData("1.2.3 10:30 2026-10-30 50% $5 #tag @user ~tilde ^caret")]
    public async Task Every_lexeme_the_swedish_parser_emits_is_quoted_safely(string text)
    {
        await using var connection = await OpenAsync();
        (await MatchesAsync(connection, text, text)).ShouldBeTrue(text);
    }

    [Fact]
    public async Task Random_text_never_breaks_the_query()
    {
        // Fuzz (to-verify #142): random words of letters, digits and the punctuation tsquery syntax uses.
        const string alphabet = "abcdefghijklmnopqrstuvwxyzåäöéü0123456789-_.:/\\'\"&|!()<>*@#%?=+,;~^ ";
        var random = new Random(4711);
        await using var connection = await OpenAsync();
        for (var i = 0; i < 300; i++)
        {
            var text = new string([.. Enumerable.Range(0, random.Next(1, 80)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            await using var command = new NpgsqlCommand("""
                SELECT to_tsvector('swedish', $1) = ''::tsvector, to_tsvector('swedish', $1) @@ content.rag_or_query(to_tsvector('swedish', $1))
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = text });
            await using var reader = await command.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            var empty = reader.GetBoolean(0);
            if (!empty)
            {
                reader.GetBoolean(1).ShouldBeTrue(text);
            }
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(database.Connection(Api));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<string?> QueryTextAsync(NpgsqlConnection connection, string text)
    {
        await using var command = new NpgsqlCommand("SELECT content.rag_or_query(to_tsvector('swedish', $1))::text", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = text });
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private static async Task<bool> MatchesAsync(NpgsqlConnection connection, string document, string query)
    {
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(to_tsvector('swedish', $1) @@ content.rag_or_query(to_tsvector('swedish', $2)), false)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = document });
        command.Parameters.Add(new NpgsqlParameter { Value = query });
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }
}
