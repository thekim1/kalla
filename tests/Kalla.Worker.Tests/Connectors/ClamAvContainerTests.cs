using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Kalla.Worker.Connectors.Malware;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// The image named by <c>KALLA_TEST_CLAMAV_IMAGE</c> (built from <c>images/clamav</c>), started with clamd alone and a
/// one-line EICAR test signature, as <c>images/clamav/test.sh</c> does offline: no download from database.clamav.net.
/// Without the variable nothing starts and the tests skip. Use a name with a registry part that has a dot (for example
/// <c>kalla.local/clamav:ci</c>): Testcontainers prefixes other names with the Docker Hub mirror CI sets.
/// </summary>
public sealed class ClamAvContainer : IAsyncLifetime
{
    public const string ImageVariable = "KALLA_TEST_CLAMAV_IMAGE";
    private const int Port = 3310;

    private IContainer? _container;

    public static string? Image => Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : null;

    public void RequireImage() => Assert.SkipWhen(Image is null, $"{ImageVariable} is not set (CI's backend job sets it).");

    public ClamAvScanner Scanner(TimeProvider? time = null) =>
        new(Options.Create(new ClamAvOptions { Host = _container!.Hostname, Port = _container.GetMappedPublicPort(Port) }),
            time ?? TimeProvider.System, ConnectorTestKit.Metrics());

    public async ValueTask InitializeAsync()
    {
        if (Image is not { } image)
        {
            return;
        }

        _container = new ContainerBuilder(image)
            .WithEntrypoint("clamd")
            .WithCommand("--foreground")
            .WithPortBinding(Port, true)
            .WithTmpfsMount("/tmp")
            // The HDB signature of test.sh: MD5 and size of the EICAR file.
            .WithResourceMapping(Encoding.ASCII.GetBytes("44d88612fea8a8f36de82e1278abb02f:68:Kalla.Test.EICAR\n"), "/var/lib/clamav/test.hdb", 1000, 1000)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new();
                parameters.HostConfig.CapDrop = ["ALL"];
                parameters.HostConfig.SecurityOpt = ["no-new-privileges"];
            })
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new AnswersPing(), wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private sealed class AnswersPing : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(container.Hostname, container.GetMappedPublicPort(Port));
                return await ClamAvProtocol.CommandAsync(socket, "PING", CancellationToken.None) == "PONG";
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                return false;
            }
        }
    }
}

/// <summary>The ClamAV client against the real clamd (connectors design §18 "ClamAV (integration, real image)").</summary>
public sealed class ClamAvContainerTests(ClamAvContainer clamd) : IClassFixture<ClamAvContainer>
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    [Fact]
    public async Task Eicar_is_malware()
    {
        clamd.RequireImage();

        var result = await clamd.Scanner().ScanAsync(new MemoryStream(Eicar), "upload", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Malware("Kalla.Test.EICAR.UNOFFICIAL"));
        ((ScanResult.Malware)result).Reason.ShouldBe("clamav.ume.rag");
    }

    [Fact]
    public async Task A_clean_file_of_several_chunks_is_clean()
    {
        clamd.RequireImage();
        var content = new byte[3 * ClamAvProtocol.ChunkSize + 5];
        Random.Shared.NextBytes(content);

        (await clamd.Scanner().ScanAsync(new MemoryStream(content), "upload", TestContext.Current.CancellationToken)).ShouldBe(ScanResult.Clean);
    }

    [Fact]
    public async Task A_stream_over_stream_max_length_is_too_large_not_clean()
    {
        clamd.RequireImage();

        var result = await clamd.Scanner().ScanAsync(new ZeroStream(60L * 1024 * 1024 + 1), "upload", TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ScanResult.TooLarge>();
    }

    [Fact]
    public async Task Zversion_with_only_a_custom_database_is_undated_and_still_scans()
    {
        clamd.RequireImage();
        var scanner = clamd.Scanner(new FakeTimeProvider(DateTimeOffset.UtcNow));

        var signatures = await scanner.GetSignaturesAsync(TestContext.Current.CancellationToken);

        signatures.Status.ShouldBe(SignatureStatus.Undated);
        signatures.Version!.Engine.ShouldStartWith("1.4.");
        signatures.AllowsScans.ShouldBeTrue();
    }

    /// <summary>A long run of zero bytes without the memory.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
