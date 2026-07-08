using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// In-process 直接呼叫,例如 Unity Tool——Unity Editor Scripting API 本質上只能 in-process 呼叫,
/// 走 MCP 跨進程會增加延遲與穩定性風險,因此 Native 對它幾乎是必要選項而非單純效能優化
/// (規格書 v3 第 2 節評估)。
/// </summary>
public sealed class NativeToolAdapter : IToolAdapter
{
    private readonly Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>> _handlers;

    public NativeToolAdapter(Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>> handlers)
    {
        _handlers = handlers;
    }

    public ToolAdapterKind Kind => ToolAdapterKind.Native;

    public bool CanHandle(string toolName) => _handlers.ContainsKey(toolName);

    public Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        if (_handlers.TryGetValue(request.Operation, out var handler))
        {
            return handler(request, cancellationToken);
        }

        return Task.FromResult(new ToolResult(false, Error: $"No native handler for operation '{request.Operation}'"));
    }
}
