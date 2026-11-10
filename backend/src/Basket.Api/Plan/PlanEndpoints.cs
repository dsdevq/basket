using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Plan;

public static class PlanEndpoints
{
    /// <summary>Signed-in people only, like every route that is not named as a machine route.</summary>
    public static void MapPlanEndpoints(this IEndpointRouteBuilder api) =>
        api.MapGet("/plan", GetPlan);

    private static async Task<IResult> GetPlan(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        var lines = await db.ListItems.AsNoTracking().Where(i => !i.IsChecked).OrderBy(i => i.AddedAt).ToListAsync(ct);
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

        return Results.Ok(PlanBuilder.Build(lines, products, listingRows.ToLookup(l => l.ProductId), latest, today, paid));
    }
}
