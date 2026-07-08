// Phase 0 — 驗證 Microsoft Agent Framework 在 .NET 10 下的穩定度與文件完整度
// (規格書 v3 第 19 節 Roadmap Phase 0)。
//
// 這個 Demo 刻意獨立於 AI.Host 之外:先確認「一個 LLM Agent + 一個 Model + 一個 Tool」
// 能透過 Microsoft Agent Framework 打通,再回頭把驗證結果套進 AI.Agents 專案裡
// PlannerAgent/CoderAgent 等的 ExecuteAsync TODO。
//
// 供應商:NVIDIA NIM(OpenAI-Compatible API)。
// 參考:https://build.nvidia.com、https://docs.api.nvidia.com/nim/reference/llm-apis
//
// 執行前設定環境變數(不要把金鑰寫進程式碼或提交進版本控制):
//   export NIM_API_KEY="nvapi-..."          # 到 https://build.nvidia.com 申請
//   export NIM_BASE_URL="https://integrate.api.nvidia.com/v1"   # 可省略,已是預設值
//   export NIM_MODEL_NAME="nvidia/llama-3.3-nemotron-super-49b-v1.5"  # 可省略,已是預設值
//                                            # 目前可用模型清單請查 https://build.nvidia.com

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

var apiKey = Environment.GetEnvironmentVariable("NIM_API_KEY")
    ?? throw new InvalidOperationException(
        "NIM_API_KEY 未設定。請先到 https://build.nvidia.com 申請 API Key," +
        "再執行:export NIM_API_KEY=\"nvapi-...\"");

var baseUrl = Environment.GetEnvironmentVariable("NIM_BASE_URL") ?? "https://integrate.api.nvidia.com/v1";
var model = Environment.GetEnvironmentVariable("NIM_MODEL_NAME") ?? "nvidia/llama-3.3-nemotron-super-49b-v1.5";

Console.WriteLine("=== Phase 0: Microsoft Agent Framework + NVIDIA NIM 驗證 ===");
Console.WriteLine($"Base URL : {baseUrl}");
Console.WriteLine($"Model    : {model}");
Console.WriteLine();

// 一個 OpenAI-Compatible 供應商,只要把 Endpoint 換成 NVIDIA NIM 的 base URL 就能沿用同一套
// Microsoft Agent Framework 整合方式(規格書 v1 第 1 節「可接任何 OpenAI Compatible API」)。
var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

// 一個 Tool:示範 Tool Calling 是否能透過 Microsoft Agent Framework 正常運作
// (對應 AI.Core.Tools.ITool 的概念,這裡先用最小可行的本地函式代替真正的 File/Git Tool)。
[Description("查詢指定 Workspace 目前的 Git 分支名稱。")]
static string GetCurrentBranch([Description("Workspace 名稱")] string workspaceName)
    => $"Workspace '{workspaceName}' 目前在 main 分支(Phase 0 示範用假資料,尚未串接真正的 Git Tool)。";

// 一個 LLM Agent:對應規格書 v3 第 2 節的 AgentKind.Llm,實際執行後端就是 Microsoft Agent Framework。
AIAgent agent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        instructions: "你是 AI Development Platform 的 Planner Agent 驗證用助理,回覆請簡短。",
        name: "Phase0PlannerDemo",
        tools: [AIFunctionFactory.Create(GetCurrentBranch)]);

var response = await agent.RunAsync(
    "請用一句話跟我打招呼,並呼叫工具查詢 workspace 'CyberPoker' 目前的分支。");

Console.WriteLine("--- Agent 回覆 ---");
Console.WriteLine(response);
