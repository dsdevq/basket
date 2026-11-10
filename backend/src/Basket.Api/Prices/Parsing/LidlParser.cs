using System.Text.Json;

namespace Basket.Api.Prices.Parsing;

/// <summary>
/// Reads lidl.ie. Lidl shows no everyday shelf prices online, only the offers of the week: the offer pages carry a
/// tile per product with the price, the Lidl Plus price and the dates. A product on no offer page falls back to its
/// own page, whose JSON-LD holds a price when there is one.
/// </summary>
public static class LidlParser
{
    /// <summary>The priced tiles of an offer or category page, keyed by Lidl's product number.</summary>
    public static IReadOnlyDictionary<string, PriceQuote> ParseOfferTiles(string html)
    {
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        foreach (var tile in PageData.GridTiles(html))
        {
            var id = PageData.String(tile, "erpNumber") ?? PageData.String(tile, "productId");
            if (id is null || TileQuote(tile) is not { } quote)
            {
                continue;
            }

            quotes[id] = quote;
        }

        return quotes;
    }

    private static PriceQuote? TileQuote(JsonElement tile)
    {
        var price = PageData.Money(tile, "price", "price");

        decimal? loyaltyPrice = null;
        string? promo = null;
        if (tile.TryGetProperty("lidlPlus", out var plus) && plus.ValueKind == JsonValueKind.Array && plus.GetArrayLength() > 0)
        {
            loyaltyPrice = PageData.Money(plus[0], "price", "price");
            promo = PageData.Clean(PageData.String(plus[0], "highlightText"));
        }

        if (price is null && loyaltyPrice is null)
        {
            return null;
        }

        promo ??= PageData.Clean(PageData.String(tile, "regionsPrices", "1", "currentLidlPlusPrice", "highlightText"));

        var was = PageData.Money(tile, "regionsPrices", "1", "currentPrice", "oldPrice")
            ?? PageData.Money(tile, "regionsPrices", "1", "currentLidlPlusPrice", "price", "oldPrice");
        string[] window = price is not null
            ? ["regionsPrices", "1", "currentPrice"]
            : ["regionsPrices", "1", "currentLidlPlusPrice", "price"];
        var title = PageData.Clean(PageData.String(tile, "fullTitle") ?? PageData.String(tile, "title"));

        return new PriceQuote(
            title,
            price,
            was,
            loyaltyPrice,
            loyaltyPrice is null ? null : Store.Lidl.LoyaltyScheme(),
            promo,
            PageData.CalendarDate(tile, [.. window, "startDate"]) ?? PageData.CalendarDate(tile, "storeStartDate"),
            PageData.CalendarDate(tile, [.. window, "endDate"]) ?? PageData.CalendarDate(tile, "storeEndDate"),
            InStock: true,
            ImageUrl: TileImage(tile));
    }

    /// <summary>The tile's own photo, or the first of its gallery.</summary>
    private static string? TileImage(JsonElement tile) =>
        PageData.ImageUrl(tile.TryGetProperty("image", out var image) ? image : null)
        ?? PageData.ImageUrl(tile.TryGetProperty("imageList", out var list) ? list : null);

    /// <summary>A product page: name, price when Lidl lists one, and whether it is in stock.</summary>
    public static PriceQuote ParseProductPage(string html)
    {
        var product = PageData.ProductJsonLd(html)
            ?? throw new ParseException("The Lidl page has no product data (the page layout may have changed).");
        var offer = PageData.FirstOffer(product);
        var price = offer is { } o ? PageData.Money(o, "price") : null;
        var availability = offer is { } a ? PageData.String(a, "availability") : null;

        var name = PageData.Clean(PageData.String(product, "name"));
        return new PriceQuote(
            name,
            price,
            WasPrice: null,
            LoyaltyPrice: null,
            LoyaltyScheme: null,
            PromoText: null,
            ValidFrom: null,
            ValidUntil: null,
            InStock: price is not null && availability is not null && availability.EndsWith("InStock", StringComparison.Ordinal),
            ImageUrl: PageData.ImageUrl(product.TryGetProperty("image", out var image) ? image : null) ?? PageData.OgImage(html),
            Category: PageData.BreadcrumbCategory(html, name, PageData.String(product, "url")));
    }
}
