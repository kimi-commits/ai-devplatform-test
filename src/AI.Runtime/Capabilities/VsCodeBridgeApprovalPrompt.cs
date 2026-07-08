using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace AI.Runtime.Capabilities;

/// <summary>
/// <see cref="IApprovalPrompt"/> 的 VS Code Extension 版本(規格書 v3 第 16 節:
/// 「High 風險 Capability 的確認 UI」)。
///
/// AI.Host 是一個跑完就結束的 Console App,VS Code Extension 是另一個獨立的 process,
/// 兩者之間沒有既有的 IPC 管道,所以這裡選最簡單、不需要額外套件、也最容易除錯的方式:
/// 用檔案系統當作訊息交換的媒介(這個專案本身就大量採用「檔案即介面」,例如 .artifacts/ 的
/// Artifact Store),而不是另外起一個 HTTP/gRPC Server。
///
/// 協定:
/// 1. 本類別把待核准的請求寫成 <c>{requestId}.request.json</c>。
/// 2. VS Code Extension(見 extensions/vscode-extension/src/approvalBridge.ts)用
///    FileSystemWatcher 偵測到新的 *.request.json,跳出 showWarningMessage 的 Modal 對話框,
///    使用者按下「核准」或「拒絕」後,寫回 <c>{requestId}.response.json</c>。
/// 3. 本類別輪詢(Poll)直到 response 檔案出現、逾時、或被取消,讀完後清掉這兩個檔案。
///
/// 如果 VS Code Extension 沒有開著(沒人在看 .ai-devplatform/approvals/ 目錄),這裡會一路等到
/// 逾時,自動視為拒絕——安全的預設值(fail closed),不會讓 High 風險操作在沒人核准的情況下
/// 因為「等不到回應」而被誤放行。
/// </summary>
public sealed class VsCodeBridgeApprovalPrompt : IApprovalPrompt
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _approvalsDirectory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<VsCodeBridgeApprovalPrompt> _logger;

    public VsCodeBridgeApprovalPrompt(
        string approvalsDirectory,
        ILogger<VsCodeBridgeApprovalPrompt> logger,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        _approvalsDirectory = approvalsDirectory;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<bool> AskAsync(string capabilityName, string context, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_approvalsDirectory);

        var requestId = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(_approvalsDirectory, $"{requestId}.request.json");
        var responsePath = Path.Combine(_approvalsDirectory, $"{requestId}.response.json");

        var request = new ApprovalRequest(requestId, capabilityName, context, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);

        Console.WriteLine();
        Console.WriteLine("==================== 等待 VS Code 確認(High 風險操作) ====================");
        Console.WriteLine($"Capability : {capabilityName}");
        Console.WriteLine($"說明       : {context}");
        Console.WriteLine($"請求檔案   : {requestPath}");
        Console.WriteLine("請在 VS Code 開啟這個 workspace,AI-DOS Extension 會自動彈出核准對話框。");
        Console.WriteLine($"逾時時間:{_timeout.TotalMinutes} 分鐘(逾時視為拒絕)。");
        Console.WriteLine("============================================================================");

        _logger.LogInformation("已送出 VS Code 核准請求:{RequestId}({Capability}),等待回應中...", requestId, capabilityName);

        try
        {
            var deadline = DateTimeOffset.UtcNow + _timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(responsePath))
                {
                    var approved = await ReadAndCleanupAsync(requestPath, responsePath, requestId, cancellationToken);
                    return approved;
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }

            _logger.LogWarning("VS Code 核准請求 {RequestId}({Capability})逾時,視為拒絕。", requestId, capabilityName);
            TryDelete(requestPath);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
            throw;
        }
    }

    private async Task<bool> ReadAndCleanupAsync(string requestPath, string responsePath, string requestId, CancellationToken cancellationToken)
    {
        ApprovalResponse? response = null;
        try
        {
            var raw = await File.ReadAllTextAsync(responsePath, cancellationToken);
            response = JsonSerializer.Deserialize<ApprovalResponse>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 VS Code 核准回應 {RequestId} 失敗,視為拒絕。", requestId);
        }

        TryDelete(requestPath);
        TryDelete(responsePath);

        return response?.Approved ?? false;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理核准請求暫存檔失敗:{Path}", path);
        }
    }

    private sealed record ApprovalRequest(string RequestId, string CapabilityName, string Context, DateTimeOffset CreatedAt);

    private sealed record ApprovalResponse(string RequestId, bool Approved, DateTimeOffset? DecidedAt);
}
