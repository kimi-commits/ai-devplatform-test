using AI.Core.Artifacts;

namespace AI.Core.Workflow;

/// <summary>
/// 解析 WorkflowDefinition 並依事件推進狀態機。Agent Orchestrator 依此決定下一個要啟動的 Agent
/// (規格書 v3 第 8、9 節)。Workflow Engine 本身不含業務邏輯,只做狀態轉移判斷。
/// </summary>
public interface IWorkflowEngine
{
    Task<WorkflowDefinition> LoadAsync(string workflowFilePath, CancellationToken cancellationToken = default);

    Task<string> StartAsync(WorkflowDefinition definition, AI.Core.Workspace.Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// 依目前 Step 執行結果,計算下一個要執行的 Step。
    /// Phase 1 限制:重試計數以 WorkflowDefinition.WorkflowId 為 key,同一個 Workflow 定義
    /// 同時只會有一個執行中的 instance(單一序列 Pipeline),尚未支援真正平行的多個 workflow 實例。
    /// </summary>
    StepTransitionResult GetNextStep(WorkflowDefinition definition, string currentStepId, bool success);
}

/// <summary>
/// GetNextStep 的結果。RetryExceeded=true 代表已超過 DSL 的 maxRetries 上限,
/// Orchestrator 應依 WorkflowDefinition.OnRetryExceeded 升級為人工介入(規格書 v3 第 8 節)。
/// </summary>
public sealed record StepTransitionResult(WorkflowStep? NextStep, bool RetryExceeded);

/// <summary>
/// 依 Workflow Engine 解析出的狀態機,監聽 Event Bus 上的事件,決定下一個要啟動的 Agent。
/// Orchestrator 本身不含業務邏輯,只做路由(規格書 v3 第 9 節)。
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// 依序執行 Workflow 的每個 Step,直到自然結束(遇到沒有 onSuccess 的 Step)或觸發 RetryExceeded。
    /// 回傳 true 代表整條 Pipeline 順利跑到底。
    ///
    /// <paramref name="seedArtifacts"/>(Phase 5 新增,選填):Workflow 開始執行前預先放入的
    /// Artifact,例如 Chat 介面收到的使用者需求文字(見 AI.Host/Server/ChatEndpoints.cs)。
    /// 不傳(或傳 null)時行為與 Phase 1~4 完全相同——從空清單開始。
    /// </summary>
    Task<bool> RunAsync(
        AI.Core.Workflow.WorkflowDefinition definition,
        AI.Core.Workspace.Workspace workspace,
        string workflowId,
        IReadOnlyList<IArtifact>? seedArtifacts = null,
        CancellationToken cancellationToken = default);
}
