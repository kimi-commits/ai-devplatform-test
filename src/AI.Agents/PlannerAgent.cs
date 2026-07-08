using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Models;

namespace AI.Agents;

/// <summary>
/// 負責分析需求、拆工作、建立 Task。不能修改程式(規格書 v1 第 8 節)。
/// AgentKind.Llm:透過 Microsoft Agent Framework 執行(見 AI.Models.Providers.OpenAiCompatibleProvider,
/// 用法已在 samples/Phase0-MafDemo 用 NVIDIA NIM 驗證過)。
/// </summary>
public sealed class PlannerAgent : IAgent
{
    private readonly IModelRegistry _modelRegistry;
    private readonly PromptTemplateLoader _prompts;

    public PlannerAgent(IModelRegistry modelRegistry, PromptTemplateLoader prompts)
    {
        _modelRegistry = modelRegistry;
        _prompts = prompts;
    }

    public string Name => "Planner";

    public AgentKind Kind => AgentKind.Llm;

    public IReadOnlyList<string> RequiredCapabilities { get; } = new[]
    {
        "Knowledge.Query"
    };

    public async Task<AgentResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _prompts.LoadAsync("planner.v1.md", cancellationToken);
        var provider = _modelRegistry.GetProviderForAgent(Name);
        var model = _modelRegistry.GetModelNameForAgent(Name);

        // Phase 5:Chat 介面(見 AI.Host/Server/ChatEndpoints.cs)會把使用者輸入的需求文字包成一個
        // DocumentArtifact,當作 Workflow 開始執行前的 seedArtifacts 放進來;CLI 模式(dotnet run
        // 直接跑 pipeline,沒有經過 Chat)則沒有這個 seed,維持 Phase 1 原本「示範性任務」的行為,
        // 不受影響。
        var userRequirement = request.InputArtifacts.OfType<DocumentArtifact>().FirstOrDefault()?.Content;

        var userMessage = userRequirement is { Length: > 0 }
            ? $"Workspace: {request.Workspace.Name} ({request.Workspace.RootPath})\n" +
              $"語言/框架: {request.Workspace.Language} / {request.Workspace.Framework}\n\n" +
              $"使用者透過 Chat 提出的需求:\n{userRequirement}\n\n" +
              "請針對這個需求,提出下一步最小可執行的任務規格。"
            : $"Workspace: {request.Workspace.Name} ({request.Workspace.RootPath})\n" +
              $"語言/框架: {request.Workspace.Language} / {request.Workspace.Framework}\n\n" +
              "請針對這個 workspace,提出下一步最小可執行的任務規格(這是 Phase 1 骨架的第一次真實呼叫," +
              "還沒有真正的使用者需求輸入,請示範性地提出一個簡單、低風險的任務即可)。";

        var response = await provider.CompleteAsync(
            new ModelRequest(model, new[]
            {
                new ModelMessage("system", systemPrompt),
                new ModelMessage("user", userMessage)
            }),
            cancellationToken);

        var artifact = new DocumentArtifact(
            ArtifactId: Guid.NewGuid().ToString("N"),
            WorkflowId: request.WorkflowId,
            SnapshotId: request.Snapshot?.SnapshotId,
            CreatedAt: DateTimeOffset.UtcNow,
            Content: response.Content);

        return new AgentResult(Success: true, OutputArtifacts: new IArtifact[] { artifact });
    }
}
