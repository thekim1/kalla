using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// The pattern targets of processing design §17: on a labelled synthetic corpus (Skatteverket's test personnummer and
/// samordningsnummer in every written form, generated phone numbers in every §9.1 format, plain and obfuscated e-mail,
/// and the look-alikes of §9.2), personnummer and samordningsnummer precision and recall ≥ 0.99, e-mail ≥ 0.99, phone
/// ≥ 0.95. The NER target (names recall ≥ 0.85, precision ≥ 0.80) needs the parser's model and is measured with item 5.
/// </summary>
public sealed class PiiTargetTests
{
    private static readonly PiiPatterns Patterns = new(new FakeTimeProvider(ProcessingHarness.Now));

    public static TheoryData<string, double> Targets => new()
    {
        { PiiTypes.Personnummer, 0.99 },
        { PiiTypes.Samordningsnummer, 0.99 },
        { PiiTypes.Email, 0.99 },
        { PiiTypes.Phone, 0.95 },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_type_reaches_its_precision_and_recall_target(string type, double target)
    {
        int tp = 0, fp = 0, fn = 0;
        var errors = new List<string>();
        foreach (var (text, expected) in Corpus())
        {
            var found = Patterns.Scan(text).Where(m => m.Type == type).Select(m => m.Value).ToList();
            var wanted = expected.Where(e => e.Type == type).Select(e => e.Value).ToList();
            tp += found.Count(wanted.Contains);
            fp += found.Count(f => !wanted.Contains(f));
            fn += wanted.Count(w => !found.Contains(w));
            if (found.Count(f => !wanted.Contains(f)) + wanted.Count(w => !found.Contains(w)) > 0)
            {
                errors.Add(text);
            }
        }

        var precision = tp + fp == 0 ? 1 : tp / (double)(tp + fp);
        var recall = tp + fn == 0 ? 1 : tp / (double)(tp + fn);
        TestContext.Current.TestOutputHelper?.WriteLine($"{type}: precision {precision:F4}, recall {recall:F4} ({tp} TP, {fp} FP, {fn} FN)");
        tp.ShouldBeGreaterThan(50);
        precision.ShouldBeGreaterThanOrEqualTo(target, $"{type}: {fp} false positives: {string.Join(" | ", errors.Take(10))}");
        recall.ShouldBeGreaterThanOrEqualTo(target, $"{type}: {fn} missed: {string.Join(" | ", errors.Take(10))}");
    }

    [Theory]
    [InlineData("Org.nr 212000-2627 för Exempelstad kommun.")]
    [InlineData("Betala till bankgiro 5050-1055 med OCR 8112189876.")]
    [InlineData("Ange ärendenr 811218-9876 i alla kontakter.")]
    [InlineData("Ring 0701234567 om du har frågor.")] // a mobile number, not a personnummer: no keyword
    [InlineData("Mötet hölls 2024-05-12 i Umeå.")]
    [InlineData("Postadress 903 25 Umeå, budget 1 234 567 kr.")]
    [InlineData("Plusgiro 12 34 56-7 och bg 123-4567.")]
    [InlineData("Följ oss på @exempelstad eller skriv till info@localhost.")]
    public void A_look_alike_is_no_personnummer_and_no_email(string text)
    {
        var matches = Patterns.Scan(text);
        matches.ShouldNotContain(m => m.Type == PiiTypes.Personnummer || m.Type == PiiTypes.Samordningsnummer || m.Type == PiiTypes.Email, text);
    }

    [Theory]
    [InlineData("Postadress 903 25 Umeå.")]
    [InlineData("Kostnaden blev 1 234 567 kr.")]
    [InlineData("Betala till bankgiro 5050-1055.")]
    [InlineData("Plusgiro 12 34 56-7.")]
    [InlineData("Org.nr 212000-2627.")]
    public void A_look_alike_is_no_phone_number(string text) =>
        Patterns.Scan(text).ShouldNotContain(m => m.Type == PiiTypes.Phone, text);

    [Fact]
    public void A_100_year_old_is_written_with_a_plus_and_both_centuries_are_counted_alike()
    {
        Patterns.Scan("Sökande 121212+1212.").Single().Type.ShouldBe(PiiTypes.Personnummer);
        Patterns.Scan("Sökande 19121212-1212.").Single().Value.ShouldBe(Patterns.Scan("Sökande 121212+1212.").Single().Value);
    }

    [Fact]
    public void An_unseparated_ten_digit_number_needs_a_keyword()
    {
        Patterns.Scan("Kundens nummer 1212121212 finns i systemet.").ShouldNotContain(m => m.Type == PiiTypes.Personnummer);
        Patterns.Scan("Personnummer: 1212121212.").ShouldContain(m => m.Type == PiiTypes.Personnummer);
    }

    [Fact]
    public void A_date_that_does_not_exist_or_a_wrong_check_digit_is_no_personnummer()
    {
        Patterns.Scan("Sökande 19230229-1234.").ShouldBeEmpty(); // 1923 is no leap year
        Patterns.Scan("Sökande 19121212-1213.").ShouldBeEmpty(); // Luhn fails
    }

    /// <summary>The labelled corpus: one sentence per item, with the values the patterns should report.</summary>
    internal static IEnumerable<(string Text, List<(string Type, string Value)> Expected)> Corpus()
    {
        var personnummer = File.ReadAllLines(Path.Combine(Fixtures.Pii, "testpersonnummer.txt")).Where(l => l.Length == 12).ToList();
        var samordning = File.ReadAllLines(Path.Combine(Fixtures.Pii, "testsamordningsnummer.txt")).Where(l => l.Length == 12).ToList();
        string[] templates =
        [
            "Sökande: {0}, adress okänd.", "Uppgifter om barnet ({0}) lämnas separat.", "Den avlidne, {0}, efterlämnade inga tillgångar.",
            "Beslutet gäller {0} från och med i dag.", "Klagande {0} har inkommit med en skrivelse.",
        ];
        var i = 0;
        foreach (var (number, type) in personnummer.Select(n => (n, PiiTypes.Personnummer)).Concat(samordning.Select(n => (n, PiiTypes.Samordningsnummer))))
        {
            var year = int.Parse(number[..4], CultureInfo.InvariantCulture);
            var ten = number[2..];
            var written = (i % 4) switch
            {
                0 => number[..8] + "-" + number[8..],
                1 => number,
                2 => ten[..6] + (ProcessingHarness.Now.Year - year >= 100 ? "+" : "-") + ten[6..],
                _ => "personnummer " + ten,
            };
            yield return (string.Format(CultureInfo.InvariantCulture, templates[i % templates.Length], written), [(type, ten)]);
            i++;
        }

        var random = new Random(9_1);
        string[] areas = ["90", "8", "31", "40", "920", "63", "651", "910"];
        string[] mobiles = ["70", "72", "73", "76", "79"];
        for (var n = 0; n < 200; n++)
        {
            var mobile = n % 2 == 0;
            var prefix = mobile ? mobiles[n % mobiles.Length] : areas[n % areas.Length];
            var length = mobile ? 9 : Math.Max(prefix.Length + 5, 7 + (n % 3));
            var subscriber = string.Concat(Enumerable.Range(0, length - prefix.Length).Select(_ => random.Next(10).ToString(CultureInfo.InvariantCulture)));
            if (subscriber[0] == '0')
            {
                subscriber = "1" + subscriber[1..];
            }

            var national = prefix + subscriber;
            var groups = string.Join(' ', Chunk(subscriber));
            var written = (n % 5) switch
            {
                0 => "0" + prefix + "-" + groups,
                1 => "0" + national,
                2 => "+46 " + prefix + " " + groups,
                3 => "+46 (0)" + prefix + "-" + groups,
                _ => "0046 " + prefix + " " + subscriber,
            };
            yield return ($"Ring {written} för mer information.", [(PiiTypes.Phone, national)]);
        }

        yield return ("Norska kontoret nås på +47 22 33 44 55.", [(PiiTypes.Phone, "+4722334455")]);
        string[] names = ["anna.berg", "erik_lund", "info", "kontakt-center", "maria.nilsson2", "bo"];
        string[] domains = ["exempelstad.se", "umea.se", "forbundet.example.org", "skola.exempelstad.se", "gmail.com"];
        for (var n = 0; n < 120; n++)
        {
            var local = names[n % names.Length];
            var domain = domains[n % domains.Length];
            var written = (n % 6) switch
            {
                0 => $"{local}[at]{domain}",
                1 => $"{local} (at) {domain}",
                2 => $"{local} snabel-a {domain}",
                _ => $"{local}@{domain}",
            };
            yield return ($"Skriv till {written} om du har frågor.", [(PiiTypes.Email, $"{local}@{domain}")]);
        }

        // Look-alikes in the same corpus: none of them is any type.
        string[] negatives =
        [
            "Org.nr 212000-2627.", "Organisationsnummer 556677-8899 tillhör bolaget.", "OCR 8112189876 anges vid betalning.",
            "Bankgiro 5050-1055.", "Ärendenr 811218-9876.", "Dnr 2024-00123.", "Mötet hölls 2024-05-12.", "Postadress 903 25 Umeå.",
            "Totalt 1 234 567 kr.", "År 2026 infördes taxan.", "Fakturanr 1912121212 ska anges.", "Version 1.2 släpptes.",
            "Följ @exempelstad.", "Kundnr 1212121212.",
        ];
        foreach (var negative in negatives)
        {
            yield return (negative, []);
        }
    }

    private static IEnumerable<string> Chunk(string digits)
    {
        for (var i = 0; i < digits.Length; i += 2)
        {
            yield return digits.Substring(i, Math.Min(digits.Length - i == 3 ? 3 : 2, digits.Length - i));
            if (digits.Length - i == 3)
            {
                yield break;
            }
        }
    }
}
