using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The test panel (admin design §4.9): <c>Ume.Rag.Answering</c>'s own <see cref="ChatService"/> and
/// <see cref="KnowledgeService"/> run in <c>rag-admin</c> over a per-call view of the policy snapshot, so the panel never
/// answers with different code than chat. The view: the assistant's draft (or published version) as its published
/// version, its linked collections live even when not yet published, the principal <c>admin-console</c> with the admin's
/// <c>oid</c>, and a policy that allows the assistant (the admin's department scope was checked; audiences are ignored).
/// Nothing is stored: no audit entry, no 6007, no conversation; the retrieval counter of the panel is never flushed.
/// </summary>
public static class AssistantTestEndpoints
{
    /// <summary>The panel's rate limiter policy: 20 test questions per minute per user (§4.9).</summary>
    public const string RatePolicy = "test-panel";

    public const int QuestionsPerMinute = 20;

    public static RouteGroupBuilder MapAssistantTest(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/assistants/{id:guid}/test", async (Guid id, AssistantTestRequest request, AdminContext admin, AdminTestPanel panel, CancellationToken ct) =>
                await panel.RunAsync(admin, id, request, ct))
            .WithTags("Assistants").RequireAuthorization(AdminPolicies.EditOrEditSkills).RequireRateLimiting(RatePolicy).WithName("TestAssistant")
            .WithSummary("Answer one question with the draft (or the published version); nothing is stored");
        return api;
    }
}

