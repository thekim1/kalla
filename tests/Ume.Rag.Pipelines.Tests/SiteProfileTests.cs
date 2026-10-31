using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Pipelines.SiteProfiles;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>The site-profile schema (connectors design §5.6).</summary>
public sealed class SiteProfileTests
{
    private static readonly Ume.Rag.Pipelines.Json.SchemaValidator Schema = ContractSchemas.Validator(typeof(SiteProfile));

    [Fact]
    public void A_profile_needs_only_its_main_root_and_defaults_the_rest()
    {
        Schema.Validate(Repository.Json("""{"mainRoot":"main"}""")).ShouldBeEmpty();
        var profile = Ume.Rag.Pipelines.Json.ContractJson.Deserialize<SiteProfile>(Repository.Json("""{"mainRoot":"main"}"""));
        profile.ExcludedTypes.ShouldBe(["image/*", "video/*"]);
        profile.LastmodReliable.ShouldBeFalse();
        profile.IdentityRule.ShouldBeNull();
    }

    [Fact]
    public void The_hand_checked_umea_profile_is_valid_and_round_trips()
    {
        var json = File.ReadAllText(Path.Combine(Repository.Root, "contracts", "site-profiles", "v1", "www.umea.se.profile.json"));
        var element = Repository.Json(json);
        Schema.Validate(element).ShouldBeEmpty();

        var profile = Ume.Rag.Pipelines.Json.ContractJson.Deserialize<SiteProfile>(element);
        profile.Cms.ShouldBe("sitevision");
        profile.IdentityRule.ShouldNotBeNull().Verified.ShouldBeTrue();
        profile.LastmodReliable.ShouldBeFalse();
        Schema.Validate(Ume.Rag.Pipelines.Json.ContractJson.ToElement(profile)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"mainRoot":""}""")]
    [InlineData("""{"mainRoot":"main","datePattern":"(?<d>\\d{4}"}""")]
    [InlineData("""{"mainRoot":"main","identityRule":{"pattern":"^/x$"}}""")]
    [InlineData("""{"mainRoot":"main","excludedTypes":["images"]}""")]
    [InlineData("""{"mainRoot":"main","learnedFrom":"run-1"}""")]
    public void Invalid_profiles_fail_the_schema(string json) =>
        Schema.Validate(Repository.Json(json)).ShouldNotBeEmpty();
}
