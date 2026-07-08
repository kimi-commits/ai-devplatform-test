namespace AI.Core.Events;

/// <summary>
/// Agent 之間不直接呼叫,而是發布事件,由訂閱者處理(規格書 v3 第 7 節)。
/// 任何 Plugin(例如未來的 Documentation Agent)都可以訂閱事件,不需修改既有 Agent 程式碼。
/// </summary>
public abstract record AgentEvent(string WorkflowId, string StepId, DateTimeOffset OccurredAt)
{
    public string EventType => GetType().Name;
}

public sealed record TaskCreated(string WorkflowId, string StepId, DateTimeOffset OccurredAt, string TaskDescription)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record CodeGenerated(string WorkflowId, string StepId, DateTimeOffset OccurredAt, string ArtifactId)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record ReviewRequested(string WorkflowId, string StepId, DateTimeOffset OccurredAt, string ArtifactId)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record BuildFailed(string WorkflowId, string StepId, DateTimeOffset OccurredAt, string ArtifactId, int AttemptCount)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

/// <summary>Step 開始執行時發布(Phase 5:規格書 v3 第 16 節「Task Tree」需要即時顯示目前跑到哪個
/// Step,不只是事後的成功/失敗)。AgentNames 對一般 Step 只有一個元素,平行 Step
/// (見 WorkflowStep.Parallel)則會有多個。</summary>
public sealed record StepStarted(string WorkflowId, string StepId, DateTimeOffset OccurredAt, IReadOnlyList<string> AgentNames)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record StepSucceeded(string WorkflowId, string StepId, DateTimeOffset OccurredAt, IReadOnlyList<string> ArtifactIds)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record StepFailed(string WorkflowId, string StepId, DateTimeOffset OccurredAt, string Reason, int AttemptCount)
    : AgentEvent(WorkflowId, StepId, OccurredAt);

public sealed record RetryExceeded(string WorkflowId, string StepId, DateTimeOffset OccurredAt)
    : AgentEvent(WorkflowId, StepId, OccurredAt);
