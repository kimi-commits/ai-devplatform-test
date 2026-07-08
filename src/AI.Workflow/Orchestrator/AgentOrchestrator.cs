using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Events;
using AI.Core.Workflow;
using AI.Core.Workspace;
using Microsoft.Extensions.Logging;

namespace AI.Workflow.Orchestrator;

/// <summary>
/// 依 Workflow Engine 解析出的狀態機決定下一個要啟動的 Agent,並透過 Event Bus 廣播每一步的結果。
/// Orchestrator 本身不含業務邏輯,只做路由(規格書 v3 第 9 節)。
///
/// Phase 1 實作:單一序列 Pipeline,同一時間只跑一個 Workflow instance,依序執行每個 Step、
/// 把前面所有 Step 產出的 Artifact 一併傳給下一個 Agent 當作 InputArtifacts(對應規格書 v3
/// 第 10 節「Coder → CodeArtifact → Reviewer → ReviewArtifact → QA」的範例流程)。
///
/// Phase 4 新增:支援 Step.Parallel(規格書 v3 第 8 節,例如
/// <c>{ "parallel": ["CoderA", "CoderB"], "onAllSuccess": "merge" }</c>)。當一個 Step
/// 沒有 Agent、但有 Parallel 清單時,改用 <see cref="RunParallelBranchesAsync"/>:
/// 每個分支各自透過 IWorkspaceSnapshotProvider 拿一個獨立的 WorkspaceSnapshot(讓輸出的
/// Artifact 可以標記自己是哪個分支產生的),用 Task.WhenAll 同時執行,整體成功與否是
/// 所有分支的 AND;之後仍走同一套 GetNextStep 轉移邏輯(current.OnSuccess ?? current.OnAllSuccess
/// 早在 Phase 1 就已經支援)。
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IEventBus _eventBus;
    private readonly IArtifactStore _artifactStore;
    private readonly IExecutionEngine _executionEngine;
    private readonly IWorkspaceSnapshotProvider _snapshotProvider;
    private readonly IReadOnlyDictionary<string, IAgent> _agentsByName;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IWorkflowEngine workflowEngine,
        IEventBus eventBus,
        IArtifactStore artifactStore,
        IExecutionEngine executionEngine,
        IWorkspaceSnapshotProvider snapshotProvider,
        IEnumerable<IAgent> agents,
        ILogger<AgentOrchestrator> logger)
    {
        _workflowEngine = workflowEngine;
        _eventBus = eventBus;
        _artifactStore = artifactStore;
        _executionEngine = executionEngine;
        _snapshotProvider = snapshotProvider;
        _agentsByName = agents.ToDictionary(a => a.Name);
        _logger = logger;
    }

    public async Task<bool> RunAsync(
        AI.Core.Workflow.WorkflowDefinition definition,
        AI.Core.Workspace.Workspace workspace,
        string workflowId,
        IReadOnlyList<IArtifact>? seedArtifacts = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AgentOrchestrator 開始執行 workflow {WorkflowId}(定義:{DefinitionId})", workflowId, definition.WorkflowId);

        var artifacts = seedArtifacts is null ? new List<IArtifact>() : new List<IArtifact>(seedArtifacts);
        var attemptCounts = new Dictionary<string, int>();
        var currentStep = definition.Steps.FirstOrDefault();

        while (currentStep is not null)
        {
            var stepAgentNames = currentStep.Parallel is { Count: > 0 } names
                ? names
                : currentStep.Agent is not null ? new[] { currentStep.Agent } : Array.Empty<string>();
            await _eventBus.PublishAsync(new StepStarted(workflowId, currentStep.Id, DateTimeOffset.UtcNow, stepAgentNames), cancellationToken);

            bool stepSuccess;
            string? failureReason;
            IReadOnlyList<IArtifact> stepOutputArtifacts;

            if (currentStep.Parallel is { Count: > 0 } parallelAgentNames)
            {
                (stepSuccess, failureReason, stepOutputArtifacts) = await RunParallelBranchesAsync(
                    currentStep, parallelAgentNames, workflowId, workspace, artifacts, cancellationToken);
            }
            else if (currentStep.Agent is not null && _agentsByName.TryGetValue(currentStep.Agent, out var agent))
            {
                _logger.LogInformation("--> Step '{StepId}':執行 Agent '{AgentName}'", currentStep.Id, agent.Name);

                var request = new AgentExecutionRequest(workflowId, workspace, Snapshot: null, InputArtifacts: artifacts);
                AgentResult result;
                try
                {
                    result = await _executionEngine.RunAsync(agent, request, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Step '{StepId}'({AgentName})執行時發生例外", currentStep.Id, agent.Name);
                    result = new AgentResult(Success: false, OutputArtifacts: Array.Empty<IArtifact>(), FailureReason: ex.Message);
                }

                stepSuccess = result.Success;
                failureReason = result.FailureReason;
                stepOutputArtifacts = result.OutputArtifacts;
            }
            else
            {
                _logger.LogError("找不到 Step '{StepId}' 對應的 Agent '{AgentName}',且沒有 Parallel 清單,Workflow 中止", currentStep.Id, currentStep.Agent);
                return false;
            }

            foreach (var artifact in stepOutputArtifacts)
            {
                await _artifactStore.SaveAsync(artifact, cancellationToken);
                artifacts.Add(artifact);
            }

            attemptCounts[currentStep.Id] = attemptCounts.GetValueOrDefault(currentStep.Id) + 1;

            if (stepSuccess)
            {
                _logger.LogInformation("<-- Step '{StepId}' 成功,產出 {Count} 個 Artifact", currentStep.Id, stepOutputArtifacts.Count);
                await _eventBus.PublishAsync(
                    new StepSucceeded(workflowId, currentStep.Id, DateTimeOffset.UtcNow, stepOutputArtifacts.Select(a => a.ArtifactId).ToList()),
                    cancellationToken);
            }
            else
            {
                _logger.LogWarning("<-- Step '{StepId}' 失敗:{Reason}", currentStep.Id, failureReason);
                await _eventBus.PublishAsync(
                    new StepFailed(workflowId, currentStep.Id, DateTimeOffset.UtcNow, failureReason ?? "unknown", attemptCounts[currentStep.Id]),
                    cancellationToken);
            }

            var transition = _workflowEngine.GetNextStep(definition, currentStep.Id, stepSuccess);

            if (transition.RetryExceeded)
            {
                await _eventBus.PublishAsync(new RetryExceeded(workflowId, currentStep.Id, DateTimeOffset.UtcNow), cancellationToken);
                _logger.LogError(
                    "Step '{StepId}' 超過重試上限,依 OnRetryExceeded='{Policy}' 升級為人工介入,Workflow 中止",
                    currentStep.Id, definition.OnRetryExceeded);
                return false;
            }

            currentStep = transition.NextStep;
        }

        _logger.LogInformation("Workflow {WorkflowId} 執行完畢,共產出 {Count} 個 Artifact", workflowId, artifacts.Count);
        return true;
    }

    /// <summary>
    /// 執行一個 Parallel Step(規格書 v3 第 8 節,例如 <c>{ "parallel": ["CoderA", "CoderB"] }</c>)。
    /// 每個分支各自取得一個獨立的 WorkspaceSnapshot(Phase 4:規格書 v3 第 4 節),用 Task.WhenAll
    /// 同時執行,整體成功與否是所有分支的 AND(其中一個分支失敗,整個 Parallel Step 就視為失敗,
    /// 交由 DSL 的 onFailure/maxRetries 處理,不嘗試「部分成功也算過」這種模糊語意)。
    /// </summary>
    private async Task<(bool Success, string? FailureReason, IReadOnlyList<IArtifact> OutputArtifacts)> RunParallelBranchesAsync(
        WorkflowStep step,
        IReadOnlyList<string> agentNames,
        string workflowId,
        AI.Core.Workspace.Workspace workspace,
        IReadOnlyList<IArtifact> inputArtifacts,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "--> Step '{StepId}':平行執行 {Count} 個分支:{AgentNames}",
            step.Id, agentNames.Count, string.Join(", ", agentNames));

        var branchTasks = agentNames.Select(agentName => RunSingleBranchAsync(step.Id, agentName, workflowId, workspace, inputArtifacts, cancellationToken));
        var branchResults = await Task.WhenAll(branchTasks);

        var allArtifacts = branchResults.SelectMany(r => r.OutputArtifacts).ToList();
        var success = branchResults.All(r => r.Success);
        var failureReason = success
            ? null
            : string.Join("; ", branchResults.Where(r => !r.Success).Select(r => $"{r.AgentName}: {r.FailureReason ?? "unknown"}"));

        return (success, failureReason, allArtifacts);
    }

    private async Task<(string AgentName, bool Success, string? FailureReason, IReadOnlyList<IArtifact> OutputArtifacts)> RunSingleBranchAsync(
        string stepId,
        string agentName,
        string workflowId,
        AI.Core.Workspace.Workspace workspace,
        IReadOnlyList<IArtifact> inputArtifacts,
        CancellationToken cancellationToken)
    {
        if (!_agentsByName.TryGetValue(agentName, out var agent))
        {
            _logger.LogError("Parallel Step '{StepId}' 找不到分支 Agent '{AgentName}'", stepId, agentName);
            return (agentName, false, $"找不到 Agent '{agentName}'", Array.Empty<IArtifact>());
        }

        WorkspaceSnapshot snapshot;
        try
        {
            snapshot = await _snapshotProvider.CreateSnapshotAsync(workspace, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分支 '{AgentName}' 建立 WorkspaceSnapshot 失敗", agentName);
            return (agentName, false, $"建立 WorkspaceSnapshot 失敗:{ex.Message}", Array.Empty<IArtifact>());
        }

        _logger.LogInformation("----> 分支 '{AgentName}' 使用 Snapshot {SnapshotId}", agentName, snapshot.SnapshotId);

        var request = new AgentExecutionRequest(workflowId, workspace, Snapshot: snapshot, InputArtifacts: inputArtifacts);
        try
        {
            var result = await _executionEngine.RunAsync(agent, request, cancellationToken);
            _logger.LogInformation(
                "<---- 分支 '{AgentName}'(Snapshot {SnapshotId})執行結果:{Result}",
                agentName, snapshot.SnapshotId, result.Success ? "成功" : "失敗");
            return (agentName, result.Success, result.FailureReason, result.OutputArtifacts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分支 '{AgentName}'(Snapshot {SnapshotId})執行時發生例外", agentName, snapshot.SnapshotId);
            return (agentName, false, ex.Message, Array.Empty<IArtifact>());
        }
    }
}
