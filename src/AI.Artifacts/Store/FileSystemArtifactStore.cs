using System.Text.Json;
using AI.Core.Artifacts;

namespace AI.Artifacts.Store;

/// <summary>
/// MVP 實作:內容存檔案系統、metadata 存索引檔(對應規格書 v3 技術選型的 SQLite,
/// Phase 1 先以 JSON 索引檔簡化,Phase 2 之後可換成 SQLite 而不影響 IArtifactStore 介面)。
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _rootPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileSystemArtifactStore(string rootPath)
    {
        _rootPath = rootPath;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task SaveAsync(IArtifact artifact, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_rootPath, $"{artifact.ArtifactId}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, artifact, artifact.GetType(), JsonOptions, cancellationToken);
    }

    public async Task<IArtifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_rootPath, $"{artifactId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var type = doc.RootElement.GetProperty("Type").GetString();
        // 依 Type 反序列化為對應的具體 Artifact 型別。
        return type switch
        {
            nameof(CodeArtifact) => JsonSerializer.Deserialize<CodeArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(DiffArtifact) => JsonSerializer.Deserialize<DiffArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(ReviewArtifact) => JsonSerializer.Deserialize<ReviewArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(TestArtifact) => JsonSerializer.Deserialize<TestArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(BuildLogArtifact) => JsonSerializer.Deserialize<BuildLogArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(ScreenshotArtifact) => JsonSerializer.Deserialize<ScreenshotArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(PrArtifact) => JsonSerializer.Deserialize<PrArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            nameof(DocumentArtifact) => JsonSerializer.Deserialize<DocumentArtifact>(doc.RootElement.GetRawText(), JsonOptions),
            _ => null
        };
    }

    public async Task<IReadOnlyList<IArtifact>> GetByWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var results = new List<IArtifact>();
        foreach (var file in Directory.EnumerateFiles(_rootPath, "*.json"))
        {
            var artifactId = Path.GetFileNameWithoutExtension(file);
            var artifact = await GetAsync(artifactId, cancellationToken);
            if (artifact is not null && artifact.WorkflowId == workflowId)
            {
                results.Add(artifact);
            }
        }

        return results;
    }
}
