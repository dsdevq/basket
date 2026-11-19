using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Promotions;

namespace Basket.Api.Prices.Parsing;

/// <summary>What <see cref="CentraParser"/> found on the Special Offers page: the offers it could use, and how many tiles it could not.</summary>
public sealed record CentraOffers(IReadOnlyList<CatalogItem> Items, int Unusable);

/// <summary>
/// Reads centra.ie's Special Offers page (<c>/offers</c>). Centra has no online shop and no catalogue: the only prices it publishes
/// are this page's weekly offers, each tile a product with a euro price or a multibuy and an "Offer valid until" day. The page is
/// plain server-rendered HTML, one <c>&lt;h2&gt;</c> per category with its tiles under it:
/// <code>
/// &lt;a href="/offers/dairygold-lighter-454g" title="Dairygold Lighter 454g" class="item product-id-5915 ..."&gt;
///   &lt;img src="/thumbnail/..." alt="..."/&gt;
///   &lt;p class="message"&gt;€3.00&lt;/p&gt;           (or "3 for €10")
///   &lt;p class="price "&gt;€4 Each&lt;/p&gt;          (the single price, on a multibuy tile only)
///   &lt;h4 class="product-name"&gt;&lt;span&gt;Dairygold Lighter 454g&lt;/span&gt;&lt;/h4&gt;
///   &lt;p class="offer-valid"&gt;Offer valid until &lt;span class="date"&gt;9th December&lt;/span&gt;&lt;/p&gt;
/// &lt;/a&gt;
/// </code>
/// Every offer is an offer, never an everyday shelf price: it carries <see cref="PromoPrefix"/> as its promo text and the day it is
/// valid until. A tile that cannot be used (no name, no address, no readable end date, no euro price or multibuy with its single price) is counted, never guessed at: an offer
/// without an end date would never stop counting.
/// </summary>
public static partial class CentraParser
{
    /// <summary>What marks a Centra price as a weekly offer; a multibuy's wording follows it ("Weekly offer: 3 for €10").</summary>
    public const string PromoPrefix = "Weekly offer";

    private const string Origin = "https://centra.ie";

    /// <summary>
    /// The offers on the page. <paramref name="today"/> is the Dublin day it was read: the page writes "25th November" with no year,
    /// which is read as the day of that name nearest to today (so "3rd January" read on 28 December is next year's, and
    /// "25th November" read on 26 November is yesterday's: an ended offer, not one a year away).
    /// </summary>
    public static CentraOffers Parse(string html, DateOnly today)
    {
        var items = new List<CatalogItem>();
        var unusable = 0;
        string? category = null;
        foreach (Match match in BlockPattern().Matches(html))
        {
            if (match.Groups["heading"].Success)
            {
                category = Text(match.Groups["heading"].Value);
                continue;
            }

            var attrs = match.Groups["attrs"].Value;
            var body = match.Groups["body"].Value;
            var item = TileItem(attrs, body, category, today);
            if (item is null)
            {
                unusable++;
            }
            else
            {
                items.Add(item);
            }
        }

        return new CentraOffers(items, unusable);
    }

    private static CatalogItem? TileItem(string attrs, string body, string? category, DateOnly today)
    {
        var href = Attribute(attrs, "href");
        var name = Text(NamePattern().Match(body).Groups["v"].Value) is { Length: > 0 } shown ? shown : Attribute(attrs, "title") is { } title ? Text(title) : null;
        if (string.IsNullOrWhiteSpace(name) || ValidUntil(body, today) is not { } until || Address(href) is not { } url
            || Stores.ProductId(Store.Centra, url) is not { } id)
        {
            return null;
        }

        var message = Text(MessagePattern().Match(body).Groups["v"].Value);
        var each = Text(PricePattern().Match(body).Groups["v"].Value);
        decimal? price;
        string promo;
        if (Money(message) is { } single && MoneyOnly().IsMatch(message))
        {
            price = single;
            promo = PromoPrefix;
        }
        else if (PromoText.ParseMultiBuy(message) is not null && Money(each) is { } one)
        {
            price = one;
            promo = $"{PromoPrefix}: {message}";
        }
        else
        {
            return null;
        }

        return new CatalogItem(
            id,
            url,
            name,
            Category: category,
            ImageUrl: Image(body),
            Price: price,
            PromoText: promo,
            ValidUntil: until);
    }

    /// <summary>The offer's page as an https address on centra.ie; null for a link that leaves the site.</summary>
    private static string? Address(string? href)
    {
        if (!Uri.TryCreate(new Uri(Origin + "/offers"), WebUtility.HtmlDecode(href ?? string.Empty), out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host == "centra.ie" || url.Host == "www.centra.ie"))
        {
            return null;
        }

        return Origin + url.AbsolutePath.TrimEnd('/');
    }

    private static string? Image(string body)
    {
        var src = ImagePattern().Match(body).Groups["v"].Value;
        return Uri.TryCreate(new Uri(Origin + "/offers"), WebUtility.HtmlDecode(src), out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url.GetLeftPart(UriPartial.Path)
            : null;
    }

    private static DateOnly? ValidUntil(string body, DateOnly today)
    {
        var date = DatePattern().Match(body);
        if (!date.Success
            || !int.TryParse(date.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !DateTime.TryParseExact(date.Groups["m"].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return null;
        }

        DateOnly? nearest = null;
        foreach (var year in new[] { today.Year - 1, today.Year, today.Year + 1 })
        {
            if (day >= 1 && day <= DateTime.DaysInMonth(year, month.Month))
            {
                var candidate = new DateOnly(year, month.Month, day);
                if (nearest is null || Math.Abs(candidate.DayNumber - today.DayNumber) < Math.Abs(nearest.Value.DayNumber - today.DayNumber))
                {
                    nearest = candidate;
                }
            }
        }

        return nearest;
    }

    private static decimal? Money(string text) =>
        MoneyPattern().Match(text) is { Success: true } m
            && decimal.TryParse(m.Groups["v"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : null;

    private static string? Attribute(string attrs, string name) =>
        Regex.Match(attrs, $@"\b{name}=""(?<v>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } m
            ? m.Groups["v"].Value
            : null;

    /// <summary>The text of a fragment: tags gone, entities decoded, white space single.</summary>
    private static string Text(string fragment) =>
        WhiteSpace().Replace(WebUtility.HtmlDecode(Tags().Replace(fragment, " ")), " ").Trim();

    [GeneratedRegex(@"<h2[^>]*>(?<heading>.*?)</h2>|<a\s(?<attrs>[^>]*\bclass=""[^""]*\bitem\b[^""]*""[^>]*)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"<h4[^>]*\bproduct-name\b[^>]*>(?<v>.*?)</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"<p[^>]*\bclass=""message""[^>]*>(?<v>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessagePattern();

    [GeneratedRegex(@"<p[^>]*\bclass=""price\s*""[^>]*>(?<v>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"<img[^>]*\bsrc=""(?<v>[^""]*)""", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"Offer\s+valid\s+until\s*<span[^>]*>\s*(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?<m>[A-Za-z]+)\s*</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"€\s*(?<v>\d+(?:\.\d{1,2})?)", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();

    /// <summary>The whole text is one euro amount ("€3.00"), not a multibuy that mentions one.</summary>
    [GeneratedRegex(@"^€\s*\d+(?:\.\d{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyOnly();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
