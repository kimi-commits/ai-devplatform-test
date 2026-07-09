using System.Text;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>一輪規劃對話,Role 只會是 "user" 或 "pm"。</summary>
public sealed record PlanningTurn(string Role, string Content);

/// <summary>
/// Stage B(使用者自訂擴充,見 README「迭代開發迴圈」章節):Product Manager Agent,跟使用者
/// 多輪對話討論規格,不是像 Planner/Coder/Reviewer/QA 那樣單次呼叫就結束。
///
/// 刻意不實作 <see cref="IAgent"/>:IAgent.ExecuteAsync 的參數(Workspace、InputArtifacts、
/// Snapshot)是為了「AgentOrchestrator 依 Workflow DSL 狀態機分派一次性 Step」這個模型設計的,
/// 跟「使用者在 Chat 面板來回打字討論,伺服器要記住對話歷史」這種用法不搭。這裡改成一個單純的
/// DI 服務,由 AI.Host/Server/ChatEndpoints.cs 的 /api/planning* 端點直接呼叫,不透過
/// AgentOrchestrator/WorkflowStep,所以也不會出現在 AgentOrchestrator 的 _agentsByName 裡。
///
/// 已知限制(誠實記錄,不假裝用了真正的多輪 Chat API):
/// <see cref="AI.Models.Providers.OpenAiCompatibleProvider"/> 目前的 CompleteAsync 實作會把
/// <c>ModelRequest.Messages</c> 裡所有非 system 的訊息合併成一段
/// 文字,餵給底層 Microsoft Agent Framework 的 <c>AIAgent.RunAsync(string)</c>(單一字串參數),
/// 不是真的用 Chat API 的多輪 messages 陣列——這是 Phase 0 建立以來就有的既有限制,牽動的是
/// 5 個 Agent 共用的底層元件,這次不改它(風險與範圍都超出 Stage B)。這裡改成自己把對話歷史
/// 組成一份帶「使用者/PM」角色標籤的逐字稿,當作單一個 user 訊息送出,讓模型至少看得出來
/// 每一句是誰說的,不會完全失去對話脈絡,但終究不是原生的多輪對話格式。
/// </summary>
public sealed class ProductManagerAgent
{
    private const string AgentName = "ProductManager";

    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;

    public ProductManagerAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts)
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
    }

    /// <summary>依目前為止的對話歷史(已經包含使用者最新一句),產生 PM 的下一句回覆。</summary>
    public async Task<string> ReplyAsync(IReadOnlyList<PlanningTurn> history, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("product-manager.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(AgentName);
        var model = _modelRegistry.GetModelNameForAgent(AgentName);

        var userMessage = BuildTranscript(history) +
            "\n\n請針對使用者最新一句話,繼續這場規格討論對話。只回覆你這一輪要對使用者說的話" +
            "(例如追問細節、確認理解、或提出建議),不要重複列出整段歷史逐字稿。";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        return response.Content;
    }

    /// <summary>使用者確認討論足夠後,請 PM 根據完整對話產出一份結構化規格書,交給 Planner/Coder 用。</summary>
    public async Task<string> FinalizeSpecAsync(IReadOnlyList<PlanningTurn> history, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("product-manager.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(AgentName);
        var model = _modelRegistry.GetModelNameForAgent(AgentName);

        var userMessage = BuildTranscript(history) +
            "\n\n使用者已經確認討論足夠,請根據以上完整對話,輸出一份結構化的最終規格書," +
            "給接下來的開發團隊(Planner/Coder)使用。內容至少要包含:這個小程式/功能的目的、" +
            "具體需求項目(條列)、驗收標準。不要再問問題,也不要附加開場白或客套話,直接輸出" +
            "規格書本身。";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        return response.Content;
    }

    private static string BuildTranscript(IReadOnlyList<PlanningTurn> history)
    {
        if (history.Count == 0)
        {
            return "(目前還沒有任何對話內容。)";
        }

        var sb = new StringBuilder("到目前為止的對話紀錄:\n\n");
        foreach (var turn in history)
        {
            var speaker = turn.Role == "user" ? "使用者" : "PM(你自己之前說過的話)";
            sb.AppendLine($"{speaker}:{turn.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
