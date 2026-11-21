using Microsoft.Extensions.Caching.Memory;

namespace CareConnect.Ai;

// Short-lived, bounded, per-customer cache. The key includes a hash of all current source content.
public sealed class CareSummaryCache : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<CareSummaryResult> GetAsync(string key, Func<Task<CareSummaryResult>> generate, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out CareSummaryResult? found)) return found!;
        var gate = gates[(key.GetHashCode() & int.MaxValue) % gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out found)) return found!;
            var result = await generate();
            if (result.Status == "ready") cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
            return result;
        }
        finally { gate.Release(); }
    }
    public void Dispose() { cache.Dispose(); foreach (var gate in gates) gate.Dispose(); }
}
