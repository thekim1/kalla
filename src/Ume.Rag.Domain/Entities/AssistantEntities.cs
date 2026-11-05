using System.Text.Json;

namespace Ume.Rag.Domain.Entities;

// The serving tables of config (data-model §2.1; simplification pass §15): assistants with their versions and audiences,
// and the gateway model mirror (admin design §4.10). Values the design marks P2 are left out of the CHECK constraints
// until P2 adds them.

/// <summary>Assistant states (data-model §2.1).</summary>
public static class AssistantStates
{
    public const string Active = "active";
    public const string Archived = "archived";

    public static IReadOnlyList<string> All { get; } = [Active, Archived];
}

/// <summary>
/// An assistant, served as an OpenAI model (<c>config."Assistants"</c>, data-model §2.1): identity, owner, audiences,
/// retention and the version pointers. The versioned settings are in <see cref="AssistantVersion"/>; chat reads only
/// the published one, through the view <c>config."PublishedAssistantVersions"</c>. There is no mode column: the mode is
/// derived from the published version's skills (P2).
/// </summary>
public sealed class Assistant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The model name clients see.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The contact owner: a person or a functional mailbox (chat design §3.1).</summary>
    public string? ContactName { get; set; }

    public string? ContactEmail { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? OwnerTeamId { get; set; }
    public required string Classification { get; set; }
    public string HistoryMode { get; set; } = HistoryModes.None;
    public int RetentionDays { get; set; } = 90;
    public string[] AllowedOrigins { get; set; } = [];
    public string State { get; set; } = AssistantStates.Active;

    /// <summary>The version chat serves; null = never published (not listed).</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>The one open draft, if any.</summary>
    public Guid? DraftVersionId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Postgres <c>xmin</c>, the concurrency token of an editable aggregate root (data-model §2.1).</summary>
    public uint Version { get; set; }
}

/// <summary>Assistant version states (data-model §2.1).</summary>
public static class AssistantVersionStates
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } = [Draft, Published, Superseded];
}

/// <summary>
/// A version of an assistant's settings (<c>config."AssistantVersions"</c>, data-model §2.1): at most one open draft and
/// one published version per assistant; published and superseded rows are immutable. A draft carries its own stamps and
/// <c>xmin</c> token, so editing it never touches <see cref="Assistant"/>, and its rows do not bump the config version
/// (data-model §5). Collections are linked by id with optional topics (a self-contained snapshot, §12.24). Deferred and
/// left out: <c>Skills</c> (P2 tools) and <c>TestRunId</c> (P2 <c>assistant-tests</c> runs).
/// </summary>
public sealed class AssistantVersion
{
    /// <summary>The most collections a version links (data-model §2.1).</summary>
    public const int MaxCollections = 20;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AssistantId { get; set; }
    public int Number { get; set; }
    public string State { get; set; } = AssistantVersionStates.Draft;

    /// <summary>The answer model; validated against the gateway's <c>/v1/models</c> at the level's ceiling. Required once published.</summary>
    public string? GatewayModel { get; set; }

    /// <summary>The instructions.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Condense, retrieval, history and answer steps (<c>jsonb</c>, chat design §5–§6, §10.3, §15.2).</summary>
    public string QuerySteps { get; set; } = "{}";

    /// <summary>Not-found texts and contact links (<c>jsonb</c>, chat design §10.1); empty texts fall back to <c>Settings.Chat</c>.</summary>
    public string NotFound { get; set; } = "{}";

    /// <summary>
    /// The direct collections (≤ <see cref="MaxCollections"/>) with their topics (<c>jsonb</c>), the assistant's only knowledge;
    /// no foreign key.
    /// </summary>
    public IReadOnlyList<CollectionLink> CollectionLinks { get; set; } = [];

    public IReadOnlyList<Guid> LinkedCollectionIds() => CollectionLink.Ids(CollectionLinks);

    public Guid? RestoredFromVersionId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Postgres <c>xmin</c>: the draft's own concurrency token (admin design §4.3).</summary>
    public uint Version { get; set; }
}

