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
///
/// Stage C 修正(順手修掉一個既有的 bug,不是 Stage C 才出現的,只是這次改平行 Coder 的邏輯
/// 時才發現):原本用 `.FirstOrDefault()` 抓 CodeArtifact,有兩個問題——(1) Coder 被退回重做
/// 之後,`.FirstOrDefault()` 抓到的還是「第一次」的舊版本摘要,不是重做後的最新版本,等於
/// Reviewer 永遠在審查同一份沒被修過的內容,重試迴圈的「根據意見修正」變成空轉,能不能過純粹看
/// LLM 對同一份輸入的隨機性;(2) 平行 Coder(parallel-pipeline.json / pm-dispatch-pipeline.json)
/// 有多個 CodeArtifact,`.FirstOrDefault()` 只看得到第一個分支,完全忽略其他分支寫了什麼。
/// 改成:如果有 Merge 步驟產生的彙整報告(平行 Coder 之後才會有,判斷方式是「有不只一個
/// CodeArtifact,而且有一份 DocumentArtifact 的時間點晚於最新一個 CodeArtifact」),就審查那份
/// 彙整報告;否則(單一 Coder,或還沒跑到 Merge)退回審查最新一個 CodeArtifact 的摘要。
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

        var codeSummary = BuildCodeSummary(request.InputArtifacts);

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
    /// 見類別開頭「Stage C 修正」註解:有 Merge 彙整報告就審那份(涵蓋所有平行分支、而且是
    /// 最新的),沒有的話退回審查最新一個 CodeArtifact(而不是舊的 FirstOrDefault)。
    /// </summary>
    private static string BuildCodeSummary(IReadOnlyList<IArtifact> inputArtifacts)
    {
        var codeArtifacts = inputArtifacts.OfType<CodeArtifact>().ToList();
        var latestCode = codeArtifacts.LastOrDefault();
        if (latestCode is null)
        {
            return "(沒有收到 Coder 的修改建議。)";
        }

        var latestDocument = inputArtifacts.OfType<DocumentArtifact>().LastOrDefault();
        var mergeReportLooksNewer = codeArtifacts.Count > 1
            && latestDocument is not null
            && latestDocument.CreatedAt >= latestCode.CreatedAt;

        return mergeReportLooksNewer ? latestDocument!.Content : latestCode.Summary;
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
