using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ume.Rag.Pipelines.Json;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>The properties to-verify #17 asks of the JSON Schema 2020-12 validator, as this repository uses it.</summary>
public sealed class SchemaValidatorTests
{
    [Fact]
    public void If_then_rules_hold()
    {
        var schema = SchemaValidator.Compile(Repository.Json("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "if":{"properties":{"ocr":{"const":"off"}},"required":["ocr"]},"then":{"required":["tables"]},"else":{"not":{"required":["tables"]}}}
            """));
        schema.IsValid(Repository.Json("""{"ocr":"off","tables":"fast"}""")).ShouldBeTrue();
        schema.IsValid(Repository.Json("""{"ocr":"off"}""")).ShouldBeFalse();
        schema.IsValid(Repository.Json("""{"ocr":"auto","tables":"fast"}""")).ShouldBeFalse();
    }

    [Fact]
    public void Annotations_of_our_own_are_ignored()
    {
        var schema = SchemaValidator.Compile(Repository.Json("""{"type":"object","properties":{"urls":{"type":"array","x-effect":"scope","x-labels":{"sv":"Adresser"}}}}"""));
        schema.IsValid(Repository.Json("""{"urls":[]}""")).ShouldBeTrue();
        schema.IsValid(Repository.Json("""{"urls":"x"}""")).ShouldBeFalse();
    }

    [Fact]
    public void Formats_are_asserted()
    {
        var schema = SchemaValidator.Compile(Repository.Json("""{"properties":{"id":{"type":"string","format":"uuid"},"re":{"type":"string","format":"regex"}}}"""));
        schema.IsValid(Repository.Json("""{"id":"0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a","re":"^a+$"}""")).ShouldBeTrue();
        schema.IsValid(Repository.Json("""{"id":"not-a-uuid"}""")).ShouldBeFalse();
        schema.IsValid(Repository.Json("""{"re":"(unclosed"}""")).ShouldBeFalse();
    }

    [Fact]
    public void Errors_point_at_the_instance_location()
    {
        var error = SchemaValidator.Compile(Repository.Json("""{"properties":{"a":{"type":"array","items":{"type":"integer","maximum":3}}}}"""))
            .Validate(Repository.Json("""{"a":[1,5]}""")).ShouldHaveSingleItem();
        error.InstanceLocation.ShouldBe("/a/1");
        error.Keyword.ShouldBe("maximum");
    }

    [Fact]
    public async Task A_remote_ref_is_never_fetched()
    {
        // A listener that records any connection: a validator that resolved the $ref over HTTP would connect to it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

        var outcome = Record.Exception(() => SchemaValidator
            .Compile(Repository.Json($$"""{"$ref":"http://127.0.0.1:{{port}}/schema.json"}"""))
            .Validate(Repository.Json("{}")));

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        accept.IsCompleted.ShouldBeFalse("The validator connected to resolve a remote $ref.");
        // Unresolvable means not valid: either compiling or validating refuses it.
        if (outcome is null)
        {
            SchemaValidator.Compile(Repository.Json($$"""{"$ref":"http://127.0.0.1:{{port}}/schema.json"}""")).IsValid(Repository.Json("{}")).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_catastrophic_pattern_times_out_as_invalid()
    {
        var schema = SchemaValidator.Compile(Repository.Json("""{"type":"string","pattern":"^(a+)+$"}"""));
        var watch = Stopwatch.StartNew();
        var errors = schema.Validate(Repository.Json($"\"{new string('a', 40)}!\""));
        watch.Stop();

        errors.ShouldNotBeEmpty();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
