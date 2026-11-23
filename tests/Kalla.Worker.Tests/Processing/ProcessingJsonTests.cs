using System.Text.Json;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Detect;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// The output cache's JSON (engine design §8): a stage output is written without its null members, so it must read back
/// with a null where a nullable constructor parameter was left out. A Word document has no page count and no page numbers;
/// its detect output, written by the fetch job and read by the parse job, failed with "required property missing" and the
/// parse job went dead.
/// </summary>
public sealed class ProcessingJsonTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProcessingJson.Options), ProcessingJson.Options)!;

    [Fact]
    public void A_word_documents_detect_output_reads_back_without_a_page_count()
    {
        var detect = new DetectResult("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ParseRoute.Parser)
        {
            Inspect = new InspectFacts("docx", null, false, []) { Encrypted = "none" },
        };

        JsonSerializer.Serialize(detect, ProcessingJson.Options).ShouldNotContain("pageCount"); // nulls are not written

        var read = RoundTrip(detect);
        read.Inspect.ShouldNotBeNull();
        (read.Inspect.Format, read.Inspect.PageCount, read.Inspect.Rangeable, read.Inspect.Encrypted).ShouldBe(("docx", (int?)null, false, "none"));
        read.Route.ShouldBe(ParseRoute.Parser);
    }

    [Fact]
    public void Other_outputs_with_null_constructor_members_read_back_too()
    {
        var section = RoundTrip(new Section(0, 1, ["Rutin"], null, [], null, null));
        (section.Heading, section.PageStart, section.PageEnd).ShouldBe(((string?)null, (int?)null, (int?)null));
        RoundTrip(new DocumentMetadata([], "rutin", null)).SourceModifiedAt.ShouldBeNull();
    }

    [Fact]
    public void Reading_does_not_change_what_is_written_or_its_hash()
    {
        var facts = new InspectFacts("pdf", 5, true, []);
        JsonSerializer.Serialize(facts, ProcessingJson.Options).ShouldBe("""{"format":"pdf","pageCount":5,"rangeable":true,"pages":[]}""");
        ProcessingJson.OutputHash(RoundTrip(facts)).ShouldBe(ProcessingJson.OutputHash(facts));
    }
}
