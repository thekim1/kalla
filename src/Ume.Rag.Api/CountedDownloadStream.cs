using Ume.Rag.Infrastructure.Blobs;

namespace Ume.Rag.Api;

/// <summary>
/// A read-only pass-through over a download body that reports its outcome once: <c>ok</c> when the body has been read
/// to its end (or to its known length), <c>integrity</c> when the envelope throws <see cref="BlobIntegrityException"/>
/// (the exception still propagates and aborts the response). A body disposed before its end reports nothing.
/// </summary>
internal sealed class CountedDownloadStream(Stream inner, long? length, Action<string> report) : Stream
{
    private long _read;
    private int _reported;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length ?? throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return Counted(inner.Read(buffer), buffer.Length);
        }
        catch (BlobIntegrityException)
        {
            Report("integrity");
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return Counted(await inner.ReadAsync(buffer, cancellationToken), buffer.Length);
        }
        catch (BlobIntegrityException)
        {
            Report("integrity");
            throw;
        }
    }

    private int Counted(int n, int asked)
    {
        _read += n;
        if ((n == 0 && asked > 0) || (length is { } total && _read >= total))
        {
            Report("ok");
        }

        return n;
    }

    private void Report(string outcome)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            report(outcome);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>Flushes held-back 5001 counts every 10 s (<see cref="KnowledgeSecurity.FlushAuthFailures"/>).</summary>
internal sealed class KnowledgeSecurityFlusher(KnowledgeSecurity security, TimeProvider time) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            security.FlushAuthFailures();
        }
    }
}
