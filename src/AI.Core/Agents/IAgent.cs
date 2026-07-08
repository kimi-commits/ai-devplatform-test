using AI.Core.Artifacts;
using AI.Core.Workspace;

namespace AI.Core.Agents;

/// <summary>
/// 所有 Agent 的最上層契約。Agent 永遠只拿 Workspace,不知道專案在哪(規格書 v1 第 6 節)。
/// Agent 之間不直接呼叫彼此,只透過 Execution Engine 執行、透過 Event Bus 溝通。
/// </summary>
public interface IAgent
{
    string Name { get; }

    AgentKind Kind { get; }

    /// <summary>此 Agent 需要的 Capability 名稱清單(見 ICapability)。</summary>
    IReadOnlyList<string> RequiredCapabilities { get; }

    Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>單次 Agent 執行的輸入。</summary>
public sealed record AgentExecutionRequest(
    string WorkflowId,
    AI.Core.Workspace.Workspace Workspace,
    WorkspaceSnapshot? Snapshot,
    IReadOnlyList<IArtifact> InputArtifacts);

/// <summary>單次 Agent 執行的輸出。成功與否、是否要求重試都由 Workflow Engine 依此判斷。</summary>
public sealed record AgentResult(
    bool Success,
    IReadOnlyList<IArtifact> OutputArtifacts,
    string? FailureReason = null);
