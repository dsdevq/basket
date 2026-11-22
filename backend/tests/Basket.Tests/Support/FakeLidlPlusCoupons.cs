using Basket.Api.LidlPlus;

namespace Basket.Tests.Support;

/// <summary>Lidl Plus's coupons service as a script: the coupons it lists, which activations it refuses, and every call made to it.</summary>
public sealed class FakeLidlPlusCoupons : ILidlPlusCoupons
{
    private readonly object _gate = new();
    private readonly List<LidlCouponInfo> _coupons = [];

    /// <summary>The ids switched on, in order.</summary>
    public List<string> Activations { get; } = [];

    public int ListCalls { get; private set; }

    /// <summary>When true the list is unavailable, as if Lidl moved the service.</summary>
    public bool ListFails { get; set; }

    /// <summary>Ids Lidl refuses to activate.</summary>
    public HashSet<string> Refuses { get; } = [];

    /// <summary>When set, activation stops answering from this id on.</summary>
    public string? GoesAwayAt { get; set; }

    public FakeLidlPlusCoupons Add(LidlCouponInfo coupon)
    {
        lock (_gate)
        {
            _coupons.Add(coupon);
        }

        return this;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _coupons.Clear();
            Activations.Clear();
            Refuses.Clear();
            ListCalls = 0;
            ListFails = false;
            GoesAwayAt = null;
        }
    }

    public static LidlCouponInfo Coupon(string id, string title, string? discount = null, DateOnly? from = null, DateOnly? until = null, bool activated = false) =>
        new(id, title, discount, null, from, until, activated);

    public Task<LidlCouponList> ListAsync(string accessToken, CancellationToken ct)
    {
        lock (_gate)
        {
            ListCalls++;
            return Task.FromResult(ListFails ? LidlCouponList.Unavailable : new LidlCouponList(LidlCouponsOutcome.Ok, [.. _coupons]));
        }
    }

    public Task<LidlActivationOutcome> ActivateAsync(string accessToken, string couponId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (couponId == GoesAwayAt)
            {
                return Task.FromResult(LidlActivationOutcome.Unavailable);
            }

            if (Refuses.Contains(couponId))
            {
                return Task.FromResult(LidlActivationOutcome.Refused);
            }

            Activations.Add(couponId);
            var index = _coupons.FindIndex(c => c.Id == couponId);
            if (index >= 0)
            {
                _coupons[index] = _coupons[index] with { Activated = true };
            }

            return Task.FromResult(LidlActivationOutcome.Activated);
        }
    }
}
