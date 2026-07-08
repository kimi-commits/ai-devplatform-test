namespace AI.Core.Memory;

/// <summary>
/// Agent 私有、跨次執行的長期知識。與 Artifact(單次 Workflow 內傳遞)、
/// Knowledge Base(靜態共用知識)三者職責分開,不可混用(規格書 v3 第 13 節)。
/// Memory 不跨 Agent 共享。
/// </summary>
public interface IMemory
{
    string OwnerAgentName { get; }

    Task RememberAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<string?> RecallAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> QueryAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
}
