namespace Ume.Rag.Domain.Entities;

/// <summary>What a setup test signs in to (identity design §19): rag-admin's own client now; Open WebUI's joins in its step.</summary>
public static class SetupTargets
{
    public const string RagAdmin = "rag-admin";

    public static IReadOnlyList<string> All { get; } = [RagAdmin];
}

/// <summary>
/// A setup session (<c>config."SetupSessions"</c>, identity design §19): given for a correct setup code, 30 minutes, bound to the
/// opening whose nonce it was given under. The cookie holds a random token; only its SHA-256 is stored. Short-lived rows outside
/// the settings row, so they are deleted without bumping the configuration version.
/// </summary>
public sealed class SetupSession
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the cookie's token.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The <c>Settings.SetupNonce</c> of the opening it belongs to; a new opening voids it.</summary>
    public required string OpeningNonce { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// One test sign-in of a setup session (<c>config."SetupTests"</c>, identity design §19, "How the test sign-in runs"): single use,
/// 10 minutes. <see cref="Protected"/> holds the candidate values, the PKCE verifier, the nonce and the provider's endpoints,
/// protected with Data Protection; <see cref="Result"/> what the callback found. No token is ever stored.
/// </summary>
public sealed class SetupTest
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }

    /// <summary>A <see cref="SetupTargets"/> value.</summary>
    public required string Target { get; set; }

    /// <summary>SHA-256 of the random marker in the authorization request's <c>state</c>.</summary>
    public required byte[] MarkerHash { get; set; }

    /// <summary>SHA-256 of the test cookie set in the browser that started the test; the callback must carry it.</summary>
    public required byte[] BrowserHash { get; set; }

    public required string Protected { get; set; }

    /// <summary>SHA-256 of the tested values without the secret; <c>complete</c> compares the submitted values with it.</summary>
    public required byte[] ValuesFingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the callback claimed it (single use); null until then.</summary>
    public DateTimeOffset? CallbackAt { get; set; }

    /// <summary>Null until the callback finished.</summary>
    public bool? Passed { get; set; }

    /// <summary>The refusal reason when <see cref="Passed"/> is false.</summary>
    public string? Reason { get; set; }

    /// <summary>The signed-in person as the checks saw them (<c>jsonb</c>): subject, name, roles, groups, departments, teams, platform admin.</summary>
    public string? Result { get; set; }
}
