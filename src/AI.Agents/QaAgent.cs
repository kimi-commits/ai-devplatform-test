using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>負責建立 Test、執行 Test(規格書 v1 第 8 節)。</summary>
public sealed class QaAgent : IAgent
{
    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;

    public QaAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts)
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
    }

    public string Name => "QA";

    public AgentKind Kind => AgentKind.Llm;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "File.Write", "Test.Run"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("qa.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(Name);
        var model = _modelRegistry.GetModelNameForAgent(Name);

        var reviewFindings = request.InputArtifacts.OfType<ReviewArtifact>().FirstOrDefault();
        var reviewText = reviewFindings is null
            ? "(沒有收到 Reviewer 的意見。)"
            : string.Join("\n", reviewFindings.Findings);

        var userMessage =
            "Reviewer 的意見如下,請針對這個變更提出一組最小的測試案例(條列式即可,不用真的執行):\n\n"
            + reviewText;

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        var test = new TestArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Results: new[] { response.Content },
            Coverage: 0.0);

        return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { test });
    }
}
