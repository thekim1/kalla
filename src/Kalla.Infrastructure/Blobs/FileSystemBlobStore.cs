using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Kalla.Domain.Ports;

namespace Kalla.Infrastructure.Blobs;

/// <summary>
/// The file system <see cref="IBlobStore"/> (data-model §9) for dev and the single-host pilot: a blob is the file
/// <c>{Root}/{key}</c>. A put streams into <c>{Root}/.staging/{guid}.part</c> (same file system, not a valid key, so never
/// listed), fsyncs it and publishes it with a hard link to the final path, which fails atomically when the key exists
/// (<see cref="NativeMethods"/>; on Windows <c>MoveFileEx</c> without <c>REPLACE_EXISTING</c>); readers therefore see
/// either no file or the whole file, and of concurrent writers of one key exactly one creates it. Staging files left by a
/// crash are removed after <see cref="StagingMaxAge"/>, on the first put of each process. Keys are validated by
/// <see cref="BlobKey"/> (no <c>..</c>, no absolute paths), and a key whose path runs through a symbolic link below the
/// root is refused, so no key reaches outside the root. A key cannot be both a blob and a prefix of other blobs here
/// (a path is a file or a directory); the data-model §9 layout never needs that.
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    /// <summary>The staging directory below the root; its leading dot keeps it out of every valid key.</summary>
    public const string StagingDirectoryName = ".staging";

    /// <summary>Age after which a staging file is an abandoned write (the <c>tmp/</c> sweep age, data-model §8).</summary>
    public static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(24);

    private const int PublishAttempts = 3;

    private readonly string _root;
    private readonly string _staging;
    private readonly TimeProvider _timeProvider;
    private int _stagingSwept;

    public FileSystemBlobStore(IOptions<BlobStoreOptions> options, TimeProvider timeProvider)
    {
        var configured = options.Value.Root;
        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException($"{BlobStoreOptions.SectionName}:Root must be an absolute path, not '{configured}'.");
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        _staging = Path.Join(_root, StagingDirectoryName);
        _timeProvider = timeProvider;
    }

    public async Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken cancellationToken)
    {
        var target = BlobPath(key);
        if (length is { } declared)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(declared, nameof(length));
        }

        if (File.Exists(target))
        {
            return BlobWriteResult.AlreadyExisted;
        }

        if (Interlocked.Exchange(ref _stagingSwept, 1) == 0)
        {
            DeleteStaleStagingFiles();
        }

        Directory.CreateDirectory(_staging);
        var temp = Path.Join(_staging, $"{Guid.NewGuid():N}.part");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, cancellationToken);
                if (length is { } expected && file.Length != expected)
                {
                    throw new InvalidDataException($"Blob '{key}': {file.Length} bytes read, {expected} declared.");
                }

                file.Flush(flushToDisk: true);
            }

            return Publish(temp, target) switch
            {
                PublishResult.Linked => BlobWriteResult.Created,
                PublishResult.Exists => BlobWriteResult.AlreadyExisted,
                _ => throw new IOException($"Blob '{key}': the staging file disappeared before it was published."),
            };
        }
        finally
        {
            // After a link the staging name is a second name of the blob; dropping it leaves the blob.
            File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken cancellationToken)
    {
        var path = BlobPath(key);
        FileStream file;
        try
        {
            // FileShare.Delete: a purge may remove the name while a download still reads (Windows; Unix always allows it).
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException || Directory.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        if (range is not { } r)
        {
            return Task.FromResult<Stream?>(file);
        }

        var available = Math.Max(0, file.Length - r.Offset);
        if (available > 0)
        {
            file.Seek(r.Offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream?>(new RangeReadStream(file, Math.Min(available, r.Length ?? available)));
    }

    public Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(BlobPath(key)));

    public Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken cancellationToken)
    {
        if (!from.IsScratch)
        {
            throw new ArgumentException($"Only tmp/ blobs are promoted, not '{from}'.", nameof(from));
        }

        if (to.IsScratch)
        {
            throw new ArgumentException($"A promote leaves tmp/, so '{to}' cannot be its target.", nameof(to));
        }

        var source = BlobPath(from);
        var target = BlobPath(to);
        var result = File.Exists(target) ? PublishResult.Exists : Publish(source, target);
        if (result == PublishResult.SourceMissing)
        {
            // A retry of a promote that completed, or a source that never existed.
            result = File.Exists(target) ? PublishResult.Exists
                : throw new KeyNotFoundException($"Neither '{from}' nor '{to}' exists.");
        }

        DeleteFile(source);
        return Task.FromResult(result == PublishResult.Linked ? BlobWriteResult.Created : BlobWriteResult.AlreadyExisted);
    }

    public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        DeleteFile(BlobPath(key));
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(BlobKey prefix, CancellationToken cancellationToken)
    {
        if (!prefix.IsSweepable)
        {
            throw new ArgumentException($"Prefix deletes are for the tmp/ and previews/ sweeps only, not '{prefix}'.", nameof(prefix));
        }

        try
        {
            Directory.Delete(PathOf(prefix), recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing under the prefix.
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(PathOf(prefix));
        if (!directory.Exists)
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var file in directory.EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_root, file.FullName);
            if (Path.DirectorySeparatorChar != '/')
            {
                relative = relative.Replace(Path.DirectorySeparatorChar, '/');
            }

            // Anything that is not a valid key (stray or hidden files) is not a blob.
            if (BlobKey.TryCreate(relative, out var key))
            {
                yield return new BlobInfo(key, file.Length, new DateTimeOffset(file.LastWriteTimeUtc));
            }
        }
    }

    /// <summary>
    /// Deletes staging files older than <see cref="StagingMaxAge"/>, the remains of writes interrupted by a crash (a write
    /// in progress, in this or another process on the same volume, is younger). Returns the number deleted.
    /// </summary>
    public int DeleteStaleStagingFiles()
    {
        var directory = new DirectoryInfo(_staging);
        if (!directory.Exists)
        {
            return 0;
        }

        var cutoff = (_timeProvider.GetUtcNow() - StagingMaxAge).UtcDateTime;
        var deleted = 0;
        foreach (var file in directory.EnumerateFiles("*.part"))
        {
            if (file.LastWriteTimeUtc < cutoff)
            {
                file.Delete();
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>The path of a key that names a blob.</summary>
    private string BlobPath(BlobKey key)
    {
        if (!key.IsBlob)
        {
            throw new ArgumentException($"'{key}' is a prefix, not a blob key (it needs at least two segments).", nameof(key));
        }

        return PathOf(key);
    }

    /// <summary>
    /// The path of a key or prefix below the root. <see cref="BlobKey"/> rules out <c>..</c>, rooted paths and separators
    /// other than <c>/</c>; a symbolic link on the way (placed by someone with write access to the volume) is the one
    /// remaining way out of the root, so each existing segment is checked with <c>readlink</c>.
    /// </summary>
    private string PathOf(BlobKey key)
    {
        var path = _root;
        foreach (var segment in key.Value.Split('/'))
        {
            path = Path.Join(path, segment);
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException($"Blob key '{key}' runs through a symbolic link below the blob root; refused.");
            }
        }

        return path;
    }

    /// <summary>
    /// Gives <paramref name="source"/> the additional name <paramref name="target"/> unless <paramref name="target"/>
    /// exists (atomic). The caller removes <paramref name="source"/>. A missing target directory is created, and created
    /// again if a concurrent prefix delete removes it in between.
    /// </summary>
    private static PublishResult Publish(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.Move(source, target, overwrite: false);
                    return PublishResult.Linked;
                }
                catch (FileNotFoundException) when (!File.Exists(source))
                {
                    return PublishResult.SourceMissing;
                }
                catch (DirectoryNotFoundException) when (attempt < PublishAttempts)
                {
                    continue;
                }
                catch (IOException) when (File.Exists(target))
                {
                    return PublishResult.Exists;
                }
            }

            if (NativeMethods.Link(source, target) == 0)
            {
                return PublishResult.Linked;
            }

            var error = Marshal.GetLastPInvokeError();
            switch (error)
            {
                case NativeMethods.FileExists:
                    return PublishResult.Exists;
                case NativeMethods.NoSuchFileOrDirectory when !File.Exists(source):
                    return PublishResult.SourceMissing;
                case NativeMethods.NoSuchFileOrDirectory when attempt < PublishAttempts:
                    continue;
                default:
                    throw new IOException($"link('{source}', '{target}') failed: {Marshal.GetPInvokeErrorMessage(error)} (errno {error}).");
            }
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone with its directory.
        }
    }

    private enum PublishResult
    {
        Linked,
        Exists,
        SourceMissing,
    }
}
