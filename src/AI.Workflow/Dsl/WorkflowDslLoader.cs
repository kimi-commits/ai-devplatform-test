using System.Text.Json;
using AI.Core.Workflow;

namespace AI.Workflow.Dsl;

/// <summary>
/// 讀取 workflows/*.json 並反序列化為 WorkflowDefinition(規格書 v3 第 8 節)。
/// 新增 Agent(例如 Security)只需在 DSL 加一個 step,不需重新編譯。
/// </summary>
public sealed class WorkflowDslLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WorkflowDefinition> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var definition = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, JsonOptions, cancellationToken);
        return definition ?? throw new InvalidOperationException($"Failed to parse workflow DSL at {path}");
    }

    public WorkflowDefinition LoadFromJson(string json)
    {
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonOptions);
        return definition ?? throw new InvalidOperationException("Failed to parse workflow DSL json");
    }
}
