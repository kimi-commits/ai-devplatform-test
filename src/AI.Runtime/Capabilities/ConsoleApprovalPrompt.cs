using AI.Core.Capabilities;

namespace AI.Runtime.Capabilities;

/// <summary>
/// <see cref="IApprovalPrompt"/> 最小可行版本:在 Console 印出確認提示,
/// 需要使用者輸入 y/yes 才會放行,其他任何輸入都視為拒絕。已在 Phase 3 實測驗證過
/// (核准/拒絕兩條路徑都會正確擋下或放行對應的 Tool 呼叫)。
/// </summary>
public sealed class ConsoleApprovalPrompt : IApprovalPrompt
{
    public Task<bool> AskAsync(string capabilityName, string context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("==================== 需要人工核准(High 風險操作) ====================");
        Console.WriteLine($"Capability : {capabilityName}");
        Console.WriteLine($"說明       : {context}");
        Console.WriteLine("======================================================================");
        Console.Write("是否核准執行?(y/N):");

        var input = Console.ReadLine();
        var approved = input is not null &&
            (input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(approved);
    }
}
