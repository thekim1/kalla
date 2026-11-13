using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// To-verify #146 (admin design §5.8, §6.3): the generic forms read their fields from the request schemas of
/// <c>/openapi/v1.json</c> (<c>src/admin-ui/src/platform/schema.ts</c>). The .NET 10 generator turns the DataAnnotations the
/// API validates with into JSON Schema keywords (ranges, lengths, required members, enums), and a <c>jsonb</c> member is an
/// object that comes back from the database as it was sent.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class GenericFormSchemaTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The request bodies the generic forms open (the registry in <c>platform/resources.ts</c>).</summary>
    private static readonly (string Method, string Path)[] FormOperations =
    [
        ("post", "/api/clients"), ("put", "/api/clients/{id}"),
        ("post", "/api/keys"), ("put", "/api/keys/{id}"), ("post", "/api/keys/{id}/rotate"), ("post", "/api/keys/{id}/reveal"),
        ("put", "/api/settings/network"), ("put", "/api/settings/retention"), ("put", "/api/settings/deletion-guard"), ("put", "/api/settings/crawl"),
        ("put", "/api/settings/evaluation"), ("put", "/api/settings/retrieval"), ("put", "/api/settings/chat"), ("put", "/api/settings/pii"),
        ("put", "/api/settings/worker"),
        ("put", "/api/data-protection/{level}"),
        ("post", "/api/site-profiles"), ("put", "/api/site-profiles/{id}"), ("post", "/api/site-profiles/{id}/rollback"),
        ("put", "/api/embedding-profiles/{id}"), ("put", "/api/gateway/models/{name}"),
    ];

    private static async Task<JsonObject> OpenApiAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonObject>("/openapi/v1.json", Ct)).ShouldNotBeNull();

    /// <summary>The schema behind <paramref name="node"/>, following <c>$ref</c> as <c>schema.ts</c> does.</summary>
    private static JsonObject Resolve(JsonObject document, JsonNode? node)
    {
        var schema = node.ShouldBeOfType<JsonObject>();
        for (var depth = 0; schema["$ref"] is JsonValue reference && depth < 10; depth++)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]].ShouldBeOfType<JsonObject>();
        }

        return schema;
    }

    private static JsonObject Body(JsonObject document, string method, string path) =>
        Resolve(document, document["paths"]?[path]?[method]?["requestBody"]?["content"]?["application/json"]?["schema"]
            .ShouldNotBeNull($"{method} {path} has a JSON request body"));

    private static string[] Strings(JsonNode? node) => [.. node.ShouldBeOfType<JsonArray>().Select(n => n!.GetValue<string>())];

    /// <summary>A schema's <c>type</c>, one name or a list (<c>[null, string]</c>, <c>[integer, string]</c>).</summary>
    private static string[] Types(JsonNode? schema) => schema?["type"] is JsonValue one ? [one.GetValue<string>()] : Strings(schema?["type"]);

    [Fact]
    public async Task The_request_schemas_carry_ranges_lengths_required_members_and_enums()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var document = await OpenApiAsync(admin);

        foreach (var (method, path) in FormOperations)
        {
            var body = Body(document, method, path);
            Types(body).ShouldBe(["object"], $"{method} {path}");
            body["properties"].ShouldBeOfType<JsonObject>($"{method} {path} has fields").Count.ShouldBeGreaterThan(0);
        }

        // [Required], [Range], [MinLength]/[MaxLength] on DataProtectionRequest (V29's form, history mode `none` only).
        var policy = Body(document, "put", "/api/data-protection/{level}");
        Strings(policy["required"]).ShouldBe(["allowedHistoryModes", "defaultHistoryMode"], ignoreOrder: true);
        var properties = policy["properties"]!;
        foreach (var days in new[] { "retentionDefaultDays", "retentionMaxDays" })
        {
            (properties[days]!["minimum"]?.GetValue<int>(), properties[days]!["maximum"]?.GetValue<int>()).ShouldBe((1, 90), days);
            Types(properties[days]).ShouldContain("integer", days);
        }

        (properties["allowedHistoryModes"]!["minItems"]?.GetValue<int>(), properties["allowedHistoryModes"]!["maxItems"]?.GetValue<int>()).ShouldBe((1, 3));
        Types(properties["allowedHistoryModes"]!["items"]).ShouldBe(["string"]);
        (properties["defaultHistoryMode"]!["maxLength"]?.GetValue<int>()).ShouldBe(20);
        Types(properties["blobMasterKeyName"]).ShouldBe(["null", "string"], ignoreOrder: true); // nullable: an empty field sends null
        Types(properties["encryptionRequired"]).ShouldBe(["boolean"]);

        // A [Range] on a double.
        var ner = Body(document, "put", "/api/settings/pii")["properties"]!["nerMinScore"]!;
        (ner["minimum"]?.GetValue<double>(), ner["maximum"]?.GetValue<double>()).ShouldBe((0d, 1d));

        // An enum member: its names, as the API's JsonStringEnumConverter reads them, behind a $ref.
        var mode = Resolve(document, Body(document, "post", "/api/keys/{id}/rotate")["properties"]!["mode"]);
        Strings(mode["enum"]).ShouldBe(["RevokeImmediately", "Grace24Hours"]);
        Strings(Resolve(document, Body(document, "post", "/api/keys/{id}/reveal")["properties"]!["purpose"])["enum"]).ShouldBe(["Reveal", "Copy"]);

        // A client's coded strings carry their allowed values (admin design §5.9; ClientEndpoints.AddClientSchemas), on create and update.
        foreach (var (method, path) in new[] { ("post", "/api/clients"), ("put", "/api/clients/{id}") })
        {
            var client = Body(document, method, path)["properties"]!;
            Strings(Resolve(document, client["kind"])["enum"]).ShouldBe(ClientKinds.All, $"{method} {path}");
            Strings(Resolve(document, client["classificationCeiling"])["enum"]).ShouldBe(Classification.All, $"{method} {path}");
            Strings(Resolve(document, client["toolPolicy"])["enum"]).ShouldBe(["none", "read"], $"{method} {path}");
            Resolve(document, client["name"])["enum"].ShouldBeNull($"{method} {path}");
        }

        // The first key on create only (nullable: a oneOf of null and the request's $ref).
        var firstKeyNode = Body(document, "post", "/api/clients")["properties"]!["firstKey"].ShouldNotBeNull();
        var firstKey = Resolve(document, firstKeyNode["oneOf"] is JsonArray choices ? choices.Single(c => c?["$ref"] is not null) : firstKeyNode);
        firstKey["properties"]!["name"].ShouldNotBeNull();
        firstKey["properties"]!["teamId"].ShouldNotBeNull();
        Body(document, "put", "/api/clients/{id}")["properties"]!["firstKey"].ShouldBeNull();

        // A jsonb member is an object: schema.ts gives it the JSON text area.
        foreach (var (path, member) in new[] { ("/api/settings/crawl", "value"), ("/api/settings/pii", "flagRules") })
        {
            var body = Body(document, "put", path);
            Strings(body["required"]).ShouldContain(member, path);
            Types(Resolve(document, body["properties"]![member])).ShouldBe(["object"], $"{path} {member}");
        }
    }

    [Fact]
    public async Task A_jsonb_member_comes_back_as_it_was_saved()
    {
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var before = (await admin.GetFromJsonAsync<JsonObject>("/api/settings/crawl", Ct)).ShouldNotBeNull();
        var value = JsonNode.Parse("""
            {
              "politeness": { "minDelayMs": 1500, "maxConcurrentPerHost": 2, "jitter": 0.25, "respectRobots": true },
              "hosts": ["www.umea.se", "rådhus.umea.se"],
              "windows": [{ "days": ["mon", "tue"], "from": "22:00", "to": "05:30" }],
              "note": "Ändrad av \"plattform\" – å ä ö",
              "empty": {}, "none": null, "list": []
            }
            """)!.AsObject();

        try
        {
            using var saved = await admin.PutAsJsonAsync("/api/settings/crawl", new { value, version = before["version"]!.GetValue<string>() }, Ct);
            saved.IsSuccessStatusCode.ShouldBeTrue(await saved.Content.ReadAsStringAsync(Ct));

            var read = (await admin.GetFromJsonAsync<JsonObject>("/api/settings/crawl", Ct)).ShouldNotBeNull();
            JsonNode.DeepEquals(read["value"], value).ShouldBeTrue($"read back: {read["value"]!.ToJsonString()}");
        }
        finally
        {
            var current = (await admin.GetFromJsonAsync<JsonObject>("/api/settings/crawl", Ct)).ShouldNotBeNull();
            using var restored = await admin.PutAsJsonAsync("/api/settings/crawl",
                new { value = before["value"]!.DeepClone(), version = current["version"]!.GetValue<string>() }, Ct);
            restored.IsSuccessStatusCode.ShouldBeTrue(await restored.Content.ReadAsStringAsync(Ct));
        }
    }
}
