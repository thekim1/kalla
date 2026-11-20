using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;
using static Kalla.Infrastructure.Persistence.RagDbContext;

namespace Kalla.Infrastructure.Persistence.Chat;

/// <summary>
/// The <c>chat</c> tables (data-model §2.3; P1b item 1). Config and content rows are referenced by id without a foreign key.
/// P1 writes metadata and ratings only: the ciphertext columns, <c>DataKeys</c> and <c>ToolCalls</c> are created and stay
/// unused until A1 allows stored content (weakness review §4). Retention (data-model §8) comes with the conversation writer.
/// </summary>
internal static class ChatModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataKey>(e =>
        {
            e.ToTable("DataKeys", ChatSchema, t => ClassificationCheck(t, "DataKeys"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.KeyProvider).HasMaxLength(50);
            e.Property(x => x.MasterKeyName).HasMaxLength(100);
            e.Property(x => x.MasterKeyVersion).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.Classification, x.ExpiryMonth }).IsUnique();
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.ToTable("Conversations", ChatSchema, t =>
            {
                ClassificationCheck(t, "Conversations");
                ValuesCheck(t, "Conversations", "SubjectKind", ChatSubjectKinds.All);
                ValuesCheck(t, "Conversations", "HistoryMode", HistoryModes.All);
                t.HasCheckConstraint("CK_Conversations_RetentionDays", "\"RetentionDays\" >= 0");
                NoContent(t, "Conversations", "TitleCiphertext");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.SubjectKind).HasMaxLength(20);
            e.Property(x => x.SubjectId).HasMaxLength(200);
            e.Property(x => x.ExternalChatId).HasMaxLength(200);
            e.Property(x => x.HistoryMode).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            // A model switch in one Open WebUI chat is a new conversation (chat design §11.1).
            e.HasIndex(x => new { x.ClientId, x.SubjectId, x.ExternalChatId, x.AssistantId }).IsUnique();
            e.HasIndex(x => new { x.SubjectId, x.LastMessageAt }).IsDescending(false, true);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.TitleDataKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("Messages", ChatSchema, t =>
            {
                ClassificationCheck(t, "Messages");
                ValuesCheck(t, "Messages", "Role", MessageRoles.All);
                ValuesCheck(t, "Messages", "Outcome", MessageOutcomes.All);
                ValuesCheck(t, "Messages", "Coverage", MessageCoverage.All);
                NoContent(t, "Messages", "ContentCiphertext");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.ExternalMessageId).HasMaxLength(100);
            e.Property(x => x.GatewayModel).HasMaxLength(200);
            e.Property(x => x.GatewayRequestId).HasMaxLength(100);
            e.Property(x => x.ObservedResidency).HasMaxLength(20);
            e.Property(x => x.CostUsd).HasPrecision(18, 8);
            e.Property(x => x.Coverage).HasMaxLength(10);
            e.Property(x => x.Degraded).HasMaxLength(40);
            e.Property(x => x.Language).HasColumnType("char(2)");
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            // Regeneration dedup (chat design §11.4).
            e.HasIndex(x => new { x.ConversationId, x.ExternalMessageId }).IsUnique().HasFilter("\"ExternalMessageId\" IS NOT NULL");
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.DataKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageSource>(e =>
        {
            e.ToTable("MessageSources", ChatSchema, t =>
            {
                ClassificationCheck(t, "MessageSources");
                NoContent(t, "MessageSources", "SnippetCiphertext");
            });
            e.HasKey(x => new { x.MessageId, x.Rank });
            e.Property(x => x.Classification).HasMaxLength(20);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.DataKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Feedback>(e =>
        {
            e.ToTable("Feedback", ChatSchema, t =>
            {
                ClassificationCheck(t, "Feedback");
                ValuesCheck(t, "Feedback", "Source", FeedbackSources.All);
                ValuesCheck(t, "Feedback", "ReviewState", FeedbackReviewStates.All);
                t.HasCheckConstraint("CK_Feedback_Rating", "\"Rating\" IN (-1, 1)");
                t.HasCheckConstraint("CK_Feedback_Score", "\"Score\" BETWEEN 1 AND 10");
                NoContent(t, "Feedback", "CommentCiphertext");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.Property(x => x.ReasonCategory).HasMaxLength(40);
            e.Property(x => x.ReviewState).HasMaxLength(20).HasDefaultValue(FeedbackReviewStates.Open);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.MessageId, x.Source }, "IX_Feedback_MessageId_Api").IsUnique().HasFilter("\"Source\" = 'api'");
            e.HasIndex(x => new { x.Source, x.ClientId, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.ReviewState, "IX_Feedback_Open").HasFilter("\"ReviewState\" = 'open'");
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.DataKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ToolCall>(e =>
        {
            e.ToTable("ToolCalls", ChatSchema, t =>
            {
                ClassificationCheck(t, "ToolCalls");
                NoContent(t, "ToolCalls", "ArgumentsCiphertext");
                NoContent(t, "ToolCalls", "ResultCiphertext");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(20);
            e.Property(x => x.ToolName).HasMaxLength(200);
            e.Property(x => x.Outcome).HasMaxLength(40);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.DataKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssistantDailyTotal>(e =>
        {
            e.ToTable("AssistantDailyTotals", ChatSchema, t => ClassificationCheck(t, "AssistantDailyTotals"));
            e.HasKey(x => new { x.Day, x.AssistantId, x.ClientId, x.Classification });
            e.Property(x => x.Classification).HasMaxLength(20);
        });

        modelBuilder.Entity<AssistantDailyUsage>(e =>
        {
            e.ToTable("AssistantDailyUsage", ChatSchema, t => UsageChecks(t, "AssistantDailyUsage"));
            e.HasKey(x => new { x.Day, x.AssistantId, x.ClientId, x.Classification, x.Step });
            UsageColumns(e);
        });

        modelBuilder.Entity<FeedbackImportState>(e =>
        {
            e.ToTable("FeedbackImportState", ChatSchema);
            e.HasKey(x => x.ClientId);
            e.Property(x => x.ClientId).ValueGeneratedNever();
            e.Property(x => x.LastOutcome).HasMaxLength(40);
        });
    }

    /// <summary>
    /// No stored chat content until A1 (weakness review §4; #223 review): the ciphertext column stays null. A1's
    /// migration drops these checks with <c>CK_Conversations_HistoryMode</c>.
    /// </summary>
    private static void NoContent<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder<T> table, string tableName, string column) where T : class =>
        table.HasCheckConstraint($"CK_{tableName}_No{column}", $"\"{column}\" IS NULL");
}
