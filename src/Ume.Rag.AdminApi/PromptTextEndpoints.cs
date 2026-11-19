using Microsoft.EntityFrameworkCore;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// One line of a prompt as the Settings page's <b>Prompt texts</b> panel shows it (admin design §4.5): an editable member
/// (<see cref="Member"/> set, <see cref="Text"/> its default, <see cref="Value"/> the saved text or null) or a locked line
/// (<see cref="Member"/> null, <see cref="Text"/> the line, read-only).
/// </summary>
public sealed record PromptTextLine(string? Member, string Text, string? Value, int? MaxLength, string? Placeholder, bool OneLine);

/// <summary>A prompt of the panel in order: <c>frame</c>, <c>general</c>, <c>conversational</c>, <c>sources</c>, <c>condense</c>, <c>condenseLong</c>, <c>followUps</c>, <c>caveats</c>.</summary>
public sealed record PromptTextSection(string Id, IReadOnlyList<PromptTextLine> Lines);

/// <summary>
/// <c>GET /api/settings/chat/prompts</c> (admin design §4.5; chat design §7.3): every prompt the platform sends, in order, with
/// the editable texts at their saved value and default and the locked lines between them, so the panel shows the whole frame
/// without keeping its own copy of the texts. Saving goes through <c>PUT /api/settings/chat</c> (<c>prompts</c>, V38).
/// </summary>
public static class PromptTextEndpoints
{
    public static RouteGroupBuilder MapPromptTexts(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/settings/chat/prompts", async (RagDbContext db, CancellationToken ct) =>
                Sections(PlatformChatSettings.Parse(await db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct), new ChatOptions()).Texts))
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Settings").WithName("GetChatPromptTexts")
            .WithSummary("The platform's prompt texts in order: editable members with their default, and the locked lines");
        return api;
    }

    /// <summary>The panel's sections for <paramref name="texts"/> (the saved texts).</summary>
    public static IReadOnlyList<PromptTextSection> Sections(PromptTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var members = PromptTexts.Members.ToDictionary(m => m.Path, StringComparer.Ordinal);
        PromptTextLine Edit(string path)
        {
            var m = members[path];
            return new PromptTextLine(path, m.Default, texts.Values.TryGetValue(path, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null,
                m.MaxLength, m.Placeholder, m.OneLine);
        }

        static PromptTextLine Locked(string text) => new(null, text, null, null, null, false);
        return
        [
            // §7.3: the frame with sources; supplement (and fallback) and open replace the off rule line.
            new("frame", [Edit(PromptTexts.Identity), Edit(PromptTexts.RulesOff), Edit(PromptTexts.RulesSupplement), Edit(PromptTexts.RulesOpen),
                Locked(PromptTexts.Locked.Citation), Locked(PromptTexts.Locked.NoInventedNumbers), Locked(PromptTexts.Locked.Injection), Edit(PromptTexts.Degraded),
                Locked(PromptTexts.Locked.Language)]),
            // §10.4: fallback and open without sources.
            new("general", [Edit(PromptTexts.RulesFallbackGeneral), Edit(PromptTexts.RulesOpenGeneral), Locked(PromptTexts.Locked.NoInventedNumbers)]),
            new("conversational", [Edit(PromptTexts.Conversational)]),
            // §7.2: the sources block's note.
            new("sources", [Edit(PromptTexts.SourcesLabel), Locked(PromptTexts.Locked.SourcesNote)]),
            new("condense", [Edit(PromptTexts.Condense)]),
            new("condenseLong", [Edit(PromptTexts.CondenseLong)]),
            // §9.8: the editable block, then the locked lines and the language line.
            new("followUps", [Edit(PromptTexts.FollowUps), .. PromptTexts.Locked.FollowUps.Select(Locked), Locked(PromptTexts.Locked.FollowUpsSwedish)]),
            // §9.9: the escalation summary's editable block, then its locked lines and the language line.
            new("escalation", [Edit(PromptTexts.Escalation), .. PromptTexts.Locked.Escalation.Select(Locked), Locked(PromptTexts.Locked.EscalationSwedish)]),
            new("caveats", [Edit(PromptTexts.CaveatDegradedSv), Edit(PromptTexts.CaveatDegradedEn), Edit(PromptTexts.CaveatGeneralSv), Edit(PromptTexts.CaveatGeneralEn)]),
        ];
    }
}
