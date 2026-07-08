namespace AI.Core.Tools;

/// <summary>
/// Tool Runtime 的後端種類。ReadFile 可以走 MCP 也可以走 Native,
/// Unity Tool 建議走 Native(Unity Editor API 本質上只能 in-process 呼叫)。
/// </summary>
public enum ToolAdapterKind
{
    Mcp,
    Native,
    Plugin,
    Rest
}

public interface IToolAdapter
{
    ToolAdapterKind Kind { get; }

    bool CanHandle(string toolName);

    Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 依 Tool 名稱路由到正確的 Adapter,對外提供統一的 ITool 介面。
/// </summary>
public interface IToolRuntime
{
    Task<ToolResult> InvokeAsync(string toolName, ToolRequest request, CancellationToken cancellationToken = default);

    void RegisterAdapter(IToolAdapter adapter);
}
