using System.Collections.Concurrent;
using AI.Agents;

namespace AI.Host.Server;

/// <summary>
/// Stage B(見 README「迭代開發迴圈」章節):一個 Product Manager 對話 session,存在記憶體裡
/// (AI.Host 重啟就會遺失,跟 RunTracker/RunRegistry 的 MVP 取捨一致,見 ChatEndpoints.cs)。
/// 用 lock 而不是 ConcurrentBag/Queue,是因為需要「先讀完整快照、再各自附加」的一致性,
/// 這個 session 的使用量(單一使用者的一場對話)不需要更複雜的並行結構。
/// </summary>
public sealed class PlanningSession
{
    private readonly object _gate = new();
    private readonly List<PlanningTurn> _turns = new();
    private string? _finalSpec;

    public PlanningSession(string sessionId, string origin = "fresh")
    {
        SessionId = sessionId;
        Origin = origin;
    }

    public string SessionId { get; }

    /// <summary>
    /// Stage F(使用者自訂擴充):"fresh" 是一般「PM 規劃討論」模式從零開始的對話;"revise" 是
    /// 從「Product Manager(驗收)」模式的「修改規格」按鈕帶著既有 PRD+QA 結論開始的對話
    /// (見 ChatEndpoints.cs 的 /api/reports/{id}/revise)。前端(chatPanel.js 的
    /// appendSpecReady 處理)靠這個欄位判斷:定案產生新 PRD 之後,要不要顯示「🚀 開始開發」
    /// 按鈕——revise 來源的新 PRD 應該回「Project Manager」模式重新分派給 CoderA/B/C,
    /// 不是走 default/parallel pipeline,顯示同一顆按鈕會導向錯誤的 Workflow。
    /// </summary>
    public string Origin { get; }

    public void Append(string role, string content)
    {
        lock (_gate)
        {
            _turns.Add(new PlanningTurn(role, content));
        }
    }

    public IReadOnlyList<PlanningTurn> Snapshot()
    {
        lock (_gate)
        {
            return _turns.ToList();
        }
    }

    /// <summary>
    /// 使用者按「確認規格,產生 PRD」之後把定案的規格書存回這個 session,讓後續按「開始開發」時
    /// 不用再叫 LLM 重新生一次(也保證使用者看到的 PRD 內容,跟真正拿去餵給 Planner 的內容一致)。
    /// </summary>
    public void SetFinalSpec(string finalSpec)
    {
        lock (_gate)
        {
            _finalSpec = finalSpec;
        }
    }

    public string? GetFinalSpec()
    {
        lock (_gate)
        {
            return _finalSpec;
        }
    }
}

/// <summary>跟 RunRegistry 同樣的 MVP 模式:記憶體內 ConcurrentDictionary,不做持久化。</summary>
public sealed class PlanningSessionRegistry
{
    private readonly ConcurrentDictionary<string, PlanningSession> _sessions = new();

    public PlanningSession Create(string origin = "fresh")
    {
        var session = new PlanningSession(Guid.NewGuid().ToString("N"), origin);
        _sessions[session.SessionId] = session;
        return session;
    }

    public PlanningSession? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session : null;
}
