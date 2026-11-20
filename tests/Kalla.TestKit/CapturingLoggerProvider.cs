using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Kalla.TestKit;

/// <summary>
/// One captured log entry with the scopes it was written in; <see cref="ToString"/> is <c>category|event name|message</c>,
/// then the exception on its own lines.
/// </summary>
public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception, IReadOnlyList<object?>? Scopes = null)
{
    public override string ToString() =>
        $"{Category}|{EventId.Name}|{Message}" + (Exception is null ? string.Empty : "\n" + Exception);

    /// <summary>The value of <paramref name="key"/> in the entry's scopes (key-value scopes such as a dictionary), or null.</summary>
    public object? ScopeValue(string key) =>
        Scopes?.OfType<IEnumerable<KeyValuePair<string, object>>>().SelectMany(s => s).Where(p => p.Key == key).Select(p => p.Value).LastOrDefault();
}

/// <summary>Keeps every log entry of a host, at every level, with its scopes, so tests can check what is (and is not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    /// <summary>All entries, one <see cref="LogEntry.ToString"/> per line.</summary>
    public string Text => string.Join("\n", _entries);

    /// <summary>Whether an entry of <paramref name="category"/> contains <paramref name="text"/> in its message.</summary>
    public bool Logged(string category, string text) =>
        _entries.Any(e => e.Category == category && e.Message.Contains(text, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            List<object?>? scopes = null;
            provider._scopes?.ForEachScope((scope, list) => list.Add(scope), scopes = []);
            provider._entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception, scopes));
        }
    }
}