/// <summary>
/// A collection an assistant version links (<c>AssistantVersions.CollectionLinks</c>, data-model §2.1): retrieval keeps the
/// collection's chunks that carry any of <see cref="Tags"/> (topics, in the chunk tag format of processing design §7); no
/// tags = the whole collection.
/// </summary>
public sealed record CollectionLink(Guid CollectionId, IReadOnlyList<string> Tags)
{
    /// <summary>The most topics a link carries (data-model §2.1; checked by the application).</summary>
    public const int MaxTags = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];

    public static CollectionLink Whole(Guid collectionId) => new(collectionId, []);

    public static IReadOnlyList<Guid> Ids(IEnumerable<CollectionLink> links) => [.. links.Select(l => l.CollectionId)];

    /// <summary>The stored form: <c>[{ "collectionId": uuid, "tags": [string] }]</c>.</summary>
    public static string Serialize(IEnumerable<CollectionLink> links) => JsonSerializer.Serialize(links, Json);

    public static IReadOnlyList<CollectionLink> ParseList(string json) => JsonSerializer.Deserialize<CollectionLink[]>(json, Json) ?? [];

    public bool Equals(CollectionLink? other) => other is not null && CollectionId == other.CollectionId && Tags.SequenceEqual(other.Tags);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CollectionId);
        foreach (var tag in Tags)
        {
            hash.Add(tag);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Audience kinds. P1 uses <c>client</c> only (data-model §2.1); <c>department</c> and <c>group</c> come later.</summary>
public static class AssistantAudienceKinds
{
    public const string Client = "client";

    /// <summary>The kinds the schema accepts in P1.</summary>
    public static IReadOnlyList<string> All { get; } = [Client];
}

/// <summary>
/// Who may use an assistant (<c>config."AssistantAudiences"</c>, data-model §2.1): a client or department id, or a
/// group value. Saved through its <see cref="Assistant"/>.
/// </summary>
public sealed class AssistantAudience
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AssistantId { get; set; }
    public string Kind { get; set; } = AssistantAudienceKinds.Client;

    /// <summary>The client or department id; null for a group.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>The group claim value; set for a group only (identity design §2.2).</summary>
    public string? GroupValue { get; set; }
}

/// <summary>Gateway model catalogue kinds (data-model §2.1).</summary>
public static class GatewayModelKinds
{
    public const string Model = "model";
    public const string Route = "route";

    public static IReadOnlyList<string> All { get; } = [Model, Route];
}

/// <summary>
/// What a gateway model row serves (data-model §2.1): the catalogue's <c>ModelKind</c> of the deployment; manual route rows
/// are <c>chat</c>. Only chat rows may be an assistant's model (V34) or a platform model setting (V32).
/// </summary>
public static class GatewayModelModelKinds
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";

    public static IReadOnlyList<string> All { get; } = [Chat, Embedding];
}

/// <summary>Where a gateway model row or one of its values came from (data-model §2.1).</summary>
public static class GatewayModelSources
{
    public const string Catalog = "catalog";
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Catalog, Manual];
}

/// <summary>
/// The gateway's model catalogue, synced every 5 minutes (<c>config."GatewayModels"</c>, data-model §2.1, admin design
/// §4.10). A route alias carries its targets' residencies and is servable at a ceiling when any target is within it.
/// </summary>
public sealed class GatewayModel
{
    public required string Name { get; set; }
    public string Kind { get; set; } = GatewayModelKinds.Model;

    /// <summary><c>chat</c> or <c>embedding</c> (<see cref="GatewayModelModelKinds"/>).</summary>
    public string ModelKind { get; set; } = GatewayModelModelKinds.Chat;

    /// <summary>The <c>x-ume-residency</c> values (<c>OnPrem</c>, <c>Eu</c>, <c>External</c>) the model or route's targets run at.</summary>
    public string[] Residencies { get; set; } = [];

    public string Source { get; set; } = GatewayModelSources.Catalog;
    public bool SupportsTools { get; set; }
    public string SupportsToolsSource { get; set; } = GatewayModelSources.Manual;
    public int? ContextWindow { get; set; }
    public decimal? InputPerMillionUsd { get; set; }
    public decimal? CachedInputPerMillionUsd { get; set; }
    public decimal? OutputPerMillionUsd { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
}
