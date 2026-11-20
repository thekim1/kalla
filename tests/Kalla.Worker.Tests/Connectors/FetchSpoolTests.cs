using Kalla.Worker.Connectors.Http;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>The worker's start removes spool files a killed process left, and keeps those of running fetches.</summary>
public sealed class FetchSpoolTests
{
    [Fact]
    public void Prepare_spool_deletes_stale_fetch_files_and_keeps_fresh_ones()
    {
        var stale = Path.Combine(Path.GetTempPath(), $"ume-rag-fetch-{Guid.NewGuid():N}");
        var fresh = Path.Combine(Path.GetTempPath(), $"ume-rag-fetch-{Guid.NewGuid():N}");
        var other = Path.Combine(Path.GetTempPath(), $"not-ume-rag-{Guid.NewGuid():N}");
        try
        {
            foreach (var path in new[] { stale, fresh, other })
            {
                File.WriteAllText(path, "x");
            }

            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - FetchedBody.StaleAfter - TimeSpan.FromMinutes(1));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(1));

            FetchedBody.PrepareSpool(TimeProvider.System).ShouldBeGreaterThanOrEqualTo(1);

            File.Exists(stale).ShouldBeFalse("a spool file older than StaleAfter belongs to no running fetch");
            File.Exists(fresh).ShouldBeTrue("a fresh spool file may be a running fetch's");
            File.Exists(other).ShouldBeTrue("only the worker's own spool files are removed");
        }
        finally
        {
            foreach (var path in new[] { stale, fresh, other })
            {
                File.Delete(path);
            }
        }
    }
}
