using System.Net;
using System.Text.RegularExpressions;

namespace Basket.Api.Prices.Parsing;

/// <summary>
/// Reads aldi.ie product pages. The JSON-LD holds the price being charged (already the reduced one when the
/// product is on sale); the page's price block adds the original price and the "Save 6%" tag.
/// </summary>
public static partial class AldiParser
{
    public static PriceQuote ParseProductPage(string html)
    {
        var product = PageData.ProductJsonLd(html)
            ?? throw new ParseException("The Aldi page has no product data (the page layout may have changed).");
        var offer = PageData.FirstOffer(product);
        var price = offer is { } o ? PageData.Money(o, "price") : null;
        var availability = offer is { } a ? PageData.String(a, "availability") : null;

        var brand = PageData.String(product, "brand", "name");
        var name = PageData.Clean(PageData.String(product, "name"));
        var title = brand is null || name is null ? name : $"{PageData.Clean(brand)} {name}";

        decimal? was = null;
        string? promo = null;
        var original = OriginalPrice().Match(html);
        if (original.Success)
        {
            was = PageData.EuroAmount(WebUtility.HtmlDecode(original.Groups["label"].Value));
        }

        var tag = DiscountTag().Match(html);
        if (tag.Success)
        {
            promo = PageData.Clean(tag.Groups["label"].Value);
        }

        // A "was" price that is not above the price paid is not a reduction.
        if (was is not null && price is not null && was <= price)
        {
            was = null;
            promo = null;
        }

        return new PriceQuote(
            title,
            price,
            was,
            LoyaltyPrice: null,
            LoyaltyScheme: null,
            promo,
            ValidFrom: null,
            ValidUntil: null,
            InStock: price is not null && (availability is null || availability.EndsWith("InStock", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"<del[^>]*aria-label=""Original price:\s*(?<label>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalPrice();

    [GeneratedRegex(@"data-test=""discount-tag""[^>]*aria-label=""(?<label>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DiscountTag();
}
