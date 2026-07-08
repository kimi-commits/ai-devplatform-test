namespace AI.Core.Knowledge;

/// <summary>
/// 靜態、跨 Agent 共用的知識(Coding Guideline / Architecture / Company Rule / API Doc)。
/// 實作上包裝成 Capability("Knowledge.Query")注入給 Planner/Coder/Reviewer(規格書 v3 第 12 節)。
/// MVP 用 Markdown + Search Tool,Phase 6 視需要升級向量檢索。
/// </summary>
public interface IKnowledgeBase
{
    Task<IReadOnlyList<KnowledgeDocument>> QueryAsync(string query, int topK = 5, CancellationToken cancellationToken = default);

    Task IndexAsync(KnowledgeDocument document, CancellationToken cancellationToken = default);
}

public sealed record KnowledgeDocument(string Id, string Title, string Content, string Category);
