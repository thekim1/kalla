using System.Globalization;
using System.Text;

namespace Kalla.Worker.Processing.Detect;

/// <summary>
/// The worker's own language detector (processing design §2): on the first 5,000 characters, the share of words that
/// are Swedish function words against the share that are English ones (<c>stopwords-sv.txt</c>, <c>stopwords-en.txt</c>,
/// embedded). A language wins when its words are at least <see cref="MinShare"/> of all words and twice the other's;
/// otherwise <c>und</c>, as for texts under <see cref="MinWords"/> words.
/// </summary>
public static class LanguageDetector
{
    public const string Swedish = "sv";
    public const string English = "en";
    public const string Undetermined = "und";
    public const int SampleChars = 5_000;
    public const int MinWords = 8;
    public const double MinShare = 0.08;

    private static readonly Lazy<(HashSet<string> Sv, HashSet<string> En)> Lists = new(Load);

    public static string Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sample = text.Length > SampleChars ? text[..SampleChars] : text;
        var (sv, en) = Lists.Value;
        int words = 0, swedish = 0, english = 0;
        foreach (var word in Words(sample))
        {
            words++;
            if (sv.Contains(word))
            {
                swedish++;
            }
            else if (en.Contains(word))
            {
                english++;
            }
        }

        if (words < MinWords)
        {
            return Undetermined;
        }

        if (swedish >= MinShare * words && swedish >= 2 * english)
        {
            return Swedish;
        }

        return english >= MinShare * words && english >= 2 * swedish ? English : Undetermined;
    }

    /// <summary>The primary subtag of an HTML <c>lang</c> value when it is <c>sv</c> or <c>en</c>, else null.</summary>
    public static string? FromLangAttribute(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }

        var primary = lang.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary is Swedish or English ? primary : null;
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c) || (c is '.' or '\'' && word.Length > 0))
            {
                word.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString().TrimEnd('.', '\'');
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString().TrimEnd('.', '\'');
        }
    }

    private static (HashSet<string>, HashSet<string>) Load() => (Read("stopwords-sv.txt"), Read("stopwords-en.txt"));

    private static HashSet<string> Read(string name)
    {
        var assembly = typeof(LanguageDetector).Assembly;
        using var stream = assembly.GetManifestResourceStream("Kalla.Worker.Processing.Detect." + name)
            ?? throw new InvalidOperationException($"Embedded {name} not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                words.Add(trimmed.TrimEnd('.'));
            }
        }

        return words;
    }
}

/// <summary>
/// Text decoding for the worker-side parsers (processing design §3.7, parser design §3): a BOM wins, then strict UTF-8,
/// then Windows-1252.
/// </summary>
public static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252.GetString(bytes);
        }
    }

    private static Encoding Windows1252
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }
}
