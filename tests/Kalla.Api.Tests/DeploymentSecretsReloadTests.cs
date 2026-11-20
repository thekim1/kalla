using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Api.Tests;

/// <summary>
/// The secret files reload on change (<see cref="InfrastructureExtensions.AddDeploymentSecrets"/>): a gateway credential key
/// written, changed or removed by a deployment's bootstrap reaches <see cref="IOptionsMonitor{TOptions}"/> without a restart,
/// so a Portainer stack update is enough (docs/portainer.md).
/// </summary>
public sealed class DeploymentSecretsReloadTests : IDisposable
{
    private readonly DirectoryInfo _secrets = Directory.CreateTempSubdirectory("kalla-secrets-");

    [Fact]
    public async Task A_key_file_added_changed_and_removed_after_the_start_reaches_the_options_monitor()
    {
        await File.WriteAllTextAsync(Path.Join(_secrets.FullName, "Security__KeyPepper"), "pepper", TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Security:SecretsDirectory"] = _secrets.FullName;
        builder.AddDeploymentSecrets();
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);
        using var host = builder.Build();
        var gateway = host.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>();
        gateway.CurrentValue.Key.ShouldBeNull("no key at the start");

        await File.WriteAllTextAsync(Path.Join(_secrets.FullName, "Gateway__Key"), "first-key", TestContext.Current.CancellationToken);
        await Until(() => gateway.CurrentValue.Key == "first-key");

        await File.WriteAllTextAsync(Path.Join(_secrets.FullName, "Gateway__Key"), "rotated-key", TestContext.Current.CancellationToken);
        await Until(() => gateway.CurrentValue.Key == "rotated-key");

        File.Delete(Path.Join(_secrets.FullName, "Gateway__Key"));
        await Until(() => string.IsNullOrEmpty(gateway.CurrentValue.Key));
    }

    [Fact]
    public async Task The_feedback_key_written_into_the_extra_directory_by_rename_reaches_the_options_and_wins()
    {
        // M-17 C6: kalla-openwebui-setup writes ignore.<key>.tmp and renames it; the directory is optional and its keys win.
        var extra = Directory.CreateTempSubdirectory("kalla-feedback-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(_secrets.FullName, "OpenWebUi__AdminApiKey"), "old-key", TestContext.Current.CancellationToken);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["Security:SecretsDirectory"] = _secrets.FullName;
            builder.Configuration["Security:ExtraSecretsDirectory"] = extra.FullName;
            builder.AddDeploymentSecrets();
            builder.Services.AddOptions<Kalla.Api.OpenWebUiOptions>().BindConfiguration(Kalla.Api.OpenWebUiOptions.SectionName);
            using var host = builder.Build();
            var owui = host.Services.GetRequiredService<IOptionsMonitor<Kalla.Api.OpenWebUiOptions>>();
            owui.CurrentValue.AdminApiKey.ShouldBe("old-key");

            var temporary = Path.Join(extra.FullName, "ignore.OpenWebUi__AdminApiKey.tmp");
            await File.WriteAllTextAsync(temporary, "sk-minted", TestContext.Current.CancellationToken);
            File.Move(temporary, Path.Join(extra.FullName, "OpenWebUi__AdminApiKey"), overwrite: true);
            await Until(() => owui.CurrentValue.AdminApiKey == "sk-minted");
        }
        finally
        {
            extra.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_extra_directory_yields_only_the_feedback_key_and_cannot_override_other_configuration()
    {
        // #689 review: another uid owns the directory, so a file there must not rewrite kalla-api's own settings.
        var extra = Directory.CreateTempSubdirectory("kalla-feedback-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(_secrets.FullName, "Gateway__Key"), "own-key", TestContext.Current.CancellationToken);
            foreach (var (name, value) in new[]
            {
                ("Gateway__Key", "planted-key"), ("Gateway__BaseUrl", "https://elsewhere.invalid"), ("ConnectionStrings__ragdb", "Host=elsewhere"),
                ("OpenWebUi__AdminApiKey", "sk-minted"),
            })
            {
                await File.WriteAllTextAsync(Path.Join(extra.FullName, name), value, TestContext.Current.CancellationToken);
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["Security:SecretsDirectory"] = _secrets.FullName;
            builder.Configuration["Security:ExtraSecretsDirectory"] = extra.FullName;
            builder.AddDeploymentSecrets();
            using var host = builder.Build();
            var configuration = host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            configuration["OpenWebUi:AdminApiKey"].ShouldBe("sk-minted");
            configuration["Gateway:Key"].ShouldBe("own-key");
            configuration["Gateway:BaseUrl"].ShouldNotBe("https://elsewhere.invalid");
            configuration["ConnectionStrings:ragdb"].ShouldNotBe("Host=elsewhere");

            // A file added there after the start is ignored too.
            await File.WriteAllTextAsync(Path.Join(extra.FullName, "Oidc__Authority"), "https://idp.invalid", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(extra.FullName, "OpenWebUi__AdminApiKey"), "sk-rotated", TestContext.Current.CancellationToken);
            await Until(() => configuration["OpenWebUi:AdminApiKey"] == "sk-rotated");
            configuration["Oidc:Authority"].ShouldNotBe("https://idp.invalid");
        }
        finally
        {
            extra.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_extra_directory_is_no_error()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Security:SecretsDirectory"] = _secrets.FullName;
        builder.Configuration["Security:ExtraSecretsDirectory"] = Path.Join(_secrets.FullName, "absent");
        builder.AddDeploymentSecrets();
        using var host = builder.Build();
        host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["OpenWebUi:AdminApiKey"].ShouldBeNull();
    }

    public void Dispose() => _secrets.Delete(recursive: true);

    private static async Task Until(Func<bool> condition)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "the secret files did not reload");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
