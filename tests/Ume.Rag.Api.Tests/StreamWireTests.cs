using System.Text;
using System.Text.Json;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The per-token stream paths of #333 (finding 4): <see cref="SseChunk"/> writes the same bytes the serializer wrote for
/// a content chunk, and <see cref="GatewayStreamLine"/> reads the gateway's lines as the <c>JsonNode</c> reader did.
/// </summary>
public sealed class StreamWireTests
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData("Bygglov")]
    [InlineData(" krävs för en altan över 1,8 m [1].")]
    [InlineData("Åsa säger \"hej\" och \\ <b>")]
    [InlineData("rad ett\nrad två\ttab \u0001")]
    [InlineData("emoji 👍 och 中文")]
    [InlineData("")]
    public void A_content_chunk_is_byte_for_byte_what_the_serializer_wrote(string content)
    {
        using var chunk = new SseChunk();
        foreach (var model in new[] { "bygglovsassistenten", null })
        {
            var expected = "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl-0192f0c8a1b27c3e9d4f5a6b7c8d9e0f", @object = "chat.completion.chunk", created = 1_762_000_000L, model,
                choices = new[] { new { index = 0, delta = new { content }, finish_reason = (string?)null } },
            }, Json) + "\n\n";

            Encoding.UTF8.GetString(chunk.Content("chatcmpl-0192f0c8a1b27c3e9d4f5a6b7c8d9e0f", 1_762_000_000L, model, content).Span).ShouldBe(expected);
        }
    }

    [Fact]
    public void Gateway_lines_give_delta_finish_and_usage_with_cached_input_in_order_and_stop_at_done()
    {
        var events = new List<GatewayStreamEvent>();

        GatewayStreamLine.Read("""data: {"choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""", events).ShouldBeTrue();
        events.ShouldBeEmpty(); // an empty content is no delta
        GatewayStreamLine.Read("""data: {"choices":[{"index":0,"delta":{"content":"Hej"},"finish_reason":null}]}""", events).ShouldBeTrue();
        GatewayStreamLine.Read("""data:{"choices":[{"index":0,"delta":{"content":" då"},"finish_reason":"stop"}]}""", events).ShouldBeTrue();
        GatewayStreamLine.Read("""data: {"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15,"prompt_tokens_details":{"cached_tokens":8}}}""", events).ShouldBeTrue();
        GatewayStreamLine.Read(": keep-alive comment", events).ShouldBeTrue();
        GatewayStreamLine.Read("", events).ShouldBeTrue();

        events.ShouldBe([
            new GatewayStreamEvent.Delta("Hej"), new GatewayStreamEvent.Delta(" då"), new GatewayStreamEvent.Finish("stop"),
            new GatewayStreamEvent.Usage(new GatewayTokens(12, 8, 3))]);
        GatewayStreamLine.Read("data: [DONE]", events).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""data: {"error":{"message":"upstream failed"}}""")]
    [InlineData("data: {not json")]
    public void An_error_event_or_a_broken_line_interrupts_the_stream(string line)
    {
        var refused = Should.Throw<ChatException>(() => GatewayStreamLine.Read(line, new List<GatewayStreamEvent>()));
        (refused.Status, refused.Code).ShouldBe((502, "stream_interrupted"));
    }

    [Fact]
    public void A_null_error_and_odd_shapes_carry_nothing()
    {
        var events = new List<GatewayStreamEvent>();
        GatewayStreamLine.Read("""data: {"error":null,"choices":[{"delta":{"content":"x"}}]}""", events).ShouldBeTrue();
        GatewayStreamLine.Read("""data: {"choices":[1]}""", events).ShouldBeTrue();
        GatewayStreamLine.Read("""data: [1, 2]""", events).ShouldBeTrue();
        GatewayStreamLine.Read("""data: {"choices":[{"delta":{"content":5}}],"usage":{"prompt_tokens":"x"}}""", events).ShouldBeTrue();
        events.ShouldBe([new GatewayStreamEvent.Delta("x"), new GatewayStreamEvent.Usage(default)]);
    }
}
