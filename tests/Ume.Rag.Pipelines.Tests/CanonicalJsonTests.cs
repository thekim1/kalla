using System.Text;
using System.Text.Json;
using Ume.Rag.Pipelines.Json;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>RFC 8785 (engine design §3.3; to-verify #17 asks for it with the validator).</summary>
public sealed class CanonicalJsonTests
{
    private static string Canonical(string json) => Encoding.UTF8.GetString(CanonicalJson.Serialize(Repository.Json(json)));

    [Fact]
    public void Sorts_members_by_utf16_code_units_and_drops_whitespace()
    {
        // The sorting example of RFC 8785 §3.2.3, plus nesting.
        Canonical("""{ "\u20ac": 1, "\r": 2, "\ufb33": 3, "1": 4, "\ud83d\ude00": 5, "\u0080": 6, "\u00f6": 7, "n": { "z": 1, "a": [ 2 , 1 ] } }""")
            .ShouldBe("{\"\\r\":2,\"1\":4,\"n\":{\"a\":[2,1],\"z\":1},\"\u0080\":6,\"ö\":7,\"€\":1,\"😀\":5,\"\ufb33\":3}");
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("1.0", "1")]
    [InlineData("100", "100")]
    [InlineData("4.50", "4.5")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("1.5e-7", "1.5e-7")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("1e21", "1e+21")]
    [InlineData("123456789012e10", "1.23456789012e+21")]
    [InlineData("-1.25e30", "-1.25e+30")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("0.1", "0.1")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    public void Writes_numbers_as_ecmascript_does(string input, string expected) =>
        CanonicalJson.FormatNumber(double.Parse(input, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Fact]
    public void Escapes_only_what_rfc_8785_escapes()
    {
        Canonical("\"a\\\"b\\\\c\\n\\u0001\\u001f/é\\u007f\"").ShouldBe("\"a\\\"b\\\\c\\n\\u0001\\u001f/é\u007f\"");
    }

    [Fact]
    public void Equal_documents_written_differently_hash_alike()
    {
        CanonicalJson.Sha256(Repository.Json("""{"b":[1,2.0],"a":"x"}"""))
            .ShouldBe(CanonicalJson.Sha256(Repository.Json("""{ "a" : "x", "b" : [1e0, 2] }""")));
    }

    [Fact]
    public void Refuses_duplicate_names() =>
        Should.Throw<JsonException>(() => CanonicalJson.Serialize(Repository.Json("""{"a":1,"a":2}""")));
}
