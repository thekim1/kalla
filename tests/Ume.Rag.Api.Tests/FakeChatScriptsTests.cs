extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using FakeChatScripts = fakegateway::Ume.Rag.FakeGateway.FakeChatScripts;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// FakeGateway's scripted JSON chat answers for the worker's evaluation steps (issue #129): keyed by <c>x-ume-step</c>,
/// from <c>FakeGateway:ChatScripts:&lt;step&gt;</c> (string or cycling array), built-in defaults otherwise, after the
/// usual access checks; any other call keeps the echo answer.
/// </summary>
public sealed class FakeChatScriptsTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, string Content)> AskAsync(WebApplicationFactory<fakegateway::Program> host, string? step, string user,
        string? key = FakeGatewayFactory.OnPremKey)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "ume/chat-onprem", messages = new[] { new { role = "user", content = user } } }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        request.Headers.TryAddWithoutValidation("x-ume-max-residency", "OnPrem");
        if (step is not null)
        {
            request.Headers.TryAddWithoutValidation(FakeChatScripts.StepHeader, step);
        }

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, body);
        }

        using var json = JsonDocument.Parse(body);
        return (response.StatusCode, json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!);
    }

    [Fact]
    public async Task Without_scripts_the_steps_get_valid_defaults_and_other_calls_the_echo()
    {
        const string Prompt = "Skriv en fråga. Dokument: Bygglov. Avsnitt: Altan. Text: Altaner högre än 1,2 meter kräver bygglov. Ansök i e-tjänsten.";
        var judge = await AskAsync(fake, FakeChatScripts.JudgeStep, "Fråga: Vad kostar bygglov?");
        judge.Content.ShouldBe(FakeChatScripts.DefaultJudge);

        var generated = await AskAsync(fake, FakeChatScripts.GenerateStep, Prompt);
        using var json = JsonDocument.Parse(generated.Content);
        var question = json.RootElement.GetProperty("fraga").GetString()!;
        question.ShouldBe("Vad gäller i praktiken för altaner högre meter?");
        question.Split(' ').Length.ShouldBeInRange(6, 25); // the worker's rule filter: 6-25 words, ends in ?, no deictic word
        question.ShouldNotContain("texten");
        json.RootElement.GetProperty("svar").GetString().ShouldBe("Altaner högre än 1,2 meter kräver bygglov.");
        json.RootElement.GetProperty("typ").GetString().ShouldBe("fakta");

        // condense-long (chat design §5.1): the message's first words on one line, a query the condense acceptance rule takes.
        var condensed = await AskAsync(fake, FakeChatScripts.CondenseLongStep,
            "Användare: Hej\n\nMeddelande: Ärende 114233: Procapita\nFel 0x80070005 vid inloggning, " + string.Join(' ', Enumerable.Repeat("logg", 2000)));
        condensed.Content.ShouldBe("Ärende 114233: Procapita Fel 0x80070005 vid inloggning, logg logg logg logg logg");
        Ume.Rag.Answering.Chat.Condenser.Accept(condensed.Content).ShouldBe(condensed.Content);
        FakeChatScripts.DefaultCondenseLong("Meddelande: " + new string('x', 3000)).Length.ShouldBe(FakeChatScripts.CondenseLongMaxChars);

        (await AskAsync(fake, null, "Hur söker jag bygglov?")).Content.ShouldStartWith("Testsvar på");
        (await AskAsync(fake, "eval-dedup", "Hur söker jag bygglov?")).Content.ShouldStartWith("Testsvar på"); // an embedding step: no chat script
    }

    [Fact]
    public async Task A_string_script_answers_every_call_and_an_array_cycles_per_step()
    {
        await using var scripted = fake.WithWebHostBuilder(web =>
        {
            web.UseSetting("FakeGateway:ChatScripts:eval-judge", """{"besvarbar": 2, "begriplig": 5, "specifik": 5}""");
            web.UseSetting("FakeGateway:ChatScripts:eval-generate:0", """{"fraga": "A?", "svar": "a", "typ": "fakta"}""");
            web.UseSetting("FakeGateway:ChatScripts:eval-generate:1", """{"fraga": "B?", "svar": "b", "typ": "fakta"}""");
        });

        for (var i = 0; i < 2; i++)
        {
            (await AskAsync(scripted, FakeChatScripts.JudgeStep, "x")).Content.ShouldBe("""{"besvarbar": 2, "begriplig": 5, "specifik": 5}""");
        }

        var answers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            answers.Add((await AskAsync(scripted, FakeChatScripts.GenerateStep, "x")).Content);
        }

        answers.Select(a => JsonDocument.Parse(a).RootElement.GetProperty("fraga").GetString()).ShouldBe(["A?", "B?", "A?"]);
    }

    [Fact]
    public async Task A_scripted_step_still_needs_a_valid_key()
    {
        (await AskAsync(fake, FakeChatScripts.JudgeStep, "x", key: null)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
