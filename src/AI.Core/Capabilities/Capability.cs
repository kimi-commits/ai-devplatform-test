namespace AI.Core.Capabilities;

/// <summary>
/// Agent 不直接知道 Tool 是什麼,而是宣告需要的 Capability,
/// 由 Runtime 在執行時注入對應的 Tool Runtime 實作(規格書 v3 第 6 節)。
/// </summary>
public sealed record Capability(string Name, RiskLevel Risk);

/// <summary>
/// 依 Capability 名稱決定是否需要人工核准、是否允許執行。
/// VS Code Extension 的 High 風險確認 UI 即掛在這個介面上。
/// </summary>
public interface ICapabilityGuard
{
    RiskLevel GetRisk(string capabilityName);

    /// <summary>High 風險操作需回傳 true 才可執行;由使用者互動介面提供實作。</summary>
    Task<bool> RequestApprovalAsync(string capabilityName, string context, CancellationToken cancellationToken = default);
}

/// <summary>
/// 「怎麼問人」的策略,跟 <see cref="ICapabilityGuard.GetRisk"/> 的風險分級邏輯分開,
/// 方便單獨替換——例如從 Console y/n 換成 VS Code Extension 的確認 UI(規格書 v3 第 16 節),
/// 不需要動到風險分級的程式碼。<see cref="ICapabilityGuard"/> 的實作透過建構子注入這個介面。
/// </summary>
public interface IApprovalPrompt
{
    Task<bool> AskAsync(string capabilityName, string context, CancellationToken cancellationToken = default);
}
