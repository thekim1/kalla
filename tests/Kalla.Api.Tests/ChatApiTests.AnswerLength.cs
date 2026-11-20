extern alias fakegateway;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain.Access;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// B-31 (chat design §6.3, §8.1; Decisions "Answer length from the model"): the answer's <c>max_tokens</c> through the real
/// pipeline and the FakeGateway: left out when the model's limit is unknown, the model's limit less the prompt when known,
/// the assistant's override (lowered to the model's limit), and the client's own <c>max_tokens</c> only lowering it.
/// </summary>
public sealed partial class ChatApiTests
{
    private async Task<JsonElement> AnswerCallAsync(Func<PolicySnapshot, PolicySnapshot>? change = null, object? request = null)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = FollowUpHost(calls, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, change: change);
        using var client = Client(api, db.IntegrationKey);
        using var response = request is null
            ? await Ask(client, AltanQuestion)
            : await client.PostAsJsonAsync("/v1/chat/completions", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return calls.Single(c => c.Step == "chat-answer").Body;
    }

    private PolicySnapshot WithLimits(PolicySnapshot snapshot, int? window, int? output) => snapshot with
    {
        ModelLimits = new Dictionary<string, ModelLimitsEntry>(StringComparer.Ordinal)
        {
            [snapshot.Assistants[db.AssistantId].Published!.GatewayModel] = new(window, output),
        },
    };

    private PolicySnapshot WithSteps(PolicySnapshot snapshot, string querySteps) =>
        WithAssistant(snapshot, a => a with { Published = a.Published! with { QuerySteps = querySteps } });

    [Fact]
    public async Task Max_tokens_is_left_out_for_a_model_without_limits_and_follows_the_model_or_the_override_otherwise()
    {
        // Unknown limit: no max_tokens, so the gateway's or the provider's default applies (§6.3).
        (await AnswerCallAsync()).TryGetProperty("max_tokens", out _).ShouldBeFalse();

        // Known: min(O, C − P); with a large window that is O.
        (await AnswerCallAsync(s => WithLimits(s, 128_000, 16_384))).GetProperty("max_tokens").GetInt32().ShouldBe(16_384);

        // A window that leaves less than O after the prompt: C − P (> 0, below O).
        var tight = (await AnswerCallAsync(s => WithLimits(s, 3_000, 2_999))).GetProperty("max_tokens").GetInt32();
        tight.ShouldBeLessThan(2_999);
        tight.ShouldBeGreaterThan(0);

        // The override, as set; above a lowered model limit it is lowered to the limit, never a failed chat.
        (await AnswerCallAsync(s => WithSteps(s, """{"answer":{"maxOutputTokens":500}}"""))).GetProperty("max_tokens").GetInt32().ShouldBe(500);
        (await AnswerCallAsync(s => WithLimits(WithSteps(s, """{"answer":{"maxOutputTokens":500}}"""), null, 300)))
            .GetProperty("max_tokens").GetInt32().ShouldBe(300);

        // The client's max_tokens lowers the limit (§3.2) and is the limit when none is known.
        var asked = new { model = "bygg", messages = new[] { new { role = "user", content = AltanQuestion } }, max_tokens = 100 };
        (await AnswerCallAsync(s => WithLimits(s, 128_000, 16_384), asked)).GetProperty("max_tokens").GetInt32().ShouldBe(100);
        (await AnswerCallAsync(request: asked)).GetProperty("max_tokens").GetInt32().ShouldBe(100);
    }
}
