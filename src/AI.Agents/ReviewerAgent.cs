using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>
/// 負責 Code Review、Security、Performance 檢查。不能修改程式(規格書 v1 第 8 節)。
/// 輸入 CodeArtifact,輸出 ReviewArtifact(規格書 v3 第 10 節範例流程)。
/// </summary>
public sealed class ReviewerAgent : IAgent
{
    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;

    public ReviewerAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts)
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
    }

    public string Name => "Reviewer";

    public AgentKind Kind => AgentKind.Llm;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "File.Read", "Knowledge.Query"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("reviewer.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(Name);
        var model = _modelRegistry.GetModelNameForAgent(Name);

        var codeSummary = request.InputArtifacts.OfType<CodeArtifact>().FirstOrDefault()?.Summary
            ?? "(沒有收到 Coder 的修改建議。)";

        var userMessage =
            "請 Review 以下 Coder 提出的修改建議,指出 Security / Performance 上的問題(如果沒有明顯問題," +
            $"請明確說『沒有發現問題』):\n\n{codeSummary}";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        var review = new ReviewArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Findings: new[] { response.Content },
            Verdict: true);

        return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { review });
    }
}
