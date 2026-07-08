using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// Native Adapter 的 <c>deploy.execute</c> 實作(規格書 v1 第 8 節「Deploy Agent」,
/// <c>AI.Tools.Runtime.ToolCapabilityMap</c> 早在 Phase 3 就把 "deploy.execute" 對應到
/// High 風險的 "Deploy.Execute" Capability,只是一直沒有東西真的呼叫它)。
///
/// 這個專案沒有真實的雲端/Docker/Kubernetes 部署目標可以測試(規格書 Roadmap 把那些留到
/// Phase 8),所以刻意不假裝支援;真正做的事情很單純:執行使用者在
/// <c>config/appsettings.json</c> 的 <c>Deploy.Command</c> 設定的一句 shell 指令
/// (<see cref="AI.Configuration.DeployOptions"/>),用 <c>ExitCode == 0</c> 判斷成功與否。
/// 用 Native Adapter(直接 Process,不透過 MCP 跨進程)是因為這跟 BuildAgent 執行
/// <c>dotnet build</c> 本質上是同一種操作,不需要額外的跨進程開銷。
/// </summary>
public static class NativeDeployToolHandlers
{
    public static Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>> CreateHandlers()
    {
        return new Dictionary<string, Func<ToolRequest, CancellationToken, Task<ToolResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["deploy.execute"] = ExecuteAsync
        };
    }

    private static async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        string command;
        try
        {
            command = RequireString(request.Parameters, "command");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: ex.Message);
        }

        var workingDirectory = request.Parameters.TryGetValue("workingDirectory", out var wd) && wd is string wdString && wdString.Length > 0
            ? wdString
            : Directory.GetCurrentDirectory();

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // 用 ArgumentList 而不是手動拼接 Arguments 字串,避免部署指令裡的空白/特殊字元需要自己處理跳脫;
        // 外層的 shell/cmd 只負責把整句 command 當一個參數交給裡面的 -c/-c 解讀。
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        var log = new StringBuilder();
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var success = process.ExitCode == 0;
            return new ToolResult(
                success,
                Output: JsonSerializer.SerializeToElement(new { success, exitCode = process.ExitCode, output = log.ToString() }),
                Error: success ? null : $"部署指令結束代碼 {process.ExitCode}:{log}");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, Error: $"無法執行部署指令:{ex.Message}");
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
