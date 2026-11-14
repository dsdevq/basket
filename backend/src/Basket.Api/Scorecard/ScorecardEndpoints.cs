using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Scorecard;

public static class ScorecardEndpoints
{
    /// <summary>Signed-in people only. A view the person opens: nothing is scheduled or sent.</summary>
    public static void MapScorecardEndpoints(this IEndpointRouteBuilder api) =>
        api.MapGet("/scorecard", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await Load(db, clock, ct)));

    private static async Task<ScorecardResponse> Load(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        // Trips are cut at Dublin midnight of the window's first day, the day prices are filed on.
        var from = ScorecardBuilder.WindowStart(today);
        var dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");
        var cutoff = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), dublin), TimeSpan.Zero);
        var rows = await db.Trips.AsNoTracking().Where(t => t.ClosedAt >= cutoff).ToListAsync(ct);
        var tripIds = rows.Select(t => t.Id).ToList();
        var items = (await db.TripItems.AsNoTracking().Where(i => tripIds.Contains(i.TripId)).Select(i => new { i.TripId, i.ProductId }).ToListAsync(ct))
            .ToLookup(i => i.TripId);
        var trips = rows
            .Select(t => new ScorecardTrip(
                PriceRecorder.DublinDay(t.ClosedAt),
                items[t.Id].Count(),
                items[t.Id].Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).ToList()))
            .ToList();

        var productIds = trips.SelectMany(t => t.ProductIds).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var listingRows = await db.StoreListings.AsNoTracking().Where(l => productIds.Contains(l.ProductId)).ToListAsync(ct);
        var listingIds = listingRows.Select(l => l.Id).ToList();
        var readings = (await db.PriceObservations.AsNoTracking().Where(o => listingIds.Contains(o.ListingId)).ToListAsync(ct))
            .ToLookup(o => o.ListingId);

        return ScorecardBuilder.Build(trips, products, listingRows.ToLookup(l => l.ProductId), readings, today);
    }
}
