namespace Ume.Rag.Domain.Ports;

/// <summary>
/// The job queue seam (performance pass §8, data-model §4). Stream A fills it in P0 checklist #21 with the Postgres
/// implementation in <c>Ume.Rag.Infrastructure/Jobs</c>.
/// </summary>
#pragma warning disable CA1040 // Empty until stream A adds the members (Stage 0 placeholder).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a job queue; the name is fixed by the design (data-model §4).")]
public interface IJobQueue;
#pragma warning restore CA1040
