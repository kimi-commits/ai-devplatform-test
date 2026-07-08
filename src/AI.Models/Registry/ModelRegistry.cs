using AI.Core.Models;

namespace AI.Models.Registry;

/// <summary>
/// 集中管理各 Agent 對應模型,換模型不需改 Agent(規格書 v1 第 7 節)。
/// </summary>
public sealed class ModelRegistry : IModelRegistry
{
    private readonly Dictionary<string, (IModelProvider Provider, string ModelName)> _registrations = new();

    public void Register(string agentName, IModelProvider provider, string modelName)
    {
        _registrations[agentName] = (provider, modelName);
    }

    public IModelProvider GetProviderForAgent(string agentName)
    {
        if (!_registrations.TryGetValue(agentName, out var entry))
        {
            throw new InvalidOperationException($"No model registered for agent '{agentName}'");
        }

        return entry.Provider;
    }

    public string GetModelNameForAgent(string agentName)
    {
        if (!_registrations.TryGetValue(agentName, out var entry))
        {
            throw new InvalidOperationException($"No model registered for agent '{agentName}'");
        }

        return entry.ModelName;
    }
}
