namespace AI.Core.Tools;

/// <summary>SearchText / SearchSymbol / SearchRegex(規格書 v1 第 10 節)。</summary>
public interface ISearchTool : ITool
{
    Task<ToolResult> SearchTextAsync(string workspaceRootPath, string query, CancellationToken cancellationToken = default);

    Task<ToolResult> SearchRegexAsync(string workspaceRootPath, string pattern, CancellationToken cancellationToken = default);
}
