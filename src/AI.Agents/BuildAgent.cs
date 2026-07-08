using System.Diagnostics;
using AI.Core.Agents;
using AI.Core.Artifacts;

namespace AI.Agents;

/// <summary>
/// 負責 dotnet build / go build / Unity Build / npm build(規格書 v1 第 8 節)。
/// AgentKind.Tool:直接執行指令,不需要模型、Prompt、Memory 或 Tool Calling 協商
/// ——這正是 v3 新增 Execution Engine 拆層要解決的例子(規格書 v3 第 2 節)。
///
/// Phase 1 先實作 dotnet build,對 Workspace.RootPath 直接執行,失敗時交由 Workflow Engine
/// 依 DSL 的 maxRetries 觸發 CoderRetry(見 workflows/default-pipeline.json)。
/// </summary>
public sealed class BuildAgent : IAgent
{
    public string Name => "Build";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "Build.Execute"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
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
