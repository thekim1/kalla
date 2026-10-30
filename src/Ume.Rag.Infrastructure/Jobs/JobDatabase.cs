using Npgsql;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// Connections for the queue's plain SQL, from the process's one <see cref="NpgsqlDataSource"/> for <c>ragdb</c>
/// (<c>AddRagDatabase</c>, issue #22), which <c>RagDbContext</c> uses too, so the process has one pool and stays within
/// its <c>Maximum Pool Size</c> (data-model §1.1). Every statement is pooler-safe: no session state.
/// </summary>
public sealed class JobDatabase(NpgsqlDataSource dataSource)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) => dataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>A positional parameter (<c>$n</c>).</summary>
    internal static NpgsqlParameter<T> Parameter<T>(T value) => new() { TypedValue = value };
}
