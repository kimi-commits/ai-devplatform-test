using System.Linq;
using System.Text.Json;
using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Tools;

namespace AI.Agents;

/// <summary>
/// 負責 diff / commit / branch / push / PR(規格書 v1 第 8 節)。
/// Phase 2 起改為透過 IToolRuntime 呼叫 MCP 的 git.* 工具(extensions/mcp-server 的 gitTool.ts),
/// 不再直接用 Process 執行 git 指令——這樣可以在 Pipeline 執行過程中,真正走一次
/// Agent → ToolRuntime → McpToolAdapter → AI.MCP → Node MCP Server 的完整路徑,驗證 Tool
/// Runtime 多後端架構(規格書 v3 第 11 節)確實可用。
///
/// 真實流程(commit/push 真的接上,不再是 Phase 2 那個只查狀態的骨架):
/// 1. git.status 查工作目錄有沒有變更。
/// 2. 有變更才 commit(git.commit,Medium 風險,自動放行並記錄 Log);沒有變更就跳過,
///    不做空 commit。
/// 3. commit 成功才 push(git.push,High 風險,會透過 IToolRuntime → ICapabilityGuard 卡住,
///    等人工核准——Console y/n 或 VS Code Modal,見 AI.Runtime/Capabilities/)。
///
/// Workspace 不是 git repo、MCP Server 尚未啟動/建置、沒有設定 remote、push 被拒絕等情況,
/// 一律視為資訊性結果而非流程失敗(Success 仍為 true),避免整條 Pipeline 因為環境因素卡在這一步,
/// 無法 demo 到後面的 Deploy——這是從 Phase 2 就有的既有容錯設計,commit/push 失敗只是把原因
/// 寫進 Artifact 內容,不會讓 Workflow 中止。
/// </summary>
public sealed class GitAgent : IAgent
{
    private readonly IToolRuntime _toolRuntime;

    public GitAgent(IToolRuntime toolRuntime)
    {
        _toolRuntime = toolRuntime;
    }

    public string Name => "Git";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "Git.Commit", "Git.Push"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var rootPath = request.Workspace.RootPath;
        var lines = new List<string>();

        var statusResult = await InvokeSafelyAsync(
            "git.status",
            new Dictionary<string, object?> { ["rootPath"] = rootPath },
            cancellationToken);

        var (isRepo, hasChanges) = InterpretStatus(rootPath, statusResult, lines);

        if (isRepo && hasChanges)
        {
            await CommitAndPushAsync(request, rootPath, lines, cancellationToken);
        }

        var artifact = new DocumentArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Content: string.Join("\n\n", lines));

        return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { artifact });
    }

    private async Task CommitAndPushAsync(AgentExecutionRequest request, string rootPath, List<string> lines, CancellationToken cancellationToken)
    {
        var commitMessage = BuildCommitMessage(request);

        var commitResult = await InvokeSafelyAsync(
            "git.commit",
            new Dictionary<string, object?> { ["rootPath"] = rootPath, ["message"] = commitMessage },
            cancellationToken);

        if (!TryGetToolSuccess(commitResult, out var commitError))
        {
            lines.Add($"git.commit 失敗(視為資訊性結果,不中斷流程):{commitError ?? commitResult.Error}");
            return;
        }

        lines.Add($"git.commit 成功。訊息:{commitMessage.Split('\n')[0]}");

        var pushResult = await InvokeSafelyAsync(
            "git.push",
            new Dictionary<string, object?> { ["rootPath"] = rootPath },
            cancellationToken);

        if (!TryGetToolSuccess(pushResult, out var pushError))
        {
            lines.Add(
                "git.push 失敗(視為資訊性結果,不中斷流程;常見原因:沒有設定 remote、" +
                $"push 需要認證、或使用者未核准這個 High 風險操作):{pushError ?? pushResult.Error}");
            return;
        }

        lines.Add("git.push 成功。");
    }

    private async Task<ToolResult> InvokeSafelyAsync(string toolName, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await _toolRuntime.InvokeAsync(toolName, new ToolRequest(toolName, parameters), cancellationToken);
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: ex.Message);
        }
    }

    /// <summary>解析 git.status 的結果,並把摘要文字加進 lines。回傳 (是否為 git repo, 是否有變更)。</summary>
    private static (bool IsRepo, bool HasChanges) InterpretStatus(string workspaceRootPath, ToolResult result, List<string> lines)
    {
        if (!result.Success)
        {
            lines.Add($"呼叫 MCP git.status 失敗(視為資訊性結果,不中斷流程):{result.Error}");
            return (false, false);
        }

        if (result.Output is not JsonElement element)
        {
            lines.Add($"git.status 回傳格式非預期:{result.Output}");
            return (false, false);
        }

        var success = element.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
        if (!success)
        {
            var error = element.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : null;
            lines.Add(
                $"'{workspaceRootPath}' 目前不是 git repository,或 git 指令執行失敗" +
                $"(透過 MCP git.status 呼叫,視為資訊性結果,不中斷流程):\n{error}");
            return (false, false);
        }

        var changes = element.TryGetProperty("changes", out var changesProp) && changesProp.ValueKind == JsonValueKind.Array
            ? changesProp.EnumerateArray().Select(c => c.GetString()).Where(c => !string.IsNullOrEmpty(c)).ToList()
            : new List<string?>();

        if (changes.Count == 0)
        {
            lines.Add("工作目錄乾淨,沒有變更,略過 commit/push。");
            return (true, false);
        }

        lines.Add($"工作目錄有 {changes.Count} 項變更:\n{string.Join('\n', changes)}");
        return (true, true);
    }

    /// <summary>git.commit/git.push 回傳的是 <c>{ success, error? }</c> 這種 JSON,跟 git.status 同款格式。</summary>
    private static bool TryGetToolSuccess(ToolResult result, out string? error)
    {
        if (!result.Success)
        {
            error = result.Error;
            return false;
        }

        if (result.Output is not JsonElement element)
        {
            error = $"回傳格式非預期:{result.Output}";
            return false;
        }

        var success = element.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
        error = success
            ? null
            : element.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : "unknown";
        return success;
    }

    /// <summary>
    /// 用最新一份 Coder 產出的 Summary 當作 commit message 的主要內容,讓 commit 訊息能反映
    /// 這次 Workflow 實際做了什麼,而不是一律寫同一句空泛的話。取不到就退回通用訊息。
    /// </summary>
    private static string BuildCommitMessage(AgentExecutionRequest request)
    {
        var codeSummary = request.InputArtifacts.OfType<CodeArtifact>().LastOrDefault()?.Summary;
        var firstLine = codeSummary?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return $"AI-DOS automated commit (workflow {request.WorkflowId})";
        }

        var subject = firstLine.Length > 72 ? firstLine[..72] : firstLine;
        return $"AI-DOS: {subject}\n\nWorkflowId: {request.WorkflowId}";
    }
}
