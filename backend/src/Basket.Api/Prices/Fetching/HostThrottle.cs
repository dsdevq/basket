using Microsoft.Extensions.Options;

namespace Basket.Api.Prices.Fetching;

/// <summary>Spaces out requests to one host: each caller gets the next free slot, a fixed interval after the last.</summary>
public sealed class HostThrottle(IOptions<PricesOptions> options, TimeProvider clock)
{
    private readonly Dictionary<string, DateTimeOffset> _nextSlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public async Task WaitAsync(string host, CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var slot = _nextSlot.TryGetValue(host, out var next) && next > now ? next : now;
            _nextSlot[host] = slot + TimeSpan.FromSeconds(options.Value.MinRequestIntervalSeconds);
            delay = slot - now;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, clock, ct);
        }
    }
}
