using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Kalla.Domain.Entities;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Jobs;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Tests;

/// <summary><c>rag_jobs</c> wake-ups (data-model §4.3) and how the worker registers them.</summary>
public sealed class JobWakeUpsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_notification_of_a_job_class_wakes_that_class_only()
    {
        var wakeUps = new JobWakeUps(TimeProvider.System);

        wakeUps.OnNotification(JobClasses.Fetch);

        (await wakeUps.WaitAsync(JobClasses.Embed, Short, Ct)).ShouldBeFalse();
        (await wakeUps.WaitAsync(JobClasses.Fetch, Short, Ct)).ShouldBeTrue();
        (await wakeUps.WaitAsync(JobClasses.Fetch, Short, Ct)).ShouldBeFalse("one wake-up per notification");
    }

    [Fact]
    public async Task A_payload_that_is_no_job_class_is_ignored()
    {
        var wakeUps = new JobWakeUps(TimeProvider.System);

        wakeUps.OnNotification("crawl");
        wakeUps.OnNotification("FETCH");

        (await wakeUps.WaitAsync("crawl", Short, Ct)).ShouldBeFalse();
        (await wakeUps.WaitAsync("FETCH", Short, Ct)).ShouldBeFalse();
    }

    [Fact]
    public void The_worker_listens_on_rag_jobs_with_the_wake_ups_its_dispatcher_waits_on()
    {
        var builder = Builder();
        builder.AddRagDatabase();
        builder.AddRagWorker();

        using var services = builder.Services.BuildServiceProvider();
        var listener = services.GetServices<INotificationListener>().ShouldHaveSingleItem();
        listener.Channel.ShouldBe("rag_jobs");
        listener.ShouldBeSameAs(services.GetRequiredService<JobWakeUps>());
    }

    [Fact]
    public void A_service_that_only_enqueues_does_not_listen()
    {
        var builder = Builder();
        builder.AddRagDatabase();
        builder.AddRagJobQueue();

        using var services = builder.Services.BuildServiceProvider();
        services.GetServices<INotificationListener>().ShouldBeEmpty();
    }

    [Fact]
    public void The_queue_needs_the_shared_data_source()
    {
        var error = Should.Throw<InvalidOperationException>(() => Builder().AddRagJobQueue());
        error.Message.ShouldContain("AddRagDatabase");
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = "Host=127.0.0.1;Port=1;Database=ragdb;Username=nobody",
        });
        return builder;
    }
}
