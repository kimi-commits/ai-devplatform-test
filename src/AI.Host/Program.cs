using AI.Agents;
using AI.Artifacts.Store;
using AI.Configuration;
using AI.Core.Agents;
using AI.Core.Artifacts;
using AI.Core.Capabilities;
using AI.Core.Events;
using AI.Core.Knowledge;
using AI.Core.Memory;
using AI.Core.Models;
using AI.Core.Tools;
using AI.Core.Workflow;
using AI.Knowledge.Store;
using AI.Logging;
using AI.Memory.Store;
using AI.Models.Providers;
using AI.Models.Registry;
using AI.Host.Server;
using AI.Runtime.Capabilities;
using AI.Runtime.ExecutionEngine;
using AI.Runtime.Events;
using AI.Runtime.Workspace;
using AI.Tools.Adapters;
using AI.Tools.Runtime;
using AI.Workflow.Dsl;
using AI.Workflow.Engine;
using AI.Workflow.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// AI.Host 職責:啟動 Runtime、初始化 DI、Logging、Agent、Tool、MCP。
// 不包含任何 Business Logic(規格書 v1 第 4 節)。
//
// Phase 5 起改用 WebApplicationBuilder 而不是單純的 ServiceCollection,這樣同一份 DI 組裝
// (Agent、ToolRuntime、EventBus 等,完全不變)可以同時支援兩種模式(規格書 v3 第 16 節的
// Chat/Diff/Task Tree 需要一個常駐的 HTTP Server,跟 Phase 1~4 原本「跑一次就結束」的 CLI
// 模式是不同的執行方式,但背後的 Agent/Workflow 邏輯要共用,不重複寫兩份):
// - "pipeline"(預設,不設定 AI_DEVPLATFORM_MODE 或設成其他值):跟 Phase 1~4 完全一樣,
//   建好 DI 就跑一次 Workflow、印出結果、結束程式。這裡刻意不改動這段邏輯的任何一行,
//   確保使用者已經驗證過的 CLI 行為不受影響。
// - "serve":不跑 CLI pipeline,改成呼叫 app.MapAiDevPlatformApi(...) 掛上 Chat/Diff/Task Tree
//   的 HTTP+SSE API(見 AI.Host/Server/ChatEndpoints.cs),然後 app.RunAsync() 常駐監聽,
//   由 VS Code Extension 透過 HTTP 呼叫來觸發 Workflow、訂閱進度。

var baseDir = AppContext.BaseDirectory;
var repoRoot = FindRepoRoot(baseDir);
var configPath = Path.Combine(repoRoot, "config", "appsettings.json");
var defaultWorkflowPath = Path.Combine(repoRoot, "workflows", "default-pipeline.json");
var promptsRootPath = Path.Combine(repoRoot, "prompts");
var logPath = Path.Combine(repoRoot, "logs", "ai-devplatform-.log");

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// WebApplicationBuilder 預設會自己加 Console/Debug 這些 Logging Provider,先清掉,
// 避免跟下面 AddPlatformLogging 的 Serilog Console Sink 重複輸出同一行 Log 兩次
// (Phase 1~4 用的是純 ServiceCollection,原本就只有 Serilog 一種輸出,這裡保持一致)。
builder.Logging.ClearProviders();

// 1) Logging
services.AddPlatformLogging(logPath);

// 2) Configuration
if (!File.Exists(configPath))
{
    throw new InvalidOperationException($"找不到設定檔:{configPath}");
}

var appConfig = await AppConfigLoader.LoadAsync(configPath);
services.AddSingleton(appConfig);

// 3) Model Provider(Phase 1 簡化:所有 Agent 先共用同一個 OpenAI-Compatible 供應商,
// 用法已在 samples/Phase0-MafDemo 用 NVIDIA NIM 驗證過)
var apiKey = Environment.GetEnvironmentVariable(appConfig.ModelProvider.ApiKeyEnvVar)
    ?? throw new InvalidOperationException(
        $"環境變數 {appConfig.ModelProvider.ApiKeyEnvVar} 未設定。" +
        "請先到 https://build.nvidia.com 申請 API Key,再執行:" +
        $"export {appConfig.ModelProvider.ApiKeyEnvVar}=\"nvapi-...\"");

