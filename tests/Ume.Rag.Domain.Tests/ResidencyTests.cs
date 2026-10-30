namespace Ume.Rag.Domain.Tests;

/// <summary>The ceiling values of gateway design §3.1 and §3.6, parsed the same way on both sides.</summary>
public class ResidencyTests
{
    [Fact]
    public void The_order_is_the_order_of_the_ceiling()
    {
        // Code compares residencies with < and <=; a new member must not silently change the meaning.
        ((int)Residency.OnPrem).ShouldBeLessThan((int)Residency.Eu);
        ((int)Residency.Eu).ShouldBeLessThan((int)Residency.External);
        Enum.GetValues<Residency>().Length.ShouldBe(3);
    }

    [Theory]
    [InlineData("OnPrem", Residency.OnPrem)]
    [InlineData("onprem", Residency.OnPrem)]
    [InlineData(" EU ", Residency.Eu)]
    [InlineData("Eu", Residency.Eu)]
    [InlineData("EXTERNAL", Residency.External)]
    public void The_three_names_parse_in_any_case(string value, Residency expected)
    {
        ResidencyCeiling.TryParse(value, out var residency).ShouldBeTrue();
        residency.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("EU-West")]
    [InlineData("")]
    [InlineData("OnPrem,Eu")]
    public void Anything_else_is_refused(string value) => ResidencyCeiling.TryParse(value, out _).ShouldBeFalse();

    [Theory]
    [InlineData(Classification.Public, Residency.External)]
    [InlineData(Classification.Internal, Residency.Eu)]
    [InlineData(Classification.Sensitive, Residency.OnPrem)]
    public void Each_level_runs_at_its_ceiling(string level, Residency ceiling) => ResidencyCeiling.ForLevel(level).ShouldBe(ceiling);

    [Fact]
    public void Header_values_round_trip()
    {
        foreach (var residency in Enum.GetValues<Residency>())
        {
            ResidencyCeiling.TryParse(ResidencyCeiling.ToHeader(residency), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(residency);
        }
    }
}
