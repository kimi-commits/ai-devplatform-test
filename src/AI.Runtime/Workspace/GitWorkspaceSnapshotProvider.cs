using System.Collections.Concurrent;
using System.Diagnostics;
using AI.Core.Workspace;
using Microsoft.Extensions.Logging;

namespace AI.Runtime.Workspace;

/// <summary>
/// <see cref="IWorkspaceSnapshotProvider"/> 的第一個實作(規格書 v3 第 4 節「Workspace Snapshot」,
/// Phase 4 正式啟用)。
///
/// 用途:平行 Coder(Phase 4)同時對同一個 Workspace 提出修改建議時,每個分支各自對應一個
/// SnapshotId,讓後續的 Artifact(CodeArtifact 等)可以標記「我是基於哪個 Snapshot 產生的」,
/// Merge Agent 才能知道要比較/合併哪些分支的結果(規格書 v3 第 4 節「Agent A 改完、Agent B
/// 看到不同版本」的問題)。
///
/// 範疇說明(刻意的 MVP 簡化,務實優先於形式完整):
/// 真正的 git worktree(每個 Coder 各自一份獨立的檔案系統拷貝、互不干擾)需要 Workspace
/// 本身是一個乾淨的 git repository,而且需要能實際跑 `git worktree add` 並驗證。目前這個專案
/// 本身開發時所在的目錄(以及在 sandbox 裡驗證用的目錄)都不是 git repository(見 GitAgent
/// 的 git.status 呼叫結果),所以「建立 worktree」這件事在這個環境裡無法被端到端驗證——寫了
/// 也只是看起來動、實際上第一次真的平行跑就會出錯。因此這一版只把 GitCommitSha 老實地探測出來
/// (是 git repo 就回真的 SHA,不是就回 "unknown",不假裝),WorktreePath 先留 null,並在
/// XML 文件與 README 裡明確記錄「worktree 隔離留給下一個有真實 git repo 可驗證的環境去啟用」,
/// 而不是生出一段測不到、可能有 bug 的路徑。
///
/// 沒有真正的檔案系統隔離時,並行安全性靠的是:CoderAgent 寫入建議檔案時檔名已經包含
/// Guid(見 CoderAgent.WriteSuggestionToFileAsync 的 "{name}-{artifactId}.md" 命名),
/// 所以多個 Coder 共用同一個 RootPath 也不會互相覆寫彼此的輸出。
/// </summary>
public sealed class GitWorkspaceSnapshotProvider : IWorkspaceSnapshotProvider
{
    private readonly ConcurrentDictionary<string, WorkspaceSnapshot> _snapshots = new();
    private readonly ILogger<GitWorkspaceSnapshotProvider> _logger;

    public GitWorkspaceSnapshotProvider(ILogger<GitWorkspaceSnapshotProvider> logger)
    {
        _logger = logger;
    }

    public async Task<WorkspaceSnapshot> CreateSnapshotAsync(AI.Core.Workspace.Workspace workspace, CancellationToken cancellationToken = default)
    {
        var commitSha = await TryGetHeadCommitShaAsync(workspace.RootPath, cancellationToken);

        var snapshot = new WorkspaceSnapshot(
            SnapshotId: Guid.NewGuid().ToString("N"),
            WorkspaceId: workspace.Name,
            GitCommitSha: commitSha,
            WorktreePath: null, // 見類別註解:worktree 隔離留給下一個有真實 git repo 的環境啟用。
            CreatedAt: DateTimeOffset.UtcNow);

        _snapshots[snapshot.SnapshotId] = snapshot;
        _logger.LogInformation(
            "建立 WorkspaceSnapshot {SnapshotId}(Workspace={WorkspaceId}, CommitSha={CommitSha}, WorktreePath=(尚未啟用))",
            snapshot.SnapshotId, snapshot.WorkspaceId, snapshot.GitCommitSha);

        return snapshot;
    }

    public Task<WorkspaceSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_snapshots.TryGetValue(snapshotId, out var snapshot) ? snapshot : null);
    }

    private async Task<string> TryGetHeadCommitShaAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return stdout.Trim();
            }

            _logger.LogInformation(
                "'{Path}' 目前不是 git repository 或無法取得 HEAD commit,GitCommitSha 記為 'unknown'(Phase 4 MVP:不假裝有 commit sha)。",
                workingDirectory);
            return "unknown";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "呼叫 git rev-parse HEAD 失敗,GitCommitSha 記為 'unknown'。");
            return "unknown";
        }
    }
}
