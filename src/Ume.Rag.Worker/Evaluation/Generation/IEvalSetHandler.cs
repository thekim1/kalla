using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// The <c>eval</c> kinds <c>generate-set</c> and <c>top-up-set</c> (processing design §13.1, §13.3): the <c>eval</c> job
/// class handler resolves this from the job's scope and passes it those jobs with the payload's kind.
/// </summary>
public interface IEvalSetHandler
{
    Task<JobResult> ExecuteAsync(JobContext context, string kind, CancellationToken ct);
}
