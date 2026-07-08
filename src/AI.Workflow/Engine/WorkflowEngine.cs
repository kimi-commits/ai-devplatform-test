using AI.Core.Workflow;
using AI.Workflow.Dsl;

namespace AI.Workflow.Engine;

/// <summary>
/// 依 WorkflowDefinition 推進狀態機。含分支(onSuccess/onFailure)與重試(maxRetries)判斷,
/// 取代 v2 單純有序清單無法表達的 BuildFailed → CoderRetry(規格書 v3 第 8 節)。
/// Phase 1 先支援序列 + 分支 + 重試;Phase 4 平行 Coder 才需要用到 Parallel 節點。
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly WorkflowDslLoader _loader;
    private readonly Dictionary<string, int> _retryCounts = new();

    public WorkflowEngine(WorkflowDslLoader loader)
    {
        _loader = loader;
    }

    public Task<WorkflowDefinition> LoadAsync(string workflowFilePath, CancellationToken cancellationToken = default)
        => _loader.LoadFromFileAsync(workflowFilePath, cancellationToken);

    public Task<string> StartAsync(WorkflowDefinition definition, AI.Core.Workspace.Workspace workspace, CancellationToken cancellationToken = default)
    {
        var workflowId = $"{definition.WorkflowId}-{Guid.NewGuid():N}";
        return Task.FromResult(workflowId);
    }

    public StepTransitionResult GetNextStep(WorkflowDefinition definition, string currentStepId, bool success)
    {
        var current = definition.Steps.FirstOrDefault(s => s.Id == currentStepId);
        if (current is null)
        {
            return new StepTransitionResult(null, RetryExceeded: false);
        }

        if (!success)
        {
            var key = $"{definition.WorkflowId}:{currentStepId}";
            _retryCounts[key] = _retryCounts.GetValueOrDefault(key) + 1;

            if (current.MaxRetries > 0 && _retryCounts[key] > current.MaxRetries)
            {
                // 超過重試上限,交由 Orchestrator 依 definition.OnRetryExceeded 升級為人工介入。
                return new StepTransitionResult(null, RetryExceeded: true);
            }

            var failureStepId = current.OnFailure;
            var failureStep = failureStepId is null ? null : definition.Steps.FirstOrDefault(s => s.Id == failureStepId);
            return new StepTransitionResult(failureStep, RetryExceeded: false);
        }

        var nextStepId = current.OnSuccess ?? current.OnAllSuccess;
        var nextStep = nextStepId is null ? null : definition.Steps.FirstOrDefault(s => s.Id == nextStepId);
        return new StepTransitionResult(nextStep, RetryExceeded: false);
    }
}
