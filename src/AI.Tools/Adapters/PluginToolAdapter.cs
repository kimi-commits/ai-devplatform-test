using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>
/// 第三方語言 Plugin(Rust/Go/Python),對應規格書 v3 第 15 節的 Plugin System / Agent Package。
/// Phase 1 先留骨架,實際載入機制在 AI.Plugin 專案。
/// </summary>
public sealed class PluginToolAdapter : IToolAdapter
{
    private readonly HashSet<string> _pluginToolNames;

    public PluginToolAdapter(IEnumerable<string> pluginToolNames)
    {
        _pluginToolNames = new HashSet<string>(pluginToolNames, StringComparer.OrdinalIgnoreCase);
    }

    public ToolAdapterKind Kind => ToolAdapterKind.Plugin;

    public bool CanHandle(string toolName) => _pluginToolNames.Contains(toolName);

    public Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        // TODO: 串接 AI.Plugin 的 Plugin Loader,依 Agent Package Manifest 呼叫對應語言的 Plugin。
        return Task.FromResult(new ToolResult(false, Error: "PluginToolAdapter not yet wired (see AI.Plugin)"));
    }
}
