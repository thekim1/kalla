using Npgsql;

namespace Kalla.Retrieval.Postgres;

/// <summary>
/// The data source settings the retrieval statements need (knowledge design §6.6): pgvector types mapped
/// (<c>UseVector()</c>, Pgvector 0.3.2; to-verify #13) and auto-prepare on (<c>Max Auto Prepare</c> 64,
/// <c>Auto Prepare Min Usages</c> 2), so each statement variant is prepared once per connection after first use. A
/// value the connection string already sets is kept.
/// </summary>
public static class RetrievalDataSource
{
    public const int MaxAutoPrepare = 64;
    public const int AutoPrepareMinUsages = 2;

    public static NpgsqlDataSourceBuilder Configure(NpgsqlDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = builder.ConnectionStringBuilder;
        if (settings.MaxAutoPrepare == 0)
        {
            settings.MaxAutoPrepare = MaxAutoPrepare;
            settings.AutoPrepareMinUsages = AutoPrepareMinUsages;
        }

        builder.UseVector();
        return builder;
    }

    /// <summary>A data source for <paramref name="connectionString"/> with <see cref="Configure"/> applied.</summary>
    public static NpgsqlDataSource Create(string connectionString) => Configure(new NpgsqlDataSourceBuilder(connectionString)).Build();
}
