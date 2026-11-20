using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Admin;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Role tests generated from <see cref="PermissionMatrix"/> (admin design §3.4, §11.2): every <c>/api</c> endpoint × every
/// role answers 403 exactly when the matrix denies one of its policies; a user without a rag-* role gets 403 everywhere and
/// an anonymous caller 401.
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class PermissionMatrixTests(AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Declared: authorization metadata entries, the group's Read plus the endpoint's own.
    private sealed record ApiEndpoint(string Method, string Route, string[] Policies, int Declared)
    {
        public override string ToString() => $"{Method} {Route} [{string.Join(", ", Policies)}]";
    }

    private static List<ApiEndpoint> ApiEndpoints(IServiceProvider services) =>
    [
        .. services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(method => new ApiEndpoint(
                method, e.RoutePattern.RawText!,
                [.. e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().Distinct()],
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count))),
    ];

    [Fact]
    public async Task Every_api_endpoint_declares_its_own_policy_from_the_matrix()
    {
        var factory = host.Factory;
        var endpoints = ApiEndpoints(factory.Services);
        endpoints.ShouldNotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            endpoint.Policies.ShouldNotBeEmpty(endpoint.ToString());
            endpoint.Policies.ShouldBeSubsetOf(AdminPolicies.All, endpoint.ToString());
            // An endpoint added without its own policy would carry the group's alone.
            endpoint.Declared.ShouldBeGreaterThanOrEqualTo(2, endpoint.ToString());
        }
    }

    [Fact]
    public async Task Every_endpoint_and_role_answers_as_the_matrix_says()
    {
        var factory = host.Factory;
        var endpoints = ApiEndpoints(factory.Services);
        var mismatches = new List<string>();
        foreach (var role in RagRoles.All.Append(""))
        {
            using var user = await AdminDatabaseFixture.UserAsync(factory, role, "1000");
            foreach (var endpoint in endpoints)
            {
                var allowed = role.Length > 0 && endpoint.Policies.All(p => PermissionMatrix.Allows([role], p));
                using var response = await SendAsync(user, endpoint);
                var refused = response.StatusCode == HttpStatusCode.Forbidden;
                if (refused == allowed || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    mismatches.Add($"{(role.Length == 0 ? "(no role)" : role)} {endpoint}: {(int)response.StatusCode}");
                }
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401_on_every_endpoint()
    {
        var factory = host.Factory;
        using var anonymous = factory.CreateHttpsClient();
        foreach (var endpoint in ApiEndpoints(factory.Services))
        {
            using var response = await SendAsync(anonymous, endpoint);
            // Unsafe methods without the CSRF header stop at the antiforgery check (400) before authorization.
            response.StatusCode.ShouldBeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest], endpoint.ToString());
        }
    }

    [Fact]
    public void Admin_never_implies_review_or_feedback_comments()
    {
        PermissionMatrix.RolesFor(AdminPolicies.Review).ShouldBe([RagRoles.Reviewer]);
        PermissionMatrix.RolesFor(AdminPolicies.FeedbackComments).ShouldBe([RagRoles.DepartmentAdmin, RagRoles.Reviewer], ignoreOrder: true);
        PermissionMatrix.PoliciesByRole[RagRoles.PlatformAdmin].ShouldNotContain(AdminPolicies.Review);
        PermissionMatrix.PoliciesByRole[RagRoles.PlatformAdmin].ShouldNotContain(AdminPolicies.FeedbackComments);
    }

    [Fact]
    public void Every_role_reads_and_every_policy_has_a_role()
    {
        PermissionMatrix.PoliciesByRole.Keys.ShouldBe(RagRoles.All, ignoreOrder: true);
        PermissionMatrix.RolesFor(AdminPolicies.Read).ShouldBe(RagRoles.All, ignoreOrder: true);
        foreach (var policy in AdminPolicies.All)
        {
            PermissionMatrix.RolesFor(policy).ShouldNotBeEmpty(policy);
        }

        PermissionMatrix.Allows(["not-a-rag-role"], AdminPolicies.Read).ShouldBeFalse();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, ApiEndpoint endpoint)
    {
        // Route values are fresh ids (404 when allowed); bodies are empty objects (400 when allowed).
        var path = System.Text.RegularExpressions.Regex.Replace(endpoint.Route, @"\{[^}]+\}", Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), path);
        if (endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request, Ct);
    }
}

/// <summary>The exported error codes (admin design §4.1) and audit snapshots without secrets (§11.1).</summary>
public sealed class AdminContractTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2 };
    private static readonly string ErrorCodesFile = TestPostgres.RepositoryFile(Path.Combine("contracts", "admin", "v1", "error-codes.json"));

    /// <summary>
    /// The export, generated from <see cref="AdminErrorCodes"/>. With <c>UME_RAG_UPDATE_CONTRACTS=1</c> the test writes the
    /// file instead of comparing; commit the result.
    /// </summary>
    [Fact]
    public void Error_codes_json_matches_the_codes()
    {
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            codes = AdminErrorCodes.All.Select(c => new { code = c.Code, status = c.Status, message = c.Message }),
        }, Indented) + "\n";
        if (Environment.GetEnvironmentVariable("UME_RAG_UPDATE_CONTRACTS") == "1")
        {
            File.WriteAllText(ErrorCodesFile, json);
        }

        File.ReadAllText(ErrorCodesFile).ReplaceLineEndings("\n").ShouldBe(json, "Run with UME_RAG_UPDATE_CONTRACTS=1 to regenerate.");
    }

    [Fact]
    public void Error_codes_are_unique_snake_case_with_a_message()
    {
        AdminErrorCodes.All.Select(c => c.Code).ShouldBeUnique();
        foreach (var code in AdminErrorCodes.All)
        {
            code.Code.ShouldMatch("^[a-z]+(_[a-z]+)*$");
            code.Message.ShouldNotBeNullOrWhiteSpace();
            code.Status.ShouldBeInRange(400, 599);
        }
    }

    [Fact]
    public void Audit_snapshots_never_hold_a_secret_column()
    {
        string[] denied = ["EncryptedSecret", "KeyHash", "EncryptedKey", "Secret", "Password", "Pepper"];
        foreach (var type in AuditSnapshots.All)
        {
            foreach (var property in type.GetProperties())
            {
                denied.ShouldNotContain(d => property.Name.Contains(d, StringComparison.OrdinalIgnoreCase), $"{type.Name}.{property.Name}");
            }
        }
    }
}
