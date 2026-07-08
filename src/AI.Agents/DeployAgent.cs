using System.Text.Json;
using AI.Configuration;
using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Tools;

namespace AI.Agents;

/// <summary>
/// 負責 Docker / Azure / AWS / GCP 部署(規格書 v1 第 8 節)。
/// Deploy.Execute 屬於 High 風險 Capability,必須經人工確認(透過 IToolRuntime →
/// ICapabilityGuard,跟 Git.Push 用同一套核准機制)。
///
/// 範疇說明(誠實記錄限制,對應 <see cref="AI.Configuration.DeployOptions"/> 的類別註解):
/// 這個專案沒有真實的雲端/Docker/Kubernetes 部署目標可以測試(規格書 Roadmap 把那些留到
/// Phase 8「Cloud / Docker / Kubernetes」),所以不假裝支援「依 Configuration 選擇
/// Docker/Azure/AWS/GCP 子流程」這種完整版本。真正做的事情是:如果
/// <c>config/appsettings.json</c> 的 <c>Deploy.Command</c> 有設定一句 shell 指令,就透過
/// Native Adapter 的 <c>deploy.execute</c>(見 NativeDeployToolHandlers.cs)執行它,並讓
/// Capability Guard 在執行前卡一次人工核准;沒有設定就誠實回報「未設定部署指令,略過」,
/// 不假裝部署成功。
/// </summary>
public sealed class DeployAgent : IAgent
{
    private readonly AppConfig _appConfig;
    private readonly IToolRuntime _toolRuntime;

    public DeployAgent(AppConfig appConfig, IToolRuntime toolRuntime)
    {
        _appConfig = appConfig;
        _toolRuntime = toolRuntime;
    }

    public string Name => "Deploy";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "Deploy.Execute"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var command = _appConfig.Deploy?.Command;

        if (string.IsNullOrWhiteSpace(command))
        {
            var skippedArtifact = BuildArtifact(
                request,
                "沒有在 config/appsettings.json 的 Deploy.Command 設定部署指令,略過部署" +
                "(這是誠實的略過,不是假裝部署成功——這個專案沒有真實的雲端/Docker 部署目標可以測試," +
                "見 DeployAgent 類別註解)。");
            return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { skippedArtifact });
        }

        ToolResult result;
        try
        {
            result = await _toolRuntime.InvokeAsync(
                "deploy.execute",
                new ToolRequest(
                    "deploy.execute",
                    new Dictionary<string, object?>
                    {
                        ["command"] = command,
                        ["workingDirectory"] = request.Workspace.RootPath
                    }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            result = new ToolResult(false, Error: ex.Message);
        }

        var summary = BuildSummary(command, result);
        var artifact = BuildArtifact(request, summary);

        // 部署指令真的失敗(非未核准、非未設定)時,如實回報 Success=false,讓後續(這已經是
        // 最後一步)Workflow 執行結果反映真實情況,不像 Git 那樣為了讓後面的 Deploy 能繼續 demo
        // 而刻意吞掉失敗——Deploy 已經是 Pipeline 最後一步,沒有「後面」需要保護。
        return new AgentResult(
            Success: result.Success,
            OutputArtifacts: new IArtifact[] { artifact },
            FailureReason: result.Success ? null : result.Error);
    }

    private static string BuildSummary(string command, ToolResult result)
    {
        if (result.Success)
        {
            var output = result.Output is JsonElement element && element.TryGetProperty("output", out var outputProp)
                ? outputProp.GetString()
                : null;
            return $"部署指令執行成功:`{command}`" + (string.IsNullOrWhiteSpace(output) ? string.Empty : $"\n\n輸出:\n{output}");
        }

        return $"部署指令執行失敗:`{command}`\n{result.Error}";
    }

    private static DocumentArtifact BuildArtifact(AgentExecutionRequest request, string content) => new(
        ArtifactId: Guid.NewGuid().ToString("N"),
        WorkflowId: request.WorkflowId,
        SnapshotId: request.Snapshot?.SnapshotId,
        CreatedAt: DateTimeOffset.UtcNow,
        Content: content);
}
