extern alias fakegateway;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;
using FakeUsage = fakegateway::Ume.Rag.FakeGateway.FakeUsage;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// B-42 (#580, cost-attribution §4.1): rag-admin's test panel runs every call of its turn as the assistant's gateway key, and
/// the retrieval test its query embedding as the knowledge base's; the FakeGateway acts as the key and records the answer
/// under it.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class ActingAsKeyAdminTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-acting-admin-000000000000000"; // rag-admin's credential key
    private static readonly Guid AssistantKey = new("019a0000-0000-7000-8000-00000000b0ab");
    private static readonly Guid CollectionKey = new("019a0000-0000-7000-8000-00000000b0ac");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConcurrentQueue<(string Path, string? OnBehalfOf)> _sent = new();

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        foreach (var (n, id) in new[] { (5, AssistantKey), (6, CollectionKey) })
        {
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:Id", id.ToString());
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:Name", $"Acting {n}");
            web.UseSetting($"FakeGateway:AssignedKeys:{n}:TeamId", "019a0000-0000-7000-8000-000000001002");
        }
    });

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway) => database.HeaderFactory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            foreach (var name in new[] { GatewayChatClient.HttpClientName, GatewayQueryEmbedder.HttpClientName, GatewayContextReader.HttpClientName })
            {
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Sent(gateway.Server.CreateHandler(), _sent));
            }
        });
    });

    /// <summary>Records each request's path and <c>x-ume-on-behalf-of</c>.</summary>
    private sealed class Sent(HttpMessageHandler inner, ConcurrentQueue<(string, string?)> sent) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues(GatewayHeaders.OnBehalfOf, out var v) ? string.Join(",", v) : null));
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_test_panel_runs_as_the_assistants_key_and_the_retrieval_test_as_the_knowledge_bases()
    {
        var code = "Q" + Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Acting " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        // B-44: chat's step 7 needs the assistant's key in the mirror, usable.
        await db.GatewayKeys.Where(k => k.Id == AssistantKey).ExecuteDeleteAsync(Ct);
        db.GatewayKeys.Add(new GatewayKey
        {
            Id = AssistantKey, Name = "Acting " + code, TeamId = Guid.CreateVersion7(), DepartmentId = department.Id, Status = "Active", OwnerActive = true,
            Usable = true, PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
        });
        var collection = new Collection
        {
            Slug = "acting-" + code.ToLowerInvariant(), Name = "Acting " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile, GatewayKeyId = CollectionKey, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new Assistant
        {
            Slug = "acting-" + code.ToLowerInvariant(), Name = "Acting " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            GatewayKeyId = AssistantKey, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = "Svara kort.", CollectionLinks = [CollectionLink.Whole(collection.Id)],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = "acting-" + code, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Sidor", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, assistant, pipeline, source);
        await db.SaveChangesAsync(Ct);
        db.Add(draft);
        await db.SaveChangesAsync(Ct);
        await db.Assistants.Where(a => a.Id == assistant.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, draft.Id), Ct);
        var word = "ak" + code.ToLowerInvariant() + "kompost";
        await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Kompostering",
            Url = $"https://www.umea.se/{code}/kompost",
        }, [new FixtureChunk($"Så komposterar du {word} hemma i Umeå.") { HeadingPath = "Avfall › Kompost" }], Ct);

        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        (await factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        factory.Services.GetRequiredService<GatewayFeatures>().ActAsKey.ShouldBeTrue();
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, code);

        // The test panel: every call of its turn as the assistant's key; the FakeGateway records the answer under it.
        _sent.Clear();
        using (var response = await editor.PostAsJsonAsync($"/api/assistants/{assistant.Id}/test", new { question = $"Hur gör jag med {word}?" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        var panel = _sent.Where(s => s.Path != "/v1/ume/context").ToList();
        panel.ShouldContain(s => s.Path == "/v1/chat/completions");
        panel.ShouldAllBe(s => s.OnBehalfOf == AssistantKey.ToString("D"));
        gateway.Services.GetRequiredService<FakeUsage>().Records.Where(r => r.Step == "chat-answer").ShouldAllBe(r => r.KeyId == AssistantKey);

        // The retrieval test: the query embedding as the knowledge base's key.
        _sent.Clear();
        using (var response = await editor.PostAsJsonAsync($"/api/collections/{collection.Id}/test-retrieval", new { question = $"Hur gör jag med {word}?" }, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        var retrieval = _sent.Where(s => s.Path == "/v1/embeddings").ToList();
        retrieval.ShouldNotBeEmpty();
        retrieval.ShouldAllBe(s => s.OnBehalfOf == CollectionKey.ToString("D"));
    }
}
