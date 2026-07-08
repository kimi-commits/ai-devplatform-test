using System.Text.Json.Serialization;

namespace AI.Plugin.Packages;

/// <summary>
/// 取代單純 Plugin 概念:把 Tool + Prompt + Workflow + Configuration 打包成可安裝單位
/// (規格書 v3 第 15 節)。Phase 7 才實作安裝機制,這裡先定 Manifest 格式。
/// </summary>
public sealed record AgentPackageManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("agents")] IReadOnlyList<string> Agents,
    [property: JsonPropertyName("tools")] IReadOnlyList<string> Tools,
    [property: JsonPropertyName("prompts")] string PromptsPath,
    [property: JsonPropertyName("workflow")] string WorkflowPath);
