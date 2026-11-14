using Basket.Api.Prices;

namespace Basket.Api.Walk;

public static class WalkEndpoints
{
    /// <summary>Signed-in people only. The seeded section order of each chain, read-only: nobody edits an aisle.</summary>
    public static void MapWalkEndpoints(this IEndpointRouteBuilder api) =>
        api.MapGet("/walk/seeded", () => Results.Ok(new
        {
            stores = Stores.All.Select(s => new { store = s.Slug(), name = s.DisplayName(), sections = SeededSectionOrder.For(s) }),
        }));
}
