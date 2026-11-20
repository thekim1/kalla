using System.Text.Json;
using Kalla.Domain.Entities;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Commands;

/// <summary>
/// One admin decision applied by the worker (engine design §9.1, admin design §4.7): the payload's <c>kind</c> selects the
/// handler; the rest of the payload is its ids and parameters.
/// </summary>
public interface ICommandHandler
{
    /// <summary>The <c>kind</c> of the payload, for example <c>cancel-run</c>.</summary>
    string Kind { get; }

    Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>Runs <c>command</c> jobs: an unknown kind or a malformed payload fails at once (no retry would help).</summary>
public sealed class CommandJobHandler : IJobHandler
{
    public const string InvalidPayload = "command.invalid_payload";
    public const string UnknownKind = "command.unknown_kind";

    public string JobClass => JobClasses.Command;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(context.Job.Payload);
        }
        catch (JsonException)
        {
            return new JobResult.FatalError(InvalidPayload);
        }

        using (document)
        {
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            {
                return new JobResult.FatalError(InvalidPayload);
            }

            var name = kind.GetString();
            var handler = context.Services.GetServices<ICommandHandler>().FirstOrDefault(h => string.Equals(h.Kind, name, StringComparison.Ordinal));
            return handler is null
                ? new JobResult.FatalError(UnknownKind)
                : await handler.ExecuteAsync(context, payload, cancellationToken);
        }
    }
}
