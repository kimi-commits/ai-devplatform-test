using System.Text.Json;

namespace AI.Host.Server;

/// <summary>列給 Chat 面板下拉選單看的摘要,不含完整內容(避免下拉選單一次載入所有 PRD 全文)。</summary>
public sealed record PrdSummary(string Id, string Title, DateTimeOffset CreatedAt);

/// <summary>
/// Stage C(使用者自訂擴充,見 README「迭代開發迴圈」章節):PRD 落地成檔案
/// (.ai-devplatform/prds/{id}.json),讓 Chat 面板可以列出「所有 PRD 檔案」給使用者選,對應
/// 使用者要求的「Project Manager 模式下,輸入匡變成 PRD 檔案下拉選單」。用檔案系統當儲存媒介,
/// 跟這個專案其他地方(.ai-devplatform/approvals/、.ai-suggestions/)的「檔案即介面」風格一致,
/// 不需要額外資料庫、不需要額外套件。
/// </summary>
public sealed class PrdStore
{
    private readonly string _directory;

    public PrdStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>使用者按「確認規格,產生 PRD」時呼叫(見 ChatEndpoints.cs 的 /finalize 端點),回傳新產生的 PRD id。</summary>
    public async Task<string> SaveAsync(string content, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var record = new PrdRecord(id, DeriveTitle(content), content, DateTimeOffset.UtcNow);
        var path = Path.Combine(_directory, $"{id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record), cancellationToken);
        return id;
    }

    /// <summary>依建立時間新到舊排序,壞掉/讀不到的檔案直接忽略,不讓整個列表 API 因為單一檔案損毀而掛掉。</summary>
    public IReadOnlyList<PrdSummary> List()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<PrdSummary>();
        }

        var results = new List<PrdSummary>();
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            try
            {
                var raw = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<PrdRecord>(raw);
                if (record is not null)
                {
                    results.Add(new PrdSummary(record.Id, record.Title, record.CreatedAt));
                }
            }
            catch
            {
                // 忽略,見上方註解。
            }
        }

        return results.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public string? GetContent(string id)
    {
        var path = Path.Combine(_directory, $"{id}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PrdRecord>(raw)?.Content;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拿內容的第一個非空行當標題(通常是 PM 產出時下的標題/第一條需求),截斷避免下拉選單一行太長。</summary>
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

    private sealed record PrdRecord(string Id, string Title, string Content, DateTimeOffset CreatedAt);
}
