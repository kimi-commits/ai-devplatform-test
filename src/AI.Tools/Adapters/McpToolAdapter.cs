using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// 跨進程、標準化的 MCP 工具後端,例如 Search / Git / Build(規格書 v3 第 11 節)。
/// 實際的 MCP 呼叫邏輯透過 <see cref="IMcpToolInvoker"/> 委派給 AI.MCP 專案
/// (AI.MCP 底層接官方 ModelContextProtocol C# SDK,啟動 extensions/mcp-server 作為子行程)。
/// 依 ToolRuntime 的既有慣例(見 NativeToolAdapter),<see cref="ToolRequest.Operation"/> 就是
/// 完整的 MCP Tool 名稱(例如 "git.status"),與 ToolRuntime.InvokeAsync 呼叫時傳入的 toolName 相同。
/// </summary>
public sealed class McpToolAdapter : IToolAdapter
{
    private readonly IMcpToolInvoker _invoker;
    private readonly HashSet<string> _mcpToolNames;

    public McpToolAdapter(IMcpToolInvoker invoker, IEnumerable<string> mcpToolNames)
    {
        _invoker = invoker;
        _mcpToolNames = new HashSet<string>(mcpToolNames, StringComparer.OrdinalIgnoreCase);
    }

    public ToolAdapterKind Kind => ToolAdapterKind.Mcp;

    public bool CanHandle(string toolName) => _mcpToolNames.Contains(toolName);

    public Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default)
        => _invoker.InvokeAsync(request.Operation, request.Parameters, cancellationToken);
}
