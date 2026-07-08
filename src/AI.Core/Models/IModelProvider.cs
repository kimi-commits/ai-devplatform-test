namespace AI.Core.Models;

/// <summary>
/// 任何 OpenAI Compatible API 的抽象(NVIDIA NIM / OpenAI / OpenRouter / Ollama)。
/// Model Registry 依 Agent 名稱查出對應 Provider,換模型不需改 Agent(規格書 v1 第 7 節)。
/// </summary>
public interface IModelProvider
{
    string ProviderName { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record ModelRequest(
    string Model,
    IReadOnlyList<ModelMessage> Messages,
    double Temperature = 0.2,
    IReadOnlyList<string>? AvailableTools = null);

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelResponse(string Content, int PromptTokens, int CompletionTokens, string? ToolCallJson = null);

/// <summary>集中管理各 Agent 對應模型,對應規格書 v1 第 7 節的 Model Registry。</summary>
public interface IModelRegistry
{
    IModelProvider GetProviderForAgent(string agentName);

    void Register(string agentName, IModelProvider provider, string modelName);

    string GetModelNameForAgent(string agentName);
}
