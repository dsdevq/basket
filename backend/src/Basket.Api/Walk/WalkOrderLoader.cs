using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Walk;

/// <summary>
/// Reads the ticks <see cref="LearnedSectionOrder"/> learns from, out of the trips already kept (no table of its own). Only trips
/// closed on the list count: a receipt has no tick times. A tick counts when its line was a product (free text has no category) bought
/// at a named shop whose page for that product carries a category; its section is that category's, read the way the seed reads it.
/// The category is the listing's today, not as it was on the day.
/// </summary>
public static class WalkOrderLoader
{
    /// <summary>The latest closed trips read; more than every shop's <see cref="LearnedSectionOrder.MaxTrips"/> needs, so a shop used less often still has its own.</summary>
    public const int RecentTrips = 60;

    public static async Task<WalkOrders> LoadAsync(AppDbContext db, CancellationToken ct) => WalkOrders.From(await TicksAsync(db, ct));

    private static async Task<List<WalkTick>> TicksAsync(AppDbContext db, CancellationToken ct)
    {
        var tripIds = await db.Trips.AsNoTracking().Closed()
            .Where(t => t.Source == null)
            .OrderByDescending(t => t.ClosedAt)
            .Select(t => t.Id)
            .Take(RecentTrips)
            .ToListAsync(ct);
        var items = await db.TripItems.AsNoTracking()
            .Where(i => tripIds.Contains(i.TripId) && i.CheckedAt != null && i.ProductId != null && i.BoughtStore != null)
            .Select(i => new { i.TripId, ProductId = i.ProductId!.Value, Store = i.BoughtStore!.Value, At = i.CheckedAt!.Value })
            .ToListAsync(ct);
        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var categories = (await db.StoreListings.AsNoTracking()
                .Where(l => productIds.Contains(l.ProductId) && l.Category != null)
                .Select(l => new { l.ProductId, l.Store, l.Category })
                .ToListAsync(ct))
            .ToDictionary(l => (l.ProductId, l.Store), l => l.Category);

        return items
            .Where(i => categories.ContainsKey((i.ProductId, i.Store)))
            .Select(i => new WalkTick(i.TripId, i.Store, SeededSectionOrder.SectionOf(categories[(i.ProductId, i.Store)]), i.At))
            .ToList();
    }
}