// 使用者自訂擴充:appConfig.ModelProvider.TimeoutSeconds 覆蓋 OpenAI SDK 預設的 100 秒
// NetworkTimeout(見 OpenAiCompatibleProvider.cs 建構子註解、AppConfig.ModelProviderConfig
// 類別註解——CoderA/B/C 這類大模型單輪生成常常超過 100 秒導致 Step 失敗)。
var modelProvider = new OpenAiCompatibleProvider(
    "nvidia-nim", apiKey, appConfig.ModelProvider.BaseUrl,
    TimeSpan.FromSeconds(appConfig.ModelProvider.TimeoutSeconds));
var modelRegistry = new ModelRegistry();
foreach (var (agentName, modelName) in appConfig.Models)
{
    modelRegistry.Register(agentName, modelProvider, modelName);
}

services.AddSingleton<IModelRegistry>(modelRegistry);
services.AddSingleton(new PromptTemplateLoader(promptsRootPath));

// 4) Core infrastructure
services.AddSingleton<IEventBus, InMemoryEventBus>();
services.AddSingleton<IExecutionEngine, ExecutionEngine>();
services.AddSingleton<IArtifactStore>(new FileSystemArtifactStore(Path.Combine(repoRoot, ".artifacts")));
services.AddSingleton<IKnowledgeBase, MarkdownKnowledgeBase>();

// Phase 3:High 風險 Capability 的核准機制(規格書 v3 第 6 節)。"怎麼問人" 這件事拆成
// IApprovalPrompt 這個獨立介面,依環境變數 AI_DEVPLATFORM_APPROVAL_MODE 決定用哪一種:
// - "vscode"(使用者自訂擴充,改為預設):寫檔案到 .ai-devplatform/approvals/,由 VS Code
//   Extension 的 AI-DOS 面板跳出圖形化確認對話框(規格書 v3 第 16 節),對應
//   extensions/vscode-extension/src/approvalBridge.ts——使用者平時都在 VS Code 裡操作,
//   不想每次啟動都手動 export 這個環境變數才會跳視窗,所以改成預設值。
// - "console":Console y/n,不需要開 VS Code 就能測(例如寫自動化測試腳本、CI),已實測驗證過
//   核准/拒絕兩條路徑,需要時用 AI_DEVPLATFORM_APPROVAL_MODE=console 明確指定切回這種模式。
var approvalMode = (Environment.GetEnvironmentVariable("AI_DEVPLATFORM_APPROVAL_MODE") ?? "vscode").Trim().ToLowerInvariant();
if (approvalMode == "vscode")
{
    var approvalsDirectory = Path.Combine(repoRoot, ".ai-devplatform", "approvals");
    services.AddSingleton<IApprovalPrompt>(sp => new VsCodeBridgeApprovalPrompt(
        approvalsDirectory,
        sp.GetRequiredService<ILogger<VsCodeBridgeApprovalPrompt>>()));
}
else
{
    services.AddSingleton<IApprovalPrompt, ConsoleApprovalPrompt>();
}

services.AddSingleton<ICapabilityGuard, AppConfigCapabilityGuard>();
services.AddSingleton<IToolRuntime, ToolRuntime>();

// Phase 4:Workspace Snapshot 正式啟用(規格書 v3 第 4 節)。平行 Coder(見下方 CoderA/CoderB)
// 各自透過這個 Provider 拿一個獨立的 SnapshotId,標記自己的輸出 Artifact 是哪個分支產生的。
// 目前的執行環境不是真正的 git repository,所以 WorktreePath 尚未啟用(見
// GitWorkspaceSnapshotProvider 類別註解的完整說明),GitCommitSha 有 git repo 就探測真的值,
// 沒有就老實回報 "unknown"。
services.AddSingleton<AI.Core.Workspace.IWorkspaceSnapshotProvider, GitWorkspaceSnapshotProvider>();

// 5) Workflow
services.AddSingleton<WorkflowDslLoader>();
services.AddSingleton<IWorkflowEngine, WorkflowEngine>();
services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

