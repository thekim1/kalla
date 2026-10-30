using Ume.Rag.Infrastructure;
using Ume.Rag.Worker.Jobs;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.DefaultWorkerPoolSize();
builder.AddServiceDefaults();
builder.AddRagDatabase();
builder.AddRagConfigMonitor();
builder.AddRagWorker();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
