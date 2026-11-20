using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kalla.Worker.Parsing;

namespace Kalla.ContractTests;

/// <summary>
/// The parser image named by <c>KALLA_TEST_PARSER_IMAGE</c>, started once per test class with the production
/// hardening (read-only root, <c>/tmp</c> as tmpfs, no capabilities, no new privileges). Without the variable nothing
/// starts and the tests that need it skip. Use an image name with a registry part that has a dot (for example
/// <c>kalla.local/parser:ci</c>): Testcontainers prefixes other names with the Docker Hub mirror CI sets. Shared by every
/// test class of <see cref="ParserImageTests"/>, so no two tests use the one conversion slot at the same time.
/// </summary>
public sealed class ParserContainer : IAsyncLifetime
{
    public const string ImageVariable = "KALLA_TEST_PARSER_IMAGE";
    private const int Port = 8000;

    private IContainer? _container;
    private HttpClient? _http;

    public static string? Image =>
        Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : null;

    public HttpClient Http => _http ?? throw new InvalidOperationException("The parser container is not running.");

    public ParserClient Client(bool strict = true) => new(Http, ParserJson.Create(strict));

    /// <summary>How long a test waits for the convert lane (a recycled slot respawns and warms up again).</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Skips the calling test when no image is configured.</summary>
    public void RequireImage() => Assert.SkipWhen(Image is null, $"{ImageVariable} is not set (CI's contract job sets it).");

    /// <summary>
    /// Skips without an image, else waits (bounded by <see cref="ReadyTimeout"/>) until <c>/v1/info</c> reports every
    /// conversion slot warm. The one slot recycles on RSS growth (to-verify #65); a request during the respawn answers
    /// <c>not_ready</c>, which the worker requeues, but a contract test must start on a ready parser.
    /// </summary>
    public async Task RequireReadyAsync()
    {
        RequireImage();
        var ct = TestContext.Current.CancellationToken;
        var client = Client(strict: false);
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        ParserCapacity? capacity = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                capacity = (await client.GetInfoAsync(ct)).Capacity;
                if (capacity.ConvertSlots > 0 && capacity.SlotsReady >= capacity.ConvertSlots)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // the parser is restarting its listener: keep waiting within the bound
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        Assert.Fail($"The parser's convert lane was not ready within {ReadyTimeout} (last capacity: {capacity}).");
    }

    /// <summary>
    /// <see cref="ParserClient.ConvertAsync"/> of the file at <paramref name="path"/>, retried while the parser answers
    /// <c>not_ready</c>: the slot can pass <see cref="RequireReadyAsync"/> and then recycle on RSS growth from the previous
    /// conversion (to-verify #65). Each attempt waits for readiness again, reopens the file and empties
    /// <paramref name="docling"/>. Bounded by <see cref="ReadyTimeout"/>; any other answer is returned or thrown as it is.
    /// </summary>
    public async Task<ParserConvertResult> ConvertWhenReadyAsync(string path, ParserConvertOptions options, MemoryStream? docling, bool strict = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        while (true)
        {
            await RequireReadyAsync();
            docling?.SetLength(0);
            await using var file = File.OpenRead(path);
            try
            {
                return await Client(strict).ConvertAsync(file, options, docling, ct);
            }
            catch (ParserException e) when (e.Error?.Error.Code == "not_ready" && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
    }

    /// <summary>
    /// A raw <c>POST /v1/convert</c> with the content <paramref name="content"/> builds for each attempt, retried like
    /// <see cref="ConvertWhenReadyAsync"/> while the answer is 503 <c>not_ready</c> (no error case expects that code).
    /// </summary>
    public async Task<HttpResponseMessage> PostConvertWhenReadyAsync(Func<HttpContent> content)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        while (true)
        {
            await RequireReadyAsync();
            using var body = content();
            var response = await Http.PostAsync(new Uri("v1/convert", UriKind.Relative), body, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable || DateTimeOffset.UtcNow >= deadline
                || !(await response.Content.ReadAsStringAsync(ct)).Contains("\"not_ready\"", StringComparison.Ordinal))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

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

/// <summary>The test classes that use the parser image: one container, run one test at a time.</summary>
[CollectionDefinition(Name)]
public sealed class ParserImageTests : ICollectionFixture<ParserContainer>
{
    public const string Name = "parser-image";
}
