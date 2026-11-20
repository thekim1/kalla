using System.Text;
using BenchmarkDotNet.Attributes;
using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;

namespace Kalla.Benchmarks;

/// <summary>
/// The per-request access work of the data plane (identity design §4): hashing the presented API key (HMAC with the
/// pepper, before the cache lookup), one <see cref="AccessPolicy.Decide"/> for a chat turn, and the
/// <c>GET /v1/models</c> filter over every assistant. <see cref="Assistants"/> 10 is the pilot; 1,000 is 20 departments
/// with many assistants each (performance pass §2).
/// </summary>
[MemoryDiagnoser]
public class AccessBenchmarks
{
    private readonly AccessPolicy _policy = new(AccessPolicy.DefaultKnowledgeClientKinds);
    private PolicySnapshot _snapshot = null!;
    private RagPrincipal _who = null!;
    private ResourceRef _assistant = null!;
    private ResourceRef[] _all = null!;
    private KeyHasher _hasher = null!;
    private string _key = null!;

    [Params(10, 1000)]
    public int Assistants { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var owui = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var department = Guid.NewGuid();
        var clients = new Dictionary<Guid, ClientEntry> { [owui] = new(owui, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, true, true) };
        var collections = Enumerable.Range(0, Assistants).Select(i => new CollectionEntry(Guid.NewGuid(), $"c-{i}", $"Samling {i}", department,
            Classification.Public, profile, "{}", DateTimeOffset.UnixEpoch, false)).ToDictionary(c => c.Id);
        var ids = collections.Keys.ToArray();
        var assistants = Enumerable.Range(0, Assistants).Select(i =>
        {
            var id = Guid.NewGuid();
            return new AssistantEntry(id, $"a-{i}", $"Assistent {i}", Classification.Internal, HistoryModes.None, AssistantStates.Active, department, null,
                new PublishedVersionEntry(Guid.NewGuid(), 1, "fors/chat-eu", [CollectionLink.Whole(ids[i]), CollectionLink.Whole(ids[(i + 1) % ids.Length])]),
                [new AudienceEntry(AssistantAudienceKinds.Client, i % 2 == 0 ? owui : Guid.NewGuid(), null)]);
        }).ToDictionary(a => a.Id);
        _snapshot = PolicySnapshot.Empty with
        {
            Version = 42,
            Clients = clients,
            Assistants = assistants,
            Collections = collections,
            EmbeddingProfiles = new Dictionary<Guid, EmbeddingProfileEntry>
            {
                [profile] = new(profile, "pilot", "bge-m3", 1024, Residency.Eu, EmbeddingProfileStates.Active, null),
            },
            LevelPolicies = Classification.All.ToDictionary(l => l, l => new LevelPolicyEntry(l, [HistoryModes.None], l == Classification.Sensitive),
                StringComparer.Ordinal),
        };
        _who = new RagPrincipal(SubjectKind.OwuiUser, "owui:u-1", null, null, clients[owui], CredentialKind.SecretKey, null, new HashSet<string>(),
            MembershipFacts.None, _snapshot.Version);
        _assistant = ResourceRef.Assistant(assistants.Keys.First());
        _all = [.. assistants.Keys.Select(ResourceRef.Assistant)];
        _hasher = new KeyHasher(Encoding.UTF8.GetBytes("benchmark-pepper-0123456789abcdef0123456789"));
        _key = _hasher.Generate().PlainText;
    }

    [Benchmark(Description = "Hash presented API key (HMAC)")]
    public string HashKey() => _hasher.Hash(_key);

    [Benchmark(Description = "Decide: chat on one assistant")]
    public AccessDecision DecideChat() => _policy.Decide(_who, _assistant, RagAction.Chat, _snapshot, confirmed: true);

    [Benchmark(Description = "Filter: /v1/models over all assistants")]
    public IReadOnlyList<ResourceRef> ListModels() => _policy.Filter(_who, _all, RagAction.ListModels, _snapshot, confirmed: true);
}
