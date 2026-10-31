using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using static Ume.Rag.Infrastructure.Persistence.RagDbContext;

namespace Ume.Rag.Infrastructure.Persistence.Content;

/// <summary>
/// The serving <c>content</c> tables of P1 (data-model §2.2; processing design §13, §14; weakness review §2; knowledge
/// design §4.7): evaluation sets, questions, runs and results, and the retrieval counters. Config rows are referenced by
/// id without a foreign key. Raw SQL in the migration adds <c>content.rag_count_retrievals</c>, the api's only way to
/// the counters (data-model §12.3).
/// </summary>
internal static class ServingContentModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvaluationSet>(e =>
        {
            e.ToTable("EvaluationSets", ContentSchema, t => ClassificationCheck(t, "EvaluationSets"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.CollectionId).IsUnique();
        });

        modelBuilder.Entity<EvaluationQuestion>(e =>
        {
            e.ToTable("EvaluationQuestions", ContentSchema, t =>
            {
                ClassificationCheck(t, "EvaluationQuestions");
                ValuesCheck(t, "EvaluationQuestions", "Layer", EvaluationLayers.All);
                ValuesCheck(t, "EvaluationQuestions", "State", EvaluationQuestionStates.All);
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Layer).HasMaxLength(20);
            e.Property(x => x.ExpectedDocumentIds).HasColumnType("uuid[]");
            e.Property(x => x.Origin).HasColumnType("jsonb");
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.SetId, "IX_EvaluationQuestions_SetId_Current").HasFilter("\"RetiredAt\" IS NULL");
            e.HasOne<EvaluationSet>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvaluationRun>(e =>
        {
            e.ToTable("EvaluationRuns", ContentSchema, t =>
            {
                ClassificationCheck(t, "EvaluationRuns");
                ValuesCheck(t, "EvaluationRuns", "Kind", EvaluationRunKinds.All);
                ValuesCheck(t, "EvaluationRuns", "Variant", EvaluationVariants.All);
                t.HasCheckConstraint("CK_EvaluationRuns_K", "\"K\" BETWEEN 1 AND 100");
                t.HasCheckConstraint("CK_EvaluationRuns_Counts",
                    "\"QuestionCount\" >= 0 AND \"Found\" BETWEEN 0 AND \"QuestionCount\" AND \"Wins\" >= 0 AND \"Losses\" >= 0");
                t.HasCheckConstraint("CK_EvaluationRuns_Metrics", "\"RecallAtK\" BETWEEN 0 AND 1 AND \"Mrr\" BETWEEN 0 AND 1");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.RecallAtK).HasPrecision(5, 4);
            e.Property(x => x.Mrr).HasPrecision(5, 4);
            e.Property(x => x.Variant).HasMaxLength(30).HasDefaultValue(EvaluationVariants.Hybrid);
            e.Property(x => x.PromptVersion).HasMaxLength(50);
            e.HasIndex(x => new { x.SetId, x.StartedAt }).IsDescending(false, true);
            e.HasOne<EvaluationSet>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvaluationResult>(e =>
        {
            e.ToTable("EvaluationResults", ContentSchema, t =>
            {
                ClassificationCheck(t, "EvaluationResults");
                t.HasCheckConstraint("CK_EvaluationResults_Coverage", "\"Coverage\" IS NULL OR \"Coverage\" IN ('ok', 'low')");
                t.HasCheckConstraint("CK_EvaluationResults_ExpectedRank", "\"ExpectedRank\" >= 1");
            });
            e.HasKey(x => new { x.RunId, x.QuestionId });
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Coverage).HasMaxLength(10);
            e.HasOne<EvaluationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RetrievalStat>(e =>
        {
            e.ToTable("RetrievalStats", ContentSchema, t =>
            {
                ClassificationCheck(t, "RetrievalStats");
                t.HasCheckConstraint("CK_RetrievalStats_Counts", "\"Retrieved\" >= 0 AND \"Cited\" >= 0");
            });
            e.HasKey(x => new { x.Day, x.DocumentId, x.TextHash, x.CollectionId });
            e.Property(x => x.Classification).HasMaxLength(20);
        });
    }
}
