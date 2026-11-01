using System.Diagnostics;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// What a job log may say about an exception without its message, which can carry content (engine design §7.3,
/// repo-and-deployment §9): the type chain, outermost first (<c>System.AggregateException ← System.IO.IOException</c>), and
/// the innermost exception's throw site as <c>DeclaringType.Method</c> from its stack trace. Neither holds document text.
/// </summary>
public static class ExceptionSummary
{
    private const int MaxDepth = 5;

    /// <summary>The exception types from outermost to innermost, joined with <c> ← </c> (at most five).</summary>
    public static string Types(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var types = new List<string>();
        for (var e = exception; e is not null && types.Count < MaxDepth; e = e.InnerException)
        {
            types.Add(e.GetType().FullName ?? e.GetType().Name);
        }

        return string.Join(" ← ", types);
    }

    /// <summary>The innermost exception's first stack frame as <c>Namespace.Type.Method</c>, or <c>unknown</c> without a trace.</summary>
    public static string Site(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var innermost = exception;
        for (var depth = 1; innermost.InnerException is { } inner && depth < MaxDepth; depth++)
        {
            innermost = inner;
        }

        foreach (var frame in new StackTrace(innermost, fNeedFileInfo: false).GetFrames())
        {
            if (frame.GetMethod() is { } method)
            {
                var type = method.DeclaringType;
                // An async method runs in its state machine <Method>d__N; name the method, not the machine.
                if (type is not null && type.Name.StartsWith('<') && type.DeclaringType is { } owner)
                {
                    var name = type.Name[1..type.Name.IndexOf('>', StringComparison.Ordinal)];
                    return $"{owner.FullName}.{name}";
                }

                return type is null ? method.Name : $"{type.FullName}.{method.Name}";
            }
        }

        return "unknown";
    }
}