// 6) Agents(每個 Agent 私有 Memory,不共享,規格書 v3 第 13 節)
// Stage B(使用者自訂擴充,見 README「迭代開發迴圈」章節):ProductManagerAgent 刻意不透過
// IAgent 集合註冊——它不是 Workflow Step 會呼叫的一次性 Agent,是給 ChatEndpoints.cs 的
// /api/planning* 端點直接注入使用的多輪對話服務,注入 IEnumerable<IAgent> 的地方
// (AgentOrchestrator 建構子)完全不會看到它,不會有名稱衝突的疑慮。
services.AddSingleton(sp => new AI.Agents.ProductManagerAgent(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<PromptTemplateLoader>()));
// Stage C(使用者自訂擴充,見 README「迭代開發迴圈」章節):ProjectManagerAgent 是「專案經理」,
// 跟上面的 ProductManagerAgent(「產品經理」)是不同角色,不要搞混。這裡「有」透過 IAgent
// 集合註冊(跟 ProductManagerAgent 不同)——因為 ProjectManagerAgent 是 Workflow DSL 裡一次性
// 呼叫的 Step(見 workflows/pm-dispatch-pipeline.json 的 "dispatch" 步驟),不是多輪對話服務,
// 走的是一般 AgentOrchestrator 流程,自然會出現在 _agentsByName 裡(Name="ProjectManager")。
services.AddSingleton<IAgent, AI.Agents.ProjectManagerAgent>();
services.AddSingleton<IAgent, PlannerAgent>();
services.AddSingleton<IAgent>(sp => new CoderAgent(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<PromptTemplateLoader>(),
    sp.GetRequiredService<IToolRuntime>(),
    "Coder"));
services.AddSingleton<IAgent, ReviewerAgent>();
services.AddSingleton<IAgent, QaAgent>();
// Stage E(使用者自訂擴充):QA 判定 PASS 之後把 PRD+QA 結論落地成測試報告,交給人工驗收,
// 見 TestReportAgent.cs 類別註解、workflows/pm-dispatch-pipeline.json 的 "report" 步驟。
// 不呼叫 LLM,不需要在 config/appsettings.json 的 Models 設定對應項目。
services.AddSingleton<IAgent, AI.Agents.TestReportAgent>();
services.AddSingleton<IAgent>(sp => new BuildAgent(
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<IToolRuntime>()));
services.AddSingleton<IAgent, GitAgent>();
services.AddSingleton<IAgent>(sp => new DeployAgent(
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<IToolRuntime>()));

// Phase 4:平行 Coder(規格書 v3 第 19 節)。CoderAgent 從 Phase 1 就支援具名多實例
// (constructor 的 name 參數),這裡各自用 "CoderA"/"CoderB"/"CoderC" 註冊,對應
// config/appsettings.json 的 Models 設定、以及 workflows/parallel-pipeline.json 的
// "parallel": ["CoderA", "CoderB"] 和 workflows/pm-dispatch-pipeline.json 的
// "parallel": ["CoderA", "CoderB", "CoderC"]。
//
// Stage C(使用者自訂擴充):這三個名字現在對應具體角色——CoderA 前端、CoderB 後端、
// CoderC 系統架構師(使用者原始需求),各自載入獨立的 prompt 檔案(coder-frontend.v1.md /
// coder-backend.v1.md / coder-architect.v1.md),之後使用者要調整某個角色的「skill」,
// 直接編輯對應的 prompt 檔案即可,不需要改這裡的程式碼。這也代表 CoderA/CoderB 從原本
// parallel-pipeline.json 用的「泛用、對稱的 Coder」變成「有專屬角色分工的 Coder」——
// parallel-pipeline.json 原本的用法(兩個 Coder 各自獨立解同一題、之後比較)語意上會跟著
// 改變(兩人現在會用各自的角色視角看同一份任務),但因為都還是同一份完整任務規格、沒有實際
// 拆解分工,實際影響有限;真正「動態分派不同任務給三人」的是新的 pm-dispatch-pipeline.json。
// 只有在真的要跑這些平行 Pipeline 時才會被 Orchestrator 用到,序列 Pipeline
// (default-pipeline.json,用的是 Name="Coder"、預設 promptFile="coder.v1.md")不受影響。
services.AddSingleton<IAgent>(sp => new CoderAgent(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<PromptTemplateLoader>(),
    sp.GetRequiredService<IToolRuntime>(),
    "CoderA",
    "coder-frontend.v1.md"));
