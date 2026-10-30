using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// Connections for the queue's plain SQL. They come from the same connection string as <c>RagDbContext</c>
/// (<c>ConnectionStrings:ragdb</c>, unchanged), so Npgsql serves both from one pool and the process stays within its
/// <c>Maximum Pool Size</c> (data-model §1.1). Every statement is pooler-safe: no session state.
/// </summary>
public sealed class JobDatabase(IConfiguration configuration)
{
    public string ConnectionString { get; } = configuration.GetConnectionString(InfrastructureExtensions.DatabaseResourceName)
        ?? throw new InvalidOperationException($"ConnectionStrings:{InfrastructureExtensions.DatabaseResourceName} is not configured.");

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>A positional parameter (<c>$n</c>).</summary>
    internal static NpgsqlParameter<T> Parameter<T>(T value) => new() { TypedValue = value };
}
