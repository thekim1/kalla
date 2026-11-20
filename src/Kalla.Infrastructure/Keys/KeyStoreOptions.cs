using System.ComponentModel.DataAnnotations;

namespace Kalla.Infrastructure.Keys;

/// <summary>Key lookup settings, mirroring the gateway's <c>Gateway:KeyCacheSeconds</c> (identity design §6.2).</summary>
public sealed class KeyStoreOptions
{
    public const string SectionName = "Api";

    /// <summary>How long a looked-up key (or an unknown hash) is served from memory before a background refresh; 0 = no cache.</summary>
    [Range(0, 300)]
    public int KeyCacheSeconds { get; set; } = 30;
}
