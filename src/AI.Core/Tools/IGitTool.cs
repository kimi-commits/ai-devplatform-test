namespace AI.Core.Tools;

/// <summary>diff / commit / branch / push / PR(規格書 v1 第 10 節)。push / PR 屬於 High 風險 Capability。</summary>
public interface IGitTool : ITool
{
    Task<ToolResult> StatusAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

    Task<ToolResult> DiffAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

    Task<ToolResult> CommitAsync(string workspaceRootPath, string message, CancellationToken cancellationToken = default);

    Task<ToolResult> CreateWorktreeAsync(string workspaceRootPath, string branchName, CancellationToken cancellationToken = default);

    Task<ToolResult> PushAsync(string workspaceRootPath, string branchName, CancellationToken cancellationToken = default);
}
