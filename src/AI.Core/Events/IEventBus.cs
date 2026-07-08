namespace AI.Core.Events;

/// <summary>
/// MVP 階段用記憶體內 Pub/Sub 即可,介面設計成可替換,未來要跨行程時
/// 可換成分散式後端(Redis/NATS)而不影響 Agent 程式碼(規格書 v3 第 7 節)。
/// 需保證同一 WorkflowId 內的事件處理順序。
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : AgentEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : AgentEvent;
}
