using Basket.Api.Data;
using Basket.Api.Prices;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.LidlPlus;

public enum CouponSyncOutcome
{
    /// <summary>The stored coupons are what Lidl lists now.</summary>
    Done,

    NotConnected,

    /// <summary>Lidl refused the saved token, or it cannot be read; the owner has to paste a new one.</summary>
    NeedsNewToken,

    /// <summary>Lidl could not be reached or its coupons service did not answer as expected. The stored coupons were left as they were.</summary>
    Unavailable,
}

/// <param name="Activated">Coupons switched on by this call.</param>
/// <param name="Refused">Coupons Lidl would not switch on (gone, used up); the rest were still tried.</param>
/// <param name="Unknown">A single activation named a coupon that is not listed.</param>
public sealed record CouponActivationResult(CouponSyncOutcome Outcome, int Activated, int Refused, bool Unknown = false);

/// <summary>
/// The household's Lidl Plus coupons: read Lidl's list into the <see cref="LidlCoupon"/> snapshot and switch coupons on. In Ireland a
/// coupon has to be activated before the till, and only an activated, in-date one changes a price. Every call asks Lidl for the live
/// list first (an activation is only attempted on what Lidl says is still inactive), and a Lidl that cannot answer changes nothing.
/// </summary>
public sealed class LidlCouponService(
    AppDbContext db,
    LidlPlusConnectionService connection,
    ILidlPlusCoupons coupons,
    TimeProvider clock)
{
    // One household, one app instance: two reads or activations at once would race on the snapshot and on Lidl.
    private static readonly SemaphoreSlim Working = new(1, 1);

    /// <summary>The stored coupons, soonest to end first.</summary>
    public async Task<IReadOnlyList<LidlCoupon>> StoredAsync(CancellationToken ct) =>
        (await db.LidlCoupons.AsNoTracking().ToListAsync(ct))
            .OrderBy(c => c.ValidUntil ?? DateOnly.MaxValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<CouponSyncOutcome> SyncAsync(CancellationToken ct)
    {
        await Working.WaitAsync(ct);
        try
        {
            return (await OpenAsync(ct)).Outcome;
        }
        finally
        {
            Working.Release();
        }
    }

    /// <summary>Switches on one coupon, or with a null id every coupon that is in date and still off.</summary>
    public async Task<CouponActivationResult> ActivateAsync(string? id, CancellationToken ct)
    {
        await Working.WaitAsync(ct);
        try
        {
            var (outcome, access, live) = await OpenAsync(ct);
            if (outcome != CouponSyncOutcome.Done)
            {
                return new CouponActivationResult(outcome, 0, 0);
            }

            var today = PriceRecorder.DublinDay(clock.GetUtcNow());
            var targets = live.Where(c => !c.Activated && InDate(c.ValidFrom, c.ValidUntil, today) && (id is null || c.Id == id)).ToList();
            if (id is not null && live.All(c => c.Id != id))
            {
                return new CouponActivationResult(CouponSyncOutcome.Done, 0, 0, Unknown: true);
            }

            var activated = 0;
            var refused = 0;
            foreach (var coupon in targets)
            {
                switch (await coupons.ActivateAsync(access!, coupon.Id, ct))
                {
                    case LidlActivationOutcome.Activated:
                        activated++;
                        await db.LidlCoupons.Where(c => c.Id == coupon.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Activated, true), ct);
                        break;
                    case LidlActivationOutcome.Refused:
                        refused++;
                        break;
                    default:
                        // Lidl as a whole stopped answering: what was switched on stays on, the rest waits for another try.
                        return new CouponActivationResult(CouponSyncOutcome.Unavailable, activated, refused);
                }
            }

            return new CouponActivationResult(CouponSyncOutcome.Done, activated, refused);
        }
        finally
        {
            Working.Release();
        }
    }

    /// <summary>The coupons that change a price today: activated and in date.</summary>
    public static IReadOnlyList<LidlCoupon> ActiveOn(IEnumerable<LidlCoupon> stored, DateOnly today) =>
        stored.Where(c => c.Activated && InDate(c.ValidFrom, c.ValidUntil, today)).ToList();

    public static bool InDate(DateOnly? from, DateOnly? until, DateOnly today) =>
        (from is null || from <= today) && (until is null || until >= today);

    /// <summary>Asks Lidl for the live list and, when it is a list, makes the snapshot match it.</summary>
    private async Task<(CouponSyncOutcome Outcome, string? Access, IReadOnlyList<LidlCouponInfo> Live)> OpenAsync(CancellationToken ct)
    {
        var access = await connection.AccessAsync(ct);
        switch (access.State)
        {
            case LidlPlusState.NotConnected:
                return (CouponSyncOutcome.NotConnected, null, []);
            case LidlPlusState.NeedsNewToken or LidlPlusState.Unreadable:
                return (CouponSyncOutcome.NeedsNewToken, null, []);
        }

        if (access.AccessToken is not { } token)
        {
            return (CouponSyncOutcome.Unavailable, null, []);
        }

        var list = await coupons.ListAsync(token, ct);
        if (list.Outcome != LidlCouponsOutcome.Ok)
        {
            return (CouponSyncOutcome.Unavailable, null, []);
        }

        await ReplaceAsync(list.Coupons, ct);
        return (CouponSyncOutcome.Done, token, list.Coupons);
    }

    private async Task ReplaceAsync(IReadOnlyList<LidlCouponInfo> live, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.LidlCoupons.ToDictionaryAsync(c => c.Id, ct);
        foreach (var gone in rows.Values.Where(r => live.All(c => c.Id != r.Id)))
        {
            db.LidlCoupons.Remove(gone);
        }

        foreach (var coupon in live)
        {
            if (!rows.TryGetValue(coupon.Id, out var row))
            {
                row = new LidlCoupon { Id = coupon.Id };
                db.LidlCoupons.Add(row);
            }

            row.Title = coupon.Title;
            row.Discount = coupon.Discount;
            row.Description = coupon.Description;
            row.ValidFrom = coupon.ValidFrom;
            row.ValidUntil = coupon.ValidUntil;
            row.Activated = coupon.Activated;
            row.SyncedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The nightly coupons read, so a price can count a coupon the owner activated. Nothing to do until Lidl Plus is connected; it never activates anything.</summary>
public sealed class LidlCouponsJob(LidlCouponService service, ILogger<LidlCouponsJob> log)
{
    public const string Id = "lidl-coupons";

    public async Task RunAsync(CancellationToken ct)
    {
        var outcome = await service.SyncAsync(ct);
        if (outcome is CouponSyncOutcome.NeedsNewToken or CouponSyncOutcome.Unavailable)
        {
            log.LogWarning("The nightly Lidl Plus coupons read stopped: {Outcome}", outcome);
        }
    }

    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<LidlCouponsJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "30 5 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin") });
}
