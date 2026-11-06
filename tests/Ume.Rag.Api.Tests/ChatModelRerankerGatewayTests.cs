extern alias fakegateway;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure;
using Ume.Rag.Retrieval;
using FakeChatScripts = fakegateway::Ume.Rag.FakeGateway.FakeChatScripts;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// <see cref="ChatModelReranker"/> through the services' gateway client (<see cref="Infrastructure.Gateway.ResidencyCeilingHandler"/>:
/// the level's key, the ceiling, <c>x-ume-residency</c>) to the FakeGateway, which answers the rerank prompt deterministically
/// (B-12; <c>FakeChatScripts.DefaultRerank</c>); and the assistant setting that turns rerank on.
/// </summary>
public sealed class ChatModelRerankerGatewayTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RerankPassage[] Passages =
    [
        new(Guid.NewGuid(), "Matsedel för skolan i vecka 12."),
        new(Guid.NewGuid(), "Bygglov för altan: en altan högre än 1,2 meter kräver bygglov."),
        new(Guid.NewGuid(), "Altan och uteplats."),
    ];

    private static (ChatModelReranker, IHost) Build(WebApplicationFactory<fakegateway::Program> gateway)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Keys:Eu", FakeGatewayFactory.EuKey),
        ]);
        builder.AddGatewayClient(ChatModelReranker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
        var host = builder.Build();
        return (new ChatModelReranker(host.Services.GetRequiredService<IHttpClientFactory>()), host);
    }

    [Fact]
    public async Task The_fake_gateway_scores_the_prompt_deterministically_at_the_ceiling()
    {
        var (reranker, host) = Build(fake);
        using var _ = host;
        var request = new RerankRequest("bygglov altan", Passages, "ume/chat-eu", Residency.Eu, Guid.NewGuid());

        var first = await reranker.ScoreAsync(request, Ct);
        var second = await reranker.ScoreAsync(request, Ct);

        // Share of the question's words in each passage, on the 0–10 scale, returned as 0–1.
        first!.Scores.ShouldBe([0.0, 1.0, 0.5]);
        second!.Scores.ShouldBe(first.Scores);
        first.Usage!.Model.ShouldBe("ume/chat-eu");
        first.Usage.PromptTokens.ShouldBeGreaterThan(0);
        first.Usage.CompletionTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_model_without_a_target_within_the_ceiling_is_refused()
    {
        var (reranker, host) = Build(fake);
        using var _ = host;

        // ume/chat-advanced runs only at External: the gateway answers 403 max_residency_not_met at Eu.
        await Should.ThrowAsync<HttpRequestException>(() =>
            reranker.ScoreAsync(new RerankRequest("bygglov altan", Passages, "ume/chat-advanced", Residency.Eu, null), Ct));
    }

    [Fact]
    public async Task A_scripted_malformed_answer_is_refused()
    {
        await using var scripted = fake.WithWebHostBuilder(web => web.UseSetting($"{FakeChatScripts.SectionName}:{FakeChatScripts.RerankStep}", "Passage 2 is best."));
        var (reranker, host) = Build(scripted);
        using var _ = host;

        await Should.ThrowAsync<RerankOutputException>(() =>
            reranker.ScoreAsync(new RerankRequest("bygglov altan", Passages, "ume/chat-eu", Residency.Eu, null), Ct));
    }

    [Fact]
    public void Rerank_is_an_explicit_assistant_choice_and_off_by_default()
    {
        AssistantChatSettings.Parse("{}").Rerank.ShouldBeNull();
        AssistantChatSettings.Parse("""{"retrieval":{"topK":8}}""").Rerank.ShouldBeNull();
        AssistantChatSettings.Parse("""{"retrieval":{"rerank":true}}""").Rerank.ShouldBeNull();
        AssistantChatSettings.Parse("""{"retrieval":{"rerank":"off"}}""").Rerank.ShouldBeNull();
        AssistantChatSettings.Parse("""{"retrieval":{"rerank":"chat"}}""").Rerank.ShouldBe(Ume.Rag.Domain.Retrieval.RerankerKind.ChatModel);
        AssistantChatSettings.Parse("""{"retrieval":{"rerank":"gateway"}}""").Rerank.ShouldBe(Ume.Rag.Domain.Retrieval.RerankerKind.Gateway);
    }

    [Fact]
    public void The_default_rerank_answer_scores_every_passage_by_word_overlap()
    {
        var prompt = ChatModelReranker.Prompt("bygglov altan", Passages);
        FakeChatScripts.DefaultRerank(prompt).ShouldBe("""{"scores":[{"id":1,"score":0},{"id":2,"score":10},{"id":3,"score":5}]}""");
        ChatModelReranker.Parse(FakeChatScripts.DefaultRerank(prompt), Passages.Length).ShouldBe([0.0, 1.0, 0.5]);
    }
}
