using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>
/// 負責建立 Test、執行 Test(規格書 v1 第 8 節)。
///
/// Stage A 修正(跟 ReviewerAgent 同一批,見該檔案類別註解的完整說明):原本不管 LLM 回覆什麼
/// 都寫死 Success=true,現在改成解析 `VERDICT: PASS` / `VERDICT: FAIL`(見 prompts/qa.v1.md),
/// FAIL 時真的把流程退回 Coder。老實說:這裡的「測試」是 LLM 對著程式碼內容做推演式判斷,不是
/// 真的執行測試框架(這個平台目前沒有這種自動化測試執行環境),所以 Coverage 目前還是固定 0.0,
/// 不假裝有真實的測試覆蓋率數字。
/// </summary>
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

        var (passed, parseWarning) = ParseVerdict(response.Content);
        var results = new List<string>();
        if (parseWarning is not null)
        {
            results.Add(parseWarning);
        }
        results.Add(response.Content);

        var test = new TestArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Results: results,
            Coverage: 0.0,
            Passed: passed);

        return new AgentResult(
            Success: passed,
            OutputArtifacts: new IArtifact[] { test },
            FailureReason: passed ? null : string.Join("\n", results));
    }

    /// <summary>解析 LLM 回覆第一行的 VERDICT 標記(見 prompts/qa.v1.md 的格式規定)。
    /// 沒有照格式回覆時保守地視為 FAIL,理由跟 ReviewerAgent.ParseVerdict 一致:看不懂結論就不能
    /// 算過,不能靜默放行。</summary>
    private static (bool Passed, string? ParseWarning) ParseVerdict(string content)
    {
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

        if (firstLine.Equals("VERDICT: PASS", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (firstLine.Equals("VERDICT: FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        return (false, $"⚠️ QA 沒有照格式回覆 VERDICT(第一行是:「{firstLine}」),保守地視為 FAIL。");
    }
}
