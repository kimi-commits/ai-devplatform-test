using AI.Configuration;
using AI.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace AI.Runtime.Capabilities;

/// <summary>
/// <see cref="ICapabilityGuard"/> 的 Phase 3 實作(規格書 v3 第 6 節)。
/// 風險等級來源是 <c>config/appsettings.json</c> 的 <c>CapabilityRisk</c>,支援精確比對與
/// <c>"Docker.*"</c> 這種前綴通配比對。「怎麼問人」的部分委派給 <see cref="IApprovalPrompt"/>——
/// 目前有 Console y/n(<see cref="ConsoleApprovalPrompt"/>)和 VS Code Extension 確認 UI
/// (<see cref="VsCodeBridgeApprovalPrompt"/>)兩種實作,由 <c>AI.Host</c> 依環境變數
/// <c>AI_DEVPLATFORM_APPROVAL_MODE</c> 決定要注入哪一個,這個類別本身完全不用改。
/// </summary>
public sealed class AppConfigCapabilityGuard : ICapabilityGuard
{
    private readonly AppConfig _config;
    private readonly IApprovalPrompt _approvalPrompt;
    private readonly ILogger<AppConfigCapabilityGuard> _logger;

    public AppConfigCapabilityGuard(AppConfig config, IApprovalPrompt approvalPrompt, ILogger<AppConfigCapabilityGuard> logger)
    {
        _config = config;
        _approvalPrompt = approvalPrompt;
        _logger = logger;
    }

    public RiskLevel GetRisk(string capabilityName)
    {
        if (_config.CapabilityRisk.TryGetValue(capabilityName, out var exact))
        {
            return ParseRisk(capabilityName, exact);
        }

        // 支援 "Docker.*"、"Deploy.*" 這種前綴通配(規格書 v3 第 18 節的設定範例)。
        foreach (var (key, value) in _config.CapabilityRisk)
        {
            if (key.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = key[..^1]; // 保留結尾的 "."(去掉 "*"),例如 "Docker.*" -> "Docker."
                if (capabilityName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return ParseRisk(capabilityName, value);
                }
            }
        }

        // 設定檔沒列出的 Capability,一律視為 Low(全自動執行)。這個系統的風險分級策略是
        // 「明確列出才管制」,而不是「沒列出就當作最危險」——因為目前會被實際呼叫到的高風險
        // 操作(File.Delete、Git.Push、Deploy.Execute、Docker.*、Terminal.Execute)都已經明確
        // 列在 config/appsettings.json 裡,新增一個真正危險的 Capability 時,務必記得同步補上設定。
        return RiskLevel.Low;
    }

    public async Task<bool> RequestApprovalAsync(string capabilityName, string context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("High 風險 Capability 待核准:{Capability} — {Context}", capabilityName, context);

        var approved = await _approvalPrompt.AskAsync(capabilityName, context, cancellationToken);

        _logger.LogInformation("Capability {Capability} 核准結果:{Approved}", capabilityName, approved ? "核准" : "拒絕");

        return approved;
    }

    private RiskLevel ParseRisk(string capabilityName, string rawValue)
    {
        if (Enum.TryParse<RiskLevel>(rawValue, ignoreCase: true, out var risk))
        {
            return risk;
        }

        _logger.LogWarning(
            "CapabilityRisk 設定中 '{Capability}' 的風險等級 '{RawValue}' 無法解析(應為 Low/Medium/High),視為 High 以確保安全。",
            capabilityName, rawValue);
        return RiskLevel.High;
    }
}
