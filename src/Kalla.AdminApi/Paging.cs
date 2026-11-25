namespace Kalla.AdminApi;

/// <summary>
/// The page size of a keyset list (admin design §4.1): <c>limit</c> from 1 to <see cref="MaxLimit"/>, the endpoint's default
/// when it is missing; a smaller or larger value is clamped, not refused.
/// </summary>
public static class Keyset
{
    /// <summary>The largest page any admin list returns.</summary>
    public const int MaxLimit = 200;

    public static int Take(int? limit, int fallback = 50) => Math.Clamp(limit ?? fallback, 1, MaxLimit);
}

/// <summary>A keyset page (admin design §4.1): the items and the <c>after</c> value of the next page, null at the end.</summary>
public sealed record KeysetPage<T, TKey>(IReadOnlyList<T> Items, TKey Next);

/// <summary>Builds a <see cref="KeysetPage{T, TKey}"/> from rows read with <c>Take(take + 1)</c>.</summary>
public static class KeysetPage
{
    /// <summary>
    /// The first <paramref name="take"/> rows and, when the query found one more, the key of the last row returned (the
    /// next page continues after it); null at the end. Works on the materialized rows, so the query keeps its
    /// <c>Take(take + 1)</c> in SQL.
    /// </summary>
    public static KeysetPage<T, TKey?> From<T, TKey>(IReadOnlyList<T> rows, int take, Func<T, TKey> key)
        where TKey : struct
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);
        return new KeysetPage<T, TKey?>([.. rows.Take(take)], rows.Count > take ? key(rows[take - 1]) : null);
    }
}

/// <summary><c>page</c> (from 1) and <c>pageSize</c> (≤ 200, default 50), and <c>q</c> (admin design §4.1).</summary>
/// <remarks>
/// Properties, not a positional record: the request delegate generator emits <c>typeof(string?)</c> for a constructor
/// parameter of a nullable reference type, which does not compile (Kalla.AdminApi.csproj, EnableRequestDelegateGenerator).
/// </remarks>
public sealed record PageQuery
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }

    public string? Q { get; init; }

    public int Take => Keyset.Take(PageSize);

    public int Skip => (Math.Max(Page ?? 1, 1) - 1) * Take;
}

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

/// <summary><c>ILIKE</c> patterns from user text: <c>%</c>, <c>_</c> and the escape character match themselves.</summary>
public static class Like
{
    public const string EscapeCharacter = "\\";

    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    }

    /// <summary>The pattern that finds <paramref name="text"/> anywhere (<c>%text%</c>), escaped; used with <see cref="EscapeCharacter"/>.</summary>
    public static string Contains(string text) => $"%{Escape(text)}%";
}
