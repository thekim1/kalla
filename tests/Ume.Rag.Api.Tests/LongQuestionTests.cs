using System.Globalization;
using System.Text.Json;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Long questions (chat design §3.2, §5.1, §6.3; Decisions "Long questions condensed into the search query"; M-6), the pure
/// parts: the two forms of the question and its limit, the long-question condense input, the word-boundary fallback, the
/// prompt budget, and a multi-line ticket reaching the answer prompt unchanged (golden file in
/// <c>tests/Ume.Rag.Api.Tests/Golden/prompts</c>; <c>UME_RAG_UPDATE_GOLDEN=1</c> rewrites it).
/// </summary>
public sealed class LongQuestionTests
{
    private static readonly string GoldenDirectory = Path.Combine(Path.GetDirectoryName(TestPostgres.RepositoryFile("UmeRag.slnx"))!,
        "tests", "Ume.Rag.Api.Tests", "Golden", "prompts");

    private static ParsedChat Parse(string question, params object[] history) =>
        ChatRequestParser.Parse(JsonSerializer.SerializeToElement(new { model = "bygg", messages = history.Append(new { role = "user", content = question }) }));

    private static ChatException Refused(string question) => Should.Throw<ChatException>(() => Parse(question));

    /// <summary>A support ticket as pasted: a mail thread, an indented error line, blank lines and a log, over 2,000 characters.</summary>
    public static string Ticket()
    {
        List<string> lines =
        [
            "Ärende 2026-114233: Inloggning i Procapita misslyckas", "Prioritet: Normal", "Kategori: Verksamhetssystem", "",
            "Från: Anna Andersson <anna.andersson@example.org>", "Skickat: den 4 november 2026 08:12", "Till: Servicedesk", "", "Hej!", "",
            "Sedan i morse kan ingen på enheten logga in i Procapita. Vi får felet:", "", "\tFel 0x80070005: Åtkomst nekad (AUTH-403)", "",
            "Vi har startat om datorerna utan att det hjälpt. Gäller det fler enheter?", "", "Mvh Anna", "", "--- Logg ---",
        ];
        for (var i = 0; i < 24; i++)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"2026-11-04 08:{i:00}:13 ERROR  SsoClient  Token request failed: invalid_grant (AUTH-403), attempt {i + 1}"));
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public void A_question_may_be_16000_characters_counted_after_normalisation()
    {
        var words = string.Join(' ', Enumerable.Repeat("bygglov", 2000)); // 15,999 characters
        Parse(words + "s").Question.Length.ShouldBe(ChatRequestParser.MaxQuestionChars);
        Refused(words + "sx").Code.ShouldBe("question_too_long");
        Refused(new string('x', ChatRequestParser.MaxQuestionChars + 1)).Code.ShouldBe("question_too_long");

        // Whitespace runs count once (the query form), so a question padded with line breaks still fits.
        Parse(words.Replace(" ", " \n\n ", StringComparison.Ordinal) + "s").Question.Length.ShouldBe(ChatRequestParser.MaxQuestionChars);
        Parse(new string('x', 2001)).Question.Length.ShouldBe(2001);
        Refused(" \n\t ").Code.ShouldBe("invalid_request");
    }

    [Fact]
    public void The_prompt_form_keeps_line_breaks_and_tabs_and_the_query_form_collapses_them()
    {
        var parsed = Parse("  Rad ett\r\nRad\ttvå\u0007\rRad tre\n\n\n\n\n \nRad fyraé  \n");
        parsed.PromptQuestion.ShouldBe("Rad ett\nRad\ttvå \nRad tre\n\n\nRad fyraé");
        parsed.Question.ShouldBe("Rad ett Rad två Rad tre Rad fyraé");
        ChatRequestParser.PromptForm(Ticket()).ShouldBe(Ticket()); // a pasted ticket is already its prompt form
    }

    [Fact]
    public void The_long_condense_gets_the_transcript_and_the_whole_message()
    {
        var question = Parse(Ticket()).Question;
        var first = Condenser.LongMessages([], question);
        first[0].Text.ShouldStartWith("Du får ett långt meddelande, ofta ett supportärende med e-posttråd eller loggar.");
        first[0].Text.ShouldEndWith("Skriv endast sökfrågan på en rad.");
        first[1].Text.ShouldBe("Meddelande: " + question);

        ChatMessageIn[] history = [new("user", "Hej, jag har ett problem med Procapita."), new("assistant", "Vad står det i felet [1]? Se [guiden](https://www.umea.se/guide).")];
        Condenser.LongMessages(history, question)[1].Text
            .ShouldBe("Användare: Hej, jag har ett problem med Procapita.\nAssistent: Vad står det i felet? Se guiden.\n\nMeddelande: " + question);

        // The §5 cuts (user 500, assistant 600 characters, the last six messages); the message is never cut.
        var longHistory = Enumerable.Range(0, 8).Select(i => new ChatMessageIn(i % 2 == 0 ? "user" : "assistant", new string((char)('a' + i), 900))).ToList();
        var huge = new string('q', ChatRequestParser.MaxQuestionChars);
        var user = Condenser.LongMessages(longHistory, huge)[1].Text;
        user.ShouldStartWith("Användare: " + new string('c', 500) + "\nAssistent: " + new string('d', 600) + "\n");
        user.ShouldEndWith(new string('h', 600) + "\n\nMeddelande: " + huge);
        user.Length.ShouldBe((3 * (11 + 500 + 1)) + (3 * (11 + 600 + 1)) + "\nMeddelande: ".Length + huge.Length);
    }

    [Fact]
    public void The_fallback_is_the_first_2000_characters_cut_at_the_last_space_within_the_last_200()
    {
        Condenser.Fallback(new string('x', 2500)).ShouldBe(new string('x', 2000)); // a log line without spaces

        var spaced = new string('a', 1900) + " " + new string('b', 600);
        Condenser.Fallback(spaced).ShouldBe(new string('a', 1900));

        var early = new string('a', 1700) + " " + new string('b', 800); // the only space lies before the last 200
        Condenser.Fallback(early).ShouldBe(early[..2000]);

        var words = string.Join(' ', Enumerable.Repeat("ordet", 500)); // 2,999 characters, a space every sixth
        var cut = Condenser.Fallback(words);
        cut.Length.ShouldBe(1997);
        words.ShouldStartWith(cut + " ");
        QueryNormalizer.NormalizeAndValidate(cut).ShouldBe(cut);

        // Characters are Unicode scalar values, as QueryNormalizer counts them: a surrogate pair is never split.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 2100));
        var emojiCut = Condenser.Fallback(emoji);
        QueryNormalizer.Length(emojiCut).ShouldBe(2000);
        emojiCut.Length.ShouldBe(4000);
    }

    private static RetrievedChunk Chunk(string text) =>
        new(1, Guid.NewGuid(), Guid.Parse("0199a000-0000-7000-8000-000000000002"), [], text, "Procapita › Inloggning", null, null, 1.0,
            new ChunkScores(null, null, null, null, null, null), null, Guid.Empty, "c", "internal",
            new Citation(Guid.Empty, "Inloggning i Procapita", null, "https://intranat.umea.se/procapita", null, "web", "page", "text/html", "sv",
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

    private static string Render(BuiltPrompt prompt) =>
        string.Join("\n", prompt.Messages.Select(m => $"=== {m.Role}\n{m.Text}")) + "\n";

    [Fact]
    public void A_multi_line_ticket_reaches_the_answer_prompt_unchanged()
    {
        var parsed = Parse(Ticket());
        QueryNormalizer.Length(parsed.Question).ShouldBeGreaterThan(QueryNormalizer.MaxLength);
        var prompt = PromptBuilder.Build(PromptKind.Sources, new PromptContext("Support", "Svara som IT-servicedesk.", LanguageGuess.Swedish, parsed.PromptQuestion, [],
            AssistantChatSettings.Default), [Chunk("Felet AUTH-403 betyder att inloggningstoken har gått ut. Logga ut och in i Windows.")]);
        prompt.Messages[^1].Text.ShouldEndWith("\n\nFråga: " + Ticket());

        var actual = Render(prompt);
        var file = Path.Combine(GoldenDirectory, "long-ticket-multiline.txt");
        if (Environment.GetEnvironmentVariable("UME_RAG_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDirectory);
            File.WriteAllText(file, actual);
        }

        File.Exists(file).ShouldBeTrue($"{file} is missing; run with UME_RAG_UPDATE_GOLDEN=1 to write it.");
        actual.ShouldBe(File.ReadAllText(file).ReplaceLineEndings("\n"), "long-ticket-multiline: run with UME_RAG_UPDATE_GOLDEN=1 to regenerate.");
    }

    [Fact]
    public void A_16000_character_question_shrinks_the_history_window_first()
    {
        // §6.3 "Long questions": the question is mandatory and never cut; sources come before history.
        var question = string.Join(' ', Enumerable.Repeat("bygglov", 2000)) + "s";
        var history = Enumerable.Range(0, 10).Select(i => new ChatMessageIn(i % 2 == 0 ? "user" : "assistant", new string('h', 1200))).ToList();
        var sources = Chunk(string.Join(' ', Enumerable.Repeat("Felet AUTH-403 betyder att inloggningstoken har gått ut.", 500))[..23_000]);

        var fits = PromptBuilder.Build(PromptKind.Sources, new PromptContext("Bygg", "Var kortfattad.", "sv", question, history, AssistantChatSettings.Default), [sources]);
        fits.Messages[^1].Text.ShouldEndWith("Fråga: " + question);
        fits.Sources.ShouldHaveSingleItem();
        var window = fits.Messages.Count - 2;
        window.ShouldBeInRange(1, history.Count - 1); // history trimmed, not dropped
        // Without model limits the budget is C = 16,384 with the 1,024-token answer reserve (§6.3).
        fits.Messages.Sum(m => PromptBuilder.Tokens(m.Text)).ShouldBeLessThanOrEqualTo(AnswerLimits.DefaultContext - AnswerLimits.DefaultReserve);

        // Without the long sources the same question keeps more history.
        var lean = PromptBuilder.Build(PromptKind.Sources, new PromptContext("Bygg", "Var kortfattad.", "sv", question, history, AssistantChatSettings.Default),
            [Chunk("Kort källa.")]);
        (lean.Messages.Count - 2).ShouldBeGreaterThan(window);

        // contextTokens 8,192 leaves no room for 1,000 source tokens once the assistant's prompt is a page long.
        var small = AssistantChatSettings.Default with { ContextTokens = 8192 };
        Should.Throw<ChatException>(() => PromptBuilder.Build(PromptKind.Sources,
                new PromptContext("Bygg", new string('p', 2000), "sv", question, history, small), [sources]))
            .Code.ShouldBe("context_length_exceeded");
    }
}
