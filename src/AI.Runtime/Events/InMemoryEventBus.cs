using System.Collections.Concurrent;
using AI.Core.Events;
using Microsoft.Extensions.Logging;

namespace AI.Runtime.Events;

/// <summary>
/// MVP 階段的記憶體內 Pub/Sub(規格書 v3 第 7 節)。介面(IEventBus)設計成可替換,
/// 未來要跨行程時可換成分散式後端而不影響 Agent 程式碼。
/// 同一 WorkflowId 的事件依序透過 per-workflow 佇列處理,保證順序。
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<AgentEvent, CancellationToken, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workflowLocks = new();
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        _logger = logger;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : AgentEvent
    {
        _logger.LogInformation("Event published: {EventType} workflow={WorkflowId} step={StepId}",
            @event.EventType, @event.WorkflowId, @event.StepId);

        var gate = _workflowLocks.GetOrAdd(@event.WorkflowId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            {
                return;
            }

            foreach (var handler in handlers.ToArray())
            {
                await handler(@event, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : AgentEvent
    {
        var wrapped = new Func<AgentEvent, CancellationToken, Task>((e, ct) => handler((TEvent)e, ct));
        var list = _handlers.GetOrAdd(typeof(TEvent), _ => new List<Func<AgentEvent, CancellationToken, Task>>());
        lock (list)
        {
            list.Add(wrapped);
        }

        return new Subscription(() =>
        {
            lock (list)
            {
                list.Remove(wrapped);
            }
        });
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
