using Kalla.Worker.Parsing;

namespace Kalla.Worker.Processing.Parse;

/// <summary>The parser's <c>build_hash</c> (parser design §13.1), part of every parser-bound parse key (§7.6).</summary>
public interface IParserBuild
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The build from <c>/v1/info</c>, read at most once per <see cref="MaxAge"/> (parser design §10.1: at start and every
/// minute). An unreachable parser with no build read yet is a capacity refusal (<c>parser.unavailable</c>, no attempt); once
/// read, the last build stays in force while the parser cannot be reached.
/// </summary>
public sealed class ParserInfoBuild(ParserClient client, TimeProvider time) : IParserBuild
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private volatile Snapshot? _snapshot;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_snapshot is { } current && now - current.ReadAt < MaxAge)
        {
            return current.Build;
        }

        try
        {
            var build = (await client.GetInfoAsync(cancellationToken)).BuildHash;
            _snapshot = new Snapshot(build, now);
            return build;
        }
        catch (HttpRequestException) when (_snapshot is { } last)
        {
            return last.Build;
        }
        catch (HttpRequestException)
        {
            throw new StageTransientException("parser.unavailable", TimeSpan.FromSeconds(5), countsAttempt: false);
        }
    }

    private sealed record Snapshot(string Build, DateTimeOffset ReadAt);
}
