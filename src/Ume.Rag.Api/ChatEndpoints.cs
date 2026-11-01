using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.Api;

/// <summary>
/// The chat role's routes (chat design §3; P1a item 15, part a): <c>GET /v1/models</c> and <c>POST /v1/chat/completions</c>
/// (non-streaming in this part). Errors in the OpenAI shape (§3.6), also for authentication. Open WebUI clients present
/// the user's JWT (identity §5, <see cref="ChatCallers"/>).
/// </summary>
public static class ChatEndpoints
{
    /// <summary>The chat body limit (§3.2: Open WebUI resends the whole chat); every other rag-api route keeps 16 KB.</summary>
    public const long BodyLimit = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var chat = app.MapGroup("/v1").WithTags("Chat");

        chat.MapGet("/models", async (HttpContext http, ChatCallers callers, IPolicySnapshotSource snapshots, IAccessPolicy policy, CancellationToken ct) =>
            await OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: false, ct);
                var snapshot = snapshots.Current;
                var candidates = snapshot.Assistants.Values.Where(a => a.State == "active" && a.Published is not null).ToList();
                var allowed = policy.Filter(who, candidates.Select(a => ResourceRef.Assistant(a.Id)), RagAction.ListModels, snapshot, snapshots.IsConfirmed)
                    .Select(r => r.Id).ToHashSet();
                return Results.Json(new
                {
                    @object = "list",
                    data = candidates.Where(a => allowed.Contains(a.Id)).OrderBy(a => a.Slug, StringComparer.Ordinal).Select(a =>
                    {
                        var description = Description(a);
                        return new
                        {
                            id = a.Slug, @object = "model", created = (a.Published!.PublishedAt ?? DateTimeOffset.UnixEpoch).ToUnixTimeSeconds(), owned_by = "umea-kommun",
                            name = a.Name, description, info = new { meta = new { description } },
                        };
                    }),
                });
            }))
            .WithName("ListModels").WithSummary("The assistants this caller may use, as OpenAI models");

        chat.MapPost("/chat/completions", async (HttpContext http, ChatCallers callers, ChatService service, ChatRateLimiter limiter, CancellationToken ct) =>
            await OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                JsonElement body;
                try
                {
                    body = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)).RootElement;
                }
                catch (JsonException)
                {
                    throw new ChatException(400, "invalid_request", "The body is not valid JSON.");
                }

                var parsed = ChatRequestParser.Parse(body);
                limiter.Check(who); // §4.1 step 4: per user and minute, before any model call
                if (!parsed.Stream)
                {
                    return Json(await service.CompleteAsync(who, parsed, ct));
                }

                var plan = await service.PlanAsync(who, parsed, ct);
                if (plan.Fixed is not null)
                {
                    return new ChatSse(plan, null, service, who.Client.Kind == "open-webui", parsed.IncludeUsage);
                }

                // §8.2: status and residency are checked here, before our first byte; a refusal is still a JSON error.
                var response = await service.Gateway.OpenStreamAsync(plan.Assistant.Published!.GatewayModel, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens,
                    plan.Settings.Temperature, plan.MessageId, ct);
                return new ChatSse(plan, response, service, who.Client.Kind == "open-webui", parsed.IncludeUsage);
            }))
            .WithMetadata(new ChatBodyLimit())
            .WithName("ChatCompletions").WithSummary("Ask an assistant (OpenAI chat completions, JSON or stream)");

        return app;
    }

    private static IResult Json(ChatCompletion answer) => Results.Json(new
    {
        id = "chatcmpl-" + answer.MessageId.ToString("N"),
        @object = "chat.completion",
        created = answer.Created.ToUnixTimeSeconds(),
        model = answer.Model,
        choices = new[]
        {
            new
            {
                index = 0, finish_reason = answer.FinishReason,
                message = new
                {
                    role = "assistant", content = answer.Content,
                    annotations = answer.Annotations.Select(a => new
                    {
                        type = "url_citation", url_citation = new { url = a.Url, title = a.Title, start_index = a.StartIndex, end_index = a.EndIndex },
                    }),
                },
            },
        },
        usage = new { prompt_tokens = answer.PromptTokens, completion_tokens = answer.CompletionTokens, total_tokens = answer.PromptTokens + answer.CompletionTokens },
    });

    /// <summary>The description Open WebUI shows (§3.1): the assistant's description and its contact.</summary>
    private static string? Description(AssistantEntry assistant)
    {
        var contact = string.Join(", ", new[] { assistant.ContactName, assistant.ContactEmail }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var text = string.Join(" ", new[] { assistant.Description, contact.Length > 0 ? "Kontakt: " + contact : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return text.Length > 0 ? text : null;
    }

    /// <summary>Runs the handler and writes refusals as OpenAI errors (§3.6, identity §4.4).</summary>
    private static async Task<IResult> OpenAiAsync(HttpContext http, Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (KnowledgeFaultException fault)
        {
            if (fault.Status == 401)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
            }

            return Error(http, fault.Status, fault.Code, fault.Message, null, fault.Status == 429 ? 60 : null);
        }
        catch (ChatException refusal)
        {
            return Error(http, refusal.Status, refusal.Code, refusal.Message, refusal.Param, refusal.RetryAfterSeconds);
        }
    }

    public static IResult Error(HttpContext http, int status, string code, string message, string? param, int? retryAfterSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (retryAfterSeconds is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var type = status switch
        {
            400 or 413 => "invalid_request_error",
            401 => "authentication_error",
            402 => "insufficient_quota",
            403 => "permission_error",
            404 => "not_found_error",
            429 => "rate_limit_error",
            _ => "api_error",
        };
        return Results.Json(new { error = new { message, type, code, param, request_id = http.TraceIdentifier } }, statusCode: status);
    }

    private sealed class ChatBodyLimit : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => BodyLimit;
    }
}
