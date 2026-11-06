using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Keys;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The chat rate limit per key (chat design §4.1 step 4, §18.9 "Rate limit per key"; Decisions "Chat rate limit per key"):
/// a key that is itself the subject gets its <c>Keys.RequestsPerMinute</c>, else the per-user value; Open WebUI users always
/// get the per-user value.
/// </summary>
public sealed partial class ChatApiTests
{
    private static readonly ClientEntry Integration = new(Guid.CreateVersion7(), "Integration", ClientKinds.Integration, Classification.Internal, false, true);
    private static readonly ClientEntry OpenWebUi = new(Guid.CreateVersion7(), "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, true, true);

    private static RagPrincipal KeySubject(ClientEntry client, int? requestsPerMinute)
    {
        var keyId = Guid.CreateVersion7();
        return new RagPrincipal(SubjectKind.Application, keyId.ToString("D"), null, null, client, CredentialKind.SecretKey,
            new KeyEntry(keyId, client.Id, KeyKinds.Secret, null, [], [], requestsPerMinute), new HashSet<string>(), MembershipFacts.None, 1);
    }

    private static ChatRateLimiter Limiter(FakeTimeProvider time, int perUser = 20) =>
        new(Options.Create(new ChatOptions { MessagesPerMinutePerUser = perUser }), time);

    /// <summary>Messages accepted before the first 429 (stops at <paramref name="max"/>).</summary>
    private static int Accepted(ChatRateLimiter limiter, RagPrincipal who, int? perUser, int max = 1000)
    {
        for (var sent = 0; sent < max; sent++)
        {
            try
            {
                limiter.Check(who, perUser);
            }
            catch (ChatException refused) when (refused.Code == "rate_limited")
            {
                return sent;
            }
        }

        return max;
    }

    [Fact]
    public void An_integration_key_with_its_own_limit_gets_that_many_messages_a_minute()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        var limiter = Limiter(time);
        var who = KeySubject(Integration, 60);

        for (var i = 0; i < 60; i++)
        {
            limiter.Check(who, 20); // Settings.Chat.messagesPerMinutePerUser 20 does not apply to a key subject with a value
        }

        time.Advance(TimeSpan.FromSeconds(15));
        var refused = Should.Throw<ChatException>(() => limiter.Check(who, 20));
        (refused.Status, refused.Code, refused.RetryAfterSeconds).ShouldBe((429, "rate_limited", 45));

        time.Advance(TimeSpan.FromSeconds(45)); // a new minute
        Accepted(limiter, who, 20).ShouldBe(60);
    }

    [Fact]
    public void A_key_without_a_value_gets_the_per_user_limit()
    {
        var time = new FakeTimeProvider();
        Accepted(Limiter(time), KeySubject(Integration, null), 5).ShouldBe(5); // Settings.Chat.messagesPerMinutePerUser
        Accepted(Limiter(time, perUser: 3), KeySubject(Integration, null), null).ShouldBe(3); // else Chat:MessagesPerMinutePerUser
    }

    [Fact]
    public void Open_webui_users_get_the_per_user_limit_each_whatever_their_connection_key_says()
    {
        var limiter = Limiter(new FakeTimeProvider());
        var connection = KeySubject(OpenWebUi, 5);
        var first = connection with { Kind = SubjectKind.OwuiUser, SubjectId = "owui:u-1" };
        var second = connection with { Kind = SubjectKind.OwuiUser, SubjectId = "owui:u-2" };

        Accepted(limiter, first, 8).ShouldBe(8);
        Accepted(limiter, second, 8).ShouldBe(8);
        ChatRateLimiter.LimitFor(first, 8).ShouldBe(8);
        ChatRateLimiter.LimitFor(connection, 8).ShouldBe(5);
    }

    [Fact]
    public async Task A_changed_key_limit_applies_after_the_key_cache_reset_and_a_rotated_key_keeps_it()
    {
        var hasher = new KeyHasher(System.Text.Encoding.UTF8.GetBytes(KnowledgeFixture.KeyPepper));
        var generated = hasher.Generate();
        var keyId = Guid.CreateVersion7();
        await ConfigWriteAsync((context, _) =>
        {
            context.Keys.Add(new ApiKey
            {
                Id = keyId, ClientId = db.IntegrationClient, Name = "Shared app key", Prefix = generated.Prefix, KeyHash = generated.Hash,
                CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test", RequestsPerMinute = 1,
            });
            return Task.CompletedTask;
        });

        await using var api = Host();
        using var client = Client(api, generated.PlainText);
        const string Question = "Hur långt är det till månen i kilometer?"; // the not-found answer: no model call
        (await StatusAsync(client, Question)).ShouldBe(HttpStatusCode.OK);
        using (var limited = await Ask(client, Question))
        {
            limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter.ShouldNotBeNull();
            (await limited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetProperty("code").GetString().ShouldBe("rate_limited");
        }

        // The admin raises the value to 4; the key change empties the key cache (identity §6.3 step 1). The window keeps its
        // count of 2 (the refused message counts), so two more are accepted this minute.
        await ConfigWriteAsync(async (context, ct) => (await context.Keys.SingleAsync(k => k.Id == keyId, ct)).RequestsPerMinute = 4);
        await api.Services.GetRequiredService<KeyStore>().ApplyAsync(new ConfigChange(0, 1, false, new HashSet<string> { "Keys" }, true), Ct);
        (await StatusAsync(client, Question)).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(client, Question)).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(client, Question)).ShouldBe(HttpStatusCode.TooManyRequests);

        // Rotation copies the value (KeyRotation.cs:44); the new key is its own subject, so it starts its own window.
        var rotated = hasher.Generate();
        await ConfigWriteAsync(async (context, ct) =>
            context.Keys.Add(KeyRotation.Rotate(await context.Keys.SingleAsync(k => k.Id == keyId, ct), rotated, KeyRotationMode.Grace24Hours, DateTimeOffset.UtcNow, "test")));
        using var replacement = Client(api, rotated.PlainText);
        for (var i = 0; i < 4; i++)
        {
            (await StatusAsync(replacement, Question)).ShouldBe(HttpStatusCode.OK);
        }

        (await StatusAsync(replacement, Question)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string question)
    {
        using var response = await Ask(client, question);
        return response.StatusCode;
    }

    private async Task ConfigWriteAsync(Func<RagDbContext, CancellationToken, Task> write)
    {
        await using var context = TestPostgres.Context(db.Connection(RagDatabase.Admin));
        await context.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            await write(context, ct);
            await context.SaveChangesAsync(ct);
            return 0;
        }, Ct);
    }
}
