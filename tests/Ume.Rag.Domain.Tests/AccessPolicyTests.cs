using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Domain.Tests;

/// <summary>Identity design §4.3 steps 0–11 as a table (the policy is pure, §4.2): the first failing step decides.</summary>
public sealed class AccessPolicyTests
{
    private static readonly Guid OwuiId = Guid.NewGuid(), InternalId = Guid.NewGuid(), IntegrationId = Guid.NewGuid(), WidgetId = Guid.NewGuid();
    private static readonly Guid PublicAssistant = Guid.NewGuid(), InternalAssistant = Guid.NewGuid(), SensitiveAssistant = Guid.NewGuid();
    private static readonly Guid Unpublished = Guid.NewGuid(), OtherAudience = Guid.NewGuid(), DepartmentAudience = Guid.NewGuid();
    private static readonly Guid PublicCollection = Guid.NewGuid(), SensitiveCollection = Guid.NewGuid(), DeletedCollection = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();

    private static readonly AccessPolicy Policy = new(AccessPolicy.DefaultKnowledgeClientKinds);

    private static readonly PolicySnapshot Config = Build();

    private static PolicySnapshot Build()
    {
        var clients = new[]
        {
            new ClientEntry(OwuiId, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, StoresHistory: true, IsEnabled: true),
            new ClientEntry(InternalId, "rag-internal", ClientKinds.Internal, Classification.Sensitive, false, true),
            new ClientEntry(IntegrationId, "Integration", ClientKinds.Integration, Classification.Internal, false, true),
            new ClientEntry(WidgetId, "Widget", ClientKinds.Widget, Classification.Public, false, true),
        }.ToDictionary(c => c.Id);
        PublishedVersionEntry Version() => new(Guid.NewGuid(), 1, "ume/chat-eu", [CollectionLink.Whole(PublicCollection)]);
        AssistantEntry Assistant(Guid id, string level, PublishedVersionEntry? version, params AudienceEntry[] audiences) =>
            new(id, id.ToString("N"), "A", level, HistoryModes.None, AssistantStates.Active, Department, null, version, audiences);
        AudienceEntry ClientAudience(Guid client) => new(AssistantAudienceKinds.Client, client, null);
        var everyone = new[] { ClientAudience(OwuiId), ClientAudience(InternalId), ClientAudience(IntegrationId), ClientAudience(WidgetId) };
        var assistants = new[]
        {
            Assistant(PublicAssistant, Classification.Public, Version(), everyone),
            Assistant(InternalAssistant, Classification.Internal, Version(), everyone),
            Assistant(SensitiveAssistant, Classification.Sensitive, Version(), everyone),
            Assistant(Unpublished, Classification.Public, null, everyone),
            Assistant(OtherAudience, Classification.Public, Version(), ClientAudience(Guid.NewGuid())),
            Assistant(DepartmentAudience, Classification.Public, Version(), new AudienceEntry("department", Department, null)),
        }.ToDictionary(a => a.Id);
        CollectionEntry Collection(Guid id, string level, bool deleted = false) =>
            new(id, id.ToString("N"), "C", Department, level, Guid.NewGuid(), "{}", DateTimeOffset.UnixEpoch, deleted);
        var collections = new[]
        {
            Collection(PublicCollection, Classification.Public), Collection(SensitiveCollection, Classification.Sensitive),
            Collection(DeletedCollection, Classification.Public, deleted: true),
        }.ToDictionary(c => c.Id);
        var levels = new[]
        {
            new LevelPolicyEntry(Classification.Public, [HistoryModes.None], false),
            new LevelPolicyEntry(Classification.Internal, [HistoryModes.None], false),
            new LevelPolicyEntry(Classification.Sensitive, [HistoryModes.None], ClientsWithoutHistoryOnly: true),
        }.ToDictionary(l => l.Classification, StringComparer.Ordinal);
        return PolicySnapshot.Empty with { Version = 7, Clients = clients, Assistants = assistants, Collections = collections, LevelPolicies = levels };
    }

