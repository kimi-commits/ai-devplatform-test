using AI.Core.Agents;
using AI.Core.Artifacts;

namespace AI.Agents;

/// <summary>
/// 平行 Coder(Phase 4)跑完之後,負責彙整各分支結果的 Agent(規格書 v3 第 9 節,
/// Workflow DSL 的 "parallel" step 的 onAllSuccess 目標)。
///
/// AgentKind.Tool:不呼叫模型、決定性(deterministic),只是把多個 CodeArtifact
/// 依 SnapshotId 分組整理成一份比較報告——刻意不做「自動選一個分支、丟掉另一個」的決策,
/// 因為那屬於需要人判斷或需要 Reviewer/QA 評分的事,不該由 Merge 這一步自己決定。
///
/// 範疇說明(對應 GitWorkspaceSnapshotProvider 的 MVP 簡化):因為目前的執行環境不是真正的
/// git repository、沒有啟用 git worktree 隔離,這一版的 Merge 不做真正的 git branch merge
/// (`git merge` / 衝突解決),而是老實地產出一份「各分支寫了哪些檔案、Summary 是什麼」的
/// 比較報告,讓下一步(Reviewer/QA)或人可以接著判斷要採用哪個分支。等 Workspace Snapshot
/// 的 WorktreePath 真正啟用(見 GitWorkspaceSnapshotProvider 註解)之後,才適合在這裡接上
/// 真正的 git merge 邏輯。
/// </summary>
public sealed class MergeAgent : IAgent
{
    public string Name => "Merge";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = Array.Empty<string>();

    public Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var branches = request.InputArtifacts
            .OfType<CodeArtifact>()
            .GroupBy(a => a.SnapshotId ?? "(no-snapshot)")
            .ToList();

        var report = BuildReport(branches);

        var artifact = new DocumentArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Content: report);

        // 找不到任何平行 Coder 產出(例如序列 Pipeline 誤接了 Merge 這步)時,仍回報成功但在
        // 報告裡老實說明,不讓 Workflow 卡死在這裡——診斷資訊留在 Artifact 內容供人查看。
        return Task.FromResult(new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { artifact }));
    }

    private static string BuildReport(IReadOnlyList<IGrouping<string, CodeArtifact>> branches)
    {
        if (branches.Count == 0)
        {
            return "Merge Agent:沒有收到任何 CodeArtifact 可比較(上一步可能不是平行 Coder,或平行 Coder 全部失敗)。";
        }

        var lines = new List<string>
        {
            $"Merge Agent:收到 {branches.Count} 個分支(依 SnapshotId 分組)的產出,比較如下。",
            "本階段只產出比較報告,不自動選擇/合併分支(見 MergeAgent 類別註解),請由下一步(Reviewer/QA)或人工決定採用哪個分支。",
            string.Empty
        };

        foreach (var branch in branches)
        {
            lines.Add($"## SnapshotId: {branch.Key}");
            foreach (var artifact in branch)
            {
                lines.Add($"- Coder 產出 ArtifactId={artifact.ArtifactId}");
                lines.Add($"  檔案:{(artifact.Files.Count == 0 ? "(無,寫入失敗或未產生檔案)" : string.Join(", ", artifact.Files))}");
                lines.Add($"  摘要:{artifact.Summary}");
            }

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines);
    }
}
