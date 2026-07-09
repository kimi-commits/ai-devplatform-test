using System.ClientModel;
using System.Linq;
using AI.Core.Models;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;

namespace AI.Models.Providers;

/// <summary>
/// 任何 OpenAI Compatible API 的通用實作(NVIDIA NIM / OpenAI / OpenRouter / Ollama 皆適用,
/// 只要 Base URL 與 API Key 不同,規格書 v1 第 1 節)。
///
/// 底層透過 Microsoft Agent Framework(Microsoft.Agents.AI + Microsoft.Agents.AI.OpenAI)呼叫,
/// 用法已在 samples/Phase0-MafDemo 用 NVIDIA NIM 實際跑通並驗證過(見該專案 Program.cs)。
/// 這裡刻意維持 IModelProvider 這層中立、不含 MAF 型別的介面,理由是規格書 v3 第 2 節強調
/// MAF 只服務 AgentKind.Llm,Tool/Script/Workflow Agent 不應被迫依賴 MAF 型別。
/// </summary>
public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly OpenAIClient _client;

    public string ProviderName { get; }

    /// <summary>
    /// networkTimeout(使用者自訂擴充,見 AppConfig.ModelProviderConfig.TimeoutSeconds 類別
    /// 註解):`OpenAIClientOptions` 繼承自 `System.ClientModel.Primitives.
    /// ClientPipelineOptions`,`NetworkTimeout` 預設只有 100 秒——49B 大模型單輪生成常常超過
    /// 這個時間,逾時後 SDK 自己的 `ClientRetryPolicy` 還會重試 3 次(每次都套用同一個 100 秒
    /// 逾時),4 次全部逾時才把 `"Retry failed after 4 tries."` 往上拋成整個 Step 失敗。這裡
    /// 開放呼叫端指定逾時時間,預設值 300 秒是保底(沒有人特地傳參數進來的情況)。
    /// </summary>
    public OpenAiCompatibleProvider(string providerName, string apiKey, string baseUrl, TimeSpan? networkTimeout = null)
    {
        ProviderName = providerName;
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            NetworkTimeout = networkTimeout ?? TimeSpan.FromSeconds(300)
        };
        _client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var userMessage = string.Join(
            "\n\n",
            request.Messages.Where(m => m.Role != "system").Select(m => m.Content));

        // 一個 LLM Agent(規格書 v3 第 2 節 AgentKind.Llm),透過 Microsoft Agent Framework 執行。
        // Phase 1 尚未把 Native/MCP Tool 接進這條路徑(function calling),先只做單輪文字生成;
        // Tool Calling 留給 Phase 2 MCP Tool 串接後再補上。
        AIAgent agent = _client.GetChatClient(request.Model).AsAIAgent(instructions: systemMessage);

        var response = await agent.RunAsync(userMessage, cancellationToken: cancellationToken);

        return new ModelResponse(
            Content: response.ToString() ?? string.Empty,
            PromptTokens: 0,
            CompletionTokens: 0);
    }
}
