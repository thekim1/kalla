using Kalla.Worker.Evaluation.Generation;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// The candidate filters of processing design §13.1, one case each: rules (Swedish, 6–25 words, <c>?</c>, deictic
/// phrases, §9.1 patterns), answer leakage (answer in the question, copied wording), the judge's minimum and the
/// near-duplicate cosine; and the reading of the model's JSON.
/// </summary>
public sealed class QuestionFilterTests
{
    private const string Chunk = "Umeå kommun ansvarar för bygglov i hela kommunen. Handläggningstiden är normalt fyra veckor från att ärendet är komplett.";
    private readonly QuestionFilters _filters = new(new PiiPatterns(TimeProvider.System));

    [Fact]
    public void A_good_question_passes_rules_and_leakage()
    {
        const string question = "Vilken nämnd beslutar om bygglov och hur lång väntan blir det för en villa?";

        _filters.Rules(question).ShouldBeNull();
        QuestionFilters.Leakage(question, "Byggnadsnämnden, cirka en månad.", Chunk).ShouldBeNull();
    }

    [Theory]
    [InlineData("How long does it take to get a building permit from the municipality?", FilterReasons.Language)]
    [InlineData("Vad kostar bygglov?", FilterReasons.Length)]
    [InlineData("Hur lång tid tar det att få ett beslut om bygglov för en villa när man har skickat in alla ritningar och alla andra handlingar som nämnden har begärt in?", FilterReasons.Length)]
    [InlineData("Vilken nämnd beslutar om bygglov och hur lång väntan blir det för en villa.", FilterReasons.QuestionMark)]
    [InlineData("Vad säger texten om vem som beslutar om bygglov för en villa?", FilterReasons.Deictic)]
    [InlineData("Vilken nämnd som nämns nedan beslutar om bygglov för en villa?", FilterReasons.Deictic)]
    [InlineData("Vem beslutar enligt detta avsnitt om bygglov för en villa?", FilterReasons.Deictic)]
    [InlineData("Kan jag mejla anna.berg@example.com om bygglov för en villa redan idag?", FilterReasons.PersonalData)]
    public void A_question_that_breaks_a_rule_is_dropped(string question, string reason) =>
        _filters.Rules(question).ShouldBe(reason);

    [Fact]
    public void A_question_that_contains_its_answer_is_dropped()
    {
        QuestionFilters.Leakage("Varför tar handläggningen av bygglov normalt fyra veckor för en villa?", "Fyra veckor.", Chunk)
            .ShouldBe(FilterReasons.AnswerInQuestion);
        QuestionFilters.Leakage("Vilken nämnd beslutar om bygglov och vem kan jag fråga?", "ja", Chunk).ShouldBeNull("whole words only, not 'ja' in 'jag'");
    }

    [Fact]
    public void A_question_copied_from_the_text_is_dropped()
    {
        const string question = "Ansvarar Umeå kommun för bygglov i hela kommunen när ärendet är komplett?";

        QuestionFilters.CopiedShare(question, Chunk).ShouldBeGreaterThan(QuestionFilters.MaxCopiedShare);
        QuestionFilters.Leakage(question, "Ja.", Chunk).ShouldBe(FilterReasons.CopiedFromText);
    }

    [Theory]
    [InlineData(4, 4, 4, true)]
    [InlineData(5, 5, 3, false)]
    [InlineData(3, 5, 5, false)]
    [InlineData(5, 3, 5, false)]
    public void The_judge_keeps_only_all_ratings_of_four_or_more(int answerable, int understandable, int specific, bool kept) =>
        QuestionFilters.Passes(new JudgeScores(answerable, understandable, specific)).ShouldBe(kept);

    [Fact]
    public void A_near_duplicate_is_dropped_at_cosine_092()
    {
        float[] kept = [1, 0, 0];

        QuestionFilters.IsDuplicate([0.95f, 0.31f, 0], [kept]).ShouldBeTrue();
        QuestionFilters.IsDuplicate([0.9f, 0.43f, 0], [kept]).ShouldBeFalse();
    }

    [Fact]
    public void The_models_json_is_read_and_anything_else_is_refused()
    {
        EvalPrompts.ReadCandidate("""{"fraga": "Vem beslutar?", "svar": "Nämnden.", "typ": "fakta"}""")
            .ShouldBe(new GeneratedCandidate("Vem beslutar?", "Nämnden.", "fakta"));
        EvalPrompts.ReadCandidate("```json\n{\"fraga\": \"Vem?\", \"svar\": \"Hon.\", \"typ\": \"annat\"}\n```")
            .ShouldBe(new GeneratedCandidate("Vem?", "Hon.", null));
        EvalPrompts.ReadCandidate("inte json").ShouldBeNull();
        EvalPrompts.ReadCandidate("""{"fraga": "Vem?"}""").ShouldBeNull();
        EvalPrompts.ReadScores("""{"besvarbar": 5, "begriplig": 4.0, "specifik": 4}""").ShouldBe(new JudgeScores(5, 4, 4));
        EvalPrompts.ReadScores("""{"besvarbar": 9, "begriplig": 4, "specifik": 4}""").ShouldBeNull();
    }
}
