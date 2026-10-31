using System.Runtime.CompilerServices;

namespace Ume.Rag.Domain.Access;

/// <summary>An assistant's published version as the api sees it (<c>config."PublishedAssistantVersions"</c>; never a draft).</summary>
public sealed record PublishedVersionEntry(Guid Id, int Number, string GatewayModel, IReadOnlyList<Guid> CollectionIds);

/// <summary>An audience row (<c>AssistantAudiences</c>): <c>client</c> (P1), <c>department</c>/<c>group</c> (P2).</summary>
public sealed record AudienceEntry(string Kind, Guid? SubjectId, string? GroupValue);

/// <summary>An assistant with its access fields, its published version (null: none yet) and its audiences.</summary>
public sealed record AssistantEntry(
    Guid Id, string Slug, string Name, string Classification, string HistoryMode, string State, Guid DepartmentId, Guid? OwnerTeamId,
    PublishedVersionEntry? Published, IReadOnlyList<AudienceEntry> Audiences);

/// <summary>A collection: level, profile, settings, publication and deletion (data-model §2.1).</summary>
public sealed record CollectionEntry(
    Guid Id, string Slug, string Name, Guid DepartmentId, string Classification, Guid EmbeddingProfileId, string RetrievalSettings,
    DateTimeOffset? PublishedAt, bool Deleted)
{
    /// <summary>Live for the data plane: not deleted and published (knowledge design §4.5).</summary>
    public bool IsLive => !Deleted && PublishedAt is not null;
}

/// <summary>An embedding profile (data-model §2.1).</summary>
public sealed record EmbeddingProfileEntry(
    Guid Id, string Name, string GatewayModel, int Dimensions, Residency MaxResidency, string State, string? CanaryText, bool RequestDimensions = false)
{
    /// <summary>The <c>dimensions</c> to send on an embedding call (#142), or null when the profile must not send it.</summary>
    public int? DimensionsParameter => RequestDimensions ? Dimensions : null;
}

/// <summary>A level's data-protection policy fields the policy needs (identity §4.3 step 9).</summary>
public sealed record LevelPolicyEntry(string Classification, IReadOnlyList<string> AllowedHistoryModes, bool ClientsWithoutHistoryOnly);

/// <summary>
/// The config snapshot decisions are made on (identity design §4.2, data-model §5): everything the policy and the
/// knowledge role read from <c>config</c>, at one version. Built once per config version and never changed.
/// </summary>
public sealed record PolicySnapshot(
    long Version,
    IReadOnlyDictionary<Guid, ClientEntry> Clients,
    IReadOnlyDictionary<Guid, AssistantEntry> Assistants,
    IReadOnlyDictionary<Guid, CollectionEntry> Collections,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> SourcesByCollection,
    IReadOnlyDictionary<Guid, EmbeddingProfileEntry> EmbeddingProfiles,
    IReadOnlyDictionary<string, LevelPolicyEntry> LevelPolicies,
    IReadOnlyDictionary<Guid, string> DepartmentNames,
    string? RetrievalSettings)
{
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<Guid, CollectionEntry>, IReadOnlyDictionary<string, CollectionEntry>> BySlug = new();

    /// <summary>
    /// Collections by slug (slugs are unique, data-model §2.1), built once per <see cref="Collections"/> instance: keyed
    /// on the dictionary rather than stored in the record, so a <c>with</c> copy that replaces the collections never
    /// carries a stale index.
    /// </summary>
    public IReadOnlyDictionary<string, CollectionEntry> CollectionsBySlug =>
        BySlug.GetValue(Collections, static c => c.Values.ToDictionary(e => e.Slug, StringComparer.Ordinal));

    /// <summary>An empty snapshot (before the first load; every decision refuses).</summary>
    public static PolicySnapshot Empty { get; } = new(-1, new Dictionary<Guid, ClientEntry>(), new Dictionary<Guid, AssistantEntry>(),
        new Dictionary<Guid, CollectionEntry>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, EmbeddingProfileEntry>(),
        new Dictionary<string, LevelPolicyEntry>(StringComparer.Ordinal), new Dictionary<Guid, string>(), null);
}
