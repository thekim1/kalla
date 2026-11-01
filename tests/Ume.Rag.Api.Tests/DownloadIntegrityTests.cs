using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The follow-ups of #163's review: held-back 5001 counts are flushed once their minute has passed, and a download is
/// counted <c>ok</c> when fully sent or <c>integrity</c> when a tampered ciphertext segment aborts it (knowledge §3.5, §16.3).
/// </summary>
public sealed class DownloadIntegrityTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("ume-rag-integrity-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (KnowledgeSecurity Security, CapturingLoggerProvider Logs, FakeTimeProvider Time, IMeterFactory Meters) Security()
    {
        var logs = new CapturingLoggerProvider();
        var loggers = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var state = new ConfigVersionState(Options.Create(new ConfigOptions()), time, new HostingEnvironment { EnvironmentName = "Development" }, loggers);
        return (new KnowledgeSecurity(loggers, state, time, Options.Create(new ApiSecurityOptions()), meters), logs, time, meters);
    }

    private static DefaultHttpContext From(string address)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return http;
    }

    private static List<LogEntry> AuthFailures(CapturingLoggerProvider logs) =>
        [.. logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5001)];

    [Fact]
    public void A_burst_after_the_last_emit_is_flushed_once_its_minute_has_passed()
    {
        var (security, logs, time, _) = Security();
        var http = From("192.0.2.10");

        security.AuthFailed(http, "invalid_key", "key");
        AuthFailures(logs).ShouldHaveSingleItem().Message.ShouldContain("x1 ");

        for (var i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            security.AuthFailed(http, "invalid_key", "key");
        }

        security.FlushAuthFailures().ShouldBe(0); // still inside the minute of the last emit
        AuthFailures(logs).Count.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(11));
        security.FlushAuthFailures().ShouldBe(1);
        var flushed = AuthFailures(logs)[1].Message;
        flushed.ShouldContain("x50 ");
        flushed.ShouldContain("invalid_key (key)");
        flushed.ShouldContain("192.0.2.0"); // truncated address, as the inline emit

        security.FlushAuthFailures().ShouldBe(0); // nothing held back any more
        time.Advance(TimeSpan.FromMinutes(2));
        security.FlushAuthFailures().ShouldBe(0); // the idle bucket is dropped, not re-reported
        AuthFailures(logs).Count.ShouldBe(2);

        security.AuthFailed(http, "invalid_key", "key"); // a new bucket emits at once again
        AuthFailures(logs).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_body_counts_ok_when_read_to_its_end_and_integrity_when_a_segment_fails()
    {
        var (security, logs, _, meters) = Security();
        var outcomes = new List<string>();
        using var listener = Listener(outcomes, meters);

        await using (var ok = security.CountDownload(new MemoryStream(new byte[100]), 100, "key", Guid.Empty, null))
        {
            await ok.CopyToAsync(Stream.Null, Ct);
        }

        await using (var damaged = security.CountDownload(new ThrowingStream(), 100, "grant", Guid.Empty, null))
        {
            await Should.ThrowAsync<BlobIntegrityException>(() => damaged.CopyToAsync(Stream.Null, Ct));
        }

        await using (var abandoned = security.CountDownload(new MemoryStream(new byte[100]), 100, "key", Guid.Empty, null))
        {
            _ = await abandoned.ReadAsync(new byte[10], Ct); // the client went away
        }

        outcomes.ShouldBe(["ok/key", "integrity/grant"]);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("integrity check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tampered_ciphertext_segment_aborts_the_download_and_counts_integrity()
    {
        // Knowledge §16.3: "tampered ciphertext segment → aborted response and integrity metric". The upload's blob is
        // replaced by an internal-level URB2 blob of several segments with one byte of the last segment flipped.
        var keys = Directory.CreateDirectory(Path.Combine(_root, "keys")).FullName;
        File.WriteAllText(Path.Combine(keys, "rag-blob-internal"), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "\n");
        var store = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = Path.Combine(_root, "blobs") }), TimeProvider.System);
        var envelope = new BlobEnvelope(store, new FileKeyProvider(Options.Create(new KeyProviderOptions { Directory = keys })), new InternalPolicy());
        var key = new BlobKey($"uploads/{db.PublicSource:D}/{db.UploadSlot:D}/{Convert.ToHexStringLower(KnowledgeFixture.UploadHash)}");
        var data = new byte[200_000];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        await envelope.PutAsync(key, Classification.Internal, new MemoryStream(data), data.Length, Ct);
        var path = Path.Combine(_root, "blobs", key.Value.Replace('/', Path.DirectorySeparatorChar));
        File.SetAttributes(path, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[^100] ^= 0xff;
        File.Delete(path);
        await File.WriteAllBytesAsync(path, bytes, Ct);

        var outcomes = new List<string>();
        using var listener = Listener(outcomes, meter: null);
        using var api = db.ApiHost().WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            services.AddSingleton<IDocumentContentReader>(new AsInternal(new EnvelopeContentReader(envelope, NullLogger<EnvelopeContentReader>.Instance)))));
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.InternalKey);

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var response = await client.GetAsync(new Uri($"/v1/documents/{db.Upload.Id}/content", UriKind.Relative), Ct);
            await response.Content.ReadAsByteArrayAsync(Ct);
        });

        lock (outcomes)
        {
            outcomes.ShouldContain("integrity/key");
            outcomes.ShouldNotContain("ok/key");
        }
    }

    /// <summary>Records <c>outcome/kind</c> of <c>rag.documents.downloads</c>; from <paramref name="meter"/>'s meters only, or any.</summary>
    private static MeterListener Listener(List<string> outcomes, IMeterFactory? meter)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "rag.documents.downloads" && (meter is null || ReferenceEquals(instrument.Meter.Scope, meter)))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? outcome = null, kind = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") { outcome = tag.Value as string; }
                if (tag.Key == "kind") { kind = tag.Value as string; }
            }

            lock (outcomes)
            {
                outcomes.Add($"{outcome}/{kind}");
            }
        });
        listener.Start();
        return listener;
    }

    private sealed class InternalPolicy : IBlobKeyPolicy
    {
        public Task<string?> MasterKeyNameAsync(string level, CancellationToken cancellationToken) =>
            Task.FromResult(DataProtectionBlobKeyPolicy.DefaultKeyName(level));
    }

    /// <summary>Reads the fixture's (public) upload as an internal blob, so the envelope decrypts and verifies it.</summary>
    private sealed class AsInternal(IDocumentContentReader inner) : IDocumentContentReader
    {
        public Task<DocumentBytes?> OpenAsync(DocumentContent content, CancellationToken ct) =>
            inner.OpenAsync(content with { Level = Classification.Internal }, ct);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BlobIntegrityException("segment 3 failed its tag");
    }
}
