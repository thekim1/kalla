namespace Ume.Rag.Domain.Ports;

/// <summary>
/// The write-once blob store seam (performance pass §8, data-model §9). Stream A fills it in P0 checklist #21 with the
/// file-system implementation in <c>Ume.Rag.Infrastructure/Blobs</c>.
/// </summary>
#pragma warning disable CA1040 // Empty until stream A adds the members (Stage 0 placeholder).
public interface IBlobStore;
#pragma warning restore CA1040
