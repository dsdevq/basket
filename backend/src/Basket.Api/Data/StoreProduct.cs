using Basket.Api.Prices;
using NpgsqlTypes;

namespace Basket.Api.Data;

/// <summary>
/// One product in one shop's catalog, whether or not anyone on the shelf buys it: the combined catalog has one row per
/// product per shop, keyed by the shop's own id (<see cref="ExternalId"/>). Filled by the catalog sweeps (the server for
/// the shops it can read, the PC reader for the rest) through <c>CatalogRecorder</c>. A shelf <see cref="Product"/> stays the
/// canonical node; a <see cref="StoreListing"/> may point at the catalog row that is its equivalent.
/// </summary>
public sealed class StoreProduct
{
    public const int MaxExternalIdLength = 64;
    public const int MaxNameLength = 300;
    public const int MaxBrandLength = 100;
    public const int MaxSizeTextLength = 50;
    public const int MaxUnitPriceBasisLength = 32;

    /// <summary>The base units <see cref="SizeUnit"/> is stored in.</summary>
    public const string Grams = "g";

    public const string Millilitres = "ml";

    public const string Each = "each";

    public Guid Id { get; set; }

    public Store Store { get; set; }

    /// <summary>The shop's own id (Aldi's sku, Lidl's product number, Tesco's id, Dunnes' number). Unique within a shop.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The product page, in the shape <c>Stores.TryNormalizeProductUrl</c> accepts, so it can be matched to a listing.</summary>
    public string Url { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Brand { get; set; }

    /// <summary>The size as the shop shows it ("250 g", "6 x 330ml").</summary>
    public string? SizeText { get; set; }

    /// <summary>
    /// <see cref="SizeText"/> read by <c>ProductSize</c> and reduced to its base unit, per pack: 1500 for "1.5L". Null when the
    /// text states no size.
    /// </summary>
    public decimal? SizeAmount { get; set; }

    /// <summary><see cref="Grams"/>, <see cref="Millilitres"/> or <see cref="Each"/>; set exactly when <see cref="SizeAmount"/> is.</summary>
    public string? SizeUnit { get; set; }

    /// <summary>How many packs of <see cref="SizeAmount"/> the size states ("6 x 330ml" is 6), so a multipack is never taken for one bottle.</summary>
    public int SizePacks { get; set; } = 1;

    /// <summary>The shop's own comparison price (euro per <see cref="UnitPriceBasis"/>), when it gives one.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>What <see cref="UnitPrice"/> is per, as the shop writes it ("1 KG", "100g").</summary>
    public string? UnitPriceBasis { get; set; }

    /// <summary>The barcode as a 14-digit GTIN (<c>Gtin.TryNormalize</c>); null when the shop does not publish one.</summary>
    public string? Barcode { get; set; }

    /// <summary>The shop's category path, outermost first, joined with <see cref="StoreListing.CategorySeparator"/>.</summary>
    public string? Category { get; set; }

    /// <summary>An https address of the shop's photo (<c>ImageUrls</c>); the picture itself is never kept.</summary>
    public string? ImageUrl { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The last time a sweep listed this product.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Set when a complete sweep no longer listed it (and the sweep saw nearly all of what was known, see the 90 percent
    /// guard). A sweep that lists it again clears this.
    /// </summary>
    public DateTimeOffset? DiscontinuedAt { get; set; }

    /// <summary>Full-text search over name and brand; computed by the database, never written.</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}

/// <summary>
/// What a catalog product cost from <see cref="ObservedOn"/> (the Dublin day the value first appeared) until the next row.
/// Written only when the price tuple changes, so a stable price over a year is one row, not 365.
/// </summary>
public sealed class StoreProductPrice
{
    public Guid StoreProductId { get; set; }

    public DateOnly ObservedOn { get; set; }

    public decimal? Price { get; set; }

    public decimal? WasPrice { get; set; }

    public decimal? LoyaltyPrice { get; set; }

    public string? LoyaltyScheme { get; set; }

    public string? PromoText { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidUntil { get; set; }

    public bool InStock { get; set; }
}
