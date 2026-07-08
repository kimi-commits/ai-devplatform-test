using System.Collections.Concurrent;
using AI.Core.Memory;

namespace AI.Memory.Store;

/// <summary>
/// Agent 私有、跨次執行的長期知識(規格書 v3 第 13 節)。每個 Agent 一份獨立實例,不跨 Agent 共享。
/// MVP 用記憶體內字典;Phase 2 之後可換成 SQLite-backed 實作而不影響 IMemory 介面。
/// </summary>
public sealed class InMemoryAgentMemory : IMemory
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public string OwnerAgentName { get; }

    public InMemoryAgentMemory(string ownerAgentName)
    {
        OwnerAgentName = ownerAgentName;
    }

    public Task RememberAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> RecallAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<string>> QueryAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var matches = _store.Values
            .Where(v => v.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(matches);
    }
}
