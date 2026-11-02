using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Commands;

/// <summary>The <c>command</c> handlers (admin design §4.7, engine design §9.1).</summary>
public static class CommandExtensions
{
    /// <summary>
    /// Every P1 command kind. The handlers depend only on what <c>AddRagWorker</c> registers, because
    /// <see cref="CommandJobHandler"/> resolves all of them for each job; <see cref="ReprocessDocumentHandler"/> takes the
    /// engine's parts from the job's scope, <see cref="CheckHnswIndexHandler"/> its <see cref="HnswIndexHealth"/>, and the parse-rule handlers drop the engine's rule cache when it is there.
    /// </summary>
    public static IServiceCollection AddRagCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, CancelRunCommand>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, EditSiteProfileHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, RollbackSiteProfileHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, EditEvalQuestionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, ConfirmDeletionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, RejectDeletionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, ReleaseHoldHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, ClearPiiFlagHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, ReprocessDocumentHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, EditParseRuleHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, DisableParseRuleHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICommandHandler, CheckHnswIndexHandler>());
        return services;
    }
}

/// <summary>Reading a command payload against its contract (<c>contracts/jobs/v1/command.{kind}.payload.schema.json</c>).</summary>
internal static class CommandPayload
{
    /// <summary>The payload when it is valid against its schema (lengths, patterns, required members), else null.</summary>
    public static T? Read<T>(JsonElement payload) where T : class
    {
        if (!ContractSchemas.Validator(typeof(T)).IsValid(payload))
        {
            return null;
        }

        try
        {
            return ContractJson.Deserialize<T>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The commands' single transaction (engine design §7.6): the writes and the job's completion with its result commit
/// together. The completion is the fence: a job that lost its lease (reclaimed or cancelled) completes no row, and
/// everything it wrote is rolled back.
/// </summary>
internal static class CommandCommit
{
    public static async Task<JobResult> CompleteAsync<TResult>(EngineCommit commit, IJobQueue queue, LeasedJob job, WorkerIdentity identity, TResult result,
        CancellationToken cancellationToken)
    {
        if (!await commit.CompleteJobAsync(queue, job.Id, identity.InstanceId, ContractJson.Serialize(result), cancellationToken))
        {
            // The lease is gone: nothing is written; the dispatcher records the loss.
            return new JobResult.Success();
        }

        await commit.CommitAsync(cancellationToken);
        return new JobResult.Committed();
    }

    public static async Task<int> ExecuteAsync(EngineCommit commit, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, commit.Connection, commit.Transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<object?> ScalarAsync(EngineCommit commit, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, commit.Connection, commit.Transaction);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }
}
