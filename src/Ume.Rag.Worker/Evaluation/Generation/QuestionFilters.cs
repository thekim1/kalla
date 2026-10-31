using System.Globalization;
using System.Text;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Evaluation.Generation;

/// <summary>Why a generated candidate was dropped (logged per chunk, never with text).</summary>
public static class FilterReasons
{
    public const string Format = "format";
    public const string Language = "rules.language";
    public const string Length = "rules.length";
    public const string QuestionMark = "rules.question_mark";
    public const string Deictic = "rules.deictic";
    public const string PersonalData = "rules.personal_data";
    public const string AnswerInQuestion = "leakage.answer";
    public const string CopiedFromText = "leakage.copied";
    public const string Judge = "judge";
    public const string Duplicate = "duplicate";
}

/// <summary>The judge's three ratings (1–5) of a candidate (processing design §13.1 filter 3).</summary>
public sealed record JudgeScores(int Answerable, int Understandable, int Specific);

/// <summary>
/// The filters of processing design §13.1, cheap first: (1) rules: Swedish (the §2 detector), 6–25 words, ends with
/// <c>?</c>, no deictic phrase, no §9.1 pattern hit; (2) answer leakage: the expected answer is not in the question and at
/// most 0.8 of the question's content words are found verbatim in the chunk; (3) the judge's ratings all ≥ 4;
/// (4) near-duplicates: cosine ≥ 0.92 to a kept question. Retrieval is never consulted (rule 5).
/// </summary>
public sealed class QuestionFilters(PiiPatterns patterns)
{
    public const int MinWords = 6;
    public const int MaxWords = 25;
    public const double MaxCopiedShare = 0.8;
    public const int MinJudgeScore = 4;
    public const double DuplicateCosine = 0.92;

    private static readonly string[] DeicticWords = ["texten", "dokumentet", "ovan", "nedan"];
    private static readonly string[][] DeicticPhrases = [["detta", "avsnitt"]];
    private static readonly Lazy<HashSet<string>> FunctionWords = new(LoadFunctionWords);

    /// <summary>Filter 1; null when the question passes.</summary>
    public string? Rules(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var words = Words(question);

        // The detector needs 8 words: a shorter question is refused only when it reads as English.
        var language = LanguageDetector.Detect(question);
        if (language == LanguageDetector.English || (words.Count >= LanguageDetector.MinWords && language != LanguageDetector.Swedish))
        {
            return FilterReasons.Language;
        }

        if (words.Count is < MinWords or > MaxWords)
        {
            return FilterReasons.Length;
        }

        if (!question.TrimEnd().EndsWith('?'))
        {
            return FilterReasons.QuestionMark;
        }

        if (words.Any(w => DeicticWords.Contains(w, StringComparer.Ordinal)) || DeicticPhrases.Any(p => ContainsPhrase(words, p)))
        {
            return FilterReasons.Deictic;
        }

        return patterns.Scan(question).Count > 0 ? FilterReasons.PersonalData : null;
    }

    /// <summary>Filter 2; null when the question passes.</summary>
    public static string? Leakage(string question, string answer, string chunkText)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(chunkText);
        var questionWords = Words(question);
        var answerWords = Words(answer);
        if (answerWords.Count > 0 && (" " + string.Join(' ', questionWords) + " ").Contains(" " + string.Join(' ', answerWords) + " ", StringComparison.Ordinal))
        {
            return FilterReasons.AnswerInQuestion;
        }

        return CopiedShare(question, chunkText) > MaxCopiedShare ? FilterReasons.CopiedFromText : null;
    }

    /// <summary>Share of the question's distinct content words (not Swedish function words) found verbatim in <paramref name="text"/>.</summary>
    public static double CopiedShare(string question, string text)
    {
        var content = ContentWords(question);
        if (content.Count == 0)
        {
            return 0;
        }

        var found = Words(text).ToHashSet(StringComparer.Ordinal);
        return (double)content.Count(found.Contains) / content.Count;
    }

    /// <summary>Filter 3.</summary>
    public static bool Passes(JudgeScores scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        return scores.Answerable >= MinJudgeScore && scores.Understandable >= MinJudgeScore && scores.Specific >= MinJudgeScore;
    }

    /// <summary>Filter 4: <paramref name="vector"/> is within <see cref="DuplicateCosine"/> of one of <paramref name="kept"/>.</summary>
    public static bool IsDuplicate(float[] vector, IEnumerable<float[]> kept)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(kept);
        return kept.Any(k => Cosine(vector, k) >= DuplicateCosine);
    }

    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>The distinct content words of <paramref name="text"/>: lower-case words that are not Swedish function words.</summary>
    public static IReadOnlyList<string> ContentWords(string text) =>
        [.. Words(text).Where(w => !FunctionWords.Value.Contains(w)).Distinct(StringComparer.Ordinal)];

    /// <summary>Lower-case, NFC runs of letters and digits.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static bool ContainsPhrase(IReadOnlyList<string> words, string[] phrase)
    {
        for (var i = 0; i + phrase.Length <= words.Count; i++)
        {
            if (phrase.Select((p, j) => words[i + j] == p).All(match => match))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The detector's Swedish function words (<c>stopwords-sv.txt</c>, embedded in this assembly).</summary>
    private static HashSet<string> LoadFunctionWords()
    {
        using var stream = typeof(LanguageDetector).Assembly.GetManifestResourceStream("Ume.Rag.Worker.Processing.Detect.stopwords-sv.txt")
            ?? throw new InvalidOperationException("Embedded stopwords-sv.txt not found.");
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