/// <summary>Runs the panel: builds the snapshot view per call and the chat pipeline over it.</summary>
public sealed class AdminTestPanel(
    PolicySnapshotStore snapshots, AdminRetrieval retrieval, GatewayChatClient gateway, IOptions<ChatOptions> chatOptions, IOptions<GatewayOptions> gatewayOptions,
    NpgsqlDataSource dataSource, TimeProvider time, ILoggerFactory loggers, IMeterFactory meters) : IDisposable
{
    /// <summary>The candidate text shown per hit (§4.9).</summary>
    public const int TextLength = 600;

    // Never started as a hosted service, so nothing it counts is written (§4.9: nothing is stored).
    private readonly RetrievalCounter _counter = new(dataSource, time, loggers.CreateLogger<RetrievalCounter>());
    private readonly Counter<long> _runs = meters.Create("Ume.Rag.AdminApi").CreateCounter<long>("rag.admin.test_panel");

    public async Task<AssistantTestResult> RunAsync(AdminContext admin, Guid id, AssistantTestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(request);
        var departments = admin.Departments().Select(d => d.Id);
        var assistant = await admin.Db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && departments.Contains(a.DepartmentId), ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var versionId = request.Target == AssistantTestTarget.Published ? assistant.PublishedVersionId : assistant.DraftVersionId ?? assistant.PublishedVersionId;
        var version = versionId is { } v ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(x => x.Id == v, ct) : throw AdminFaults.Of(AdminErrorCodes.NoDraft);
        if (string.IsNullOrWhiteSpace(version.GatewayModel))
        {
            throw AdminFaults.Field("gatewayModel", AdminErrorCodes.InvalidValue);
        }

        var configVersion = await admin.Db.ConfigVersion.AsNoTracking().Select(x => x.Version).SingleAsync(ct);
        var current = await snapshots.LoadAsync(configVersion, ct);
        var audience = await admin.Db.AssistantAudiences.AsNoTracking().Where(a => a.AssistantId == id && a.Kind == Domain.Entities.AssistantAudienceKinds.Client)
            .Select(a => a.SubjectId).ToListAsync(ct);
        var (view, client, notPublished) = View(current, assistant, version, audience, request.History.Count > 0);
        var who = new RagPrincipal(SubjectKind.EntraUser, admin.Actor ?? "admin", null, null, client, CredentialKind.AccessToken, null,
            new HashSet<string>(StringComparer.Ordinal), new MembershipFacts(new HashSet<Guid>(), new HashSet<string>(StringComparer.Ordinal), true), view.Version);

        var source = new FixedSnapshot(view);
        var policy = new PanelPolicy(id);
        var knowledge = new KnowledgeService(source, policy, retrieval.Engine, retrieval.Hydrator, retrieval.Embedder, _counter, dataSource, new NoDownloadLinks(),
            loggers.CreateLogger<KnowledgeService>());
        var chat = new ChatService(source, policy, knowledge, gateway, chatOptions, gatewayOptions, time, loggers.CreateLogger<ChatService>());
        var parsed = new ParsedChat(assistant.Slug, request.Question.Trim(), [.. request.History.Select(m => new ChatMessageIn(m.Role, m.Text))], false, true, null);

        var started = Stopwatch.GetTimestamp();
        // B-42 (cost-attribution §4.1): every call of the panel's turn, the answer included, runs as the assistant's gateway key.
        using var acting = GatewayCallScope.Begin(new GatewayCall(assistant.GatewayKeyId, AssistantId: assistant.Id));
        try
        {
            var plan = await chat.PlanAsync(who, parsed, ct);
            GatewayAnswer? answer = null;
            string delivered;
            var filtered = new Dictionary<string, int>(StringComparer.Ordinal);
            IReadOnlyList<PromptSource> cited = [];
            double? latencyMs = null;
            if (plan.Fixed is { } text)
            {
                delivered = text;
            }
            else
            {
                var call = Stopwatch.GetTimestamp();
                answer = await gateway.CompleteAsync(version.GatewayModel, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens, plan.Settings.Temperature, plan.MessageId,
                    "chat-answer", ct);
                latencyMs = Stopwatch.GetElapsedTime(call).TotalMilliseconds;
                var transducer = new AnswerTransducer(plan.Prompt.Sources, plan.Platform.AllowedLinkHosts);
                var pieces = transducer.Feed(answer.Text).Concat(transducer.Flush()).OfType<AnswerPiece.Text>();
                delivered = (plan.Caveat ?? "") + string.Concat(pieces.Select(p => p.Content)) + plan.Trailer;
                filtered = transducer.Filtered;
                cited = transducer.Cited;
            }

            _runs.Add(1, new KeyValuePair<string, object?>("outcome", ChatOutcomes.Name(plan.Outcome)));
            var citedChunks = cited.SelectMany(s => s.Chunks.Select(c => c.ChunkId)).ToHashSet();
            var linked = version.LinkedCollectionIds().Where(view.Collections.ContainsKey).Select(c => view.Collections[c])
                .Select(c => new TestedCollectionRef(c.Id, c.Name, !notPublished.Contains(c.Id) && c.PublishedAt is not null)).ToList();
            var result = plan.Retrieval;
            return new AssistantTestResult(
                request.Target, version.Number,
                new TestRetrieval(linked, result?.Coverage, result?.Degraded,
                    [.. (result?.Results ?? []).Select(r => new TestCandidate(r.Rank, r.DocumentId, r.Citation.Title, r.Citation.Url ?? r.Citation.DisplayName, r.HeadingPath,
                        r.Text.Length <= TextLength ? r.Text : r.Text[..TextLength], r.Text.Length > TextLength, r.Scores.VectorDistance, r.Scores.KeywordRank,
                        r.Scores.VectorRank, r.Scores.Fused, citedChunks.Contains(r.ChunkId)))],
                    result?.Timings),
                new TestCondense(plan.Condense is CondenseOutcome.Rewritten, plan.Condense?.ToString()),
                new TestAnswer(answer?.Text, delivered, filtered, [.. cited.Select(s => new TestCitation(s.Number, s.Citation.Id, s.Citation.Title))],
                    ChatOutcomes.Name(plan.Outcome), plan.Language),
                new TestGateway(version.GatewayModel, ResidencyCeiling.ToHeader(plan.Ceiling), latencyMs, answer?.PromptTokens, answer?.CompletionTokens,
                    plan.Fixed is null ? plan.MaxTokens : null, plan.Fixed is null ? plan.MaxTokensSource : null, answer?.FinishReason),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (ChatException ex)
        {
            _runs.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
            throw AdminFaults.Of(AdminErrorCodes.TestAnswerFailed, new Dictionary<string, string> { ["reason"] = ex.Code });
        }
        catch (GatewayKeyMissingException missing)
        {
            _runs.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyMissing,
                new Dictionary<string, string> { ["model"] = version.GatewayModel, ["ceiling"] = ResidencyCeiling.ToHeader(missing.Ceiling) });
        }
    }

    /// <summary>
    /// The snapshot as the panel sees it: the tested version as the assistant's published one (active), its collections
    /// live, and the <c>admin-console</c> client at the assistant's level, or with test history at the level of the audience's
    /// history-storing clients (chat design §8.2). A distinct negative version keeps the per-instance caches apart.
    /// </summary>
    private static (PolicySnapshot View, ClientEntry Client, HashSet<Guid> NotPublished) View(PolicySnapshot current, Domain.Entities.Assistant assistant,
        Domain.Entities.AssistantVersion version, IReadOnlyList<Guid?> audience, bool withHistory)
    {
        var entry = current.Assistants.TryGetValue(assistant.Id, out var known)
            ? known
            : new AssistantEntry(assistant.Id, assistant.Slug, assistant.Name, assistant.Classification, assistant.HistoryMode, assistant.State, assistant.DepartmentId,
                assistant.OwnerTeamId, null, []);
        entry = entry with
        {
            State = Domain.Entities.AssistantStates.Active,
            GatewayKeyId = assistant.GatewayKeyId, // the key as saved now, as the test runs (B-42)
            Published = new PublishedVersionEntry(version.Id, version.Number, version.GatewayModel!, version.CollectionLinks, version.SystemPrompt, version.QuerySteps,
                version.NotFound, version.PublishedAt),
        };
        var collections = current.Collections.ToDictionary();
        var notPublished = new HashSet<Guid>();
        foreach (var id in version.LinkedCollectionIds())
        {
            // Only the assistant's own department's unpublished collections: a shared one stays invisible until its owner publishes it.
            if (collections.TryGetValue(id, out var c) && !c.Deleted && c.PublishedAt is null && c.DepartmentId == assistant.DepartmentId)
            {
                notPublished.Add(id);
                collections[id] = c with { PublishedAt = DateTimeOffset.UnixEpoch };
            }
        }

        var level = assistant.Classification;
        var storesHistory = false;
        if (withHistory)
        {
            foreach (var c in audience.OfType<Guid>().Where(current.Clients.ContainsKey).Select(c => current.Clients[c]).Where(c => c.StoresHistory))
            {
                storesHistory = true;
                level = Levels.Rank(c.Ceiling) > Levels.Rank(level) ? c.Ceiling : level;
            }
        }

        var client = new ClientEntry(Guid.CreateVersion7(), "admin-console", "internal", level, storesHistory, true);
        var clients = current.Clients.ToDictionary();
        clients[client.Id] = client;
        var assistants = current.Assistants.ToDictionary();
        assistants[assistant.Id] = entry;
        var view = current with { Version = -1 - Interlocked.Increment(ref _views), Clients = clients, Assistants = assistants, Collections = collections };
        return (view, client, notPublished);
    }

    private static long _views;

    public void Dispose() => _counter.Dispose();

    private sealed class FixedSnapshot(PolicySnapshot snapshot) : IPolicySnapshotSource
    {
        public PolicySnapshot Current => snapshot;

        public bool IsConfirmed => true;
    }

    /// <summary>Allows the tested assistant at its level; anything else is not the panel's (404, as the data plane says it).</summary>
    private sealed class PanelPolicy(Guid assistantId) : IAccessPolicy
    {
        public AccessDecision Decide(RagPrincipal who, ResourceRef what, RagAction action, PolicySnapshot config, bool confirmed)
        {
            ArgumentNullException.ThrowIfNull(who);
            ArgumentNullException.ThrowIfNull(what);
            ArgumentNullException.ThrowIfNull(config);
            if (what.Id != assistantId || !config.Assistants.TryGetValue(assistantId, out var assistant))
            {
                return AccessDecision.Deny("5", DenyReasons.SubjectNotAllowed, 404, config.Version);
            }

            var level = Levels.Rank(who.Client.Ceiling) > Levels.Rank(assistant.Classification) ? who.Client.Ceiling : assistant.Classification;
            return new AccessDecision(true, DenyReasons.None, 200, "", level, ResidencyCeiling.ForLevel(level), config.Version);
        }

        public IReadOnlyList<ResourceRef> Filter(RagPrincipal who, IEnumerable<ResourceRef> candidates, RagAction action, PolicySnapshot config, bool confirmed) =>
            [.. candidates.Where(c => Decide(who, c, action, config, confirmed).Allowed)];
    }
}

public enum AssistantTestTarget
{
    Draft,
    Published,
}

public sealed record TestHistoryMessage
{
    [Required, RegularExpression("^(user|assistant)$")]
    public string Role { get; init; } = "user";

    [Required, StringLength(4000, MinimumLength = 1)]
    public string Text { get; init; } = "";
}

public sealed record AssistantTestRequest
{
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Question { get; init; } = "";

    /// <summary>Up to 10 earlier messages, to test condense (§4.9).</summary>
    [MaxLength(10)]
    public IReadOnlyList<TestHistoryMessage> History { get; init; } = [];

    /// <summary><c>Draft</c> (default; the published version when there is no draft) or <c>Published</c>.</summary>
    public AssistantTestTarget Target { get; init; } = AssistantTestTarget.Draft;
}

public sealed record TestedCollectionRef(Guid Id, string Name, bool Published);

public sealed record TestCandidate(
    int Rank, Guid DocumentId, string Title, string? UrlOrFileName, string HeadingPath, string Text, bool Truncated, double? Distance, int? KeywordRank, int? VectorRank,
    double? Fused, bool Cited);

public sealed record TestRetrieval(IReadOnlyList<TestedCollectionRef> Collections, Coverage? Coverage, string? Degraded, IReadOnlyList<TestCandidate> Candidates,
    RetrievalTimings? Timings);

public sealed record TestCondense(bool Used, string? Outcome);

public sealed record TestCitation(int Number, Guid DocumentId, string Title);

/// <summary>The model's raw text and the delivered text (after the transducer's filters, with the caveat line), and what was filtered.</summary>
public sealed record TestAnswer(string? Raw, string Delivered, IReadOnlyDictionary<string, int> Filtered, IReadOnlyList<TestCitation> Citations, string Outcome,
    string Language);

/// <summary>
/// The answer call (admin design §4.9): <see cref="MaxTokens"/> the <c>max_tokens</c> sent (null when left out),
/// <see cref="MaxTokensSource"/> <c>override</c>/<c>model</c>/<c>provider</c>, <see cref="FinishReason"/> the gateway's
/// (<c>length</c>: the answer stopped at the limit). Decisions "Answer length from the model".
/// </summary>
public sealed record TestGateway(string Model, string Residency, double? LatencyMs, int? InputTokens, int? OutputTokens, int? MaxTokens = null,
    string? MaxTokensSource = null, string? FinishReason = null);

public sealed record AssistantTestResult(
    AssistantTestTarget Target, int VersionNumber, TestRetrieval Retrieval, TestCondense Condense, TestAnswer Answer, TestGateway Gateway, double DurationMs);
