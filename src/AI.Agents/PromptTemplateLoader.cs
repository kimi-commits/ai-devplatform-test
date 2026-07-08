namespace AI.Agents;

/// <summary>
/// 讀取 prompts/*.v1.md(規格書 v3 第 14 節 Prompt Template 外部化)。
/// System Prompt 全部抽離程式碼,修改不需重新編譯。
/// </summary>
public sealed class PromptTemplateLoader
{
    private readonly string _promptsRootPath;

    public PromptTemplateLoader(string promptsRootPath)
    {
        _promptsRootPath = promptsRootPath;
    }

    public async Task<string> LoadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_promptsRootPath, fileName);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
