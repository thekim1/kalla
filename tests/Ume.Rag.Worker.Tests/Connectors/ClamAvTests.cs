using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Worker.Connectors.Malware;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// The ClamAV client against recorded clamd replies (connectors design §11, §18; formats measured in P0, to-verify #81) and a
/// fake clamd on loopback: every reply that is not a clean or a finding fails closed.
/// </summary>
public sealed class ClamAvTests
{
    [Theory]
    [InlineData("stream: OK", "clean")]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", "malware")]
    [InlineData("stream: Ume.Rag.Test.EICAR.UNOFFICIAL FOUND", "malware")]
    [InlineData("INSTREAM size limit exceeded. ERROR", "too_large")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxFileSize FOUND", "too_large")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxScanSize FOUND", "too_large")]
    [InlineData("stream: Can't allocate memory ERROR", "unavailable")]
    [InlineData("UNKNOWN COMMAND", "unavailable")]
    [InlineData("stream:  FOUND", "unavailable")]
    [InlineData("", "unavailable")]
    [InlineData("stream: OK trailing", "unavailable")]
    public void Maps_instream_replies_and_fails_closed_on_anything_else(string reply, string expected)
    {
        var result = ClamAvProtocol.ParseScanReply(reply);

        Name(result).ShouldBe(expected);
        if (result is ScanResult.Unavailable unavailable)
        {
            unavailable.CountsAttempt.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("Win.Test.EICAR_HDB-1", "clamav.win.test")]
    [InlineData("Doc.Malware.Agent-123456", "clamav.doc.malware")]
    [InlineData("Ume.Rag.Test.EICAR", "clamav.ume.rag")]
    [InlineData("Eicar-Test-Signature", "clamav.eicar_test_signature")]
    public void Malware_reasons_name_the_signature_family(string signature, string reason) =>
        new ScanResult.Malware(signature).Reason.ShouldBe(reason);

    [Theory]
    [InlineData("ClamAV 1.4.5/28169/Fri Oct 30 06:26:48 2026", "1.4.5", 28169L, "2026-10-30T06:26:48Z")]
    [InlineData("ClamAV 1.4.6/28172/Mon Nov  2 07:01:02 2026", "1.4.6", 28172L, "2026-11-02T07:01:02Z")]
    [InlineData("ClamAV 1.4.5", "1.4.5", null, null)]
    public void Parses_zversion(string reply, string engine, long? database, string? date)
    {
        var version = ClamAvProtocol.ParseVersion(reply).ShouldNotBeNull();

        version.Engine.ShouldBe(engine);
        version.DatabaseVersion.ShouldBe(database);
        version.DatabaseDate.ShouldBe(date is null ? null : DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    // No engine after a failed reload (measured in P0): clamd still answers PING.
    [InlineData("ClamAV 1.4.5/4294967295/invalid timestam")]
    [InlineData("ClamAV 1.4.5/4294967295/Fri Oct 30 06:26:48 2026")]
    [InlineData("ClamAV 1.4.5/28169/not a date")]
    [InlineData("PONG")]
    [InlineData("")]
    [InlineData("ClamAV /1/Fri Oct 30 06:26:48 2026")]
    public void Refuses_a_zversion_no_working_engine_sends(string reply) =>
        ClamAvProtocol.ParseVersion(reply).ShouldBeNull();

    [Fact]
    public async Task Streams_instream_chunks_and_reads_a_clean_reply()
    {
        await using var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "stream: OK");
        var scanner = clamd.Scanner();
        var content = new byte[ClamAvProtocol.ChunkSize * 2 + 17];
        Random.Shared.NextBytes(content);

        var result = await scanner.ScanAsync(new MemoryStream(content), "upload", TestContext.Current.CancellationToken);

        result.ShouldBe(ScanResult.Clean);
        clamd.Received.ShouldBe(content);
        clamd.ChunkSizes.ShouldBe([ClamAvProtocol.ChunkSize, ClamAvProtocol.ChunkSize, 17]);
    }

    [Fact]
    public async Task Reports_a_finding()
    {
        await using var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "stream: Win.Test.EICAR_HDB-1 FOUND");

        var result = await clamd.Scanner().ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Malware("Win.Test.EICAR_HDB-1"));
    }

    [Fact]
    public async Task Reads_the_size_limit_reply_after_clamd_closes_during_the_send()
    {
        await using var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "INSTREAM size limit exceeded. ERROR", closeAfterBytes: 100_000);
        var content = new byte[4 * 1024 * 1024];

        var result = await clamd.Scanner().ScanAsync(new MemoryStream(content), "upload", TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ScanResult.TooLarge>();
    }

    [Fact]
    public async Task Fails_closed_when_clamd_is_down()
    {
        var port = FakeClamd.FreePort();
        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = port }), TimeProvider.System, ConnectorTestKit.Metrics());

        var result = await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "crawl", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Unavailable("clamav.unavailable", CountsAttempt: false));
        (await new ClamAvHealthCheck(scanner).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status
            .ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Fails_closed_when_clamd_goes_away_between_version_and_scan()
    {
        var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "stream: OK");
        var scanner = clamd.Scanner();
        (await scanner.GetSignaturesAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(SignatureStatus.Fresh);
        await clamd.DisposeAsync();

        var result = await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "crawl", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Unavailable("clamav.unavailable", CountsAttempt: false));
    }

    [Fact]
    public async Task Fails_closed_without_a_configured_host()
    {
        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions()), TimeProvider.System, ConnectorTestKit.Metrics());

        (await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "crawl", TestContext.Current.CancellationToken))
            .ShouldBeOfType<ScanResult.Unavailable>();
    }

    [Theory]
    [InlineData("ClamAV 1.4.5/4294967295/invalid timestam")]
    [InlineData("garbage")]
    public async Task Fails_closed_on_a_version_reply_it_cannot_parse_without_scanning(string version)
    {
        await using var clamd = await FakeClamd.StartAsync(version, "stream: OK");

        var result = await clamd.Scanner().ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Unavailable("clamav.unavailable", CountsAttempt: false));
        clamd.Scans.ShouldBe(0);
    }

    [Fact]
    public async Task Scans_nothing_with_signatures_older_than_seven_days_and_reports_unhealthy()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var clamd = await FakeClamd.StartAsync("ClamAV 1.4.6/28169/Fri Oct 30 06:26:48 2026", "stream: OK");
        var scanner = clamd.Scanner(time);

        var result = await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);

        result.ShouldBe(new ScanResult.Unavailable("clamav.unavailable", CountsAttempt: false));
        clamd.Scans.ShouldBe(0);
        (await new ClamAvHealthCheck(scanner).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status
            .ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Signatures_older_than_48_hours_degrade_health_but_still_scan()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        await using var clamd = await FakeClamd.StartAsync("ClamAV 1.4.6/28169/Fri Oct 30 06:26:48 2026", "stream: OK");
        var scanner = clamd.Scanner(time);

        (await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken)).ShouldBe(ScanResult.Clean);
        (await new ClamAvHealthCheck(scanner).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status
            .ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Reads_zversion_at_most_every_ten_minutes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        await using var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "stream: OK");
        var scanner = clamd.Scanner(time);

        for (var i = 0; i < 3; i++)
        {
            await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);
        }

        clamd.VersionRequests.ShouldBe(1);
        time.Advance(TimeSpan.FromMinutes(11));
        await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);
        clamd.VersionRequests.ShouldBe(2);
    }

    [Fact]
    public async Task An_error_reply_makes_the_next_file_ask_zversion_again()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        await using var clamd = await FakeClamd.StartAsync(FakeClamd.FreshVersion, "stream: Can't load engine ERROR");
        var scanner = clamd.Scanner(time);

        (await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken)).ShouldBeOfType<ScanResult.Unavailable>();
        await scanner.ScanAsync(new MemoryStream("x"u8.ToArray()), "upload", TestContext.Current.CancellationToken);

        clamd.VersionRequests.ShouldBe(2);
    }

    [Fact]
    public async Task The_development_scanner_finds_eicar_only()
    {
        var scanner = new EicarOnlyScanner();
        var eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

        (await scanner.ScanAsync(new MemoryStream(eicar), "upload", TestContext.Current.CancellationToken)).ShouldBeOfType<ScanResult.Malware>();
        (await scanner.ScanAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "upload", TestContext.Current.CancellationToken)).ShouldBe(ScanResult.Clean);
    }

    internal static string Name(ScanResult result) => result switch
    {
        ScanResult.CleanResult => "clean",
        ScanResult.Malware => "malware",
        ScanResult.TooLarge => "too_large",
        _ => "unavailable",
    };

    /// <summary>
    /// A clamd on loopback that answers <c>zVERSION</c> with a fixed reply and every <c>zINSTREAM</c> with another, recording
    /// the chunks it got; <c>closeAfterBytes</c> makes it reply and close mid-stream, as clamd does over <c>StreamMaxLength</c>.
    /// </summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        public const string FreshVersion = "ClamAV 1.4.6/28169/Fri Oct 30 06:26:48 2026";

        private readonly TcpListener _listener;
        private readonly string _version;
        private readonly string _scanReply;
        private readonly long? _closeAfterBytes;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly MemoryStream _received = new();

        private FakeClamd(string version, string scanReply, long? closeAfterBytes)
        {
            _version = version;
            _scanReply = scanReply;
            _closeAfterBytes = closeAfterBytes;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int VersionRequests { get; private set; }

        public int Scans { get; private set; }

        public List<int> ChunkSizes { get; } = [];

        public byte[] Received => _received.ToArray();

        public static Task<FakeClamd> StartAsync(string version, string scanReply, long? closeAfterBytes = null) =>
            Task.FromResult(new FakeClamd(version, scanReply, closeAfterBytes));

        public static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        public ClamAvScanner Scanner(TimeProvider? time = null) =>
            new(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = Port }),
                time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)), ConnectorTestKit.Metrics());

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Cancelling and stopping race: the pending accept ends with either exception.
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var command = await ReadCommandAsync(stream);
                if (command == "zVERSION")
                {
                    VersionRequests++;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_version + "\0"), _stop.Token);
                }
                else if (command == "zINSTREAM")
                {
                    Scans++;
                    await ReadChunksAsync(stream);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_scanReply + "\0"), _stop.Token);
                }

                // Closed without draining the rest of a refused stream, as clamd does: the client's next send fails.
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }

        private async Task<string> ReadCommandAsync(NetworkStream stream)
        {
            var command = new StringBuilder();
            var one = new byte[1];
            while (await stream.ReadAsync(one, _stop.Token) == 1 && one[0] != 0)
            {
                command.Append((char)one[0]);
            }

            return command.ToString();
        }

        private async Task ReadChunksAsync(NetworkStream stream)
        {
            var header = new byte[4];
            long total = 0;
            while (true)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0)
                {
                    return;
                }

                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk, _stop.Token);
                ChunkSizes.Add(length);
                _received.Write(chunk);
                total += length;
                if (total > _closeAfterBytes)
                {
                    return;
                }
            }
        }
    }
}
