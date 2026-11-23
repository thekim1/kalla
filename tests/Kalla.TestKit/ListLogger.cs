using Microsoft.Extensions.Logging;

namespace Kalla.TestKit;

/// <summary>
/// An <see cref="ILogger{T}"/> for code that takes its logger directly: keeps every entry at <paramref name="minimum"/> and
/// above (default all), without scopes. For a whole host's logs use <see cref="CapturingLoggerProvider"/>.
/// </summary>
public sealed class ListLogger<T>(LogLevel minimum = LogLevel.Trace) : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The formatted messages, in order.</summary>
    public IReadOnlyList<string> Messages => [.. Entries.Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < minimum)
        {
            return;
        }

        lock (_entries)
        {
            _entries.Add(new LogEntry(typeof(T).FullName!, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
