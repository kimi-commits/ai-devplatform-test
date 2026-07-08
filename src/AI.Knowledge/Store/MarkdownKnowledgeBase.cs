using AI.Core.Knowledge;

namespace AI.Knowledge.Store;

/// <summary>
/// MVP 實作:用 Markdown 文件 + 簡單關鍵字比對(規格書 v3 第 12 節)。
/// Phase 6 視需要升級為向量檢索,IKnowledgeBase 介面不需變動。
/// </summary>
public sealed class MarkdownKnowledgeBase : IKnowledgeBase
{
    private readonly List<KnowledgeDocument> _documents = new();

    public Task IndexAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        _documents.Add(document);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeDocument>> QueryAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var matches = _documents
            .Where(d => d.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || d.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<KnowledgeDocument>>(matches);
    }
}
