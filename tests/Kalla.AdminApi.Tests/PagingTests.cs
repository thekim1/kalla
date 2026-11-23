namespace Kalla.AdminApi.Tests;

/// <summary>
/// The admin API's paging kit (admin design §4.1): the page size, the keyset page from rows read with
/// <c>Take(take + 1)</c>, and the <c>ILIKE</c> pattern from user text.
/// </summary>
public sealed class PagingTests
{
    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(120, 120)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(int.MaxValue, 200)]
    public void Take_clamps_the_limit_and_defaults_to_50(int? limit, int expected) => Keyset.Take(limit).ShouldBe(expected);

    [Fact]
    public void Take_uses_the_endpoints_default_when_the_limit_is_missing()
    {
        Keyset.Take(null, 20).ShouldBe(20);
        Keyset.Take(7, 20).ShouldBe(7);
    }

    [Fact]
    public void From_with_one_row_more_than_the_page_continues_after_the_last_row_returned()
    {
        var page = KeysetPage.From([1L, 2L, 3L, 4L], 3, r => r);

        page.Items.ShouldBe([1L, 2L, 3L]);
        page.Next.ShouldBe(3L);
    }

    [Fact]
    public void From_with_exactly_a_page_of_rows_is_the_last_page()
    {
        var page = KeysetPage.From([1L, 2L, 3L], 3, r => r);

        page.Items.ShouldBe([1L, 2L, 3L]);
        page.Next.ShouldBeNull();
    }

    [Fact]
    public void From_with_no_rows_is_an_empty_last_page()
    {
        var page = KeysetPage.From(Array.Empty<Guid>(), 3, r => r);

        page.Items.ShouldBeEmpty();
        page.Next.ShouldBeNull();
    }

    [Fact]
    public void From_takes_the_key_from_the_row()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var page = KeysetPage.From([.. ids.Select(id => (Id: id, Name: id.ToString()))], 2, r => r.Id);

        page.Items.Select(r => r.Id).ShouldBe(ids[..2]);
        page.Next.ShouldBe(ids[1]);
    }

    [Theory]
    [InlineData("guide", "%guide%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("file_name", "%file\\_name%")]
    [InlineData("C:\\temp", "%C:\\\\temp%")]
    [InlineData("\\%_", "%\\\\\\%\\_%")]
    [InlineData("", "%%")]
    public void Contains_escapes_the_wildcards_and_the_escape_character(string text, string expected) => Like.Contains(text).ShouldBe(expected);
}
