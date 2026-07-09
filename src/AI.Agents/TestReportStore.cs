using System.Text.Json;

namespace AI.Agents;

/// <summary>列給 Chat 面板「Product Manager(驗收)」模式下拉選單看的摘要。</summary>
public sealed record TestReportSummary(string Id, string Title, DateTimeOffset CreatedAt, bool Passed);

/// <summary>單一測試報告的完整內容,給 /api/reports/{id}/revise、/api/reports/{id}/accept 用。</summary>
public sealed record TestReportDetail(string Id, string Title, string PrdContent, string QaSummary, bool Passed, DateTimeOffset CreatedAt);

/// <summary>
/// Stage E(使用者自訂擴充,見 README「迭代開發迴圈」章節):QA 判定完成後,把「這次跑的是哪份
/// PRD、QA 判定結果」落地成一份測試報告檔案(.ai-devplatform/test-reports/{id}.json),
/// 跟 AI.Host/Server/PrdStore.cs 同一種「檔案即介面」風格(不需要資料庫)。
///
/// 放在 AI.Agents 專案而不是 AI.Host.Server(PrdStore 所在位置),是因為這個類別同時要給
/// TestReportAgent(AI.Agents 專案,Workflow Step 結束時呼叫)跟 ChatEndpoints.cs
/// (AI.Host 專案,/api/reports* 端點)兩邊用——AI.Host 本來就參照 AI.Agents(反過來 AI.Agents
/// 參照 AI.Host 會變成循環參照,編譯不過),所以共用的類別要放在 AI.Agents 這邊,
/// 跟 ProductManagerAgent/PlanningTurn 已經確立的慣例一致(那兩個也是「AI.Host 直接拿來用的
/// AI.Agents 類別」)。
///
/// 標題刻意跟 PrdStore.DeriveTitle 用同一套推導邏輯(取內容第一個非空行),讓測試報告的標題
/// 天生就會跟它對應的 PRD 標題對得上,不需要另外維護一個 PrdId 外鍵欄位去追蹤兩者的關聯
/// (使用者要求「測試報告命名需對應 PRD」,這是最簡單、不會跟原始內容脫鉤的做法)。
/// </summary>
public sealed class TestReportStore
{
    private readonly string _directory;

    public TestReportStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>QA 這一輪判定完成後呼叫(見 TestReportAgent),回傳新報告的 (Id, Title)。</summary>
    public async Task<(string Id, string Title)> SaveAsync(
        string prdContent, string qaSummary, bool passed, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var title = $"{DeriveTitle(prdContent)} - 測試報告";
        var record = new TestReportRecord(id, title, prdContent, qaSummary, passed, DateTimeOffset.UtcNow);
        var path = Path.Combine(_directory, $"{id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record), cancellationToken);
        return (id, title);
    }

    /// <summary>依建立時間新到舊排序,壞掉/讀不到的檔案直接忽略(跟 PrdStore.List 同樣的容錯邏輯)。</summary>
    public IReadOnlyList<TestReportSummary> List()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<TestReportSummary>();
        }

        var results = new List<TestReportSummary>();
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            try
            {
                var raw = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<TestReportRecord>(raw);
                if (record is not null)
                {
                    results.Add(new TestReportSummary(record.Id, record.Title, record.CreatedAt, record.Passed));
                }
            }
            catch
            {
                // 忽略,見上方註解。
            }
        }

        return results.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public TestReportDetail? GetDetail(string id)
    {
        var path = Path.Combine(_directory, $"{id}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize<TestReportRecord>(raw);
            return record is null
                ? null
                : new TestReportDetail(record.Id, record.Title, record.PrdContent, record.QaSummary, record.Passed, record.CreatedAt);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>跟 PrdStore.DeriveTitle 完全相同的推導邏輯,刻意重複而不是抽共用方法——見類別註解
    /// 的專案相依方向說明,PrdStore 在 AI.Host.Server,這裡不能反向參照它。</summary>
    private static string DeriveTitle(string content)
    {
        var firstLine = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimStart('#', '*', ' '))
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "(無標題 PRD)";
        }

        return firstLine.Length > 50 ? firstLine[..50] + "…" : firstLine;
    }

    private sealed record TestReportRecord(string Id, string Title, string PrdContent, string QaSummary, bool Passed, DateTimeOffset CreatedAt);
}
