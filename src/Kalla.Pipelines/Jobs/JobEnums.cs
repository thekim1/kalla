namespace Kalla.Pipelines.Jobs;

/// <summary><c>sync</c> payload <c>kind</c> = <c>Runs.Kind</c> (engine design §9.1, §10.1).</summary>
public enum SyncKind
{
    Sync,
    RetryFailed,
    Reprocess,

    /// <summary>A crawl run that goes on from an earlier run of its source (#804), named by <c>continuesRunId</c>.</summary>
    Continue,
}

/// <summary><c>Runs.Trigger</c> (engine design §10.1); <c>notification</c> (SharePoint) is P2.</summary>
public enum RunTrigger
{
    Schedule,
    Manual,
    Upload,
    Publish,
    Rollback,
    SiteProfile,
    Retry,
    ConfigChange,
}

/// <summary>Why a <c>check-hnsw-index</c> command was enqueued (knowledge design §6.4).</summary>
public enum HnswCheckTrigger
{
    Nightly,

    /// <summary>A finished run made more than 10 % of the profile's live chunks live.</summary>
    Sync,
}

/// <summary>The phase a <c>sync</c> job runs (engine design §9.2, connectors design §2.2).</summary>
public enum SyncPhase
{
    List,
    Fetch,
    Reconcile,
}

/// <summary>A document's terminal outcome in a run (engine design §10.2) = <c>RunItems.Outcome</c>.</summary>
public enum RunItemOutcome
{
    Added,
    Updated,
    Unchanged,
    Skipped,
    Degraded,
    Deleted,
    Unsupported,
    TooLarge,
    Encrypted,
    Malware,
    Blocked,
    Failed,
    Superseded,
}

/// <summary>The class of the continuation a job enqueued in its commit (engine design §7.4).</summary>
public enum Continuation
{
    Parse,
    Embed,
}

/// <summary>What the index writer's swap did (engine design §14, metric <c>rag.index.swaps</c>).</summary>
public enum SwapKind
{
    New,
    SwappedBack,
    Rekeyed,
    Held,
}

/// <summary>
/// Retrieval variant of an evaluation run (<c>EvaluationRuns.Variant</c>; processing design §13.4). <c>hybrid-rerank</c> is
/// <c>hybrid</c> with the interim chat-model reranker (#399, Decisions "Rerank pulled forward"): its row is <c>Variant</c>
/// <c>hybrid</c> with <c>Reranked</c> set. <c>hybrid-rerank-gateway</c> is the same with the gateway's <c>/v1/rerank</c>
/// reranker (#414, B-16).
/// </summary>
public enum EvaluationVariant
{
    Hybrid,
    Vector,
    HybridNoDecompounder,
    HybridRerank,
    HybridRerankGateway,
}

/// <summary>Question layer of an evaluation set (<c>EvaluationQuestions.Layer</c>; processing design §13.4).</summary>
public enum EvaluationLayer
{
    Human,
    Generated,
}

/// <summary>What <c>edit-eval-question</c> does to a <c>human</c> question (processing design §13.4).</summary>
public enum QuestionAction
{
    Create,
    Update,
    Retire,
}

/// <summary><c>ParseProfiles.State</c> of an admin-made parse rule after a parse-rule command (processing design §3.5).</summary>
public enum ParseRuleState
{
    Active,
    Disabled,
}

/// <summary>What happened to one stage of a <c>stage-preview</c> document (engine design §11).</summary>
public enum PreviewStageState
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>The outcome of one preflight check (connectors design §2.2 <c>PreflightState</c>): fail blocks the save, warn explains.</summary>
public enum PreflightState
{
    Pass,
    Warn,
    Fail,
}

/// <summary>Where the site profile of a preview's before/after comes from (P1: a manual profile or a built-in preset; learned is P2).</summary>
public enum SiteProfileOrigin
{
    Manual,
    Preset,
}
