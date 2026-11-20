using Kalla.Infrastructure;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.DefaultWorkerPoolSize();
builder.AddServiceDefaults();
builder.AddRagDatabase(admitAtStart: true);
builder.AddRagConfigMonitor();
builder.AddRagWorker();
builder.AddRagEngine();

var app = builder.Build();
Kalla.Worker.Connectors.Http.FetchedBody.PrepareSpool(TimeProvider.System);
app.MapDefaultEndpoints();
app.Run();
