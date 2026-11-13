using Microsoft.Extensions.Options;

namespace Ume.Rag.TestKit;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> with one fixed value, for code that reads its options as they change.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
