using System.Diagnostics;
using System.Text.Json;
using AI.Configuration;
using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Tools;

namespace AI.Agents;

/// <summary>
/// 負責 dotnet build / go build / Unity Build / npm build(規格書 v1 第 8 節)。
/// AgentKind.Tool:直接執行指令,不需要模型、Prompt、Memory 或 Tool Calling 協商
/// ——這正是 v3 新增 Execution Engine 拆層要解決的例子(規格書 v3 第 2 節)。
///
/// Phase 1 先實作 dotnet build,對 Workspace.RootPath 直接執行,失敗時交由 Workflow Engine
/// 依 DSL 的 maxRetries 觸發 CoderRetry(見 workflows/default-pipeline.json)。
///
/// 規格書 Roadmap 真正定義的 Phase 5(Unity Tool,採 Native Adapter)完成後,這裡新增一個分流:
/// 如果 Workspace.RootPath 底下同時有 Assets/ 和 ProjectSettings/(Unity 專案的標準特徵),就走
/// unity.build(見 AI.Tools/Adapters/NativeUnityToolHandlers.cs);否則維持 Phase 1 就有的
/// dotnet build 行為完全不變,不影響既有的 C#/.NET Pipeline 示範。
/// </summary>
public sealed class BuildAgent : IAgent
{
    private readonly AppConfig _appConfig;
    private readonly IToolRuntime _toolRuntime;

    public BuildAgent(AppConfig appConfig, IToolRuntime toolRuntime)
    {
        _appConfig = appConfig;
        _toolRuntime = toolRuntime;
    }

    public string Name => "Build";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "Build.Execute"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (IsUnityProject(request.Workspace.RootPath))
        {
            return await ExecuteUnityBuildAsync(request, cancellationToken);
        }

        var (exitCode, log) = await RunDotnetBuildAsync(request.Workspace.RootPath, cancellationToken);

        var artifact = new BuildLogArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Log: log,
            ExitCode: exitCode);

        var success = exitCode == 0;
        return new AgentResult(
            Success: success,
            OutputArtifacts: new IArtifact[] { artifact },
            FailureReason: success ? null : $"dotnet build exited with code {exitCode}");
    }

    /// <summary>Unity 專案的標準特徵:根目錄同時有 Assets/ 和 ProjectSettings/。</summary>
    private static bool IsUnityProject(string rootPath) =>
        Directory.Exists(Path.Combine(rootPath, "Assets")) &&
        Directory.Exists(Path.Combine(rootPath, "ProjectSettings"));

    private async Task<AgentResult> ExecuteUnityBuildAsync(AgentExecutionRequest request, CancellationToken cancellationToken)
    {
        var unityOptions = _appConfig.Unity;

        if (string.IsNullOrWhiteSpace(unityOptions?.EditorPath))
        {
            var skippedArtifact = new BuildLogArtifact(
                ArtifactId: Guid.NewGuid().ToString("N"),
                WorkflowId: request.WorkflowId,
                SnapshotId: request.Snapshot?.SnapshotId,
                CreatedAt: DateTimeOffset.UtcNow,
                Log: "偵測到這是 Unity 專案(有 Assets/ 與 ProjectSettings/),但沒有在 " +
                     "config/appsettings.json 的 Unity.EditorPath 設定 Unity Editor 執行檔路徑,略過 " +
                     "Unity Build(這是誠實的略過,不是假裝建置成功——這個專案的執行環境沒有安裝 " +
                     "Unity Editor 可以測試,見 UnityOptions 類別註解)。",
                ExitCode: -1);

            // 沒設定就當作跳過,Success 仍為 true,不讓這個示範性專案因為環境沒裝 Unity 而卡住整條
            // Pipeline——跟 DeployAgent 沒設定 Deploy.Command 時的處理方式一致。
            return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { skippedArtifact });
        }

        ToolResult result;
        try
        {
            result = await _toolRuntime.InvokeAsync(
                "unity.build",
                new ToolRequest(
                    "unity.build",
                    new Dictionary<string, object?>
                    {
                        ["projectPath"] = request.Workspace.RootPath,
                        ["editorPath"] = unityOptions.EditorPath,
                        ["buildTarget"] = unityOptions.BuildTarget,
                        ["executeMethod"] = unityOptions.ExecuteMethod
                    }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            result = new ToolResult(false, Error: ex.Message);
        }

        var (log, exitCode) = ExtractLogAndExitCode(result);
        var artifact = new BuildLogArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Log: log,
            ExitCode: exitCode);

        return new AgentResult(
            Success: result.Success,
            OutputArtifacts: new IArtifact[] { artifact },
            FailureReason: result.Success ? null : result.Error);
    }

    private static (string Log, int ExitCode) ExtractLogAndExitCode(ToolResult result)
    {
        if (result.Output is JsonElement element)
        {
            var log = element.TryGetProperty("log", out var logProp) ? logProp.GetString() ?? string.Empty : string.Empty;
            var exitCode = element.TryGetProperty("exitCode", out var exitCodeProp) && exitCodeProp.TryGetInt32(out var code) ? code : -1;
            return (log, exitCode);
        }

        return (result.Error ?? "(無 Log)", result.Success ? 0 : -1);
    }

    private static async Task<(int ExitCode, string Log)> RunDotnetBuildAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet", "build")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var log = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, $"無法啟動 dotnet build:{ex.Message}(請確認本機已安裝 .NET SDK)");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, log.ToString());
    }
}
