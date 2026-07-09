using AI.Core.Agents;
using AI.Core.Artifacts;

namespace AI.Agents;

/// <summary>
/// Stage E(使用者自訂擴充,見 README「迭代開發迴圈」章節):QA 判定 PASS 之後,把這次的
/// PRD 內容 + QA 結論落地成一份「測試報告」檔案(見 TestReportStore),交給人工驗收——對應
/// Chat 面板新增的「Product Manager(驗收)」模式下拉選單。
///
/// 這個 Agent 本身不呼叫 LLM,純粹是「把已經跑完的結果整理存檔」的收尾動作,Kind 是 Tool
/// 不是 Llm(跟 MergeAgent/GitAgent/DeployAgent 同一類,也因此不需要在
/// config/appsettings.json 的 Models 裡設定對應項目)。
///
/// 「人工驗收關卡」的實作方式:workflows/pm-dispatch-pipeline.json 裡這是最後一步,
/// `onSuccess` 刻意留空——這個平台的 Workflow Run 本來就是「一次性狀態機執行到底」的模型,
/// 沒有可以被喚醒的持久化暫停狀態,所以不假裝真的把一個執行中的 Run「暫停」在半路。
/// 老實的做法是:這條自動化 pipeline 就在產生測試報告這裡結束(AgentOrchestrator.RunAsync
/// 看到 GetNextStep 回傳 null 就正常收尾,回傳 Success=true),後續的「完成驗收」
/// (Git commit/push + Deploy,見 workflows/accept-pipeline.json)或「修改規格」(回到 PM
/// 討論)都是使用者在 Chat 面板挑一份測試報告後,主動觸發的「新的一次」動作
/// (見 ChatEndpoints.cs 的 /api/reports/{id}/accept、/api/reports/{id}/revise),
/// 不是同一個 Run 的延續。
/// </summary>
public sealed class TestReportAgent : IAgent
{
    public string Name => "TestReport";

    public AgentKind Kind => AgentKind.Tool;

    public IReadOnlyList<string> RequiredCapabilities { get; } = Array.Empty<string>();

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        // 慣例跟 ProjectManagerAgent/ReviewerAgent 一致:FirstOrDefault 拿整條 Pipeline 一開始
        // seed 進來的「最原始」PRD 內容,LastOrDefault 拿 QA 這一輪(可能已經是第 N 次 retry)的
        // 最新判定,避免抓到舊的 Artifact。
        var prdContent = request.InputArtifacts.OfType<DocumentArtifact>().FirstOrDefault()?.Content
            ?? "(找不到原始 PRD 內容——這次執行可能不是從 PRD 檔案啟動的。)";
        var latestTest = request.InputArtifacts.OfType<TestArtifact>().LastOrDefault();
        var qaSummary = latestTest is null
            ? "(沒有收到 QA 的判定結果。)"
            : string.Join("\n", latestTest.Results);
        var passed = latestTest?.Passed ?? false;

        var store = new TestReportStore(Path.Combine(request.Workspace.RootPath, ".ai-devplatform", "test-reports"));

        try
        {
            var (reportId, title) = await store.SaveAsync(prdContent, qaSummary, passed, cancellationToken);

            var artifact = new DocumentArtifact(
                Guid.NewGuid().ToString("N"), request.WorkflowId, request.Snapshot?.SnapshotId, DateTimeOffset.UtcNow,
                $"📋 測試報告已產生:{title}(reportId={reportId})。" +
                "請到 Chat 面板選「Product Manager(驗收)」模式,選這份測試報告," +
                "決定要「完成驗收」(Git/Deploy)還是「修改規格」(回到 PM 討論)。");

            return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { artifact });
        }
        catch (Exception ex)
        {
            // 存檔失敗不該讓已經跑完、QA 也 PASS 的開發流程被判定失敗——誠實記錄原因,但
            // Success 仍為 true(跟 GitAgent/DeployAgent「環境因素不中斷流程」的既有慣例一致)。
            var failArtifact = new DocumentArtifact(
                Guid.NewGuid().ToString("N"), request.WorkflowId, request.Snapshot?.SnapshotId, DateTimeOffset.UtcNow,
                $"⚠️ 測試報告存檔失敗(不影響本次開發流程判定,但無法在「Product Manager(驗收)」" +
                $"模式下拉選單看到這一筆):{ex.Message}");

            return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { failArtifact });
        }
    }
}
