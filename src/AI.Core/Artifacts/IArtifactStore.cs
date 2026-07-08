namespace AI.Core.Artifacts;

/// <summary>
/// Artifact 的持久化層:檔案系統存實際內容 + SQLite 存 metadata(規格書 v3 第 10 節)。
/// 事件只攜帶 ArtifactId,實際內容透過此介面查詢,避免 Event Bus 訊息過大。
/// </summary>
public interface IArtifactStore
{
    Task SaveAsync(IArtifact artifact, CancellationToken cancellationToken = default);

    Task<IArtifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IArtifact>> GetByWorkflowAsync(string workflowId, CancellationToken cancellationToken = default);
}
