namespace AI.Core.Tools;

/// <summary>
/// Tool Runtime 對 MCP 後端的抽象介面(規格書 v3 第 11 節,Phase 2)。
/// AI.Tools 的 McpToolAdapter 依賴這個介面,而不是直接依賴 AI.MCP 專案——
/// 因為 AI.MCP 需要參照 AI.Tools 才能重用 ToolRequest/ToolResult 型別,若 AI.Tools 再反過來
/// 直接參照 AI.MCP,就會形成循環參照。真正的實作(呼叫官方 ModelContextProtocol C# SDK)
/// 放在 AI.MCP 專案,由 AI.Host 在組裝 DI 時把具體實例注入給 McpToolAdapter。
/// </summary>
public interface IMcpToolInvoker
{
    /// <summary>取得 MCP Server 目前公開的所有 Tool 名稱(例如 "file.readFile"、"git.status")。</summary>
    Task<IReadOnlyList<string>> ListToolNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>呼叫一個 MCP Tool 並回傳統一格式的 ToolResult。</summary>
    Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default);
}
