namespace Ume.Rag.Domain.Entities;

// The serving tables of content (data-model §2.2; processing design §13, §14; weakness review §2): evaluation sets,
// questions, runs and results, and the retrieval counters (knowledge design §4.7). Config rows are referenced by id
// without a foreign key. Values the design marks P2 are left out of the CHECK constraints until P2 adds them.

/// <summary>The evaluation set of one collection (<c>content."EvaluationSets"</c>).</summary>
public sealed class EvaluationSet
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CollectionId { get; set; }
    public required string Classification { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The generation round of the set's newest questions (#130): 1 for the first set, +1 per top-up.</summary>
    public int Round { get; set; } = 1;
}

/// <summary>Question layers of P1 (<c>feedback</c> and <c>skill</c> are P2; processing design §13.4).</summary>
public static class EvaluationLayers
{
    /// <summary>Generated from one chunk and judge-filtered (P1b).</summary>
    public const string Generated = "generated";

    /// <summary>Written by people; the pilot gold set (weakness review §2).</summary>
    public const string Human = "human";

    public static IReadOnlyList<string> All { get; } = [Generated, Human];
}

/// <summary>Evaluation question states (data-model §2.2).</summary>
public static class EvaluationQuestionStates
{
    public const string Active = "active";
    public const string Stale = "stale";
    public const string Retired = "retired";

    public static IReadOnlyList<string> All { get; } = [Active, Stale, Retired];
}

/// <summary>
/// One evaluation question (<c>content."EvaluationQuestions"</c>). A <c>human</c> question with no expected documents
/// expects "not found" (weakness review §2). Deferred and left out: <c>SkillVersionId</c> (the P2 <c>skill</c> layer).
/// </summary>
public sealed class EvaluationQuestion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SetId { get; set; }
    public required string Classification { get; set; }
    public required string Layer { get; set; }
    public required string Question { get; set; }

    /// <summary>The facts a correct answer contains.</summary>
    public string? ExpectedAnswer { get; set; }

    /// <summary>Empty = the expected outcome is "not found".</summary>
    public Guid[] ExpectedDocumentIds { get; set; } = [];

    /// <summary>The source chunk's content hash when generated, so a changed source marks the question stale.</summary>
    public byte[]? SourceContentHash { get; set; }

    /// <summary>Model, prompt version, seed and judge scores of a generated question (<c>jsonb</c>).</summary>
    public string? Origin { get; set; }

    public string State { get; set; } = EvaluationQuestionStates.Active;

    /// <summary>Provenance only (no foreign key).</summary>
    public Guid? SourceChunkId { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>
/// Run kinds of P1: the worker's retrieval runs (processing design §14). <c>answers</c> and <c>assistant-tests</c>
/// (run by <c>rag-admin</c>) and <c>skill-gate</c> are P2 (data-model §15).
/// </summary>
public static class EvaluationRunKinds
{
    public const string PublishReport = "publish-report";
    public const string Nightly = "nightly";
    public const string OnDemand = "on-demand";

    public static IReadOnlyList<string> All { get; } = [PublishReport, Nightly, OnDemand];
}

/// <summary>Retrieval variants of a run (weakness review §2: the P1a three-way gold-set run).</summary>
public static class EvaluationVariants
{
    public const string Hybrid = "hybrid";
    public const string Vector = "vector";
    public const string HybridNoDecompounder = "hybrid-no-decompounder";

    public static IReadOnlyList<string> All { get; } = [Hybrid, Vector, HybridNoDecompounder];
}

/// <summary>
/// One advisory run over a collection's full set (<c>content."EvaluationRuns"</c>, processing design §14): no
/// decision or override. Counts and metrics are null until the run finishes. Deferred and left out: <c>Faithfulness</c>
/// and <c>CitationPrecision</c> (P2 <c>answers</c> runs).
/// </summary>
public sealed class EvaluationRun
{
    /// <summary>The cut-off of recall@k and MRR@k (processing design §13.6).</summary>
    public const int DefaultK = 10;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SetId { get; set; }
    public required string Classification { get; set; }
    public required string Kind { get; set; }

    /// <summary>What was evaluated (for example the pipeline version of a publish report); no foreign key.</summary>
    public Guid? SubjectId { get; set; }

    public int? SubjectVersion { get; set; }

    /// <summary>The run the per-question wins and losses are counted against; no foreign key.</summary>
    public Guid? ComparedRunId { get; set; }

    public int? QuestionCount { get; set; }
    public int K { get; set; } = DefaultK;
    public int? Found { get; set; }
    public int? Wins { get; set; }
    public int? Losses { get; set; }
    public decimal? RecallAtK { get; set; }
    public decimal? Mrr { get; set; }
    public bool Reranked { get; set; }
    public string Variant { get; set; } = EvaluationVariants.Hybrid;
    public string? PromptVersion { get; set; }
    public byte[]? Seed { get; set; }

    /// <summary>A run of candidate settings (#129), not of the collection's saved ones.</summary>
    public bool Candidate { get; set; }

    /// <summary><c>jsonb</c>: the coverage threshold proposal of an on-demand run (#129), kept beyond job retention.</summary>
    public string? ThresholdProposal { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>The coverage status of a question's retrieval (knowledge design §7).</summary>
public static class CoverageStates
{
    public const string Ok = "ok";
    public const string Low = "low";

    public static IReadOnlyList<string> All { get; } = [Ok, Low];
}

/// <summary>
/// One question's result in one run (<c>content."EvaluationResults"</c>). The coverage fields calibrate the not-found
/// thresholds from answerable and unanswerable <c>human</c> questions (weakness review §2). Deferred and left out:
/// <c>Faithfulness</c>, <c>CitationPrecision</c> (P2 <c>answers</c>) and <c>Verdict</c>, <c>Question</c>,
/// <c>Answer</c>, <c>JudgeNote</c> (P2 <c>assistant-tests</c>).
/// </summary>
public sealed class EvaluationResult
{
    public Guid RunId { get; set; }

    /// <summary>The <see cref="EvaluationQuestion"/>; no foreign key (P2 <c>assistant-tests</c> results point elsewhere).</summary>
    public Guid QuestionId { get; set; }

    public required string Classification { get; set; }

    /// <summary>Rank of the first expected document in the top k; null = not found.</summary>
    public int? ExpectedRank { get; set; }

    public bool? Hit { get; set; }
    public string? Coverage { get; set; }
    public float? BestDistance { get; set; }
    public float? BestKeywordScore { get; set; }
}

/// <summary>
/// Retrieval counters per day, document text and collection (<c>content."RetrievalStats"</c>, knowledge design §4.7):
/// counts only, never query text. Keyed by text hash, so counts survive re-chunking. The api writes them only through
/// <c>content.rag_count_retrievals</c> (data-model §12.3).
/// </summary>
public sealed class RetrievalStat
{
    /// <summary>The Europe/Stockholm day.</summary>
    public DateOnly Day { get; set; }

    public Guid DocumentId { get; set; }
    public required byte[] TextHash { get; set; }
    public Guid CollectionId { get; set; }
    public required string Classification { get; set; }
    public int Retrieved { get; set; }
    public int Cited { get; set; }
}
