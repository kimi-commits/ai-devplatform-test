using System.Text;
using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>
/// Stage C(使用者自訂擴充,見 README「迭代開發迴圈」章節):Project Manager Agent(專案經理)。
/// 對應使用者原始 7 階段願景裡的「專案經理 Agent 分發任務給軟體工程 Coders Agents」——
/// 跟 Stage B 的 <see cref="ProductManagerAgent"/>(產品經理,負責跟使用者對話產出 PRD)是
/// 不同角色,不要搞混:ProductManager 對話產出 PRD,ProjectManager 讀 PRD 決定怎麼分工。
///
/// 跟 <see cref="PlannerAgent"/> 的差異:PlannerAgent 產生「一份」任務規格,給後面對稱、
/// 收到相同輸入的 Coder(們)共用(Phase 4 的 parallel-pipeline.json 就是這樣,CoderA/CoderB
/// 收到一模一樣的任務,是「兩個人各自獨立想解法、之後比較」的用法)。ProjectManagerAgent 則是
/// 針對三個「固定但具名」的角色——CoderA(前端)、CoderB(後端)、CoderC(系統架構)——各自
/// 產生「不同」的任務內容,輸出 <see cref="TaskAssignmentArtifact"/>(不是 DocumentArtifact),
/// 讓 CoderAgent 可以只挑「屬於自己名字」的那一份(見 CoderAgent.ExecuteAsync)。
///
/// 「動態分派」的意思:不是這裡的程式碼寫死每個角色固定做什麼,而是每次都讓 LLM 讀當下這份
/// PRD 的實際內容,自行判斷這次的前端/後端/架構工作範疇分別是什麼——某些 PRD 可能某個角色
/// 的任務很少甚至沒有(例如純後端 API 專案),由 LLM 自行拿捏並老實反映在輸出裡,不是這裡的
/// 程式碼邏輯決定「一定要三個角色都分到差不多份量的工作」。
/// </summary>
public sealed class ProjectManagerAgent : IAgent
{
    /// <summary>
    /// 固定的三個具名 Coder 角色。使用者要求至少要有這三個(CoderA-前端、CoderB-後端、
    /// CoderC-系統架構),之後會各自對應獨立的 prompt 檔案(見 CoderAgent 的 promptFile 參數)
    /// 讓使用者可以直接編輯 prompts/coder-*.v1.md 來「寫 skill」,不用改程式碼。
    /// </summary>
    public static readonly IReadOnlyList<string> CoderNames = new[] { "CoderA", "CoderB", "CoderC" };

    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;

    public ProjectManagerAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts)
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
    }

    public string Name => "ProjectManager";

    public AgentKind Kind => AgentKind.Llm;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[] { "Knowledge.Query" };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("project-manager.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(Name);
        var model = _modelRegistry.GetModelNameForAgent(Name);

        // 用 LastOrDefault:seedArtifacts(PRD 全文)是這個 Workflow 一開始就種進去的
        // DocumentArtifact,這是 dispatch 這一步唯一會看到的 DocumentArtifact,理論上取
        // First/Last 都一樣,用 LastOrDefault 是跟專案裡其他 Agent 保持一致的寫法。
        var prd = request.InputArtifacts.OfType<DocumentArtifact>().LastOrDefault()?.Content
            ?? "(沒有收到 PRD 內容,請自行示範一個最小的任務拆解範例。)";

        var userMessage =
            "以下是這次要開發的 PRD(產品需求規格書)全文:\n\n" + prd +
            "\n\n請針對 CoderA(前端工程師)、CoderB(後端工程師)、CoderC(系統架構師)三個角色," +
            "分別拆解出這份 PRD 裡屬於他們負責範疇的具體任務。";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        var assignments = ParseAssignments(response.Content);

        var artifacts = CoderNames.Select(coderName =>
        {
            var content = assignments.TryGetValue(coderName, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                // Project Manager 沒有照格式回覆、或這個角色那段是空的:誠實地把完整 PRD
                // 原文交給這個 Coder,並說明沒有解析到專屬任務,而不是讓這個角色完全沒有輸入
                // 內容可用(空字串會讓 CoderAgent 的 Prompt 看起來像什麼都沒交代)。
                : $"(Project Manager 沒有針對 {coderName} 解析出獨立任務,可能是回覆格式跟預期不符," +
                  "請自行判讀以下完整 PRD、找出屬於你負責範疇的工作;如果真的沒有相關工作,請在" +
                  "回覆中明確說明「這個角色在這份 PRD 沒有對應任務」。)\n\nPRD 全文:\n" + prd;

            return (IArtifact)new TaskAssignmentArtifact(
                ArtifactId: Guid.NewGuid().ToString("N"),
                WorkflowId: request.WorkflowId,
                SnapshotId: request.Snapshot?.SnapshotId,
                CreatedAt: DateTimeOffset.UtcNow,
                AssigneeAgentName: coderName,
                Content: content);
        }).ToList();

        return new AgentResult(Success: true, OutputArtifacts: artifacts);
    }

    /// <summary>
    /// 解析格式:每個角色一個 "### CoderX" 標題,後面接該角色的任務內容,直到下一個標題或字串
    /// 結尾(見 prompts/project-manager.v1.md 的格式規定)。找不到某個角色的標題時,該角色就不會
    /// 出現在回傳的 Dictionary 裡,由呼叫端(ExecuteAsync)決定 fallback,這裡不假裝一定找得到
    /// 三個都有內容。
    /// </summary>
    private static Dictionary<string, string> ParseAssignments(string content)
    {
        var result = new Dictionary<string, string>();
        string? currentCoder = null;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (currentCoder is not null)
            {
                result[currentCoder] = buffer.ToString().Trim();
            }
            buffer.Clear();
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            var matched = CoderNames.FirstOrDefault(name =>
                trimmed.Equals($"### {name}", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals($"###{name}", StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
            {
                Flush();
                currentCoder = matched;
            }
            else if (currentCoder is not null)
            {
                buffer.AppendLine(line);
            }
        }
        Flush();

        return result;
    }
}
