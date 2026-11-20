using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Answering.Chat;
using Kalla.Domain.Admin;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// V38 (admin design; chat design §7.3; Decisions "Chat prompt texts configurable"): <c>Settings.Chat.prompts</c> on
/// <c>PUT /api/settings/chat</c>, and the Settings panel's <c>GET /api/settings/chat/prompts</c>.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class PromptTextRulesTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Field, string Reason)? Problem(string json) => PromptTextRules.Problem(JsonNode.Parse(json));

    [Theory]
    [InlineData("""{"rules":{"off":"Läs <sources> noga."}}""", "rules.off", "reserved tag")]
    [InlineData("""{"rules":{"off":"Läs < / SOURCE> noga."}}""", "rules.off", "reserved tag")]
    [InlineData("""{"degraded":"<skill_content>"}""", "degraded", "reserved tag")]
    [InlineData("""{"conversational":"Se <tool_result>."}""", "conversational", "reserved tag")]
    [InlineData("""{"identity":"Du är {name}."}""", "identity", "unknown placeholder {name}")]
    [InlineData("""{"identity":"Ge {N} svar."}""", "identity", "unknown placeholder {N}")]
    [InlineData("""{"followUps":"Du är {assistant}."}""", "followUps", "unknown placeholder {assistant}")]
    [InlineData("""{"escalation":"Sammanfatta {N} ärenden."}""", "escalation", "unknown placeholder {N}")]
    [InlineData("""{"tone":"vänlig"}""", "tone", "unknown member")]
    [InlineData("""{"rules":{"maybe":"x"}}""", "rules.maybe", "unknown member")]
    [InlineData("""{"rules":"x"}""", "rules", "not an object")]
    [InlineData("""{"identity":42}""", "identity", "not a text")]
    [InlineData("""{"sourcesLabel":"Rad ett\nRad två"}""", "sourcesLabel", "must be one line")]
    [InlineData("""{"degraded":"Tab\there"}""", "degraded", "contains a control character")]
    public void V38_refuses_a_text_that_breaks_a_rule(string prompts, string field, string reason)
    {
        var problem = Problem(prompts).ShouldNotBeNull();
        problem.Field.ShouldBe(field);
        problem.Reason.ShouldContain(reason);
    }

    [Fact]
    public void V38_refuses_a_text_over_its_length_and_accepts_the_placeholders_where_they_belong()
    {
        Problem($$"""{"identity":"{{new string('x', 1001)}}"}""")!.Value.Field.ShouldBe("identity");
        Problem($$"""{"condense":"{{new string('x', 2001)}}"}""")!.Value.Field.ShouldBe("condense");
        Problem("{\"caveats\":{\"general\":{\"sv\":\"" + new string('x', 501) + "\"}}}")!.Value.Field.ShouldBe("caveats.general.sv");
        Problem("""{"identity":"Hej från {assistant}.","followUps":"Ge högst {N} frågor.\nKorta.","condense":"Rad ett\r\nRad två","rules":{"off":null},"degraded":""}""").ShouldBeNull();
        PromptTextRules.Problem(null).ShouldBeNull();
    }

    [Fact]
    public async Task The_chat_section_refuses_bad_texts_saves_good_ones_and_the_panel_shows_them_with_the_locked_lines()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = await (await admin.GetAsync("/api/settings/chat", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var original = JsonNode.Parse(before.GetProperty("value").GetRawText())!.AsObject();
        var version = before.GetProperty("version").GetString();
        JsonObject With(string prompts)
        {
            var value = original.DeepClone().AsObject();
            value["prompts"] = JsonNode.Parse(prompts);
            return value;
        }

        foreach (var (prompts, field) in new[]
                 {
                     ("""{"rules":{"off":"Följ <sources>."}}""", "rules.off"),
                     ("""{"identity":"Du är {namn}."}""", "identity"),
                     ("""{"signature":"Hälsningar"}""", "signature"),
                 })
        {
            using var refused = await admin.PutAsJsonAsync("/api/settings/chat", new { value = With(prompts), version }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, prompts);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.PromptTextInvalid.Code);
            problem.GetProperty("params").GetProperty("field").GetString().ShouldBe(field);
        }

        using (var saved = await admin.PutAsJsonAsync("/api/settings/chat",
                   new { value = With("""{"identity":"Hej från {assistant}.","followUps":"Ge {N} frågor."}"""), version }, Ct))
        {
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            version = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetString();
        }

        try
        {
            var sections = (await admin.GetFromJsonAsync<PromptTextSection[]>("/api/settings/chat/prompts", Ct))!;
            sections.Select(s => s.Id).ShouldBe(["frame", "general", "conversational", "sources", "condense", "condenseLong", "followUps", "escalation", "caveats"]);
            var frame = sections[0].Lines;
            frame[0].ShouldBe(new PromptTextLine("identity", "Du är {assistant}, en assistent för Umeå kommun.", "Hej från {assistant}.", 1000, "{assistant}", false));
            frame.Where(l => l.Member is null).Select(l => l.Text).ShouldBe(
                [PromptTexts.Locked.Citation, PromptTexts.Locked.NoInventedNumbers, PromptTexts.Locked.Injection, PromptTexts.Locked.Language]);
            var followUps = sections.Single(s => s.Id == "followUps").Lines;
            (followUps[0].Member, followUps[0].Value).ShouldBe(("followUps", "Ge {N} frågor."));
            followUps.Skip(1).ShouldAllBe(l => l.Member == null);
            // §9.9: the escalation summary's editable block (≤ 2,000), then its locked lines and the language line.
            var escalation = sections.Single(s => s.Id == "escalation").Lines;
            (escalation[0].Member, escalation[0].MaxLength).ShouldBe(("escalation", 2000));
            escalation.Skip(1).Select(l => l.Text).ShouldBe([.. PromptTexts.Locked.Escalation, PromptTexts.Locked.EscalationSwedish]);
            sections.SelectMany(s => s.Lines).Where(l => l.Member is not null).Select(l => l.Member).Distinct()
                .ShouldBe(PromptTexts.Members.Select(m => m.Path), ignoreOrder: true);
        }
        finally
        {
            using var restored = await admin.PutAsJsonAsync("/api/settings/chat", new { value = original, version }, Ct);
            restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
