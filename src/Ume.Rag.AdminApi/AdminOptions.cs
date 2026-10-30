using System.ComponentModel.DataAnnotations;

namespace Ume.Rag.AdminApi;

/// <summary>Bound from the <c>Admin</c> configuration section (adapted from the gateway's; identity design §1.4).</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Requests per minute per signed-in user (or client address before sign-in).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// Hours a session may last from sign-in, however often it is extended. After that the user signs in at the identity
    /// provider again (which also picks up role changes made there).
    /// </summary>
    [Range(1, 24)]
    public int MaxSessionHours { get; set; } = 12;
}
