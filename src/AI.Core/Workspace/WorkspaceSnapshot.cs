namespace AI.Core.Workspace;

/// <summary>
/// 解決「Agent A 改完、Agent B 看到不同版本」的問題(規格書 v3 第 4 節)。
/// 每個 Artifact 都應標記自己是基於哪個 SnapshotId 產生。
/// Phase 1(單一 Coder)先定義介面,不強制使用;Phase 4(平行 Coder)正式啟用。
/// </summary>
public sealed record WorkspaceSnapshot(
    string SnapshotId,
    string WorkspaceId,
    string GitCommitSha,
    string? WorktreePath,
    DateTimeOffset CreatedAt);

public interface IWorkspaceSnapshotProvider
{
    Task<WorkspaceSnapshot> CreateSnapshotAsync(Workspace workspace, CancellationToken cancellationToken = default);

    Task<WorkspaceSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);
}
