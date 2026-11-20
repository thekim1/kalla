namespace Kalla.Pipelines.Processing;

/// <summary>Whether the name check ran for a chunk set (processing design §9.3, §9.6).</summary>
public enum NerStatus
{
    /// <summary>Names were checked by the parser's <c>/v1/ner</c>.</summary>
    Done,

    /// <summary>Not needed: the names could not change the flag (§9.3).</summary>
    Skipped,

    /// <summary>Needed, but NER was not ready: the set is held in a <c>public</c> collection until the sweep checks it (§9.6).</summary>
    Pending,
}

/// <summary>
/// <c>ChunkSets."PiiCounts"</c> (processing design §9.4; data-model §2.2), serialized with <see cref="Json.ContractJson"/>:
/// numbers and fired type names only, never values. Written by the
/// <c>personal-data</c> stage for every set it produces and read by the admin for the PII list and by the index writer for
/// the flag and the clearance. <see cref="Name"/> counts distinct names; the others count matches.
/// </summary>
public sealed record PiiCounts
{
    public int Personnummer { get; init; }

    public int Samordningsnummer { get; init; }

    /// <summary>Every e-mail address, own domain and external.</summary>
    public int Email { get; init; }

    /// <summary>Addresses outside <c>Settings.PiiOwnDomains</c>.</summary>
    public int EmailExternal { get; init; }

    public int Phone { get; init; }

    public int Name { get; init; }

    public NerStatus Ner { get; init; } = NerStatus.Skipped;

    /// <summary>How many flag rules fired (§9.4).</summary>
    public int Rules { get; init; }

    /// <summary>
    /// The types whose rule fired (<see cref="PiiTypes"/> names, ordinal order; the list-like rule as <c>name</c>), as the
    /// <c>personal-data</c> stage decided them (Decisions table "M-2 open design calls", #217). Type names only, never values.
    /// </summary>
    public IReadOnlyList<string> TypesFired { get; init; } = [];

    /// <summary>The NER model as <c>name@revision</c> when names were checked.</summary>
    public string? Model { get; init; }

    /// <summary>The count of a type that can flag (<see cref="PiiTypes"/>).</summary>
    public int Count(string type) => type switch
    {
        PiiTypes.Personnummer => Personnummer,
        PiiTypes.Samordningsnummer => Samordningsnummer,
        PiiTypes.Email => Email,
        PiiTypes.EmailExternal => EmailExternal,
        PiiTypes.Phone => Phone,
        PiiTypes.Name => Name,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown personal-data type."),
    };
}

/// <summary>The personal-data types of <see cref="PiiCounts"/> (processing design §9.1, §9.4), as JSON member names.</summary>
public static class PiiTypes
{
    public const string Personnummer = "personnummer";
    public const string Samordningsnummer = "samordningsnummer";
    public const string Email = "email";
    public const string EmailExternal = "emailExternal";
    public const string Phone = "phone";
    public const string Name = "name";

    public static IReadOnlyList<string> All { get; } = [Personnummer, Samordningsnummer, Email, EmailExternal, Phone, Name];
}

/// <summary>How one type takes part in the flag (processing design §9.4); JSON <c>always</c>, <c>threshold</c>, <c>never</c> (<see cref="Json.ContractJson"/>).</summary>
public enum PiiFlagMode
{
    Always,
    Threshold,
    Never,
}

/// <summary>One type's rule: <see cref="PiiFlagMode.Always"/> fires on 1, <see cref="PiiFlagMode.Threshold"/> on <see cref="Min"/>.</summary>
public sealed record PiiTypeRule(PiiFlagMode Flag, int Min = 1);

/// <summary>
/// <c>Settings.PiiFlagRules</c> (processing design §9.4, answered §20.1 option a). Defaults: personnummer and
/// samordningsnummer always, an external e-mail address from 1; names, phones and own-domain e-mail only together, as
/// the list-like rule (≥ 5 distinct names and ≥ 5 phone or e-mail values: member lists, attendance, case lists).
/// </summary>
public sealed record PiiFlagRules
{
    public static PiiFlagRules Default { get; } = new();

    public IReadOnlyDictionary<string, PiiTypeRule> Types { get; init; } = new Dictionary<string, PiiTypeRule>(StringComparer.Ordinal)
    {
        [PiiTypes.Personnummer] = new(PiiFlagMode.Always),
        [PiiTypes.Samordningsnummer] = new(PiiFlagMode.Always),
        [PiiTypes.EmailExternal] = new(PiiFlagMode.Threshold, 1),
    };

    /// <summary>The list-like rule; null turns it off.</summary>
    public PiiListLikeRule? ListLike { get; init; } = new();
}

/// <summary>Names together with contact values: ≥ <see cref="MinNames"/> distinct names and ≥ <see cref="MinContacts"/> phone or e-mail values.</summary>
public sealed record PiiListLikeRule
{
    public int MinNames { get; init; } = 5;

    public int MinContacts { get; init; } = 5;
}