services.AddSingleton<IAgent>(sp => new CoderAgent(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<PromptTemplateLoader>(),
    sp.GetRequiredService<IToolRuntime>(),
    "CoderB",
    "coder-backend.v1.md"));
services.AddSingleton<IAgent>(sp => new CoderAgent(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<PromptTemplateLoader>(),
    sp.GetRequiredService<IToolRuntime>(),
    "CoderC",
    "coder-architect.v1.md"));
services.AddSingleton<IAgent, MergeAgent>();

var app = builder.Build();
var provider = app.Services;

var logger = provider.GetRequiredService<ILogger<Program>>();
logger.LogInformation("AI-DevPlatform Host starting. Repo root: {RepoRoot}", repoRoot);
logger.LogInformation(
    "High 風險 Capability 核准模式:{ApprovalMode}" +
    "(可用 AI_DEVPLATFORM_APPROVAL_MODE=console|vscode 切換)。",
    approvalMode);

// 7) Tool Runtime 組裝:Native Adapter(直接 in-process 呼叫)+ MCP Adapter
// (規格書 v3 第 11 節,Phase 2:McpToolAdapter + Native File Adapter 真實接線)。
// File.*/Deploy.*/Unity.* 一律走 Native;Search/Git/Build(.run)/Terminal/Browser 走 MCP,
// 呼叫 extensions/mcp-server(Node.js 子行程),兩者透過同一個 IToolRuntime 對 Agent 呈現統一介面。
// ToolRuntime.InvokeAsync 用 FirstOrDefault 找第一個 CanHandle 的 Adapter,所以 Native 一定要在
// McpToolAdapter 之前註冊,同名工具(理論上不會重複,但保險起見)才會是 Native 贏。
var toolRuntime = provider.GetRequiredService<IToolRuntime>();
toolRuntime.RegisterAdapter(new NativeToolAdapter(NativeFileToolHandlers.CreateHandlers()));
// Deploy 真實實作(規格書 v1 第 8 節,見 DeployAgent.cs / NativeDeployToolHandlers.cs 的範疇說明):
// 執行 config/appsettings.json 的 Deploy.Command 設定的一句 shell 指令,一樣走 Native Adapter,
// 跟檔案操作用同一種「直接 in-process 呼叫」後端,不需要額外一個 MCP 子行程。
toolRuntime.RegisterAdapter(new NativeToolAdapter(NativeDeployToolHandlers.CreateHandlers()));
// Phase 5 真實實作(規格書 Roadmap:Unity Tool,採 Native Adapter,見 BuildAgent.cs /
// NativeUnityToolHandlers.cs):Unity Editor Scripting API 本質上只能 in-process 呼叫,跟
// extensions/mcp-server/src/tools/unityTool.ts 裡刻意留白的 MCP 版本不同,這裡才是真正會被呼叫的
// 那一個。
toolRuntime.RegisterAdapter(new NativeToolAdapter(NativeUnityToolHandlers.CreateHandlers()));

AI.MCP.Client.McpToolInvoker? mcpInvoker = null;
var mcpServerEntry = Path.Combine(repoRoot, "extensions", "mcp-server", "dist", "index.js");
if (File.Exists(mcpServerEntry))
{
    var mcpClient = AI.MCP.Client.McpClient.CreateForNodeServer(mcpServerEntry);
    mcpInvoker = new AI.MCP.Client.McpToolInvoker(mcpClient);

    // "unity.build" 刻意不在這個清單裡:Phase 5 真實實作改成 Native Adapter(見上面
    // NativeUnityToolHandlers 的註冊),extensions/mcp-server/src/tools/unityTool.ts 那個版本
    // 保留只是文件對照用途,不會被 ToolRuntime 呼叫到。
    var mcpToolNames = new[]
    {
        "search.searchText", "search.searchSymbol", "search.searchRegex",
        "git.status", "git.diff", "git.commit", "git.checkout", "git.branch", "git.push",
        "build.run", "terminal.run", "browser.open"
    };
    toolRuntime.RegisterAdapter(new McpToolAdapter(mcpInvoker, mcpToolNames));

    try
    {
        var toolNames = await mcpInvoker.ListToolNamesAsync();
        logger.LogInformation("MCP Server 已連線,提供 {Count} 個工具:{Tools}", toolNames.Count, string.Join(", ", toolNames));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "MCP Server 連線失敗,McpToolAdapter 已註冊但實際呼叫時可能失敗。");
    }
}
else
{
    logger.LogWarning(
        "找不到 MCP Server 編譯輸出:{Path}。請先執行 `cd extensions/mcp-server && npm install && npm run build`。" +
        "本次啟動只會註冊 Native File Adapter,略過 MCP Adapter。",
        mcpServerEntry);
}

