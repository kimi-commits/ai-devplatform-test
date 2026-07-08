using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;
using AI.Core.Tools;

namespace AI.Agents;

/// <summary>
/// 負責修改程式、呼叫 Tool。不能 Build(規格書 v1 第 8 節)。
/// Phase 2 起會把 LLM 的修改建議透過 IToolRuntime 的 file.writeFile 寫成一個真實檔案
/// (走 Native File Adapter,直接 System.IO,不透過 MCP 跨進程——規格書 v3 第 11 節,
/// 驗證 Native Adapter 在真實 Agent 流程中可用)。目前還沒有讓模型直接產生「整份程式碼檔案」,
/// 只是先把建議文字落地成檔案,Phase 3+ 再讓 Coder 產生結構化的多檔案修改。
/// </summary>
public sealed class CoderAgent : IAgent
{
    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;
    private readonly IToolRuntime _toolRuntime;

    public string Name { get; }

    public AgentKind Kind => AgentKind.Llm;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "File.Read", "File.Write", "Knowledge.Query"
    };

    /// <summary>支援多個 Coder 實例(Coder A / Coder B),Phase 4 平行執行時各自對應獨立 worktree。</summary>
    public CoderAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts, IToolRuntime toolRuntime, string name = "Coder")
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
        _toolRuntime = toolRuntime;
        Name = name;
    }

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("coder.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(Name);
        var model = _modelRegistry.GetModelNameForAgent(Name);

        // 用 LastOrDefault 而不是 FirstOrDefault:Phase 5 的 Chat 介面(見
        // AI.Host/Server/ChatEndpoints.cs)會把使用者的原始需求文字當作 seedArtifacts 放在
        // InputArtifacts 最前面,Planner 執行後產生的任務規格 DocumentArtifact 接在後面——
        // Coder 應該要看 Planner「消化過」的任務規格,而不是使用者的原始需求文字。
        var taskSpec = request.InputArtifacts.OfType<DocumentArtifact>().LastOrDefault()?.Content
            ?? "(沒有收到 Planner 的任務規格,請自行示範一個最小的程式修改建議。)";

        // Stage A:Reviewer/QA 現在會真的判定 NEEDS_CHANGES/FAIL 並觸發 DSL 的 onFailure 退回這裡
        // (見 ReviewerAgent.cs / QaAgent.cs 類別註解)。因為 AgentOrchestrator 每次呼叫都會把
        // 目前為止所有 Step 累積的 Artifact 一併當作 InputArtifacts 傳進來,被退回重做時,這裡
        // 找得到最新一份 ReviewArtifact/TestArtifact——只有在它們代表「這次被打回」時才組進
        // Prompt,避免已經 APPROVED/PASS 的舊意見被誤當成還要修正的東西重複塞給模型。
        var latestReview = request.InputArtifacts.OfType<ReviewArtifact>().LastOrDefault();
        var latestTest = request.InputArtifacts.OfType<TestArtifact>().LastOrDefault();

        var feedbackSection = string.Empty;
        if (latestReview is { Verdict: false })
        {
            feedbackSection += "\n\nReviewer 打回的意見(請針對這些問題修正,不要重新從頭發想不相關的方案):\n"
                + string.Join("\n", latestReview.Findings);
        }
        if (latestTest is { Passed: false })
        {
            feedbackSection += "\n\nQA 回報的問題(請針對這些問題修正):\n"
                + string.Join("\n", latestTest.Results);
        }

        var userMessage =
            $"Workspace: {request.Workspace.Name}\n語言: {request.Workspace.Language}\n\n" +
            $"Planner 的任務規格:\n{taskSpec}\n" +
            feedbackSection + "\n\n" +
            "請提出具體的修改建議(檔案、修改內容摘要)。";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        var artifactId = Guid.NewGuid().ToString("N");
        var writtenFiles = await WriteSuggestionToFileAsync(request, artifactId, response.Content, cancellationToken);

        var artifact = new CodeArtifact(
            ArtifactId: artifactId,
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Files: writtenFiles,
            Summary: response.Content);

        return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { artifact });
    }

    /// <summary>
    /// 透過 Native File Adapter(file.writeFile)把建議文字寫到 workspace 底下的
    /// .ai-suggestions/ 目錄。回傳成功寫入的檔案路徑清單(相對於 workspace root);
    /// 寫入失敗時回傳空清單並靜默略過,不讓整條 Pipeline 因為這個示範性步驟而中斷。
    /// </summary>
    private async Task<IReadOnlyList<string>> WriteSuggestionToFileAsync(
        AgentExecutionRequest request, string artifactId, string content, CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine(".ai-suggestions", $"{Name.ToLowerInvariant()}-{artifactId}.md");
        var absolutePath = Path.Combine(request.Workspace.RootPath, relativePath);

        var toolRequest = new ToolRequest(
            Operation: "file.writeFile",
            Parameters: new Dictionary<string, object?>
            {
                ["path"] = absolutePath,
                ["content"] = content
            });

        try
        {
            var result = await _toolRuntime.InvokeAsync("file.writeFile", toolRequest, cancellationToken);
            return result.Success ? new[] { relativePath } : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
