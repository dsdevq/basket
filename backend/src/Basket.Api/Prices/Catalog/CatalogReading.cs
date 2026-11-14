using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices.Ingest;

namespace Basket.Api.Prices.Catalog;

/// <summary>
/// One catalog product as a reader posts it (the wire shape of <c>POST /api/ingest/catalog</c>). Everything is optional on
/// the wire; <see cref="CatalogReading.TryCreate"/> decides what is usable. Prices are euro with at most cents.
/// </summary>
public sealed record CatalogItem(
    string? ExternalId,
    string? Url,
    string? Name,
    string? Brand = null,
    string? SizeText = null,
    decimal? UnitPrice = null,
    string? UnitPriceBasis = null,
    string? Barcode = null,
    string? Category = null,
    string? ImageUrl = null,
    decimal? Price = null,
    decimal? WasPrice = null,
    decimal? LoyaltyPrice = null,
    string? LoyaltyScheme = null,
    string? PromoText = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidUntil = null,
    bool? InStock = null);

/// <summary>What a product costs on the day it was read: the tuple whose change writes a <see cref="StoreProductPrice"/> row.</summary>
public sealed record CatalogPrice(
    decimal? Price,
    decimal? WasPrice,
    decimal? LoyaltyPrice,
    string? LoyaltyScheme,
    string? PromoText,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    bool InStock)
{
    /// <summary>The shop said nothing about a price and nothing about stock: a catalog entry without one (Lidl's in-store-only lines).</summary>
    public bool IsEmpty => Price is null && WasPrice is null && LoyaltyPrice is null && InStock
        && LoyaltyScheme is null && PromoText is null && ValidFrom is null && ValidUntil is null;
}

/// <summary>
/// A catalog product that passed every check and is in the shapes the tables hold: the one input of <see cref="CatalogRecorder"/>,
/// whether it came from the HTTP door (PC reader) or from a server-side sweep.
/// </summary>
public sealed record CatalogReading(
    Store Store,
    string ExternalId,
    string Url,
    string Name,
    string? Brand,
    string? SizeText,
    ProductSize? Size,
    decimal? UnitPrice,
    string? UnitPriceBasis,
    string? Barcode,
    string? Category,
    string? ImageUrl,
    CatalogPrice Price)
{
    public static class Reasons
    {
        public const string ExternalIdInvalid = "external_id_invalid";
        public const string UrlInvalid = PriceIngest.Reasons.UrlInvalid;
        public const string NoName = "no_name";
        public const string PriceInvalid = PriceIngest.Reasons.PriceInvalid;
        public const string DatesInvalid = PriceIngest.Reasons.DatesInvalid;
    }

    /// <summary>
    /// The reading for <paramref name="item"/>, or the stable reason it is unusable. A product with no price at all is fine
    /// (it is a catalog entry, not a price reading). The optional extras never cost the product: an unreadable size leaves it
    /// without one, a barcode that is not a valid GTIN or a photo address that is not a plain https one is dropped, so a
    /// wrong barcode can never be taken for evidence that two products are the same.
    /// </summary>
    public static bool TryCreate(Store store, CatalogItem item, out CatalogReading? reading, out string? reason)
    {
        reading = null;
        var externalId = UserNames.Clean(item.ExternalId, StoreProduct.MaxExternalIdLength + 1);
        if (externalId is null || externalId.Length > StoreProduct.MaxExternalIdLength)
        {
            reason = Reasons.ExternalIdInvalid;
            return false;
        }

        if (!Stores.TryNormalizeProductUrl(store, item.Url, out var url))
        {
            reason = Reasons.UrlInvalid;
            return false;
        }

        var name = UserNames.Clean(item.Name, StoreProduct.MaxNameLength);
        if (name is null)
        {
            reason = Reasons.NoName;
            return false;
        }

        if (!PriceIngest.IsMoney(item.Price) || !PriceIngest.IsMoney(item.WasPrice) || !PriceIngest.IsMoney(item.LoyaltyPrice)
            || !PriceIngest.IsMoney(item.UnitPrice))
        {
            reason = Reasons.PriceInvalid;
            return false;
        }

        if (item.ValidFrom is { } from && item.ValidUntil is { } until && from > until)
        {
            reason = Reasons.DatesInvalid;
            return false;
        }

        var sizeText = UserNames.Clean(item.SizeText, StoreProduct.MaxSizeTextLength);
        var scheme = item.LoyaltyPrice is null
            ? null
            : UserNames.Clean(item.LoyaltyScheme, 32) ?? (store.LoyaltyScheme() is { Length: > 0 } own ? own : null);
        reading = new CatalogReading(
            store,
            externalId,
            url.ToString(),
            name,
            UserNames.Clean(item.Brand, StoreProduct.MaxBrandLength),
            sizeText,
            ProductSize.Parse(sizeText) ?? ProductSize.Parse(name),
            item.UnitPrice,
            item.UnitPrice is null ? null : UserNames.Clean(item.UnitPriceBasis, StoreProduct.MaxUnitPriceBasisLength),
            Gtin.TryNormalize(item.Barcode, out var gtin) ? gtin : null,
            UserNames.Clean(item.Category, StoreListing.MaxCategoryLength),
            ImageUrls.TryNormalize(item.ImageUrl, out var image) ? image : null,
            new CatalogPrice(
                item.Price,
                item.WasPrice,
                item.LoyaltyPrice,
                scheme,
                UserNames.Clean(item.PromoText, 200),
                item.ValidFrom,
                item.ValidUntil,
                item.InStock ?? true));
        reason = null;
        return true;
    }
}
