using System.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AI.MCP.Client;

/// <summary>
/// MCP Client 包裝層,底層接官方 ModelContextProtocol C# SDK 1.3.0(規格書 v3 第 11 節,Phase 2 實作)。
/// 透過 Stdio Transport 啟動 extensions/mcp-server(Node.js 子行程),對外提供簡化過的
/// ListToolNamesAsync / CallToolAsync,供 AI.Tools 的 McpToolAdapter 呼叫。
/// 第一版 MCP Tool:File / Search / Git / Build / Terminal / Browser / Unity(規格書 v1 第 10 節)。
///
/// 命名注意:官方 SDK 的用戶端類別本身也叫 McpClient(namespace ModelContextProtocol.Client),
/// 與這個包裝類別同名,因此下面一律用完整名稱 ModelContextProtocol.Client.McpClient 指涉 SDK 型別,
/// 避免在 AI.MCP.Client 這個 namespace 下產生解析歧義。
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string? _workingDirectory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ModelContextProtocol.Client.McpClient? _sdkClient;

    public McpClient(string command, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        _command = command;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// 建立一個指向 extensions/mcp-server 編譯輸出(dist/index.js)的 McpClient,
    /// 用 node 執行該檔案作為子行程(Stdio Transport)。
    /// </summary>
    public static McpClient CreateForNodeServer(string distIndexJsPath, string? workingDirectory = null)
        => new("node", new[] { distIndexJsPath }, workingDirectory);

    private async Task<ModelContextProtocol.Client.McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_sdkClient is not null)
        {
            return _sdkClient;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sdkClient is not null)
            {
                return _sdkClient;
            }

            var transportOptions = new StdioClientTransportOptions
            {
                Name = "ai-devplatform-mcp-server",
                Command = _command,
                Arguments = _arguments.ToList()
            };
            if (_workingDirectory is not null)
            {
                transportOptions.WorkingDirectory = _workingDirectory;
            }

            var transport = new StdioClientTransport(transportOptions);
            _sdkClient = await ModelContextProtocol.Client.McpClient
                .CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return _sdkClient;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>取得伺服器目前公開的所有 MCP Tool 名稱(例如 "file.readFile"、"git.status")。</summary>
    public async Task<IReadOnlyList<string>> ListToolNamesAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// 呼叫一個 MCP Tool。extensions/mcp-server 目前一律用 JSON.stringify 把回傳值包成單一
    /// TextContentBlock(見 src/index.ts 的 textResult helper),所以這裡把 Content 裡的文字
    /// 直接串接回傳,呼叫端(McpToolAdapter)再自行 JSON 解析。
    /// </summary>
    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            return new McpToolCallResult(false, string.Empty, ex.Message);
        }

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return new McpToolCallResult(
            Success: result.IsError != true,
            Text: text,
            Error: result.IsError == true ? text : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sdkClient is not null)
        {
            await _sdkClient.DisposeAsync().ConfigureAwait(false);
            _sdkClient = null;
        }
    }
}

/// <summary>MCP Tool 呼叫結果。Text 是伺服器回傳的原始 JSON 字串,由呼叫端自行解析。</summary>
public sealed record McpToolCallResult(bool Success, string Text, string? Error = null);
