using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.Api;

/// <summary><c>Api:AuthFailuresPerMinute</c> (60, 0 = off) and <c>Api:Security:SourceAddress</c> (<c>Truncated</c> or <c>Full</c>), identity §6.3, §10.</summary>
public sealed class ApiSecurityOptions
{
    public int AuthFailuresPerMinute { get; set; } = 60;

    public string SourceAddress { get; set; } = "Truncated";
}

/// <summary>
/// The knowledge role's security events and auth-failure throttle (knowledge design §4.7, identity design §6.3, §10):
/// <list type="bullet">
/// <item>5001 per bucket (reason, credential, endpoint, key, address) once a minute with the count since the last one;
/// failures held back after the last emit are flushed by <see cref="FlushAuthFailures"/> once the minute has passed
/// (<see cref="KnowledgeSecurityFlusher"/>, every 10 s), so a short burst that then stops is still counted;</item>
/// <item>the per-address window: above <c>AuthFailuresPerMinute</c> refused credentials a minute only cached keys are
/// accepted (the caller answers 429 otherwise), with 5006 once per address per window (IPv6 per /64, ≤ 100,000 addresses);</item>
/// <item>5002 (5005 for a ceiling denial) once a minute per (subject, reason, resource) for explicit denials;</item>
/// <item>5004 through <see cref="ConfigVersionState.RecordStaleRefusal"/>;</item>
/// <item><c>rag.documents.downloads{outcome, kind}</c> and an Information line per download (no subject, a grant has none);
/// a served body counts <c>ok</c> once fully read and <c>integrity</c> when a segment fails its check mid-stream
/// (<see cref="CountDownload"/>).</item>
/// </list>
/// Nothing here logs a presented key, a grant or a query.
/// </summary>
public sealed partial class KnowledgeSecurity
{
    private const int MaxTracked = 100_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ILogger _events;
    private readonly ILogger<KnowledgeSecurity> _log;
    private readonly ConfigVersionState _state;
    private readonly TimeProvider _time;
    private readonly ApiSecurityOptions _options;
    private readonly Counter<long> _downloads;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AddressWindow> _addresses = new(StringComparer.Ordinal);

    public KnowledgeSecurity(ILoggerFactory loggers, ConfigVersionState state, TimeProvider time, IOptions<ApiSecurityOptions> options, IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
        _events = SecurityEvents.CreateLogger(loggers);
        _log = loggers.CreateLogger<KnowledgeSecurity>();
        _state = state;
        _time = time;
        _options = options.Value;
        _downloads = meters.Create(Extensions.TelemetryName).CreateCounter<long>("rag.documents.downloads", description: "Document content downloads by outcome and credential kind");
    }

    /// <summary>The address as events record it (<c>Api:Security:SourceAddress</c>).</summary>
    public string? AddressOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return SecurityEvents.SourceAddress(http.Connection.RemoteIpAddress, !string.Equals(_options.SourceAddress, "Full", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether this address is over <c>AuthFailuresPerMinute</c> in the current minute (then only cached keys count).</summary>
    public bool IsThrottled(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (_options.AuthFailuresPerMinute <= 0 || !_addresses.TryGetValue(ThrottleKey(http), out var window))
        {
            return false;
        }

        lock (window)
        {
            return _time.GetUtcNow() - window.Start < Window && window.Failures > _options.AuthFailuresPerMinute;
        }
    }

    /// <summary>A refused credential: the bucket's 5001 and the address window (5006 when it crosses the limit).</summary>
    public void AuthFailed(HttpContext http, string reason, string credential, Guid? keyId = null, string? keyPrefix = null, Guid? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var now = _time.GetUtcNow();
        var endpoint = EndpointOf(http);
        var address = AddressOf(http);
        CountAddress(http, endpoint, address, now);

        var key = string.Join('|', reason, credential, endpoint, keyId?.ToString("N") ?? "", address ?? "");
        if (_buckets.Count >= MaxTracked)
        {
            _buckets.Clear();
        }

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { FirstSeen = now });
        int count;
        DateTimeOffset first;
        lock (bucket)
        {
            bucket.Failure ??= new AuthFailure(reason, credential, endpoint, address, keyId, keyPrefix, clientId);
            bucket.Pending++;
            bucket.LastSeen = now;
            if (bucket.Pending == 1)
            {
                bucket.FirstSeen = now;
            }

            if (now - bucket.LoggedAt < Window)
            {
                return;
            }

            count = bucket.Pending;
            first = bucket.FirstSeen;
            bucket.Pending = 0;
            bucket.LoggedAt = now;
        }

        SecurityEvents.AuthFailed(_events, reason, credential, endpoint, address, keyId, keyPrefix, clientId, count, first, now);
    }

    /// <summary>
    /// Emits 5001 for every bucket whose failures were held back and whose minute has passed since its last emit, with
    /// the count, first and last time of the held-back failures; drops buckets idle for a minute. Run every 10 s.
    /// </summary>
    public int FlushAuthFailures()
    {
        var now = _time.GetUtcNow();
        var flushed = 0;
        foreach (var (key, bucket) in _buckets)
        {
            AuthFailure? failure;
            int count;
            DateTimeOffset first, last;
            lock (bucket)
            {
                if (now - bucket.LoggedAt < Window)
                {
                    continue;
                }

                if (bucket.Pending == 0)
                {
                    _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket));
                    continue;
                }

                if (bucket.Failure is null)
                {
                    continue; // a 5002/5005 bucket: those report on the next denial (one per resource is enough)
                }

                failure = bucket.Failure;
                count = bucket.Pending;
                first = bucket.FirstSeen;
                last = bucket.LastSeen;
                bucket.Pending = 0;
                bucket.LoggedAt = now;
            }

            SecurityEvents.AuthFailed(_events, failure.Reason, failure.Credential, failure.Endpoint, failure.Address, failure.KeyId, failure.KeyPrefix,
                failure.ClientId, count, first, last);
            flushed++;
        }

