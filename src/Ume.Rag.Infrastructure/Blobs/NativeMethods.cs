using System.Runtime.InteropServices;

namespace Ume.Rag.Infrastructure.Blobs;

/// <summary>
/// POSIX <c>link(2)</c>, the no-overwrite publish of <see cref="FileSystemBlobStore"/> on Linux and macOS. .NET has no
/// managed hard-link API, and <c>File.Move(…, overwrite: false)</c> on Unix checks the destination with <c>lstat</c> and
/// then calls <c>rename</c>, which replaces a file that appeared in between. <c>link</c> fails with <c>EEXIST</c>
/// atomically instead. <c>"libc"</c> is mapped by the runtime to the platform C library (<c>libc.so.6</c> on glibc).
/// </summary>
internal static class NativeMethods
{
    /// <summary><c>ENOENT</c>, the same on Linux (every architecture) and macOS.</summary>
    internal const int NoSuchFileOrDirectory = 2;

    /// <summary><c>EEXIST</c>, the same on Linux (every architecture) and macOS.</summary>
    internal const int FileExists = 17;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "CA2101 guards against ANSI best-fit conversion on Windows; this is Unix-only and marshals explicitly as UTF-8.")]
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    internal static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
