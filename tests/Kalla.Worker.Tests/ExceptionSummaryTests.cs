using Kalla.Worker.Jobs;

namespace Kalla.Worker.Tests;

public sealed class ExceptionSummaryTests
{
    private const string Secret = "personnummer 19121212-1212 in the page text";

    [Fact]
    public async Task An_async_throw_names_its_method_and_never_the_message()
    {
        var thrown = await Should.ThrowAsync<UnauthorizedAccessException>(() => WriteBlobAsync());

        ExceptionSummary.Types(thrown).ShouldBe("System.UnauthorizedAccessException");
        ExceptionSummary.Site(thrown).ShouldBe($"{typeof(ExceptionSummaryTests).FullName}.{nameof(WriteBlobAsync)}");
        (ExceptionSummary.Types(thrown) + ExceptionSummary.Site(thrown)).ShouldNotContain("19121212");
    }

    [Fact]
    public void A_wrapped_exception_lists_the_chain_and_the_innermost_site()
    {
        var wrapped = Should.Throw<InvalidOperationException>(() =>
        {
            try
            {
                Parse();
            }
            catch (FormatException inner)
            {
                throw new InvalidOperationException(Secret, inner);
            }
        });

        ExceptionSummary.Types(wrapped).ShouldBe("System.InvalidOperationException ← System.FormatException");
        ExceptionSummary.Site(wrapped).ShouldBe($"{typeof(ExceptionSummaryTests).FullName}.{nameof(Parse)}");
    }

    [Fact]
    public void An_exception_never_thrown_has_no_site()
    {
        ExceptionSummary.Site(new IOException(Secret)).ShouldBe("unknown");
        ExceptionSummary.Types(new IOException(Secret)).ShouldBe("System.IO.IOException");
    }

    private static async Task WriteBlobAsync()
    {
        await Task.Yield();
        throw new UnauthorizedAccessException(Secret);
    }

    private static void Parse() => throw new FormatException(Secret);
}