        return flushed;
    }

    /// <summary>A <c>config_unconfirmed</c> refusal (5004, throttled by the state).</summary>
    public void StaleRefused() => _state.RecordStaleRefusal();

    /// <summary>
    /// 5002, or 5005 for a ceiling denial (identity §4.3 step 7), for an explicit denial with its real reason; the
    /// subject is the principal, or for a grant its client. At most once a minute per (subject, reason, resource).
    /// </summary>
    public void Denied(HttpContext http, RetrievalDeniedException denied, RagPrincipal? who, DownloadGrant? grant, PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(denied);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!denied.IsAccessDenial)
        {
            return;
        }

        var subjectKind = who?.Kind.ToString() ?? (grant is not null ? nameof(CredentialKind.DownloadGrant) : "unknown");
        var subjectId = who?.SubjectId ?? grant?.KeyId?.ToString("D") ?? grant?.ClientId.ToString("D") ?? "-";
        var clientId = who?.Client.Id ?? grant?.ClientId;
        var keyId = who?.Key?.Id ?? grant?.KeyId;
        var key = string.Join('|', "deny", subjectKind, subjectId, denied.Reason, denied.ResourceType, denied.ResourceId);
        if (!Due(key, out var count))
        {
            return;
        }

        var address = AddressOf(http);
        var ceiling = denied.Step == "7" || denied.Reason == DenyReasons.CeilingExceeded;
        if (ceiling)
        {
            var level = denied.ResourceType == "collection" && Guid.TryParse(denied.ResourceId, out var cid) && snapshot.Collections.TryGetValue(cid, out var c)
                ? c.Classification
                : "unknown";
            var clientCeiling = clientId is { } id && snapshot.Clients.TryGetValue(id, out var client) ? client.Ceiling : "unknown";
            SecurityEvents.CeilingRefused(_events, denied.ResourceType!, denied.ResourceId!, level, clientCeiling, denied.Action ?? "-", subjectKind, subjectId,
                clientId, keyId, snapshot.Version, address, denied.Reason!, count);
        }
        else
        {
            SecurityEvents.AccessDenied(_events, denied.Reason!, denied.Action ?? "-", denied.ResourceType!, denied.ResourceId!, subjectKind, subjectId, clientId,
                keyId, snapshot.Version, address, count);
        }
    }

    /// <summary>
    /// 5002 with reason <c>conversation_not_owned</c> (chat design §11.1, identity design; #284): a conversation id refused
    /// for this subject, on a chat turn, the conversation detail or a delete. The resource id is the conversation's id when
    /// the presented value names one, never content; throttled like every denial, per action.
    /// </summary>
    public void ConversationNotOwned(HttpContext http, RagPrincipal who, string presented, string action, long configVersion)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(presented);
        var id = (presented.StartsWith(ConversationIds.Prefix, StringComparison.Ordinal) ? ConversationIds.Parse(presented) : Guid.TryParse(presented, out var g) ? g : null)
            ?.ToString("D") ?? "invalid";
        const string reason = "conversation_not_owned";
        var key = string.Join('|', "deny", who.Kind.ToString(), who.SubjectId, reason, "conversation", id, action);
        if (!Due(key, out var count))
        {
            return;
        }

        SecurityEvents.AccessDenied(_events, reason, action, "conversation", id, who.Kind.ToString(), who.SubjectId, who.Client.Id, who.Key?.Id, configVersion,
            AddressOf(http), count);
    }

    /// <summary>
    /// A download's outcome (<c>ok</c>, <c>integrity</c>, <c>not_found</c>, <c>not_served</c>, <c>unavailable</c>,
    /// <c>missing</c>) and credential kind.
    /// </summary>
    public void Download(string outcome, string kind, Guid documentId, Guid? clientId)
    {
        _downloads.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("kind", kind));
        if (outcome == "ok")
        {
            LogDownload(_log, documentId, clientId, kind);
        }
    }

    /// <summary>
    /// The served body, counted when it ends: <c>ok</c> once read to its end, <c>integrity</c> (Error logged) when the
    /// envelope finds a damaged segment mid-stream, which then aborts the response (knowledge §3.5). A body the client
    /// stops reading counts neither.
    /// </summary>
    public Stream CountDownload(Stream body, long? length, string kind, Guid documentId, Guid? clientId) =>
        new CountedDownloadStream(body, length, outcome =>
        {
            if (outcome == "integrity")
            {
                LogIntegrity(_log, documentId, kind);
            }

            Download(outcome, kind, documentId, clientId);
        });

    /// <summary>The route template (never the path: it would carry ids, and the query a grant).</summary>
    public static string EndpointOf(HttpContext http) =>
        (http?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";

    private bool Due(string key, out int count)
    {
        var now = _time.GetUtcNow();
        if (_buckets.Count >= MaxTracked)
        {
            _buckets.Clear();
        }

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (bucket)
        {
            bucket.Pending++;
            if (now - bucket.LoggedAt < Window)
            {
                count = 0;
                return false;
            }

            count = bucket.Pending;
            bucket.Pending = 0;
            bucket.LoggedAt = now;
            return true;
        }
    }

    private void CountAddress(HttpContext http, string endpoint, string? address, DateTimeOffset now)
    {
        if (_options.AuthFailuresPerMinute <= 0)
        {
            return;
        }

        if (_addresses.Count >= MaxTracked)
        {
            _addresses.Clear();
        }

        var window = _addresses.GetOrAdd(ThrottleKey(http), _ => new AddressWindow { Start = now });
        int crossed;
        lock (window)
        {
            if (now - window.Start >= Window)
            {
                window.Start = now;
                window.Failures = 0;
            }

            window.Failures++;
            crossed = window.Failures == _options.AuthFailuresPerMinute + 1 ? window.Failures : 0;
        }

        if (crossed > 0)
        {
            SecurityEvents.AuthThrottled(_events, address, endpoint, crossed);
        }
    }

    /// <summary>The throttle's key: the full IPv4 address, or the IPv6 /64 (one host easily holds many /128s).</summary>
    private static string ThrottleKey(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }

    [LoggerMessage(LogLevel.Information, "Document {DocumentId} downloaded by client {ClientId} with {CredentialKind}")]
    private static partial void LogDownload(ILogger logger, Guid documentId, Guid? clientId, string credentialKind);

    [LoggerMessage(LogLevel.Error, "Document {DocumentId}'s blob failed its integrity check mid-download ({CredentialKind}); the response was aborted")]
    private static partial void LogIntegrity(ILogger logger, Guid documentId, string credentialKind);

    private sealed record AuthFailure(string Reason, string Credential, string Endpoint, string? Address, Guid? KeyId, string? KeyPrefix, Guid? ClientId);

    private sealed class Bucket
    {
        public AuthFailure? Failure;
        public int Pending;
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
        public DateTimeOffset LoggedAt = DateTimeOffset.MinValue;
    }

    private sealed class AddressWindow
    {
        public DateTimeOffset Start;
        public int Failures;
    }
}
