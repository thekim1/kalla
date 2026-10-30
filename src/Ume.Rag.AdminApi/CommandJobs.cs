using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The admin's <c>command</c> jobs (admin design §4.7), as the worker reads them (H4, #28): the payload's <c>kind</c> selects
/// the handler; ids and parameters only, never content. Each goes into <see cref="AdminChange{T}.Jobs"/> so it is enqueued
/// with the change and its audit entry.
/// </summary>
public static class CommandJobs
{
    public const string CancelRun = "cancel-run";

    /// <summary>Cancels a run's queued and running jobs; dedup <c>cancel:{runId}</c>. The endpoint comes with <c>Runs</c> (P1).</summary>
    public static JobRequest CancelRunJob(Guid runId, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification, JsonSerializer.Serialize(new { kind = CancelRun, runId }))
        {
            DedupKey = $"cancel:{runId}",
            RunId = runId,
        };
}
