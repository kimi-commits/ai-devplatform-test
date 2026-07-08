using AI.Core.Capabilities;
using AI.Core.Tools;
using Microsoft.Extensions.Logging;

namespace AI.Tools.Runtime;

/// <summary>
/// 依 Tool 名稱路由到正確的 Adapter,對外提供統一介面。
/// Agent 只透過 Capability 取得 Tool,不知道背後是 MCP / Native / Plugin / REST 哪一種
/// (規格書 v3 第 11 節)。更換後端不影響 Agent 程式碼。
///
/// Phase 3 起,每次呼叫前會先用 <see cref="ToolCapabilityMap"/> 查出這個 Tool 對應的 Capability,
/// 再透過 <see cref="ICapabilityGuard"/> 決定風險等級:Low 直接放行,Medium 放行但記一筆 Log
/// 供事後審查,High 會擋下來要求人工核准,核准前完全不會呼叫到底層的 Adapter(規格書 v3
/// 第 6 節「風險等級對應執行方式」)。
/// </summary>
public sealed class ToolRuntime : IToolRuntime
{
    private readonly List<IToolAdapter> _adapters = new();
    private readonly ICapabilityGuard _capabilityGuard;
    private readonly ILogger<ToolRuntime> _logger;

    public ToolRuntime(ICapabilityGuard capabilityGuard, ILogger<ToolRuntime> logger)
    {
        _capabilityGuard = capabilityGuard;
        _logger = logger;
    }

    public void RegisterAdapter(IToolAdapter adapter)
    {
        _adapters.Add(adapter);
        _logger.LogInformation("Registered tool adapter: {Kind}", adapter.Kind);
    }

    public async Task<ToolResult> InvokeAsync(string toolName, ToolRequest request, CancellationToken cancellationToken = default)
    {
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(toolName));
        if (adapter is null)
        {
            return new ToolResult(false, Error: $"No adapter registered for tool '{toolName}'");
        }

        var capabilityName = ToolCapabilityMap.GetCapabilityName(toolName);
        if (capabilityName is not null)
        {
            var risk = _capabilityGuard.GetRisk(capabilityName);
            switch (risk)
            {
                case RiskLevel.High:
                    var context = $"呼叫工具 '{toolName}'(Adapter: {adapter.Kind}),參數:{FormatParameters(request.Parameters)}";
                    var approved = await _capabilityGuard.RequestApprovalAsync(capabilityName, context, cancellationToken);
                    if (!approved)
                    {
                        _logger.LogWarning("Tool {Tool} 因 Capability {Capability} 未獲人工核准而被拒絕執行。", toolName, capabilityName);
                        return new ToolResult(false, Error: $"Capability '{capabilityName}' 是 High 風險操作,使用者未核准執行,已中止 '{toolName}'。");
                    }
                    break;

                case RiskLevel.Medium:
                    _logger.LogWarning("Tool {Tool} 對應 Medium 風險 Capability {Capability},自動執行並記錄於 Log 供事後審查。", toolName, capabilityName);
                    break;

                case RiskLevel.Low:
                default:
                    break;
            }
        }

        _logger.LogInformation("Invoking tool {Tool} via {Adapter}", toolName, adapter.Kind);
        return await adapter.InvokeAsync(request, cancellationToken);
    }

    private static string FormatParameters(IReadOnlyDictionary<string, object?> parameters)
        => string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}"));
}
