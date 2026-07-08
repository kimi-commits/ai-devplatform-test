using System.Text.Json;
using System.Threading.Channels;
using AI.Core.Workflow;

namespace AI.Host.Server;

/// <summary>
/// Phase 5(規格書 v3 第 16 節)。一個 Chat 訊息 = 啟動一次 Workflow(見 ChatEndpoints.cs 開頭
/// 註解說明的「Chat 定位」決策)。這個類別追蹤單一次 Workflow 執行(RunId 直接沿用
/// IWorkflowEngine.StartAsync 產生的 workflowId,兩者是同一個值,不需要額外映射)的即時狀態,
/// 給 SSE 端點(/api/chat/{runId}/stream)與快照端點(/api/tasks/{runId})共用。
///
/// 用 <see cref="Channel{T}"/> 實作簡易 Pub/Sub:每個連上 SSE 的用戶端各拿一個 Channel;
/// 同時保留 History,讓比較晚連上的用戶端(例如 VS Code Extension 在 Workflow 已經跑了幾步之後
/// 才打開 Chat 面板)可以先補播已經發生過的事件,再繼續收後續即時事件。
/// </summary>
public sealed class RunTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly List<string> _history = new();
    private readonly List<Channel<string>> _subscribers = new();
    private readonly object _gate = new();

    public required string RunId { get; init; }

    public required string WorkflowDefinitionId { get; init; }

    public List<StepState> Steps { get; } = new();

    public bool Completed { get; private set; }

    public bool? Success { get; private set; }

    public void OnStepStarted(string stepId, IReadOnlyList<string> agentNames)
    {
        lock (_gate)
        {
            var step = Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is not null)
            {
                step.Status = StepStatus.Running;
            }

            Broadcast(new { type = "stepStarted", stepId, agentNames });
        }
    }

    public void OnStepSucceeded(string stepId, IReadOnlyList<string> artifactIds)
    {
        lock (_gate)
        {
            var step = Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is not null)
            {
                step.Status = StepStatus.Succeeded;
                step.ArtifactIds = artifactIds;
            }

            Broadcast(new { type = "stepSucceeded", stepId, artifactIds });
        }
    }

    public void OnStepFailed(string stepId, string reason)
    {
        lock (_gate)
        {
            var step = Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is not null)
            {
                step.Status = StepStatus.Failed;
                step.Reason = reason;
            }

            Broadcast(new { type = "stepFailed", stepId, reason });
        }
    }

    /// <summary>整條 Workflow 正常跑完(不論每個 Step 是否都成功)時呼叫,結束所有訂閱者的串流。</summary>
    public void Complete(bool success)
    {
        lock (_gate)
        {
            Completed = true;
            Success = success;
            Broadcast(new { type = "completed", success });
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>Orchestrator 拋出未預期例外時呼叫(理論上不該發生,Orchestrator 內部已經有
    /// try/catch,這裡是最後一道防線,避免 Chat 用戶端永遠卡在等待狀態)。</summary>
    public void Fail(string reason)
    {
        lock (_gate)
        {
            Completed = true;
            Success = false;
            Broadcast(new { type = "completed", success = false, reason });
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>訂閱這個 Run 的即時事件。<paramref name="replay"/> 是訂閱當下已經發生過的歷史事件,
    /// 呼叫端應該先把 replay 的內容送給用戶端,再繼續讀傳回的 ChannelReader。</summary>
    public ChannelReader<string> Subscribe(out IReadOnlyList<string> replay)
    {
        lock (_gate)
        {
            var channel = Channel.CreateUnbounded<string>();
            replay = _history.ToArray();

            if (Completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }
    }

    private void Broadcast(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _history.Add(json);
        foreach (var channel in _subscribers)
        {
            channel.Writer.TryWrite(json);
        }
    }
}

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public sealed class StepState
{
    public required string Id { get; init; }

    public required IReadOnlyList<string> AgentNames { get; init; }

    public StepStatus Status { get; set; } = StepStatus.Pending;

    public string? Reason { get; set; }

    public IReadOnlyList<string> ArtifactIds { get; set; } = Array.Empty<string>();
}

/// <summary>所有進行中/已完成 Run 的登記表。RunId 直接沿用 Workflow 的 workflowId。</summary>
public sealed class RunRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunTracker> _runs = new();

    public RunTracker Create(string runId, string workflowDefinitionId, IEnumerable<WorkflowStep> steps)
    {
        var tracker = new RunTracker
        {
            RunId = runId,
            WorkflowDefinitionId = workflowDefinitionId
        };

        foreach (var step in steps)
        {
            var agentNames = step.Parallel is { Count: > 0 } parallel
                ? parallel
                : step.Agent is not null ? new[] { step.Agent } : Array.Empty<string>();

            tracker.Steps.Add(new StepState { Id = step.Id, AgentNames = agentNames });
        }

        _runs[runId] = tracker;
        return tracker;
    }

    public RunTracker? Get(string runId) => _runs.TryGetValue(runId, out var tracker) ? tracker : null;
}
