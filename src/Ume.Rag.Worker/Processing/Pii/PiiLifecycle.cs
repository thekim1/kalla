using System.Globalization;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Processing.Pii;

/// <summary>Event 5103 <c>rag.pii.flagged</c> (docs/security-events.md; processing design §9.5): numbers only, never values or text.</summary>
public static partial class PiiEvents
{
    [LoggerMessage(5103, LogLevel.Warning,
        "Personal data flagged on document {DocumentId} set {ChunkSetId} ({Classification}): {Types} {Counts}, source {SourceId}, run {RunId}, reflag {Reflag}",
        EventName = "rag.pii.flagged")]
    public static partial void Flagged(ILogger logger, Guid documentId, Guid chunkSetId, string classification, string types, string counts,
        Guid sourceId, Guid? runId, bool reflag);
}

/// <summary>
/// Where a set belongs: the collection's level decides blocking (only <c>public</c> collections are blocked) and the source's
/// <see cref="PiiMode"/> what blocks there.
/// </summary>
public sealed record PiiSwapContext(string CollectionClassification, Guid SourceId, Guid? RunId, PiiFlagRules Rules, PiiMode Mode = PiiMode.Hold);

/// <summary>
/// The personal-data lifecycle of processing design §9.5–§9.6 on top of <see cref="IChunkSetStore"/>: at the swap a
/// flagged or NER-pending set of a <c>public</c> collection that the document's clearance does not cover is swapped in
/// <c>held</c> (the old content stops being served, the new is not served); other collections never block. Clearing
/// releases the held set without re-processing and records the clearance; a later set is covered while no flagging count
/// rises above it, else it is re-flagged and held. A source in <see cref="PiiMode.Report"/> (Decisions table, 2026-11-01)
/// holds only for the personnummer and samordningsnummer rules (a clearance covers it while neither count rises); every
/// other flag, and a NER-pending set, goes in <c>live</c> and is reported (run item reason, event 5103). The sweep re-runs
/// <c>personal-data</c> for <c>pending</c> sets once NER is ready. Event 5103 is raised once per transition to flagged, and the
/// set's <c>PiiFlaggedAt</c> gets the same time unless it has one (#175). The engine's embed job decides the state with
/// <see cref="StateFor"/> inside its fenced swap, stamps <c>PiiFlaggedAt</c> there when <see cref="Flags"/> holds, and raises
/// the event with <see cref="RaiseIfFlagged"/> after the commit.
/// </summary>
public sealed class PiiLifecycle(IChunkSetStore store, ILoggerFactory loggers, TimeProvider clock)
{
    /// <summary>The run item reason of a flagged set (§9.5).</summary>
    public const string FlaggedReason = "pii.flagged";

    /// <summary>The run item reason of a set held while its names are unchecked (§9.6).</summary>
    public const string NerPendingReason = "pii.ner_pending";

    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    /// <summary>The types whose rule still holds a set of a <see cref="PiiMode.Report"/> source.</summary>
    private static readonly string[] HoldInReport = [PiiTypes.Personnummer, PiiTypes.Samordningsnummer];

    /// <summary>The state the set is swapped in as: <c>live</c> or <c>held</c>.</summary>
    public static string StateFor(ChunkSetRecord set, string collectionClassification, PiiMode mode, PiiClearance? clearance, PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(set);
        var counts = set.PiiCounts ?? new PiiCounts();
        if (collectionClassification != Classification.Public || !(set.PiiFlagged || counts.Ner == NerStatus.Pending))
        {
            return ChunkSetState.Live;
        }

        var held = mode == PiiMode.Report
            ? set.PiiFlagged && PiiFlagPolicy.Fired(counts, rules).Intersect(HoldInReport, StringComparer.Ordinal)
                .Any(type => counts.Count(type) > (clearance?.Counts.Count(type) ?? 0))
            : !PiiFlagPolicy.Covered(counts, clearance?.Counts, rules);
        return held ? ChunkSetState.Held : ChunkSetState.Live;
    }

    /// <summary>
    /// The run item's reason for a set swapped in as <paramref name="state"/> (§9.5): <c>pii.flagged</c> for a held flagged set
    /// and for a flagged set of a <c>public</c> collection that went live uncovered (a <see cref="PiiMode.Report"/> source),
    /// <c>pii.ner_pending</c> for a set held while its names are unchecked; null otherwise.
    /// </summary>
    public static string? ReasonFor(ChunkSetRecord set, string state, string collectionClassification, PiiClearance? clearance, PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (state == ChunkSetState.Held)
        {
            return set.PiiFlagged ? FlaggedReason : NerPendingReason;
        }

        return collectionClassification == Classification.Public && set.PiiFlagged
            && !PiiFlagPolicy.Covered(set.PiiCounts ?? new PiiCounts(), clearance?.Counts, rules)
            ? FlaggedReason
            : null;
    }

