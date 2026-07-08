using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>
/// 負責 Code Review、Security、Performance 檢查。不能修改程式(規格書 v1 第 8 節)。
/// 輸入 CodeArtifact,輸出 ReviewArtifact(規格書 v3 第 10 節範例流程)。
///
/// Stage A(規格書 Roadmap 之外、使用者要求的「PM/PR/Coder/Review/QA 迴圈」第一階段)修正:
/// 這裡原本不管 LLM 實際回覆什麼內容,`Success`/`Verdict` 都寫死 true,等於 Workflow DSL 早就
/// 定義好的 `onFailure: code`(見 workflows/default-pipeline.json)從來沒有機會被真的觸發過。
/// 現在改成解析 LLM 回覆第一行的 `VERDICT: APPROVED` / `VERDICT: NEEDS_CHANGES`(見
/// prompts/reviewer.v1.md 的格式規定),NEEDS_CHANGES 時 Success=false、Verdict=false,讓
/// AgentOrchestrator 真的把流程退回 Coder,並把這裡的意見透過 InputArtifacts 傳回去(見
/// CoderAgent.cs 的重試邏輯)。LLM 沒有照格式回覆時,保守地視為 NEEDS_CHANGES(誠實地當作
/// 「看不懂結論,不能算過」,而不是預設放行)。
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

        var (verdict, parseWarning) = ParseVerdict(response.Content);
        var findings = new List<string>();
        if (parseWarning is not null)
        {
            findings.Add(parseWarning);
        }
        findings.Add(response.Content);

        var review = new ReviewArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Findings: findings,
            Verdict: verdict);

        return new AgentResult(
            Success: verdict,
            OutputArtifacts: new IArtifact[] { review },
            FailureReason: verdict ? null : string.Join("\n", findings));
    }

    /// <summary>
    /// 解析 LLM 回覆第一行的 VERDICT 標記(見 prompts/reviewer.v1.md 的格式規定)。
    /// 沒有照格式回覆時保守地視為 NEEDS_CHANGES,並在 Findings 前面加一句警告,方便事後追查是不是
    /// Prompt 需要調整,而不是靜默地當作過關。
    /// </summary>
    private static (bool Verdict, string? ParseWarning) ParseVerdict(string content)
    {
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

        if (firstLine.Equals("VERDICT: APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (firstLine.Equals("VERDICT: NEEDS_CHANGES", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        return (false, $"⚠️ Reviewer 沒有照格式回覆 VERDICT(第一行是:「{firstLine}」),保守地視為 NEEDS_CHANGES。");
    }
}