var parallelWorkflowPath = Path.Combine(repoRoot, "workflows", "parallel-pipeline.json");
var hostMode = (Environment.GetEnvironmentVariable("AI_DEVPLATFORM_MODE") ?? "pipeline").Trim().ToLowerInvariant();

if (hostMode == "serve")
{
    // Phase 5:常駐 HTTP+SSE Server 模式(規格書 v3 第 16 節 Chat/Diff/Task Tree)。
    // 不主動跑任何 Workflow,等 VS Code Extension 呼叫 POST /api/chat 觸發。
    if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    {
        app.Urls.Add("http://localhost:5170");
    }

    app.MapAiDevPlatformApi(repoRoot, defaultWorkflowPath, parallelWorkflowPath);

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        if (mcpInvoker is not null)
        {
            mcpInvoker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    });

    logger.LogInformation(
        "AI-DevPlatform Host 以 serve 模式啟動,監聽 {Urls}(Ctrl+C 結束)。VS Code Extension 的 " +
        "aiDevPlatform.apiBaseUrl 設定要指向這個位址才連得上。",
        string.Join(", ", app.Urls));

    await app.RunAsync();
}
else
{
    // Phase 1~4 原本的 CLI 模式:建好 DI 就跑一次 Workflow、印出結果、結束程式,
    // 完全不受 Phase 5 新增的 serve 模式影響。
    try
    {
        var workflowEngine = provider.GetRequiredService<IWorkflowEngine>();

        // Phase 4:AI_DEVPLATFORM_WORKFLOW=parallel 可以切換到平行 Coder 的 Pipeline
        // (workflows/parallel-pipeline.json),不設定則沿用原本序列 Pipeline 的選擇邏輯,
        // 兩者可以並存,互不影響(規格書 v3 第 19 節)。
        var workflowEnvOverride = Environment.GetEnvironmentVariable("AI_DEVPLATFORM_WORKFLOW");
        var workflowPath = workflowEnvOverride == "parallel" && File.Exists(parallelWorkflowPath)
            ? parallelWorkflowPath
            : appConfig.WorkflowPath is { Length: > 0 } && File.Exists(Path.Combine(repoRoot, appConfig.WorkflowPath))
                ? Path.Combine(repoRoot, appConfig.WorkflowPath)
                : defaultWorkflowPath;

        if (!File.Exists(workflowPath))
        {
            logger.LogWarning("找不到 Workflow DSL:{Path},略過 Orchestrator。", workflowPath);
        }
        else
        {
            var definition = await workflowEngine.LoadAsync(workflowPath);
            logger.LogInformation(
                "Loaded workflow '{WorkflowId}' with {StepCount} step(s) from {Path}",
                definition.WorkflowId, definition.Steps.Count, workflowPath);

            var workspace = new AI.Core.Workspace.Workspace(
                Name: "AI-DevPlatform",
                RootPath: repoRoot,
                Language: "C#",
                Framework: ".NET",
                GitBranch: "main",
                BuildProfile: null);

            var workflowId = await workflowEngine.StartAsync(definition, workspace);

            var orchestrator = provider.GetRequiredService<IAgentOrchestrator>();
            var success = await orchestrator.RunAsync(definition, workspace, workflowId);

            logger.LogInformation("Workflow {WorkflowId} 執行結果:{Result}", workflowId, success ? "成功" : "失敗/中止");
        }
    }
    finally
    {
        if (mcpInvoker is not null)
        {
            await mcpInvoker.DisposeAsync();
        }
    }

    logger.LogInformation("AI-DevPlatform Host 執行完畢。");
    await app.DisposeAsync();
}

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI-DevPlatform.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? startDir;
}
