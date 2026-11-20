using Kalla.Infrastructure;
using Kalla.MigrationService;

var builder = Host.CreateApplicationBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddRagDatabase();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
