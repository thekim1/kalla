using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ume.Rag.Domain.Admin;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// #699: <see cref="AdminValidationFilter"/> checks only <see cref="AdminRequest"/> arguments and the <see cref="AdminRequest"/>
/// values nested in them. Every admin type with validation attributes derives from it, and the request bodies that once didn't
/// (key scopes, the assistant test panel and its history, test retrieval, collection links) are refused with 400 before the
/// endpoint runs. The ids are random: the filter answers before the endpoint looks anything up.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class AdminRequestValidationTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_admin_type_with_validation_attributes_is_an_AdminRequest()
    {
        var skipped = typeof(AdminRequest).Assembly.GetTypes()
            .Where(t => !typeof(AdminRequest).IsAssignableFrom(t))
            .Where(t => !t.Name.EndsWith("Options", StringComparison.Ordinal)) // configuration, checked at start by ValidateDataAnnotations
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.GetCustomAttributes<ValidationAttribute>(inherit: true).Any()))
            .Select(t => t.FullName)
            .ToList();

        skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_retrieval_refuses_a_topK_out_of_range()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var errors = await RefusedAsync(await admin.PostAsJsonAsync($"/api/collections/{Guid.NewGuid()}/test-retrieval",
            new { question = "Hej?", topK = 0 }, Ct));

        errors.ShouldContainKey("topK");
    }

    [Fact]
    public async Task The_assistant_test_panel_refuses_a_long_question_and_history()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var history = Enumerable.Range(0, 11).Select(i => new { role = "user", text = i == 0 ? new string('x', 4001) : "Hej" }).ToArray();

        var errors = await RefusedAsync(await admin.PostAsJsonAsync($"/api/assistants/{Guid.NewGuid()}/test",
            new { question = new string('x', 2001), history }, Ct));

        errors.ShouldContainKey("question");
        errors.ShouldContainKey("history");
        errors.ShouldContainKey("history[0].text");
    }

    [Fact]
    public async Task A_key_refuses_more_than_100_scoped_assistants()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var scopes = new { assistantIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), collectionIds = Array.Empty<Guid>() };

        var errors = await RefusedAsync(await admin.PutAsJsonAsync($"/api/keys/{Guid.NewGuid()}", new { name = "Prov", scopes }, Ct));

        errors.ShouldContainKey("scopes.assistantIds");
    }

    [Fact]
    public async Task An_assistant_refuses_a_collection_link_with_more_than_100_topics()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var link = new { collectionId = Guid.NewGuid(), tags = Enumerable.Range(0, 101).Select(i => "t" + i).ToArray() };

        var errors = await RefusedAsync(await admin.PutAsJsonAsync($"/api/assistants/{Guid.NewGuid()}/draft",
            new { collectionLinks = new object?[] { null, link } }, Ct));

        errors.ShouldContainKey("collectionLinks[1].tags");
    }

    private static async Task<Dictionary<string, string[]>> RefusedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
        }
    }
}
