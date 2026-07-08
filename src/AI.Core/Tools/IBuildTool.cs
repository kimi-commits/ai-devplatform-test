namespace AI.Core.Tools;

/// <summary>dotnet build / go build / Unity Build / npm build(規格書 v1 第 10 節)。</summary>
public interface IBuildTool : ITool
{
    Task<ToolResult> BuildAsync(string workspaceRootPath, string? buildProfile, CancellationToken cancellationToken = default);
}
