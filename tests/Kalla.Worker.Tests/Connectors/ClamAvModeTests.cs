using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kalla.TestKit;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Malware;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// <c>ClamAv:Mode</c> (connectors design §11): <c>on</c> (the default) scans with clamd, and outside Development a worker
/// without <c>ClamAv:Host</c> refuses to start; Development without one keeps the EICAR-only scanner. <c>off</c> gets the
/// scanner that reads nothing and answers "not scanned", a Healthy check with its own text and event 5106 at start. Any
/// other value fails the start.
/// </summary>
public sealed class ClamAvModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("on")]
    public void Outside_development_a_worker_without_a_clamav_host_refuses_to_start_naming_both_settings(string? mode)
    {
        var builder = Builder(Environments.Production, ("ClamAv:Mode", mode));

        var error = Should.Throw<InvalidOperationException>(() => builder.AddRagConnectors());

        error.Message.ShouldContain("ClamAv:Mode");
        error.Message.ShouldContain("ClamAv:Host");
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("false")]
    [InlineData("")]
    public void A_mode_other_than_on_or_off_fails_the_start(string mode)
    {
        var builder = Builder(Environments.Development, ("ClamAv:Mode", mode));

        Should.Throw<InvalidOperationException>(() => builder.AddRagConnectors()).Message
            .ShouldBe($"ClamAv:Mode must be on or off, not '{mode}'.");
    }

    [Fact]
    public void With_the_scan_on_a_host_gets_the_clamd_client_and_development_without_one_the_eicar_only_scanner()
    {
        ScannerType(Builder(Environments.Production, ("ClamAv:Host", "kalla-clamav")).AddRagConnectors()).ShouldBe(typeof(ClamAvScanner));
        ScannerType(Builder(Environments.Development).AddRagConnectors()).ShouldBe(typeof(EicarOnlyScanner));
        ScannerType(Builder(Environments.Development, ("ClamAv:Mode", "on"), ("ClamAv:Host", "kalla-clamav")).AddRagConnectors()).ShouldBe(typeof(ClamAvScanner));
    }

    [Fact]
    public async Task With_the_scan_off_any_environment_starts_without_clamd_with_a_healthy_check_and_logs_5106_at_start()
    {
        var builder = Builder(Environments.Production, ("ClamAv:Mode", "off"), ("ClamAv:Host", "kalla-clamav"));
        var logs = new CapturingLoggerProvider();
        builder.Logging.AddProvider(logs);
        builder.AddRagConnectors();
        await using var services = builder.Services.BuildServiceProvider();

        services.GetRequiredService<IMalwareScanner>().ShouldBeOfType<MalwareScanOff>("off wins over a configured host: no clamd is contacted");
        var health = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "clamav", Ct);
        health.Entries["clamav"].Status.ShouldBe(HealthStatus.Healthy);
        health.Entries["clamav"].Description.ShouldBe("Malware scan is off (ClamAv:Mode = off)");
        var notice = builder.Services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MalwareScanOffNotice));

        await ((IHostedService)ActivatorUtilities.CreateInstance(services, notice.ImplementationType!)).StartAsync(Ct);

        var entry = logs.Entries.Where(e => e.EventId.Id == 5106).ShouldHaveSingleItem();
        entry.EventId.Name.ShouldBe("rag.malware.scan_off");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Category.ShouldBe(Infrastructure.Security.SecurityEvents.Category);
    }

    [Fact]
    public async Task The_scanner_with_the_scan_off_reads_no_bytes_and_answers_not_scanned_never_clean()
    {
        var scanner = new MalwareScanOff();
        using var content = new MemoryStream("%PDF-1.7"u8.ToArray());

        var result = await scanner.ScanAsync(content, "crawl", Ct);

        result.ShouldBe(ScanResult.NotScanned);
        result.ShouldNotBe(ScanResult.Clean);
        content.Position.ShouldBe(0);
        (await scanner.GetSignaturesAsync(Ct)).AllowsScans.ShouldBeTrue();
        var health = await new ClamAvHealthCheck(scanner).CheckHealthAsync(new HealthCheckContext(), Ct);
        health.Status.ShouldBe(HealthStatus.Healthy);
        health.Description.ShouldBe(ClamAvHealthCheck.ScanOffDescription);
    }

    [Fact]
    public void Options_read_the_mode()
    {
        new ClamAvOptions().ScanOff.ShouldBeFalse("on is the default");
        new ClamAvOptions { Mode = ClamAvOptions.Off }.ScanOff.ShouldBeTrue();
    }

    private static HostApplicationBuilder Builder(string environment, params (string Key, string? Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return builder;
    }

    private static Type? ScannerType(IHostApplicationBuilder builder) =>
        builder.Services.Single(d => d.ServiceType == typeof(IMalwareScanner)).ImplementationType;
}
