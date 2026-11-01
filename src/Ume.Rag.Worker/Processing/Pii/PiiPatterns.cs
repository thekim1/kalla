using System.Globalization;
using System.Text.RegularExpressions;
using Ume.Rag.Pipelines.Processing;

namespace Ume.Rag.Worker.Processing.Pii;

/// <summary>One pattern match: the type and a normalised value used for distinct counting only (never stored, logged or traced).</summary>
public readonly record struct PiiMatch(string Type, int Start, int Length, string Value);

/// <summary>
/// The .NET personal-data patterns of processing design §9.1 with the false-positive rules of §9.2: personnummer and
/// samordningsnummer (valid date with the century rule, Luhn, a keyword within 30 characters before an unseparated
/// 10-digit form, negative keywords within 20), e-mail (plain and obfuscated), Swedish and international phone numbers
/// (7–9 national significant digits, mobile <c>7[02369]</c> with 9, not a personnummer, not after payment keywords).
/// No match starts or ends inside an alphanumeric token (a letter or digit directly before or after, so a digit run in
/// a hex id or hash is no match); a phone number also not inside a hyphen-joined one (a GUID's groups).
/// Source-generated regular expressions; deterministic for a fixed <see cref="TimeProvider"/> (the century rule).
/// </summary>
public sealed partial class PiiPatterns(TimeProvider clock)
{
    private static readonly string[] PersonnummerKeywords = ["personnummer", "pers.nr", "pnr", "personnr", "födelsenummer"];

    private static readonly string[] NegativeKeywords =
    [
        "ocr", "bankgiro", "plusgiro", "faktura", "fakturanr", "ärendenr", "dnr", "diarienr", "diarienummer", "kundnr", "org.nr", "organisationsnummer",
    ];

    private static readonly string[] PhoneNegativeKeywords = ["bankgiro", "plusgiro", "bg", "pg", "org.nr", "ocr", "fakturanr"];

    public IReadOnlyList<PiiMatch> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<PiiMatch>();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        foreach (Match m in Personnummer().Matches(text))
        {
            if (Identity(m, text, today) is { } match)
            {
                matches.Add(match);
            }
        }

        foreach (Match m in Email().Matches(text))
        {
            matches.Add(new PiiMatch(PiiTypes.Email, m.Index, m.Length, m.Value.ToLowerInvariant()));
        }

        foreach (Match m in ObfuscatedEmail().Matches(text))
        {
            if (!Overlaps(matches, m.Index, m.Length))
            {
                matches.Add(new PiiMatch(PiiTypes.Email, m.Index, m.Length, (m.Groups["local"].Value + "@" + m.Groups["domain"].Value).ToLowerInvariant()));
            }
        }

        foreach (var regex in new[] { SwedishPhone(), InternationalPhone() })
        {
            foreach (Match m in regex.Matches(text))
            {
                if (Phone(m, text, matches) is { } phone)
                {
                    matches.Add(phone);
                }
            }
        }

