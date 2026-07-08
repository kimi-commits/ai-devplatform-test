using System.Text.Json;
using AI.Core.Tools;

namespace AI.MCP.Client;

/// <summary>
/// 串接 <see cref="McpClient"/> 與 AI.Core.Tools.IMcpToolInvoker,讓 AI.Tools 的 McpToolAdapter
/// 可以透過抽象介面呼叫真正的 MCP Server,而不需要 AI.Tools 直接參照 AI.MCP
/// (避免 AI.Tools ↔ AI.MCP 循環參照,規格書 v3 第 11 節,Phase 2)。
/// </summary>
public sealed class McpToolInvoker : IMcpToolInvoker, IAsyncDisposable
{
    private readonly McpClient _client;

    public McpToolInvoker(McpClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyList<string>> ListToolNamesAsync(CancellationToken cancellationToken = default)
        => _client.ListToolNamesAsync(cancellationToken);

    public async Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var result = await _client.CallToolAsync(toolName, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new ToolResult(false, Error: result.Error ?? $"MCP tool call to '{toolName}' failed.");
        }

        // extensions/mcp-server 的每個工具都用 JSON.stringify 包裝回傳值(見 src/index.ts 的 textResult),
        // 這裡把它還原成物件;萬一不是合法 JSON(理論上不會發生),就原樣以字串回傳。
        object? output;
        try
        {
            output = JsonSerializer.Deserialize<object?>(result.Text);
        }
        catch (JsonException)
        {
            output = result.Text;
        }

        return new ToolResult(true, Output: output);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
