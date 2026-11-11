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

    /// <summary>
    /// <c>pg_notify('rag_jobs', class)</c> for each class (data-model §4.3), delivered at the commit of
    /// <paramref name="transaction"/>: a job of the class may be ready, or ready sooner than the workers' idle lanes planned.
    /// </summary>
    internal static async Task NotifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<string> classes,
        CancellationToken cancellationToken)
    {
        if (classes.Count == 0)
        {
            return;
        }

        await using var notify = new NpgsqlCommand("""SELECT pg_notify('rag_jobs', c) FROM unnest($1::text[]) AS c""", connection, transaction)
        {
            Parameters = { Parameter(classes.ToArray()) },
        };
        await notify.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A positional parameter (<c>$n</c>).</summary>
    internal static NpgsqlParameter<T> Parameter<T>(T value) => new() { TypedValue = value };
}
