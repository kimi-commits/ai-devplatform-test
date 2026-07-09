namespace AI.Core.Artifacts;

/// <summary>
/// Workflow 之間永遠傳遞強型別 Artifact,而不是 string 或鬆散物件
/// (取代 v2 的 WorkflowContext.Payload,見規格書 v3 第 10 節)。
/// </summary>
public interface IArtifact
{
    string ArtifactId { get; }

    string Type { get; }

    string WorkflowId { get; }

    string? SnapshotId { get; }

    DateTimeOffset CreatedAt { get; }

    /// <summary>大型內容(Build Log、截圖)存放於 Artifact Store 的實際路徑,事件只攜帶 ArtifactId 指標。</summary>
    string? RefPath { get; }
}

public abstract record ArtifactBase(
    string ArtifactId,
    string Type,
    string WorkflowId,
    string? SnapshotId,
    DateTimeOffset CreatedAt,
    string? RefPath) : IArtifact;

public sealed record CodeArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Files, string Summary = "", string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(CodeArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record DiffArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string Diff, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(DiffArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record ReviewArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Findings, bool Verdict, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(ReviewArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record TestArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Results, double Coverage, bool Passed = true, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(TestArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record BuildLogArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string Log, int ExitCode, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(BuildLogArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record ScreenshotArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string ImagePath, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(ScreenshotArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record PrArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string Url, string Branch, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(PrArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

public sealed record DocumentArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string Content, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(DocumentArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);

/// <summary>
/// Stage C(使用者自訂擴充,見 README「迭代開發迴圈」章節):ProjectManagerAgent 讀完 PRD 之後,
/// 針對平行 Coder 機制裡「每一個具名角色」各自產生一份專屬任務內容(跟 DocumentArtifact 的差異
/// 是多了 AssigneeAgentName,讓 CoderAgent 可以只挑「屬於自己」的那一份,而不是所有平行分支
/// 都收到一模一樣的內容——見 CoderAgent.ExecuteAsync 的讀取邏輯)。
/// </summary>
public sealed record TaskAssignmentArtifact(
    string ArtifactId, string WorkflowId, string? SnapshotId, DateTimeOffset CreatedAt,
    string AssigneeAgentName, string Content, string? RefPath = null)
    : ArtifactBase(ArtifactId, nameof(TaskAssignmentArtifact), WorkflowId, SnapshotId, CreatedAt, RefPath);
