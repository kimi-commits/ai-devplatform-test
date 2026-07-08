namespace AI.Core.Workspace;

/// <summary>
/// Workspace 是整個系統核心。Agent 永遠只拿 Workspace,不知道專案在哪(規格書 v1 第 6 節)。
/// </summary>
public sealed record Workspace(
    string Name,
    string RootPath,
    string Language,
    string? Framework,
    string GitBranch,
    string? BuildProfile,
    IReadOnlyDictionary<string, string>? Settings = null);
