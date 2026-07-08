using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// Native Adapter 的 <c>unity.build</c> 實作(規格書 Roadmap 真正定義的 Phase 5:Unity Tool,
/// 採 Native Adapter,而不是 <c>extensions/mcp-server/src/tools/unityTool.ts</c> 那個刻意留白的
/// MCP 版本——見該檔案開頭註解與 <see cref="AI.Configuration.UnityOptions"/> 的類別註解)。
///
/// 用 Native Adapter(直接 Process,不透過 MCP 跨進程)是因為 Unity Editor Scripting API 本質上
/// 只能 in-process/單一 Editor 行程呼叫,規格書 v3 第 2、11 節已經把這點列為評估結論,不是這裡
/// 額外決定的。
///
/// 這個專案的執行環境沒有安裝 Unity Editor,沒有真實的 Unity 專案可以測試,所以刻意不假裝支援
/// 「偵測 Unity 版本、自動找 Editor 路徑」這種完整版本;真正做的事情很單純:呼叫使用者在
/// <c>config/appsettings.json</c> 的 <c>Unity.EditorPath</c> 設定的 Unity Editor 執行檔,用標準的
/// <c>-batchmode -quit -projectPath -buildTarget</c> 參數跑一次建置,以 <c>ExitCode == 0</c>
/// 判斷成功與否,並把 Unity 自己寫的 <c>-logFile</c> 內容原封不動回傳當作建置 Log。
/// </summary>
public static class NativeUnityToolHandlers
{
    public static Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>> CreateHandlers()
    {
        return new Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["unity.build"] = ExecuteAsync
        };
    }

    private static async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        string projectPath;
        string editorPath;
        try
        {
            projectPath = RequireString(request.Parameters, "projectPath");
            editorPath = RequireString(request.Parameters, "editorPath");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: ex.Message);
        }

        var buildTarget = request.Parameters.TryGetValue("buildTarget", out var bt) && bt is string btStr && btStr.Length > 0
            ? btStr
            : "StandaloneOSX";
        var executeMethod = request.Parameters.TryGetValue("executeMethod", out var em) && em is string emStr && emStr.Length > 0
            ? emStr
            : null;

        var logFile = Path.Combine(Path.GetTempPath(), $"ai-devplatform-unity-build-{Guid.NewGuid():N}.log");

        var psi = new ProcessStartInfo
        {
            FileName = editorPath,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // 用 ArgumentList(而不是拼接字串)一項一項加,避免 projectPath/logFile 路徑帶空白時
        // 需要自己處理跳脫——跟 NativeDeployToolHandlers 的做法一致。
        psi.ArgumentList.Add("-batchmode");
        psi.ArgumentList.Add("-quit");
        psi.ArgumentList.Add("-projectPath");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-buildTarget");
        psi.ArgumentList.Add(buildTarget);
        psi.ArgumentList.Add("-logFile");
        psi.ArgumentList.Add(logFile);
        if (!string.IsNullOrWhiteSpace(executeMethod))
        {
            psi.ArgumentList.Add("-executeMethod");
            psi.ArgumentList.Add(executeMethod);
        }

        var stdio = new StringBuilder();
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdio.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdio.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Unity 的建置細節主要寫在 -logFile 指定的檔案,不是 stdout/stderr;讀不到就退回
            // stdio 緩衝區內容,至少不會回傳空白 Log。
            string unityLog;
            try
            {
                unityLog = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile, cancellationToken) : stdio.ToString();
            }
            catch
            {
                unityLog = stdio.ToString();
            }

            var success = process.ExitCode == 0;
            return new ToolResult(
                success,
                Output: JsonSerializer.SerializeToElement(new { success, exitCode = process.ExitCode, log = unityLog }),
                Error: success ? null : $"Unity Editor 結束代碼 {process.ExitCode}(完整 Log 見 Output.log)");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: $"無法啟動 Unity Editor(editorPath={editorPath}):{ex.Message}");
        }
    }

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
