using Kalla.Worker.Parsing;

namespace Kalla.Worker.Processing.Parse;

/// <summary>
/// The per-range results of one paged parse (<c>parse/{level}/{key}/ranges/</c>, engine design §8, data-model §9), written
/// as each range completes, so a yielded or crashed <c>parse</c> job resumes at the next range instead of page 1. Only
/// results are stored, never failures: a range that failed is asked again, so a passing parser problem never sticks.
/// </summary>
public interface IParseRangeStore
{
    Task<ParserConvertResult?> GetAsync(ParseRange range, CancellationToken cancellationToken);

    Task PutAsync(ParseRange range, ParserConvertResult result, CancellationToken cancellationToken);
}

/// <summary>The slice budget of one <c>parse</c> job (engine design §9.5: 10 minutes), measured from its start.</summary>
public sealed class ParseSliceClock(TimeProvider time, TimeSpan budget)
{
    private readonly long _start = time.GetTimestamp();

    public bool Expired => time.GetElapsedTime(_start) >= budget;
}

/// <summary>
/// What a paged parse inside a <c>parse</c> job carries: the range store and, when the job may yield, its slice clock. The
/// route yields only at a range boundary and only after this job has itself stored a converted range, so every slice makes
/// progress (a failed request does not count: it is asked again in the next slice).
/// </summary>
public sealed class ParseProgress(IParseRangeStore ranges, ParseSliceClock? slice = null)
{
    private int _converted;

    public IParseRangeStore Ranges => ranges;

    /// <summary>True when the next range should wait for the next slice.</summary>
    public bool ShouldYield => slice is { Expired: true } && _converted > 0;

    public void Converted() => _converted++;
}

/// <summary>
/// Thrown by the parser route at a range boundary when the slice is used up (engine design §9.5): the completed ranges are
/// stored, and the <c>parse</c> job re-enqueues itself with <c>slice + 1</c>.
/// </summary>
public sealed class ParseYieldException : Exception
{
    public ParseYieldException()
        : base("The parse slice is used up; the job continues in its next slice.")
    {
    }

    public ParseYieldException(string message)
        : base(message)
    {
    }

    public ParseYieldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
