using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Promotions;

public static class PromotionEndpoints
{
    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    /// <summary>Signed-in people only, like every route that is not named as a machine route.</summary>
    public static void MapPromotionEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/promotions", GetPromotions);
        api.MapGet("/promotions/usual", GetUsual);
    }

    /// <summary>Offers on the products the household usually buys, from the whole catalog (see <see cref="UsualOffers"/>).</summary>
    private static async Task<IResult> GetUsual(AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        Results.Ok(await UsualOffers.BuildAsync(db, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Dublin).DateTime), ct));

    private static async Task<IResult> GetPromotions(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Dublin).DateTime);
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var listings = (await db.StoreListings.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.ProductId);
        var latest = (await db.PriceObservations.AsNoTracking()
                .GroupBy(o => o.ListingId)
                .Select(g => g.OrderByDescending(o => o.ObservedOn).First())
                .ToListAsync(ct))
            .ToDictionary(o => o.ListingId);

        var start = PriceHistory.WindowStart(today);
        var history = (await db.PriceObservations.AsNoTracking()
                .Where(o => o.ObservedOn >= start && o.ObservedOn <= today)
                .ToListAsync(ct))
            .ToLookup(o => o.ListingId);

        return Results.Ok(PromotionBuilder.Build(products, listings, latest, today, history, await CouponPricing.ActiveAsync(db, today, ct)));
    }
}
