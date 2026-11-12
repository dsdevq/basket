using Basket.Api.LidlPlus;

namespace Basket.Tests.Support;

/// <summary>Lidl's account service as a script: each ask is answered by the next queued result, else by <see cref="Default"/>.</summary>
public sealed class FakeLidlPlusAuth : ILidlPlusAuth
{
    private readonly Queue<LidlAuthResult> _next = new();
    private readonly object _gate = new();

    /// <summary>Every refresh token Lidl was asked to exchange, in order.</summary>
    public List<string> Asked { get; } = [];

    public LidlAuthResult Default { get; set; } = Accepts();

    public static LidlAuthResult Accepts(string access = "access-token", string? rotated = null) =>
        new(LidlAuthOutcome.Accepted, access, rotated, 1800);

    public FakeLidlPlusAuth Then(LidlAuthResult result)
    {
        lock (_gate)
        {
            _next.Enqueue(result);
        }

        return this;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _next.Clear();
            Asked.Clear();
            Default = Accepts();
        }
    }

    public Task<LidlAuthResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        lock (_gate)
        {
            Asked.Add(refreshToken);
            return Task.FromResult(_next.Count > 0 ? _next.Dequeue() : Default);
        }
    }
}
