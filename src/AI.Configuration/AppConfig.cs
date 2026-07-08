using System.Text.Json.Serialization;

namespace AI.Configuration;

/// <summary>
/// 對應規格書 v3 第 18 節的 config/appsettings.json。
/// Models 對應 Model Registry;CapabilityRisk 對應 ICapabilityGuard 的風險分級。
/// ModelProvider 是 Phase 1 的簡化:先假設所有 Agent 共用同一個 OpenAI-Compatible 供應商
/// (已用 NVIDIA NIM 驗證過,見 samples/Phase0-MafDemo),之後如需每個 Agent 接不同供應商,
/// 再把這裡從單一物件改成 Dictionary&lt;providerName, ModelProviderConfig&gt;。
/// </summary>
public sealed record AppConfig(
    [property: JsonPropertyName("Models")] Dictionary<string, string> Models,
    [property: JsonPropertyName("Workflow")] string WorkflowPath,
    [property: JsonPropertyName("CapabilityRisk")] Dictionary<string, string> CapabilityRisk,
    [property: JsonPropertyName("ModelProvider")] ModelProviderConfig ModelProvider,
    [property: JsonPropertyName("Deploy")] DeployOptions? Deploy = null,
    [property: JsonPropertyName("Unity")] UnityOptions? Unity = null);

public sealed record ModelProviderConfig(
    [property: JsonPropertyName("BaseUrl")] string BaseUrl,
    [property: JsonPropertyName("ApiKeyEnvVar")] string ApiKeyEnvVar);

/// <summary>
/// DeployAgent 真正的部署動作(見 AI.Agents/DeployAgent.cs、AI.Tools/Adapters/
/// NativeDeployToolHandlers.cs)。這個專案沒有真實的雲端/Docker 部署目標可以測試,所以刻意做成
/// 「執行使用者自己設定的一句 shell 指令」這種最小可行版本,而不是假裝支援 Docker/Azure/AWS/GCP
/// 卻其實測不到——沒有設定 Command 時,DeployAgent 會誠實回報「略過」,不會假裝部署成功。
/// Command 為 null/空字串時視為未設定。
/// </summary>
public sealed record DeployOptions(
    [property: JsonPropertyName("Command")] string? Command = null);

/// <summary>
/// 規格書 Roadmap 真正定義的 Phase 5:Unity Tool,採 Native Adapter(見 AI.Agents/BuildAgent.cs、
/// AI.Tools/Adapters/NativeUnityToolHandlers.cs)。Unity Editor Scripting API 本質上只能
/// in-process/單一 Editor 行程呼叫,所以不像 Search/Git 那樣走 MCP 子行程,而是直接用
/// <c>Process</c> 啟動本機 Unity Editor 的 batchmode 建置(-batchmode -quit -buildTarget)。
/// 這個專案的執行環境沒有安裝 Unity Editor 可以真的測試,所以刻意用「使用者自行設定
/// EditorPath」的最小可行版本——沒有設定 EditorPath 時,BuildAgent 對 Unity 專案一律誠實回報
/// 「略過 Unity Build」,不會假裝建置成功,也不會影響既有的 dotnet build 路徑(非 Unity 專案完全
/// 不受影響)。EditorPath 為 null/空字串時視為未設定。
/// </summary>
public sealed record UnityOptions(
    [property: JsonPropertyName("EditorPath")] string? EditorPath = null,
    [property: JsonPropertyName("BuildTarget")] string BuildTarget = "StandaloneOSX",
    [property: JsonPropertyName("ExecuteMethod")] string? ExecuteMethod = null);
