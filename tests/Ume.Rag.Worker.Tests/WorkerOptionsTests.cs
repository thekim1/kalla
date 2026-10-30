using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Tests;

/// <summary><c>Worker:*</c> (repo-and-deployment §3): validated at start, unknown classes fail.</summary>
public sealed class WorkerOptionsTests
{
    [Fact]
    public void Defaults_run_every_class_with_the_design_concurrency()
    {
        var options = new WorkerOptions();

        Validate(options).Succeeded.ShouldBeTrue();
        options.ClassList().ShouldBe([JobClasses.Preview, JobClasses.Attachment, JobClasses.Command, JobClasses.Sync, JobClasses.Fetch,
            JobClasses.Parse, JobClasses.Embed, JobClasses.Eval]);
        options.ConcurrencyOf(JobClasses.Command).ShouldBe(2);
        options.ConcurrencyOf(JobClasses.Fetch).ShouldBe(8);
        options.ConcurrencyOf(JobClasses.Embed).ShouldBe(4);
        options.ConcurrencyOf(JobClasses.Eval).ShouldBe(1);
        (options.HeartbeatSeconds, options.InstanceTimeoutSeconds, options.ReaperSeconds, options.PollSeconds).ShouldBe((10, 30, 10, 2));
    }

    [Fact]
    public void Classes_and_concurrency_are_deployment_values()
    {
        var options = new WorkerOptions { Classes = " embed, parse ", Concurrency = { ["parse"] = 3 } };

        Validate(options).Succeeded.ShouldBeTrue();
        options.ClassList().ShouldBe([JobClasses.Parse, JobClasses.Embed]);
        options.ConcurrencyOf(JobClasses.Parse).ShouldBe(3);
    }

    [Theory]
    [InlineData("command,crawl", "unknown job class 'crawl'")]
    [InlineData("Command", "unknown job class 'Command'")]
    public void An_unknown_class_fails(string classes, string message) =>
        Validate(new WorkerOptions { Classes = classes }).FailureMessage!.ShouldContain(message);

    [Fact]
    public void Concurrency_of_an_unknown_class_or_out_of_range_fails()
    {
        Validate(new WorkerOptions { Concurrency = { ["ner"] = 1 } }).FailureMessage!.ShouldContain("unknown job class 'ner'");
        Validate(new WorkerOptions { Concurrency = { ["fetch"] = 0 } }).FailureMessage!.ShouldContain("Worker:Concurrency:fetch");
    }

    [Fact]
    public void Heartbeat_must_fit_the_shortest_lease_and_the_instance_timeout()
    {
        JobClassPolicy.ShortestLease.ShouldBe(TimeSpan.FromMinutes(1));
        Validate(new WorkerOptions { HeartbeatSeconds = 21 }).Failed.ShouldBeTrue();
        Validate(new WorkerOptions { HeartbeatSeconds = 15, InstanceTimeoutSeconds = 20 }).FailureMessage!.ShouldContain("InstanceTimeoutSeconds");
    }

    [Fact]
    public async Task The_worker_refuses_to_start_with_an_unknown_class()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = "Host=127.0.0.1;Port=1;Database=ragdb;Username=nobody",
            ["Worker:Classes"] = "command,crawl",
        });
        builder.AddRagConfigMonitor();
        builder.AddRagWorker();
        await using var app = builder.Build();

        var error = await Should.ThrowAsync<OptionsValidationException>(() => app.StartAsync(TestContext.Current.CancellationToken));
        error.Message.ShouldContain("crawl");
    }

    [Theory]
    [InlineData(null, null, 105)]
    [InlineData("10", null, 30)]
    [InlineData(null, "300", 300)]
    public void The_host_shutdown_timeout_covers_the_stop_grace(string? graceSeconds, string? hostSeconds, int expectedSeconds)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = "Host=127.0.0.1;Port=1;Database=ragdb;Username=nobody",
            ["Worker:StopGraceSeconds"] = graceSeconds,
        });
        if (hostSeconds is not null)
        {
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(int.Parse(hostSeconds, System.Globalization.CultureInfo.InvariantCulture)));
        }

        builder.AddRagWorker();

        using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    private static ValidateOptionsResult Validate(WorkerOptions options) => new WorkerOptionsValidator().Validate(null, options);
}