        return matches;
    }

    /// <summary>The domain of an e-mail value.</summary>
    public static string Domain(string email) => email[(email.LastIndexOf('@') + 1)..];

    private static PiiMatch? Identity(Match m, string text, DateOnly today)
    {
        var yy = int.Parse(m.Groups["yy"].Value, CultureInfo.InvariantCulture);
        var mm = int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture);
        var dd = int.Parse(m.Groups["dd"].Value, CultureInfo.InvariantCulture);
        var sep = m.Groups["sep"].Value;
        var hasCentury = m.Groups["c"].Success;
        if (mm > 12)
        {
            return null; // digits 3–4 ≥ 20: an organisationsnummer
        }

        var type = PiiTypes.Personnummer;
        if (dd is >= 60 and <= 91)
        {
            // Samordningsnummer: day + 60. Skatteverket's test numbers also use month 00 and day 60 (unknown month or day).
            type = PiiTypes.Samordningsnummer;
            dd -= 60;
        }
        else if (mm == 0)
        {
            return null;
        }

        int year;
        if (hasCentury)
        {
            year = int.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture) * 100 + yy;
        }
        else
        {
            // The latest century with a date ≤ today; minus 100 years with '+' (age 100 or more).
            year = (today.Year / 100 * 100) + yy;
            if (year > today.Year || (year == today.Year && (mm > today.Month || (mm == today.Month && dd > today.Day))))
            {
                year -= 100;
            }

            if (sep == "+")
            {
                year -= 100;
            }
        }

        var unknownPart = type == PiiTypes.Samordningsnummer && (mm == 0 || dd == 0);
        if (!unknownPart && (dd < 1 || dd > DateTime.DaysInMonth(year, mm)))
        {
            return null;
        }

        var digits = m.Groups["yy"].Value + m.Groups["mm"].Value + m.Groups["dd"].Value + m.Groups["nnn"].Value + m.Groups["k"].Value;
        if (!Luhn(digits))
        {
            return null;
        }

        var before = Before(text, m.Index, 30);
        if (!hasCentury && sep.Length == 0 && !PersonnummerKeywords.Any(k => before.Contains(k, StringComparison.Ordinal)))
        {
            return null; // an unseparated 10-digit number needs a keyword (mobile numbers look alike)
        }

        if (NegativeKeywords.Any(k => ContainsWord(Before(text, m.Index, 20), k)))
        {
            return null;
        }

        return new PiiMatch(type, m.Index, m.Length, digits);
    }

    private static PiiMatch? Phone(Match m, string text, List<PiiMatch> matches)
    {
        if (Overlaps(matches, m.Index, m.Length))
        {
            return null; // not a personnummer (or an earlier phone match)
        }

        var digits = new string([.. m.Value.Where(char.IsDigit)]);
        string value;
        if (m.Value.StartsWith('+') && !m.Value.StartsWith("+46", StringComparison.Ordinal))
        {
            if (digits.Length is < 7 or > 15)
            {
                return null;
            }

            value = "+" + digits;
        }
        else
        {
            // National significant digits: after +46, 0046 or the trunk 0, and after a "(0)" written after the country code.
            var national = m.Value.StartsWith("+46", StringComparison.Ordinal) ? digits[2..]
                : m.Value.StartsWith("0046", StringComparison.Ordinal) ? digits[4..]
                : digits[1..];
            if (national.StartsWith('0'))
            {
                national = national[1..];
            }

            var mobile = national.Length >= 2 && national[0] == '7' && "02369".Contains(national[1], StringComparison.Ordinal);
            if (national.Length is < 7 or > 9 || (mobile && national.Length != 9))
            {
                return null;
            }

            value = national;
        }

        if (PhoneNegativeKeywords.Any(k => ContainsWord(Before(text, m.Index, 20), k)))
        {
            return null;
        }

        return new PiiMatch(PiiTypes.Phone, m.Index, m.Length, value);
    }

    private static bool Overlaps(List<PiiMatch> matches, int start, int length) =>
        matches.Any(x => start < x.Start + x.Length && x.Start < start + length);

    private static string Before(string text, int index, int chars) =>
        text[Math.Max(0, index - chars)..index].ToLowerInvariant();

    private static bool ContainsWord(string haystack, string word)
    {
        var at = haystack.IndexOf(word, StringComparison.Ordinal);
        while (at >= 0)
        {
            var before = at == 0 || !char.IsLetter(haystack[at - 1]);
            var end = at + word.Length;
            var after = end >= haystack.Length || !char.IsLetter(haystack[end]);
            if (before && after)
            {
                return true;
            }

            at = haystack.IndexOf(word, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Luhn over the 10 digits <c>yymmddnnnk</c>.</summary>
    internal static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[i] - '0';
            if (i % 2 == digits.Length % 2)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}-])(?<c>18|19|20)?(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?<sep>[-+]?)(?<nnn>\d{3})(?<k>\d)(?![\p{L}\p{N}-])", RegexOptions.CultureInvariant)]
    private static partial Regex Personnummer();

    [GeneratedRegex(@"(?<![\p{L}\p{N}._%+-])[\p{L}\p{N}._%+-]{1,64}@[\p{L}\p{N}.-]{1,253}\.\p{L}{2,24}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<local>[\p{L}\p{N}._%+-]{1,64})\s*(?:\[at\]|\(at\)|\s+snabel-a\s+)\s*(?<domain>[\p{L}\p{N}-]{1,63}(?:\.[\p{L}\p{N}-]{1,63})*\.\p{L}{2,24})(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ObfuscatedEmail();

    [GeneratedRegex(@"(?<![\p{L}\p{N}+]|[\p{L}\p{N}]-)(?:\+46|0046|0)[\s-]?\(?0?\)?[1-9]\d{0,3}[\s-]?\d{2,3}(?:[\s-]?\d{2,3}){1,3}(?![\p{L}\p{N}]|-[\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex SwedishPhone();

    [GeneratedRegex(@"(?<![\p{L}\p{N}+]|[\p{L}\p{N}]-)\+(?!46)\d{1,3}[\s\d-]{6,16}(?<=\d)(?![\p{L}\p{N}]|-[\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex InternationalPhone();
}
