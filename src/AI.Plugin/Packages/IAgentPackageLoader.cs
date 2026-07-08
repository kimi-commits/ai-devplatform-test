namespace AI.Plugin.Packages;

/// <summary>
/// Agent Package 的載入契約(Phase 7)。任何人可新增 Rust / Python / Unity / Go Plugin,
/// 不用修改 Runtime(規格書 v1 第 15 節、v3 第 15 節)。
/// </summary>
public interface IAgentPackageLoader
{
    Task<AgentPackageManifest> LoadManifestAsync(string manifestPath, CancellationToken cancellationToken = default);

    Task InstallAsync(AgentPackageManifest manifest, CancellationToken cancellationToken = default);
}
