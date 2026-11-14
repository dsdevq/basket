using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

/// <summary>
/// How a shelf page finds its catalog row: both are keyed by the shop's own product id, and the id is in the page's address
/// (<see cref="Stores.ProductId"/>), so a listing is linked to the <see cref="StoreProduct"/> of the same shop and id. Nothing is
/// guessed from names: a page whose catalog row does not exist yet stays unlinked until a sweep lists the product.
/// </summary>
public static class CatalogLink
{
    /// <summary>
    /// Points <paramref name="listing"/> at the catalog row its address names, or clears the link when there is none (a changed
    /// address must not keep the old product's link). Applies to the context; the caller saves.
    /// </summary>
    public static async Task LinkAsync(AppDbContext db, StoreListing listing, CancellationToken ct)
    {
        var id = Stores.ProductId(listing.Store, listing.Url);
        listing.StoreProductId = id is null
            ? null
            : await db.StoreProducts.Where(p => p.Store == listing.Store && p.ExternalId == id).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
    }
}
