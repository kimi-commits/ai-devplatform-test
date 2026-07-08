using System.Text.Json;
using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// Native File Adapter 的具體工具實作:直接用 System.IO 讀寫檔案,不透過 MCP 跨進程呼叫。
/// 用來驗證 Tool Runtime 的 Native Adapter 後端可行性(規格書 v3 第 11 節,Phase 2)。
/// 工具名稱與參數對齊 extensions/mcp-server 的 file.* 工具(見 fileTool.ts),
/// 讓同一個 Capability 可以在 MCP 與 Native 兩種後端之間自由切換而不影響呼叫端(Agent)程式碼——
/// 這正是規格書 v3 第 11 節「Tool Runtime 多後端」設計的核心驗證點。
/// </summary>
public static class NativeFileToolHandlers
{
    public static Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>> CreateHandlers()
    {
        return new Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["file.readFile"] = ReadFileAsync,
            ["file.writeFile"] = WriteFileAsync,
            ["file.deleteFile"] = DeleteFileAsync,
            ["file.copy"] = CopyFileAsync,
            ["file.move"] = MoveFileAsync
        };
    }

    private static async Task<ToolResult> ReadFileAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var path = RequireString(request.Parameters, "path");
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, Output: content);
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: ex.Message);
        }
    }

    private static async Task<ToolResult> WriteFileAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var path = RequireString(request.Parameters, "path");
            var content = RequireString(request.Parameters, "content");

            // File.WriteAllTextAsync 在父目錄不存在時會直接丟 DirectoryNotFoundException(不像 Node
            // 的部分工具會自動 mkdir -p)。CoderAgent 會寫到 workspace 底下全新的 .ai-suggestions/
            // 子目錄,第一次執行時該目錄還不存在,所以這裡先確保父目錄存在,避免寫入靜默失敗
            // (曾經在使用者本機上實測到 Files 欄位變成空陣列,就是這個原因)。
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, Output: new { success = true });
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: ex.Message);
        }
    }

    private static Task<ToolResult> DeleteFileAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var path = RequireString(request.Parameters, "path");
            File.Delete(path);
            return Task.FromResult(new ToolResult(true, Output: new { success = true }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(false, Error: ex.Message));
        }
    }

    private static Task<ToolResult> CopyFileAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var from = RequireString(request.Parameters, "from");
            var to = RequireString(request.Parameters, "to");
            File.Copy(from, to, overwrite: true);
            return Task.FromResult(new ToolResult(true, Output: new { success = true }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(false, Error: ex.Message));
        }
    }

    private static Task<ToolResult> MoveFileAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var from = RequireString(request.Parameters, "from");
            var to = RequireString(request.Parameters, "to");
            File.Move(from, to, overwrite: true);
            return Task.FromResult(new ToolResult(true, Output: new { success = true }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(false, Error: ex.Message));
        }
    }

    /// <summary>
    /// 從 Parameters 取出字串值。Parameters 型別是 IReadOnlyDictionary&lt;string, object?&gt;,
    /// 呼叫端(Agent)通常直接放入 string,但如果值是從 JSON 反序列化來的(例如未來從 Workflow DSL
    /// 或跨進程呼叫傳入),也可能是 JsonElement,這裡一併處理。
    /// </summary>
    private static string RequireString(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required parameter '{key}'.");
        }

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String =>
                je.GetString() ?? throw new ArgumentException($"Parameter '{key}' is null."),
            _ => value.ToString() ?? throw new ArgumentException($"Parameter '{key}' could not be converted to string.")
        };
    }
}
