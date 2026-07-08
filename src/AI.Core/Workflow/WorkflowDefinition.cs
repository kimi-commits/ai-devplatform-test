namespace AI.Core.Workflow;

/// <summary>
/// Workflow 從程式碼移到 JSON DSL,支援分支與重試,取代單純有序清單
/// (無法表達 BuildFailed → CoderRetry,見規格書 v3 第 8 節)。
/// </summary>
public sealed record WorkflowDefinition(
    string WorkflowId,
    IReadOnlyList<WorkflowStep> Steps,
    string? OnRetryExceeded = "EscalateToHuman");

public sealed record WorkflowStep(
    string Id,
    string? Agent,
    IReadOnlyList<string>? Parallel = null,
    string? OnSuccess = null,
    string? OnFailure = null,
    string? OnAllSuccess = null,
    int MaxRetries = 0);
