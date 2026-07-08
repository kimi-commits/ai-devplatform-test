namespace AI.Core.Capabilities;

/// <summary>
/// 對應規格書 v3 第 6 節的執行方式:
/// Low = 全自動執行;Medium = 自動執行但記錄 Log;High = 需使用者明確確認。
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High
}
