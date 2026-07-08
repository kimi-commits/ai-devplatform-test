namespace AI.Core.Workspace;

/// <summary>管理已註冊的 Workspace(對應規格書 Phase 6 的 Multi Workspace)。</summary>
public interface IWorkspaceRepository
{
    Task<Workspace?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task RegisterAsync(Workspace workspace, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken = default);
}
