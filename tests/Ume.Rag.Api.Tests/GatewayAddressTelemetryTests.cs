using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The gateway's address is a secret (AGENTS.md; issue #232): rag-api, rag-admin and the migration service do not log
/// HttpClient requests at Information, and outgoing spans carry no host, port or query.
/// </summary>
public sealed class GatewayAddressTelemetryTests
{
    private const string ScopeSourceName = "Ume.Rag.Api.Tests.GatewayAddressTelemetry";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Ume.Rag.Api")]
    [InlineData("Ume.Rag.AdminApi")]
    [InlineData("Ume.Rag.MigrationService")]
    public void The_service_does_not_log_http_client_requests_at_information(string project)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("src", project, "appsettings.json"))));
        var levels = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        levels.TryGetProperty("System.Net.Http.HttpClient", out var level).ShouldBeTrue("System.Net.Http.HttpClient needs its own level");
        level.GetString().ShouldBeOneOf(["Warning", "Error", "Critical", "None"], "request URLs (the gateway's address) must not reach the log");
    }

    [Theory]
    [InlineData("Ume.Rag.Api")]
    [InlineData("Ume.Rag.AdminApi")]
    [InlineData("Ume.Rag.MigrationService")]
    public void The_service_does_not_log_ef_sql_text_at_information(string project)
    {
        // #503: EF's command log at Information writes every statement's SQL text and timing.
        using var settings = JsonDocument.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("src", project, "appsettings.json"))));
        var levels = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        levels.TryGetProperty("Microsoft.EntityFrameworkCore.Database.Command", out var level).ShouldBeTrue();
        level.GetString().ShouldBeOneOf(["Warning", "Error", "Critical", "None"]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("http://127.0.0.1:9", true)]
    public void Tracing_is_registered_only_with_an_otlp_endpoint(string? endpoint, bool traced)
    {
        // #503: without an exporter no tracer provider exists, so no request, HTTP or SQL span is recorded to be dropped.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint;
        builder.AddServiceDefaults();
        using var app = builder.Build();
        (app.Services.GetService<OpenTelemetry.Trace.TracerProvider>() is not null).ShouldBe(traced);
        app.Services.GetService<OpenTelemetry.Metrics.MeterProvider>().ShouldNotBeNull(); // metrics stay
    }

    [Theory]
    [InlineData("http://gateway-secret.invalid:8443/v1/embeddings?key=abc", "gateway-secret", "http://redacted/v1/embeddings")] // DNS failure
    [InlineData("http://127.0.0.1:{free}/v1/embeddings?key=abc", "127.0.0.1", "http://redacted/v1/embeddings")]              // refused
    public async Task A_failed_outgoing_span_names_no_host_in_tags_events_or_status(string url, string host, string redacted)
    {
        // A port that was just free refuses at once (a fixed low port can be dropped instead, and then the test waits).
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var free = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        url = url.Replace("{free}", free.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9"; // spans are made only to be exported (#503)
        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("gateway").RemoveStandardResilienceHandler();
        using var app = builder.Build();
        await app.StartAsync(Ct);

        var finished = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "System.Net.Http" or ScopeSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (finished) { finished.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);

        // Other tests in the process make failing HTTP calls in parallel: only spans of this test's own trace count.
        using var scopeSource = new ActivitySource(ScopeSourceName);
        ActivityTraceId trace;
        using (var scope = scopeSource.StartActivity("test")!)
        using (var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("gateway"))
        {
            trace = scope.TraceId;
            client.Timeout = TimeSpan.FromSeconds(10);
            await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync(url, null, Ct));
        }

        await app.StopAsync(Ct);
        Activity span;
        lock (finished)
        {
            span = finished.Where(a => a.TraceId == trace && a.Source.Name == "System.Net.Http" && a.Status == ActivityStatusCode.Error).ShouldHaveSingleItem();
        }

        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        tags.GetValueOrDefault("url.full").ShouldBe(redacted);
        tags.ShouldNotContainKey("server.address");
        tags.ShouldNotContainKey("server.port");
        tags.ShouldNotContainKey("url.query");
        var exception = span.Events.Where(e => e.Name == "exception").ShouldHaveSingleItem();
        var eventTags = exception.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString());
        eventTags["exception.message"].ShouldStartWith("System.Net.Http.HttpRequestException"); // the type chain only
        var everything = tags.Values.Concat(eventTags.Values).Append(span.StatusDescription).Append(span.DisplayName);
        everything.ShouldAllBe(v => v == null || !v.Contains(host, StringComparison.Ordinal), string.Join(" | ", everything));
    }
}