    private static RagPrincipal Who(Guid client, CredentialKind credential, SubjectKind subject = SubjectKind.Application, MembershipFacts? membership = null,
        KeyEntry? key = null, params string[] permissions) =>
        new(subject, "s", null, null, Config.Clients[client], credential, key, new HashSet<string>(permissions), membership ?? MembershipFacts.None, 7);

    public static TheoryData<string, RagPrincipal, ResourceRef, RagAction, bool, string, int> Cases() => new()
    {
        { "open-webui chats with an internal assistant", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Assistant(InternalAssistant), RagAction.Chat, true, "ok", 200 },
        { "1a: open-webui may not search", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Collection(PublicCollection), RagAction.Search, false, "1a", 403 },
        { "2: open-webui with a token", Who(OwuiId, CredentialKind.AccessToken, SubjectKind.OwuiUser, permissions: "Chat.Use"), ResourceRef.Assistant(PublicAssistant), RagAction.Chat, false, "2", 403 },
        { "3: anonymous outside a widget", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.Anonymous), ResourceRef.Assistant(PublicAssistant), RagAction.Chat, false, "3", 403 },
        { "3: a widget subject that is not anonymous", Who(WidgetId, CredentialKind.PublicKey, SubjectKind.OwuiUser), ResourceRef.Assistant(PublicAssistant), RagAction.Chat, false, "3", 403 },
        { "4: a pk key may not call tools", Who(WidgetId, CredentialKind.PublicKey, SubjectKind.Anonymous), ResourceRef.Assistant(PublicAssistant), RagAction.ToolCall, false, "4", 403 },
        { "5: no published version", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Assistant(Unpublished), RagAction.Chat, false, "5", 404 },
        { "5: an unknown assistant", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Assistant(Guid.NewGuid()), RagAction.Chat, false, "5", 404 },
        { "5: a deleted collection", Who(InternalId, CredentialKind.ServiceKey), ResourceRef.Collection(DeletedCollection), RagAction.Search, false, "5", 404 },
        { "6: outside the key's assistant scope", Who(IntegrationId, CredentialKind.SecretKey, key: new KeyEntry(Guid.NewGuid(), IntegrationId, "sk", null, [InternalAssistant], [])), ResourceRef.Assistant(PublicAssistant), RagAction.Chat, false, "6", 404 },
        { "7: above the client ceiling", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Assistant(SensitiveAssistant), RagAction.Chat, false, "7", 404 },
        { "7: a widget (public ceiling) and an internal assistant", Who(WidgetId, CredentialKind.PublicKey, SubjectKind.Anonymous), ResourceRef.Assistant(InternalAssistant), RagAction.Chat, false, "7", 404 },
        { "9: sensitive needs a client without history", Who(InternalId, CredentialKind.ServiceKey), ResourceRef.Assistant(SensitiveAssistant), RagAction.Chat, true, "ok", 200 },
        { "10: no audience row for this client", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser), ResourceRef.Assistant(OtherAudience), RagAction.Chat, false, "10", 404 },
        { "10: an application never matches a department row", Who(IntegrationId, CredentialKind.SecretKey), ResourceRef.Assistant(DepartmentAudience), RagAction.Chat, false, "10", 404 },
        { "11: a department row with unknown membership", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser, new MembershipFacts(new HashSet<Guid>(), new HashSet<string>(), Known: false)), ResourceRef.Assistant(DepartmentAudience), RagAction.Chat, false, "11", 503 },
        { "10: a department row with that department", Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser, new MembershipFacts(new HashSet<Guid> { Department }, new HashSet<string>(), true)), ResourceRef.Assistant(DepartmentAudience), RagAction.Chat, true, "ok", 200 },
        { "internal searches a sensitive collection", Who(InternalId, CredentialKind.ServiceKey), ResourceRef.Collection(SensitiveCollection), RagAction.Search, true, "ok", 200 },
        { "2: internal with a secret key", Who(InternalId, CredentialKind.SecretKey), ResourceRef.Collection(PublicCollection), RagAction.Search, false, "2", 403 },
        { "a grant reads a document of its collection", Who(OwuiId, CredentialKind.DownloadGrant), ResourceRef.Collection(PublicCollection), RagAction.ReadDocument, true, "ok", 200 },
        { "4: a grant never searches", Who(OwuiId, CredentialKind.DownloadGrant), ResourceRef.Collection(PublicCollection), RagAction.Search, false, "4", 403 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_first_failing_step_decides(string name, RagPrincipal who, ResourceRef what, RagAction action, bool allowed, string step, int status)
    {
        var decision = Policy.Decide(who, what, action, Config, confirmed: true);

        decision.Allowed.ShouldBe(allowed, name);
        decision.Step.ShouldBe(step, name);
        decision.HttpStatus.ShouldBe(status, name);
        decision.ConfigVersion.ShouldBe(7);
    }

    [Fact]
    public void An_unconfirmed_snapshot_refuses_everything_with_503()
    {
        var decision = Policy.Decide(Who(InternalId, CredentialKind.ServiceKey), ResourceRef.Collection(PublicCollection), RagAction.Search, Config, confirmed: false);

        decision.ShouldBe(AccessDecision.Deny("0", DenyReasons.ConfigUnconfirmed, 503, 7));
    }

    [Fact]
    public void Allowed_decisions_carry_the_level_and_its_residency_ceiling()
    {
        var who = Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser);
        var publicDecision = Policy.Decide(who, ResourceRef.Assistant(PublicAssistant), RagAction.Chat, Config, true);
        var internalDecision = Policy.Decide(who, ResourceRef.Assistant(InternalAssistant), RagAction.Chat, Config, true);

        (publicDecision.EffectiveLevel, publicDecision.MaxResidency).ShouldBe((Classification.Public, Residency.External));
        (internalDecision.EffectiveLevel, internalDecision.MaxResidency).ShouldBe((Classification.Internal, Residency.Eu));
    }

    [Fact]
    public void A_disabled_or_unparsable_client_and_a_foreign_key_are_refused_at_step_1()
    {
        var disabled = Config with { Clients = new Dictionary<Guid, ClientEntry>(Config.Clients) { [OwuiId] = Config.Clients[OwuiId] with { IsEnabled = false } } };
        var bad = Config with { Clients = new Dictionary<Guid, ClientEntry>(Config.Clients) { [OwuiId] = Config.Clients[OwuiId] with { Ceiling = "secret" } } };
        var who = Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser);

        Policy.Decide(who, ResourceRef.Assistant(PublicAssistant), RagAction.Chat, disabled, true).Reason.ShouldBe(DenyReasons.ClientDisabled);
        Policy.Decide(who, ResourceRef.Assistant(PublicAssistant), RagAction.Chat, bad, true).Reason.ShouldBe(DenyReasons.ClientDisabled);
        Policy.Decide(who with { Key = new KeyEntry(Guid.NewGuid(), Guid.NewGuid(), "sk", null, [], []) }, ResourceRef.Assistant(PublicAssistant), RagAction.Chat, Config, true)
            .Reason.ShouldBe(DenyReasons.ClientUnknown);
    }

    [Fact]
    public void Filter_keeps_only_allowed_resources()
    {
        var who = Who(OwuiId, CredentialKind.SecretKey, SubjectKind.OwuiUser);
        var all = Config.Assistants.Keys.Select(ResourceRef.Assistant).ToList();

        Policy.Filter(who, all, RagAction.ListModels, Config, true).Select(r => r.Id)
            .ShouldBe([PublicAssistant, InternalAssistant], ignoreOrder: true);
    }
}
