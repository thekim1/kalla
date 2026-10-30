using Ume.Rag.Infrastructure;
using Ume.Rag.MigrationService;

var builder = Host.CreateApplicationBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddRagDatabase();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