    /// <summary>A transition to flagged (§9.5): the set is flagged and the clearance does not cover it; event 5103 follows.</summary>
    public static bool Flags(ChunkSetRecord set, PiiClearance? clearance, PiiFlagRules rules)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.PiiFlagged && !PiiFlagPolicy.Covered(set.PiiCounts ?? new PiiCounts(), clearance?.Counts, rules);
    }

    public async Task<string> SwapAsync(Guid chunkSetId, PiiSwapContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var set = await store.GetAsync(chunkSetId, cancellationToken) ?? throw new InvalidOperationException("Unknown chunk set.");
        var clearance = await store.GetClearanceAsync(set.Key.DocumentId, cancellationToken);
        var state = StateFor(set, context.CollectionClassification, context.Mode, clearance, context.Rules);
        var now = clock.GetUtcNow();
        await store.SwapAsync(set.Id, state, now, cancellationToken);
        if (Flags(set, clearance, context.Rules))
        {
            await store.MarkFlaggedAsync(set.Id, now, cancellationToken);
        }

        RaiseIfFlagged(set, context, clearance);
        return state;
    }

    /// <summary>
    /// Event 5103 for a set that went in flagged and not covered by the clearance; the index writer calls it after its own
    /// fenced swap committed.
    /// </summary>
    public void RaiseIfFlagged(ChunkSetRecord set, PiiSwapContext context, PiiClearance? clearance)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        if (Flags(set, clearance, context.Rules))
        {
            Raise(set, context, reflag: clearance is not null);
        }
    }

    /// <summary>
    /// <c>clear-pii-flag</c> on the document (§9.5): records the clearance (= the held set's counts) and releases the held
    /// set. False when the document has no held set. The command's authorisation, reason and audit 6001 are the admin's.
    /// </summary>
    public async Task<bool> ClearAsync(Guid documentId, string clearedBy, CancellationToken cancellationToken)
    {
        var current = await store.GetCurrentAsync(documentId, cancellationToken);
        if (current is not { State: ChunkSetState.Held })
        {
            return false;
        }

        var now = clock.GetUtcNow();
        await store.SetClearanceAsync(documentId, new PiiClearance(now, clearedBy, current.PiiCounts ?? new PiiCounts()), cancellationToken);
        await store.SetStateAsync(current.Id, ChunkSetState.Live, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// The nightly sweep's part for <c>pending</c> sets (§9.6, engine design §2.3): re-runs <c>personal-data</c> on the stored
    /// chunks and settles the state by the source's mode. Sets whose NER is still not ready stay <c>pending</c> in the state they
    /// have (<c>held</c>, or <c>live</c> for a <see cref="PiiMode.Report"/> source). Returns how many settled.
    /// </summary>
    public async Task<int> SweepPendingAsync(PersonalDataStage stage, Func<ChunkSetRecord, CancellationToken, Task<(PersonalDataContext Data, PiiSwapContext Swap)>> contextOf,
        int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(contextOf);
        var settled = 0;
        foreach (var set in await store.PendingNerAsync(limit, cancellationToken))
        {
            var (data, swap) = await contextOf(set, cancellationToken);
            var chunks = await store.ChunksAsync(set.Id, cancellationToken);
            var draft = new ChunkSetDraft { Chunks = [.. chunks.Select(c => c.Draft)] };
            var (_, assessment) = await stage.RunAsync(draft, data, cancellationToken);
            if (assessment.Counts.Ner == NerStatus.Pending)
            {
                continue;
            }

            await store.UpdatePiiAsync(set.Id, assessment.Counts, assessment.Flagged, cancellationToken);
            var updated = set with { PiiCounts = assessment.Counts, PiiFlagged = assessment.Flagged };
            var clearance = await store.GetClearanceAsync(set.Key.DocumentId, cancellationToken);
            var now = clock.GetUtcNow();
            await store.SetStateAsync(set.Id, StateFor(updated, swap.CollectionClassification, swap.Mode, clearance, swap.Rules), now, cancellationToken);
            if (Flags(updated, clearance, swap.Rules))
            {
                await store.MarkFlaggedAsync(set.Id, now, cancellationToken);
                Raise(updated, swap, reflag: clearance is not null);
            }

            settled++;
        }

        return settled;
    }

    private void Raise(ChunkSetRecord set, PiiSwapContext context, bool reflag)
    {
        var counts = set.PiiCounts ?? new PiiCounts();
        var types = PiiFlagPolicy.Fired(counts, context.Rules);
        var numbers = string.Join(',', PiiTypes.All.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t}={counts.Count(t)}")));
        PiiEvents.Flagged(_security, set.Key.DocumentId, set.Id, set.Classification, string.Join(',', types), numbers, context.SourceId, context.RunId, reflag);
    }
}
