namespace AI.Core.Agents;

/// <summary>
/// 統一分派 LLM / Tool / Script / Workflow 四種 Agent 類型的執行後端。
/// Microsoft Agent Framework 只是 AgentKind.Llm 的其中一種執行後端,不是全局唯一路徑。
/// </summary>
public interface IExecutionEngine
{
    Task<AgentResult> RunAsync(IAgent agent, AgentExecutionRequest request, CancellationToken cancellationToken = default);
}
