using Ume.Rag.Infrastructure;
using Ume.Rag.Worker.Engine;
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
builder.AddRagEngine();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
