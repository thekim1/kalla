namespace Ume.Rag.Domain;

/// <summary>
/// Where a model runs, as the gateway's <c>DataResidency</c> (gateway design §3.1). The order is the order of the ceiling,
/// <c>OnPrem &lt; Eu &lt; External</c>; code relies on it, and a test pins it.
/// </summary>
public enum Residency
{
    OnPrem = 0,
    Eu = 1,
    External = 2,
}

/// <summary>The <c>x-ume-max-residency</c> / <c>x-ume-residency</c> values (gateway design §3.1, identity design §4.7).</summary>
public static class ResidencyCeiling
{
    /// <summary>
    /// Parses one of <c>OnPrem</c>, <c>Eu</c>, <c>External</c>, case-insensitive, trimmed. No aliases and no numbers
    /// (<c>Enum.TryParse</c> would accept <c>"1"</c>), so a hand-written switch; anything else is refused.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> value, out Residency residency)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("OnPrem", StringComparison.OrdinalIgnoreCase))
        {
            residency = Residency.OnPrem;
            return true;
        }

        if (trimmed.Equals("Eu", StringComparison.OrdinalIgnoreCase))
        {
            residency = Residency.Eu;
            return true;
        }

        if (trimmed.Equals("External", StringComparison.OrdinalIgnoreCase))
        {
            residency = Residency.External;
            return true;
        }

        residency = default;
        return false;
    }

    /// <summary>The header value (<c>OnPrem</c>, <c>Eu</c>, <c>External</c>).</summary>
    public static string ToHeader(Residency residency) => residency switch
    {
        Residency.OnPrem => "OnPrem",
        Residency.Eu => "Eu",
        Residency.External => "External",
        _ => throw new ArgumentOutOfRangeException(nameof(residency), residency, null),
    };

    /// <summary>The ceiling a level runs at (identity design §4.7): public External, internal Eu, sensitive OnPrem.</summary>
    public static Residency ForLevel(string classification) => classification switch
    {
        Classification.Public => Residency.External,
        Classification.Internal => Residency.Eu,
        Classification.Sensitive => Residency.OnPrem,
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown classification."),
    };
}
