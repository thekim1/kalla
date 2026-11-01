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
    public const string ClearPiiFlag = "clear-pii-flag";
    public const string ReprocessDocument = "reprocess-document";
    public const string ReleaseHold = "release-hold";
    public const string ConfirmDeletion = "confirm-deletion";
    public const string RejectDeletion = "reject-deletion";

    /// <summary>Cancels a run's queued and running jobs; dedup <c>cancel:{runId}</c>. The endpoint comes with <c>Runs</c> (P1).</summary>
    public static JobRequest CancelRunJob(Guid runId, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification, JsonSerializer.Serialize(new { kind = CancelRun, runId }))
        {
            DedupKey = $"cancel:{runId}",
            RunId = runId,
        };

    /// <summary>Releases a PII-flagged document (<c>command.clear-pii-flag</c>); dedup <c>pii-clear:{documentId}</c>.</summary>
    public static JobRequest ClearPiiFlagJob(Guid documentId, string decidedBy, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification, JsonSerializer.Serialize(new { kind = ClearPiiFlag, documentId, decidedBy }))
        {
            DedupKey = $"pii-clear:{documentId}",
        };

    /// <summary>Processes a document again (<c>command.reprocess-document</c>); dedup <c>reprocess:{documentId}</c>.</summary>
    public static JobRequest ReprocessDocumentJob(Guid documentId, string? fromStage, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification, fromStage is null
            ? JsonSerializer.Serialize(new { kind = ReprocessDocument, documentId })
            : JsonSerializer.Serialize(new { kind = ReprocessDocument, documentId, fromStage }))
        {
            DedupKey = $"reprocess:{documentId}",
        };

    /// <summary>Clears a source's deletion holds (<c>command.release-hold</c>, connectors §13.3); dedup <c>release-hold:{sourceId}</c>.</summary>
    public static JobRequest ReleaseHoldJob(Guid sourceId, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification, JsonSerializer.Serialize(new { kind = ReleaseHold, sourceId }))
        {
            DedupKey = $"release-hold:{sourceId}",
        };

    /// <summary>Confirms or rejects a run's deletion proposal (<c>command.confirm-deletion</c> / <c>reject-deletion</c>, connectors §13.3).</summary>
    public static JobRequest DeletionDecisionJob(bool confirm, Guid runId, string decidedBy, Guid departmentId, string classification) =>
        new(JobClasses.Command, departmentId, classification,
            JsonSerializer.Serialize(new { kind = confirm ? ConfirmDeletion : RejectDeletion, runId, decidedBy }))
        {
            DedupKey = $"deletion:{runId}",
            RunId = runId,
        };
}
