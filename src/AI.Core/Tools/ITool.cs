namespace AI.Core.Tools;

/// <summary>
/// 單一工具的執行契約。Agent 只透過 Capability 取得 ITool,不知道背後是哪個 Adapter
/// (MCP / Native / Plugin / REST,見規格書 v3 第 11 節)。
/// </summary>
public interface ITool
{
    string Name { get; }

    Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default);
}

public sealed record ToolRequest(string Operation, IReadOnlyDictionary<string, object?> Parameters);

public sealed record ToolResult(bool Success, object? Output = null, string? Error = null);
