using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices.Catalog;

namespace Basket.Api.Prices.Parsing;

/// <summary>
/// Reads lidl.ie. Lidl shows no everyday shelf prices online, only the offers of the week: the offer pages carry a
/// tile per product with the price, the Lidl Plus price and the dates. A product on no offer page falls back to its
/// own page, whose JSON-LD holds a price when there is one.
/// </summary>
public static partial class LidlParser
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

    /// <summary>
    /// What a Lidl search page lists: a tile per product with its name, its address and its photo, in the order Lidl ranks them.
    /// A tile without a usable address or name is skipped. No price is read here; the nightly job prices a product once it is linked.
    /// </summary>
    public static IReadOnlyList<ShopSearchResult> ParseSearchTiles(string html)
    {
        var results = new List<ShopSearchResult>();
        foreach (var tile in PageData.GridTiles(html))
        {
            var path = PageData.String(tile, "canonicalUrl") ?? PageData.String(tile, "canonicalPath");
            var title = PageData.Clean(PageData.String(tile, "fullTitle") ?? PageData.String(tile, "title"));
            if (title is null
                || path is null
                || !path.StartsWith('/')
                || !Stores.TryNormalizeProductUrl(Store.Lidl, "https://" + Store.Lidl.Host() + path, out var url))
            {
                continue;
            }

            results.Add(new ShopSearchResult(Store.Lidl.Slug(), url.ToString(), title, TileImage(tile)));
        }

        return results;
    }

    /// <summary>
    /// Every product tile of a category listing page as a catalog item, priced or not: Lidl lists its whole range online but
    /// shows a regular price on only part of it, so a tile with no price is a product without one, never an error. A tile
    /// with no product number or no address is still returned, without that field, so the caller can count it as unusable. The barcode is not on tiles; it comes from the product page
    /// (<see cref="ParseGtin"/>).
    /// </summary>
    public static IReadOnlyList<CatalogItem> ParseCatalogTiles(string html)
    {
        var items = new List<CatalogItem>();
        foreach (var tile in PageData.GridTiles(html))
        {
            var id = PageData.String(tile, "erpNumber");
            var path = PageData.String(tile, "canonicalUrl") ?? PageData.String(tile, "canonicalPath");
            var quote = TileQuote(tile);
            items.Add(new CatalogItem(
                id,
                path is not null && path.StartsWith('/') ? "https://" + Store.Lidl.Host() + path : null,
                PageData.Clean(PageData.String(tile, "fullTitle") ?? PageData.String(tile, "title")),
                Brand: PageData.Clean(PageData.String(tile, "brand", "name")),
                SizeText: PageData.Clean(PageData.String(tile, "price", "packaging", "text")),
                Category: TileCategory(tile),
                ImageUrl: TileImage(tile),
                Price: quote?.Price,
                WasPrice: quote?.WasPrice,
                LoyaltyPrice: quote?.LoyaltyPrice,
                LoyaltyScheme: quote?.LoyaltyScheme,
                PromoText: quote?.PromoText,
                ValidFrom: quote?.ValidFrom,
                ValidUntil: quote?.ValidUntil));
        }

        return items;
    }

    /// <summary>
    /// Lidl's own category path of a tile (<c>keyfacts.wonCategoryPrimary</c>, "Worlds of need/Food and near food/Meat &amp; Poultry/Poultry")
    /// as a basket category path, without the root every product shares.
    /// </summary>
    private static string? TileCategory(JsonElement tile)
    {
        var raw = PageData.String(tile, "keyfacts", "wonCategoryPrimary");
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.Equals(part, "Worlds of need", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(StoreListing.CategorySeparator, parts);
    }

    /// <summary>The addresses of a sitemap or sitemap index (<c>&lt;loc&gt;</c> elements), in order.</summary>
    public static IReadOnlyList<Uri> SitemapLocations(string xml)
    {
        var found = new List<Uri>();
        foreach (Match loc in SitemapLoc().Matches(xml))
        {
            if (Uri.TryCreate(WebUtility.HtmlDecode(loc.Groups["url"].Value.Trim()), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, Store.Lidl.Host(), StringComparison.OrdinalIgnoreCase))
            {
                found.Add(uri);
            }
        }

        return found;
    }

    private static readonly string[] FoodWords =
    [
        "food", "drink", "drinks", "fruit", "vegetables", "meat", "poultry", "fish", "seafood", "dairy", "cheese", "eggs", "bakery",
        "bread", "chilled", "frozen", "snacks", "sweets", "chocolate", "biscuits", "cereal", "pasta", "rice", "sauces", "tea",
        "coffee", "juice", "water", "wine", "beer", "cider", "spirits", "grocery", "pantry", "deli",
    ];

    /// <summary>
    /// The listing pages (<c>/c/&lt;slug&gt;/s&lt;id&gt;</c>) of a pages sitemap whose slug names food or drink, i.e. contains one
    /// of <see cref="FoodWords"/> as a whole word of its hyphenated name. Query and fragment are dropped; each page once.
    /// </summary>
    public static IReadOnlyList<Uri> FoodCategoryPages(string sitemapXml)
    {
        var pages = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in SitemapLocations(sitemapXml))
        {
            var match = CategoryPath().Match(uri.AbsolutePath.TrimEnd('/'));
            if (!match.Success
                || !match.Groups["slug"].Value.Split('-').Any(word => FoodWords.Contains(word, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var page = new Uri($"https://{Store.Lidl.Host()}{uri.AbsolutePath.TrimEnd('/')}");
            if (seen.Add(page.AbsoluteUri))
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    /// <summary>
    /// The barcode of a Lidl product page: the first valid GTIN of the JSON-LD <c>gtin13</c> (Lidl writes a list, one per pack
    /// variant). Null when the page has none or only malformed ones; a wrong barcode is never stored.
    /// </summary>
    public static string? ParseGtin(string html)
    {
        if (PageData.ProductJsonLd(html) is not { } product)
        {
            return null;
        }

        if (!product.TryGetProperty("gtin13", out var value))
        {
            return null;
        }

        var candidates = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        foreach (var candidate in candidates)
        {
            if (candidate.ValueKind == JsonValueKind.String && Gtin.TryNormalize(candidate.GetString(), out var gtin))
            {
                return gtin;
            }
        }

        return null;
    }

    [GeneratedRegex(@"<loc>\s*(?<url>[^<]+?)\s*</loc>", RegexOptions.IgnoreCase)]
    private static partial Regex SitemapLoc();

    [GeneratedRegex(@"^/c/(?<slug>[a-z0-9-]+)/s\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryPath();

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
