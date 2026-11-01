using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Retrieval.Postgres;

/// <summary>Statement limits of the search core (knowledge design §6.6).</summary>
public sealed class PostgresSearchOptions
{
    /// <summary>
    /// Client-side limit per statement (§6.6: 2 s); a statement over it is cancelled and the search answers
    /// <c>search_unavailable</c>. The role's <c>statement_timeout</c> (5 s for the api) is the server-side backstop.
    /// </summary>
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Statements K and V on Postgres (knowledge design §6.2, §6.4) and the chunk counts for the exact/HNSW rule. Each call
/// takes its own pooled connection, so K and V of one search overlap; no transaction (autocommit), no <c>SET</c> (the
/// <c>hnsw.*</c> settings are role defaults, data-model §1.1). The data source must map pgvector types
/// (<see cref="RetrievalDataSource.Configure"/>).
/// </summary>
public sealed class PostgresSearchIndex(NpgsqlDataSource dataSource, PostgresSearchOptions? options = null) : ISearchIndex, IScopeSizeSource
{
    private readonly PostgresSearchOptions _options = options ?? new PostgresSearchOptions();

    public Task<IReadOnlyList<Candidate>> KeywordAsync(
        SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(querySearchText);
        var fragments = RetrievalStatements.FragmentsOf(filters);
        var sql = RetrievalStatements.Keyword(scope.EmbeddingProfileId, limit, fragments);
        var stop = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = scope.KeywordStopLexemes.ToArray() };
        return ReadCandidatesAsync(sql, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = querySearchText }, scope, filters,
            fragments, keyword: true, cancellationToken, stop);
    }

    public Task<IReadOnlyList<Candidate>> VectorAsync(
        SearchScope scope, ReadOnlyMemory<float> vector, RetrievalFilters? filters, int limit, VectorSearch search,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (vector.Length != scope.Dimensions)
        {
            throw new ArgumentException($"The vector has {vector.Length} dimensions; the profile has {scope.Dimensions}.", nameof(vector));
        }

        var fragments = RetrievalStatements.FragmentsOf(filters);
        var sql = search == VectorSearch.Exact
            ? RetrievalStatements.VectorExact(scope.EmbeddingProfileId, scope.Dimensions, limit, fragments)
            : RetrievalStatements.VectorApproximate(scope.EmbeddingProfileId, scope.Dimensions, limit, fragments);
        return ReadCandidatesAsync(sql, new NpgsqlParameter { Value = new Vector(vector) }, scope, filters, fragments, keyword: false,
            cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken)
    {
        var counts = new List<CollectionChunkCount>();
        await RunAsync(RetrievalStatements.CountChunks, [], async (reader, ct) =>
        {
            while (await reader.ReadAsync(ct))
            {
                counts.Add(new CollectionChunkCount(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)));
            }
        }, TimeSpan.FromSeconds(30), cancellationToken);
        return counts;
    }

    public async Task<IReadOnlyList<FrequentLexeme>> FrequentLexemesAsync(long minChunks, double minShare, CancellationToken cancellationToken)
    {
        var lexemes = new List<FrequentLexeme>();
        await RunAsync(RetrievalStatements.FrequentLexemes,
            [
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = minChunks },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = minShare },
            ],
            async (reader, ct) =>
            {
                while (await reader.ReadAsync(ct))
                {
                    lexemes.Add(new FrequentLexeme(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3)));
                }
            }, TimeSpan.FromSeconds(60), cancellationToken);
        return lexemes;
    }

    /// <summary>
    /// The parameters <c>$2</c>, <c>$3</c>, then <paramref name="afterLevels"/> (K's stop list) and the present filters,
    /// after <paramref name="first"/>.
    /// </summary>
    internal static List<NpgsqlParameter> Parameters(
        NpgsqlParameter first, SearchScope scope, RetrievalFilters? filters, FilterFragments fragments, NpgsqlParameter? afterLevels = null)
    {
        var parameters = new List<NpgsqlParameter>
        {
            first,
            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = scope.CollectionIds.ToArray() },
            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = scope.AllowedLevels.ToArray() },
        };
        if (afterLevels is not null)
        {
            parameters.Add(afterLevels);
        }

        if (fragments.HasFlag(FilterFragments.Tags))
        {
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = filters!.Tags!.ToArray() });
        }

        if (fragments.HasFlag(FilterFragments.DocumentTypes))
        {
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = filters!.DocumentTypes!.ToArray() });
        }

        if (fragments.HasFlag(FilterFragments.ModifiedAfter))
        {
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = filters!.ModifiedAfter!.Value.ToUniversalTime() });
        }

        return parameters;
    }

    private async Task<IReadOnlyList<Candidate>> ReadCandidatesAsync(
        string sql, NpgsqlParameter first, SearchScope scope, RetrievalFilters? filters, FilterFragments fragments, bool keyword,
        CancellationToken cancellationToken, NpgsqlParameter? afterLevels = null)
    {
        var candidates = new List<Candidate>();
        await RunAsync(sql, Parameters(first, scope, filters, fragments, afterLevels), async (reader, ct) =>
        {
            while (await reader.ReadAsync(ct))
            {
                var score = reader.GetDouble(9);
                candidates.Add(new Candidate(
                    ChunkId: reader.GetGuid(0),
                    DocumentId: reader.GetGuid(1),
                    ChunkSetId: reader.GetGuid(2),
                    Ordinal: reader.GetInt32(3),
                    TextHash: reader.GetFieldValue<byte[]>(4),
                    CollectionId: reader.GetGuid(5),
                    Level: reader.GetString(6),
                    DocumentType: reader.IsDBNull(7) ? null : reader.GetString(7),
                    SourceModifiedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    IdentityKind: reader.GetString(10),
                    IdentityHash: reader.GetFieldValue<byte[]>(11),
                    SourceId: reader.GetGuid(12),
                    Url: reader.IsDBNull(13) ? null : reader.GetString(13),
                    Distance: keyword ? null : score,
                    KeywordScore: keyword ? score : null));
            }
        }, _options.StatementTimeout, cancellationToken);
        return candidates;
    }

    /// <summary>
    /// Runs one statement on its own connection within <paramref name="timeout"/>; a timeout or a server-side cancel
    /// (not the caller's) becomes <see cref="SearchUnavailableException"/>.
    /// </summary>
    internal async Task RunAsync(
        string sql, IReadOnlyList<NpgsqlParameter> parameters, Func<DbDataReader, CancellationToken, Task> read, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(limit.Token);
#pragma warning disable CA2100 // Statement texts are built from constants and config literals (Guid, int) only; values are parameters.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) + 1; // backstop behind the token
            command.Parameters.AddRange(parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(limit.Token);
            await read(reader, limit.Token);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && IsTimeout(e))
        {
            throw new SearchUnavailableException("A retrieval statement exceeded its time limit.", e);
        }
    }

    internal static bool IsTimeout(Exception e) => e switch
    {
        OperationCanceledException => true,
        NpgsqlException { InnerException: TimeoutException } => true,
        PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => true,
        _ => false,
    };
}
