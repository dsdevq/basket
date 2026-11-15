using Basket.Api.Data;
using Basket.Api.Prices;

namespace Basket.Api.Walk;

public static class WalkEndpoints
{
    /// <summary>Signed-in people only. Section orders are read-only: nobody edits an aisle.</summary>
    public static void MapWalkEndpoints(this IEndpointRouteBuilder api)
    {
        // The seeded section order of each chain.
        api.MapGet("/walk/seeded", () => Results.Ok(new
        {
            stores = Stores.All.Select(s => new { store = s.Slug(), name = s.DisplayName(), sections = SeededSectionOrder.For(s) }),
        }));

        // The order each shop is walked in now: learned from its trips (source "learned", with how many) or the seeded one.
        api.MapGet("/walk/order", async (AppDbContext db, CancellationToken ct) =>
        {
            var walk = await WalkOrderLoader.LoadAsync(db, ct);
            return Results.Ok(new
            {
                stores = Stores.All.Select(s => new
                {
                    store = s.Slug(),
                    name = s.DisplayName(),
                    source = walk.IsLearned(s) ? "learned" : "seeded",
                    trips = walk.TripsOf(s),
                    sections = walk.For(s),
                }),
            });
        });
    }
}
