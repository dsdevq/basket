using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Walk;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Plan;

public static class PlanEndpoints
{
    /// <summary>Signed-in people only, like every route that is not named as a machine route.</summary>
    public static void MapPlanEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/plan", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await Load(db, clock, ct, await WalkOrderLoader.LoadAsync(db, ct))));

        // The same plan, summed per shop: it reads the plan above, so a price is the same number on both screens.
        api.MapGet("/plan/by-shop", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var walk = await WalkOrderLoader.LoadAsync(db, ct);
            return Results.Ok(ByShopBuilder.Build(await Load(db, clock, ct, walk), walk));
        });
    }

    private static async Task<PlanResponse> Load(AppDbContext db, TimeProvider clock, CancellationToken ct, WalkOrders walk) =>
        await Load(db, clock, await db.ListItems.AsNoTracking().Where(i => !i.IsChecked).OrderBy(i => i.AddedAt).ToListAsync(ct), ct, walk);

    /// <summary>
    /// The shop a line is bought at unless the person says otherwise: the shop it was picked from, else the one the plan names
    /// best for it. Null when neither does (free text, no fresh price, prices that cannot be compared). The plan is built for
    /// this line alone; a line's best shop does not depend on the others.
    /// </summary>
    internal static async Task<Store?> PlannedStoreAsync(AppDbContext db, TimeProvider clock, ListItem line, CancellationToken ct)
    {
        if (line.Store is { } picked)
        {
            return picked;
        }

        var plan = await Load(db, clock, [line], ct, null);
        return plan.Items.Single().BestStore is { } best && Stores.TryParse(best, out var store) ? store : null;
    }

    private static async Task<PlanResponse> Load(AppDbContext db, TimeProvider clock, IReadOnlyList<ListItem> lines, CancellationToken ct, WalkOrders? walk)
    {
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        var productIds = lines.Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var listingRows = await db.StoreListings.AsNoTracking().Where(l => productIds.Contains(l.ProductId)).ToListAsync(ct);
        var listingIds = listingRows.Select(l => l.Id).ToList();
        var latest = (await db.PriceObservations.AsNoTracking()
                .Where(o => listingIds.Contains(o.ListingId))
                .GroupBy(o => o.ListingId)
                .Select(g => g.OrderByDescending(o => o.ObservedOn).First())
                .ToListAsync(ct))
            .ToDictionary(o => o.ListingId);

        var paid = (await db.PaidPrices.AsNoTracking().Where(p => productIds.Contains(p.ProductId)).ToListAsync(ct))
            .GroupBy(p => (p.ProductId, p.Store))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PaidOn).First());

        return PlanBuilder.Build(lines, products, listingRows.ToLookup(l => l.ProductId), latest, today, paid, walk);
    }
}
