using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ume.Rag.Worker.Parsing;

namespace Ume.Rag.ContractTests;

/// <summary>
/// The parser image named by <c>UME_RAG_TEST_PARSER_IMAGE</c>, started once per test class with the production
/// hardening (read-only root, <c>/tmp</c> as tmpfs, no capabilities, no new privileges). Without the variable nothing
/// starts and the tests that need it skip. Use an image name with a registry part that has a dot (for example
/// <c>ume-rag.local/parser:ci</c>): Testcontainers prefixes other names with the Docker Hub mirror CI sets.
/// </summary>
public sealed class ParserContainer : IAsyncLifetime
{
    public const string ImageVariable = "UME_RAG_TEST_PARSER_IMAGE";
    private const int Port = 8000;

    private IContainer? _container;
    private HttpClient? _http;

    public static string? Image =>
        Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : null;

    public HttpClient Http => _http ?? throw new InvalidOperationException("The parser container is not running.");

    public ParserClient Client(bool strict = true) => new(Http, ParserJson.Create(strict));

    /// <summary>Skips the calling test when no image is configured.</summary>
    public void RequireImage() => Assert.SkipWhen(Image is null, $"{ImageVariable} is not set (CI's contract job sets it).");

    public async ValueTask InitializeAsync()
    {
        if (Image is not { } image)
        {
            return;
        }

        _container = new ContainerBuilder(image)
            .WithPortBinding(Port, true)
            .WithTmpfsMount("/tmp")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new();
                parameters.HostConfig.ReadonlyRootfs = true;
                parameters.HostConfig.CapDrop = ["ALL"];
                parameters.HostConfig.SecurityOpt = ["no-new-privileges"];
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/health/ready"),
                wait => wait.WithTimeout(TimeSpan.FromMinutes(5))))
            .Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/"),
            Timeout = TimeSpan.FromMinutes(3),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
