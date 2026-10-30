using Ume.Rag.AdminApi;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Stores;

if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddUmeWebDefaults(maxRequestBodySize: 1024 * 1024);
builder.AddRagDatabase();
builder.AddRagDataProtection();
builder.Services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAdminSessionStore, PostgresAdminSessionStore>();
builder.AddAdminAuthentication();

var app = builder.Build();
app.UseUmeWebDefaults();
app.UseAuthentication();
app.UseMiddleware<AdminAntiforgeryMiddleware>();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapAdminAuthentication();

// /api is for signed-in users with a rag-* role; the journey pages arrive with the admin write path (P0 #11).
var api = app.MapGroup("/api").RequireAuthorization(AdminPolicies.Read);
api.MapGet("/ping", () => Results.Ok(new { ok = true }));

app.Run();

/// <summary>Entry point, public for <c>WebApplicationFactory</c> tests.</summary>
public partial class Program;
