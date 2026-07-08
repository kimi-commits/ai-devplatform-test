using AI.Core.Agents;
using Microsoft.Extensions.Logging;

namespace AI.Runtime.ExecutionEngine;

/// <summary>
/// 依 AgentKind 分派到正確的執行後端(規格書 v3 第 2 節)。
/// AgentKind.Llm 交給 Microsoft Agent Framework(見 LlmAgentExecutor);
/// Tool/Script/Workflow 直接執行,不經過 LLM,避免不必要的 ChatClient/Memory/Prompt 開銷。
/// </summary>
public sealed class ExecutionEngine : IExecutionEngine
{
    private readonly ILogger<ExecutionEngine> _logger;

    public ExecutionEngine(ILogger<ExecutionEngine> logger)
    {
        _logger = logger;
    }

    public async Task<AgentResult> RunAsync(IAgent agent, AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ExecutionEngine dispatching {Agent} as {Kind} for workflow {WorkflowId}",
            agent.Name, agent.Kind, request.WorkflowId);

        return agent.Kind switch
        {
            // LLM Agent 的實際模型呼叫、Prompt 組裝、Tool Calling 協商由 Microsoft Agent Framework 負責,
            // 這裡只是統一入口,實際整合點留在 AI.Agents 專案的 LLM Agent 實作中。
            AgentKind.Llm => await agent.ExecuteAsync(request, cancellationToken),
            AgentKind.Tool => await agent.ExecuteAsync(request, cancellationToken),
            AgentKind.Script => await agent.ExecuteAsync(request, cancellationToken),
            AgentKind.Workflow => await agent.ExecuteAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Unknown AgentKind: {agent.Kind}")
        };
    }
}
