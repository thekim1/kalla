using System.Diagnostics;
using OpenTelemetry;

namespace Ume.Rag.ServiceDefaults;

/// <summary>What a log or span may say about an exception without its messages (#232, as the worker's job log, #179).</summary>
public static class ExceptionText
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
}

/// <summary>
/// Keeps an outgoing request's host out of its span (#232): the gateway's address is a secret, and with an OTLP endpoint
/// spans leave the host. <c>Extensions.RedactOutgoing</c> cleans the tags; this cleans the failure path.
/// <list type="bullet">
/// <item>.NET's <c>System.Net.Http</c> activity records a failed request with <c>Activity.AddException</c>, whose message
/// and stack trace name the host and port ("Name or service not known (host:8443)"). The listener's
/// <see cref="ActivityListener.ExceptionRecorder"/> supplies <c>exception.message</c> and <c>exception.stacktrace</c> as the
/// type chain; .NET keeps tags a recorder supplies.</item>
/// <item><see cref="StatusScrubber"/> clears the span's status description.</item>
/// </list>
/// </summary>
public static class OutgoingHttpRedaction
{
    public const string HttpSource = "System.Net.Http";

    private static readonly Lazy<ActivityListener> Listener = new(() =>
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HttpSource,
            // Never makes an activity exist or recorded on its own: the tracer provider decides that.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            ExceptionRecorder = Record,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    });

    /// <summary>Registers the exception recorder once per process.</summary>
    public static void Register() => _ = Listener.Value;

    internal static void Record(Activity activity, Exception exception, ref TagList tags)
    {
        var types = ExceptionText.Types(exception);
        tags.Add("exception.message", types);
        tags.Add("exception.stacktrace", types);
    }

    /// <summary>Clears the status description of outgoing HTTP spans at their end (it could quote an error message).</summary>
    public sealed class StatusScrubber : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Source.Name == HttpSource && data.StatusDescription is not null)
            {
                data.SetStatus(data.Status);
            }
        }
    }
}
