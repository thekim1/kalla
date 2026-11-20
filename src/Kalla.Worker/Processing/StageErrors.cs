using Kalla.Pipelines.Jobs;

namespace Kalla.Worker.Processing;

/// <summary>
/// A stage ends the document (engine design §5.1 <c>FatalError</c>, §7.3): <see cref="Outcome"/> is the run item's
/// outcome and <see cref="Reason"/> its <c>&lt;area&gt;.&lt;code&gt;</c> reason, never document text. The engine (item 9)
/// maps it to <c>NodeResult.FatalError</c>.
/// </summary>
public sealed class StageFatalException : Exception
{
    public StageFatalException()
    {
        Reason = "node.fatal";
    }

    public StageFatalException(string message)
        : base(message)
    {
        Reason = "node.fatal";
    }

    public StageFatalException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = "node.fatal";
    }

    public StageFatalException(RunItemOutcome outcome, string reason, bool securityEvent = false)
        : base($"The document ended with {outcome}: {reason}.")
    {
        Outcome = outcome;
        Reason = reason;
        SecurityEvent = securityEvent;
    }

    public RunItemOutcome Outcome { get; } = RunItemOutcome.Failed;

    public string Reason { get; }

    /// <summary>The engine also raises event 5104 (<c>limit_exceeded</c>, parser design §7.5).</summary>
    public bool SecurityEvent { get; }
}

/// <summary>
/// A stage asks for a retry (engine design §5.1 <c>TransientError</c>, §7.3): <see cref="CountsAttempt"/> false for
/// <c>busy</c>, <c>not_ready</c> and 429 answers, which requeue after <see cref="RetryAfter"/> without an attempt.
/// </summary>
public sealed class StageTransientException : Exception
{
    public StageTransientException()
    {
        Code = "node.transient";
    }

    public StageTransientException(string message)
        : base(message)
    {
        Code = "node.transient";
    }

    public StageTransientException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "node.transient";
    }

    public StageTransientException(string code, TimeSpan? retryAfter, bool countsAttempt)
        : base($"The stage asks for a retry: {code}.")
    {
        Code = code;
        RetryAfter = retryAfter;
        CountsAttempt = countsAttempt;
    }

    public string Code { get; }

    public TimeSpan? RetryAfter { get; }

    public bool CountsAttempt { get; } = true;
}
