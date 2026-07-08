namespace AI.Core.Agents;

/// <summary>
/// Execution Engine 依此分派 Agent 到正確的執行後端。
/// 不是所有 Agent 都需要 LLM(見規格書 v3 第 2 節)。
/// </summary>
public enum AgentKind
{
    /// <summary>需要呼叫模型、有 Prompt、可能需要 Tool Calling(經由 Microsoft Agent Framework)。</summary>
    Llm,

    /// <summary>直接呼叫單一 Tool,不需要模型。例如 Build、Git。</summary>
    Tool,

    /// <summary>純程式邏輯,無模型無 Tool 呼叫協商。</summary>
    Script,

    /// <summary>本身是一段子 Workflow 的封裝。例如 Deploy。</summary>
    Workflow
}
