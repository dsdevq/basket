using Basket.Api.Data;

namespace Basket.Api.Prices.Catalog;

/// <summary>How a catalog row is worded as a shelf product.</summary>
public static class CatalogNames
{
    /// <summary>
    /// The shelf name of a catalog row: the shop keeps the brand in a field of its own, so it goes in front of the name unless the name
    /// already says it ("Milbona" + "Milbona Greek Yoghurt" stays as is). The matcher compares a catalog row under brand and name together,
    /// so the shelf product is named the same way.
    /// </summary>
    public static string ShelfName(StoreProduct row)
    {
        var name = row.Name.Trim();
        var brand = row.Brand?.Trim();
        return string.IsNullOrEmpty(brand) || name.Contains(brand, StringComparison.OrdinalIgnoreCase) ? name : $"{brand} {name}";
    }
}
