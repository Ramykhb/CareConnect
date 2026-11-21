using Microsoft.Extensions.Caching.Memory;

namespace CareConnect.Ai;

// Limits paid provider calls on the server, including calls made directly to SignalR.
public sealed class AssistantThrottle(TimeProvider clock) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly object gate = new();
    public void Check(Guid userId)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var entry = cache.Get<Queue<DateTimeOffset>>(userId) ?? new();
            while (entry.TryPeek(out var first) && first <= now.AddMinutes(-1)) entry.Dequeue();
            if (entry.Count >= 8) throw new AssistantException("Please wait a minute before sending more messages.");
            entry.Enqueue(now);
            cache.Set(userId, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        }
    }
    public void Dispose() => cache.Dispose();
}
