namespace Kalla.Domain;

/// <summary>
/// The three data levels (plan §8a). Stored as lower-case strings in <c>varchar(20)</c> columns with a CHECK constraint
/// (data-model §2), so the values here are the column values.
/// </summary>
public static class Classification
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Sensitive = "sensitive";

    public static IReadOnlyList<string> All { get; } = [Public, Internal, Sensitive];

    public static bool IsValid(string? value) => value is Public or Internal or Sensitive;
}
