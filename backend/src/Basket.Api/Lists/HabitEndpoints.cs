using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Lists;

/// <summary>One Shelf product and how often closed trips bought it. <see cref="Usual"/> is the "usually bought" set.</summary>
public sealed record HabitResponse(Guid ProductId, string Name, int BoughtIn, DateTimeOffset LastBoughtAt, bool Usual);

/// <summary>
/// What the household buys most and usually, worked out from closed trips only (the ticked lines of a trip carry the Shelf
/// product they were picked as), so it holds for Tesco, Dunnes and Aldi, which give no receipts. Nothing is stored: it is
/// counted from <c>trip_item</c> on each read, so there is no table to keep in step and no migration.
/// </summary>
public static class HabitEndpoints
{
    /// <summary>"Usually" looks at this many of the latest trips, so a product dropped long ago stops counting.</summary>
    public const int RecentTrips = 10;

    /// <summary>A product is usual when it was bought in at least this many of the <see cref="RecentTrips"/>.</summary>
    public const int UsualAfter = 2;

    public static void MapHabitEndpoints(this IEndpointRouteBuilder api) => api.MapGet("/trips/habits", GetHabits);

    /// <summary>
    /// Most bought first: the number of trips that bought the product (a product twice in one trip counts once), then the
    /// most recent trip, then the name. A free-text line or a deleted product has no product to rank and is left out.
    /// </summary>
    private static async Task<IResult> GetHabits(AppDbContext db, CancellationToken ct)
    {
        var rows = await (
            from i in db.TripItems.AsNoTracking()
            where i.ProductId != null
            join t in db.Trips.AsNoTracking() on i.TripId equals t.Id
            join p in db.Products.AsNoTracking() on i.ProductId equals p.Id
            select new { i.TripId, ProductId = p.Id, p.Name, t.ClosedAt }).ToListAsync(ct);

        var recent = (await db.Trips.AsNoTracking().OrderByDescending(t => t.ClosedAt).ThenByDescending(t => t.Id)
            .Select(t => t.Id).Take(RecentTrips).ToListAsync(ct)).ToHashSet();

        var habits = rows.GroupBy(r => r.ProductId)
            .Select(g => new HabitResponse(
                g.Key,
                g.First().Name,
                g.Select(r => r.TripId).Distinct().Count(),
                g.Max(r => r.ClosedAt),
                g.Where(r => recent.Contains(r.TripId)).Select(r => r.TripId).Distinct().Count() >= UsualAfter))
            .OrderByDescending(h => h.BoughtIn).ThenByDescending(h => h.LastBoughtAt).ThenBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(new { products = habits });
    }
}
