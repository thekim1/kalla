using System.Text.Json;
using Ume.Rag.TestKit;

namespace Ume.Rag.Worker.Tests;

/// <summary>
/// The gateway address is a secret (never in the repository, a log or a chat): HttpClient's request logging at
/// Information writes every request URL, the gateway's included. Found in the pilot crawl (2026-11-01).
/// </summary>
public sealed class LoggingConfigTests
{
    [Fact]
    public void The_worker_does_not_log_http_client_requests_at_information()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("src", "Ume.Rag.Worker", "appsettings.json"))));
        var levels = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        levels.TryGetProperty("System.Net.Http.HttpClient", out var level).ShouldBeTrue("System.Net.Http.HttpClient needs its own level");
        level.GetString().ShouldBeOneOf(["Warning", "Error", "Critical", "None"], "request URLs (the gateway's address) must not reach the log");
    }
}
